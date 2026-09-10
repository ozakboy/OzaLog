using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace OzaLog.Core
{
    /// <summary>
    /// 異步日誌處理器 v3.0 - HFT 級高並發 producer / 單一 consumer 架構。
    /// 呼叫端只做 Interlocked enqueue + count check（奈秒級），所有格式化與 I/O 由 dispatcher 執行緒承擔。
    /// </summary>
    /// <remarks>
    /// v3.0 與 v2.x 的差異：
    /// • LogItem 改 readonly struct（零 GC 壓力）
    /// • Backpressure 改為 drop oldest（觸發 OnDropped），不再降級為呼叫端同步寫入
    /// • v3.1.1：Error / Fatal 與 immediateFlush 的項目只走呼叫端同步寫入，不再重複入隊
    /// • 格式化在 dispatcher 完成（呼叫端不打 DateTime.Now / string.Format）
    /// • 過期清理改為背景 timer，不在 hot path
    /// • 100ms 定期 flush 由 FileStreamPool 透過 timer 處理
    /// </remarks>
    internal static class AsyncLogHandler
    {
        private static readonly ConcurrentQueue<LogItem> _logQueue = new ConcurrentQueue<LogItem>();
        private static readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private static readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private static Task _processTask;
        private static int _initialized;
        private static Timer _diskFlushTimer;

        // 統計：未上報的 drop 計數（即使 OnDropped 為 null 仍記錄，便於除錯）
        private static long _droppedCount;

        public static long DroppedCount => Interlocked.Read(ref _droppedCount);

        private static LogConfiguration.IAsyncLogOptions CurrentAsyncOptions
            => LogConfiguration.Current.AsyncOptions;

        public static void Initialize()
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

            // 啟動相關背景元件
            _ = TimestampCache.GetCurrentTicks();   // 觸發 TimestampCache 起動
            LogRetentionCleaner.EnsureStarted();

            _processTask = Task.Run(ProcessLogQueueAsync);

            // 100ms（或使用者設定）定期 flush 全部 stream
            var diskFlushMs = LogConfiguration.Current.DiskFlushIntervalMs;
            _diskFlushTimer = new Timer(static _ => FileStreamPool.FlushAll(), null, diskFlushMs, diskFlushMs);

            AppDomain.CurrentDomain.ProcessExit += static (s, e) => ShutdownGracefully();
            AppDomain.CurrentDomain.UnhandledException += static (s, e) => ShutdownGracefully();
        }

        /// <summary>
        /// 入隊。呼叫端執行緒成本：1× volatile read（cache 大小）+ 1× ConcurrentQueue.Enqueue（CAS）+ 1× SemaphoreSlim.Release。
        /// 若 queue 已滿，dequeue 一筆最舊的（drop oldest），觸發 OnDropped。
        /// Error / Fatal 與 immediateFlush 的項目改走呼叫端同步寫入，**不入隊**（見下方說明）。
        /// </summary>
        public static void Enqueue(in LogItem item)
        {
            if (Interlocked.CompareExchange(ref _initialized, 0, 0) == 0)
                Initialize();

            // 立即落檔路徑：由呼叫端執行緒同步寫入 + flush 確保落盤（crash 前一定看得到）。
            // ⚠️ 寫完就 return，不可再入隊：入隊會讓 dispatcher 之後把同一筆再寫一次，
            //    造成日誌檔出現兩行完全相同的內容（v3.1.0 bug，錯誤筆數被灌水一倍）。
            // ⚠️ 用明確等式判斷，避免 LogLevel.CustomName=99 被 >= Fatal 條件誤判為高嚴重性
            bool isAutoFlush = item.Level == LogLevel.Error || item.Level == LogLevel.Fatal;
            if (item.RequireImmediateFlush || isAutoFlush)
            {
                LogText.Write(in item);
                // LogText.Write 只在 RequireImmediateFlush 時自行 flush，自動 flush 級別需在此補上
                if (!item.RequireImmediateFlush)
                    FileStreamPool.Flush(item.Level, item.Name);
                return;
            }

            // Drop oldest：滿時先丟一筆最舊的
            var max = CurrentAsyncOptions.MaxQueueSize;
            if (_logQueue.Count >= max)
            {
                if (_logQueue.TryDequeue(out _))
                {
                    Interlocked.Increment(ref _droppedCount);
                    var cb = LogConfiguration.Current.OnDropped;
                    if (cb != null)
                    {
                        try { cb(); } catch { /* callback 內出錯不能拖累寫入路徑 */ }
                    }
                }
            }

            _logQueue.Enqueue(item);

            // 釋放 signal；若已被釋放過 dispatcher 仍在處理，本 release 是無傷的（會在 WaitAsync 等待時立刻通過）
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }

        private static async Task ProcessLogQueueAsync()
        {
            var token = _cancellationTokenSource.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(TimeSpan.FromMilliseconds(CurrentAsyncOptions.FlushIntervalMs)).ConfigureAwait(false);
                    DrainBatch();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"AsyncLogHandler.ProcessLogQueueAsync 錯誤: {ex.Message}");
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }

            DrainBatch();
        }

        private static void DrainBatch()
        {
            var batchLimit = CurrentAsyncOptions.MaxBatchSize;
            var processed = 0;
            while (processed < batchLimit && _logQueue.TryDequeue(out var item))
            {
                LogText.Write(in item);
                processed++;
            }
        }

        public static async Task ShutdownAsync()
        {
            if (Interlocked.CompareExchange(ref _initialized, 0, 1) != 1) return;

            try
            {
                DrainBatch();   // 清隊列
                _cancellationTokenSource.Cancel();
                try { _signal.Release(); } catch (SemaphoreFullException) { }

                if (_processTask != null)
                    await Task.WhenAny(_processTask, Task.Delay(2000)).ConfigureAwait(false);

                FileStreamPool.FlushAll();
                FileStreamPool.Shutdown();
                LogRetentionCleaner.Shutdown();
                TimestampCache.Shutdown();

                var t = Interlocked.Exchange(ref _diskFlushTimer, null);
                t?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AsyncLogHandler.ShutdownAsync 錯誤: {ex.Message}");
            }
        }

        private static void ShutdownGracefully()
        {
            try
            {
                DrainBatch();
                FileStreamPool.FlushAll();
            }
            catch
            {
                // 收尾時的錯誤吞掉
            }
        }

        // === v2.x 相容入口（保留方法名以利內部 callsite 漸進遷移） ===
        public static void EnqueueLog(LogLevel level, string name, string message, object[] args, bool immediateFlush = false)
        {
            var currentThread = Thread.CurrentThread;
            var item = new LogItem(
                level: level,
                name: name ?? string.Empty,
                message: message,
                args: (args != null && args.Length > 0) ? args : null,
                timestampTicks: TimestampCache.GetCurrentTicks(),
                threadId: currentThread.ManagedThreadId,
                threadName: currentThread.Name,
                requireImmediateFlush: immediateFlush);
            Enqueue(in item);
        }
    }
}
