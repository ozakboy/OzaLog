using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// • v3.2.0：Error / Fatal 與 immediateFlush 的項目只走呼叫端同步寫入，不再重複入隊
    /// • 格式化在 dispatcher 完成（呼叫端不打 DateTime.Now / string.Format）
    /// • 過期清理改為背景 timer，不在 hot path
    /// • 100ms 定期 flush 由 FileStreamPool 透過 timer 處理
    /// • v3.3.0：新增未完成筆數（<c>_pending</c>）追蹤與可重啟的 signal / CTS，
    ///   讓 <c>LOG.Flush()</c> 能等到「已入隊的每一筆都寫完」、<c>LOG.Shutdown()</c> 之後能再次 Configure 重啟
    /// </remarks>
    internal static class AsyncLogHandler
    {
        private static readonly ConcurrentQueue<LogItem> _logQueue = new ConcurrentQueue<LogItem>();

        // v3.3.0：Shutdown 後允許重新啟動，因此 signal / CTS 不再是 readonly（每次 Initialize 重建）
        private static SemaphoreSlim _signal = new SemaphoreSlim(0);
        private static CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private static Task _processTask;
        private static int _initialized;
        private static int _exitHooksRegistered;
        private static Timer _diskFlushTimer;

        // 統計：未上報的 drop 計數（即使 OnDropped 為 null 仍記錄，便於除錯）
        private static long _droppedCount;

        // v3.3.0：已入隊但尚未寫完的筆數。
        // 「佇列是空的」不等於「全部寫完」—— dispatcher 可能剛 TryDequeue 出一筆、還沒寫進檔案，
        // 只看佇列會讓 Flush 提早回來，宿主以為落地了其實少一筆。
        private static long _pending;

        public static long DroppedCount => Interlocked.Read(ref _droppedCount);

        private static LogConfiguration.IAsyncLogOptions CurrentAsyncOptions
            => LogConfiguration.Current.AsyncOptions;

        public static void Initialize()
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

            // 啟動相關背景元件
            _ = TimestampCache.GetCurrentTicks();   // 觸發 TimestampCache 起動
            LogRetentionCleaner.EnsureStarted();

            // v3.3.0：重啟時舊的 CTS 已 cancel、舊的 semaphore 計數可能不為 0，一律換新的
            _signal = new SemaphoreSlim(0);
            _cancellationTokenSource = new CancellationTokenSource();

            _processTask = Task.Run(ProcessLogQueueAsync);

            // 100ms（或使用者設定）定期 flush 全部 stream
            var diskFlushMs = LogConfiguration.Current.DiskFlushIntervalMs;
            _diskFlushTimer = new Timer(static _ => FileStreamPool.FlushAll(), null, diskFlushMs, diskFlushMs);

            // 行程結束的收尾只掛一次：重啟時再掛一次會讓同一個收尾跑好幾遍
            if (Interlocked.CompareExchange(ref _exitHooksRegistered, 1, 0) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += static (s, e) => ShutdownGracefully();
                AppDomain.CurrentDomain.UnhandledException += static (s, e) => ShutdownGracefully();
            }
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
                    Interlocked.Decrement(ref _pending);
                    Interlocked.Increment(ref _droppedCount);
                    var cb = LogConfiguration.Current.OnDropped;
                    if (cb != null)
                    {
                        try { cb(); } catch { /* callback 內出錯不能拖累寫入路徑 */ }
                    }
                }
            }

            // 先加計數再入隊：反過來會出現「已在佇列裡但 pending 還沒算到」的空窗，
            // 剛好落在該空窗的 Flush 會提早回來
            Interlocked.Increment(ref _pending);
            _logQueue.Enqueue(item);

            // 釋放 signal；若已被釋放過 dispatcher 仍在處理，本 release 是無傷的（會在 WaitAsync 等待時立刻通過）
            try { _signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
        }

        private static async Task ProcessLogQueueAsync()
        {
            // 在啟動當下取用，避免重啟後讀到新一輪的 signal / token
            var signal = _signal;
            var token = _cancellationTokenSource.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await signal.WaitAsync(TimeSpan.FromMilliseconds(CurrentAsyncOptions.FlushIntervalMs)).ConfigureAwait(false);
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
                try
                {
                    LogText.Write(in item);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
                processed++;
            }
        }

        // ================= v3.3.0 Flush / Shutdown =================

        /// <summary>
        /// 同步等待佇列排空並把檔案緩衝寫到磁碟。
        /// 呼叫端執行緒會一起幫忙排空（不是乾等 dispatcher 的 FlushIntervalMs 週期）。
        /// </summary>
        /// <param name="timeoutMs">等待上限（毫秒）</param>
        /// <returns>true 表示已全部落地；false 表示逾時（仍會盡力 flush 已寫入的部分）</returns>
        internal static bool Flush(int timeoutMs)
        {
            if (Interlocked.CompareExchange(ref _initialized, 0, 0) == 0)
            {
                // 管線沒啟動過（或已收尾）：沒有佇列要等，但仍把已開啟的檔案落盤
                FileStreamPool.FlushAll(flushToDisk: true);
                return true;
            }

            var sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref _pending) > 0)
            {
                DrainBatch();
                if (Interlocked.Read(ref _pending) <= 0) break;
                if (sw.ElapsedMilliseconds >= timeoutMs)
                {
                    FileStreamPool.FlushAll(flushToDisk: true);
                    return false;
                }
                Thread.Sleep(1);
            }

            FileStreamPool.FlushAll(flushToDisk: true);
            return true;
        }

        /// <summary>
        /// <see cref="Flush(int)"/> 的非同步版本。取消時回傳 false，不擲 <see cref="OperationCanceledException"/>
        /// （背景服務型套件的收尾不該把宿主弄掛）。
        /// </summary>
        internal static async Task<bool> FlushAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _initialized, 0, 0) == 0)
            {
                FileStreamPool.FlushAll(flushToDisk: true);
                return true;
            }

            var sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref _pending) > 0)
            {
                DrainBatch();
                if (Interlocked.Read(ref _pending) <= 0) break;
                if (cancellationToken.IsCancellationRequested || sw.ElapsedMilliseconds >= timeoutMs)
                {
                    FileStreamPool.FlushAll(flushToDisk: true);
                    return false;
                }
                await Task.Delay(1).ConfigureAwait(false);
            }

            FileStreamPool.FlushAll(flushToDisk: true);
            return true;
        }

        /// <summary>
        /// 收尾：排空佇列 → 停 dispatcher → 落盤 → 關檔 → 停背景計時器。冪等。
        /// </summary>
        internal static void ShutdownCore(int timeoutMs)
        {
            // 先在 _initialized 仍為 1 的狀態下排空，Flush 才走完整路徑
            Flush(timeoutMs);

            if (Interlocked.CompareExchange(ref _initialized, 0, 1) != 1)
            {
                // 沒啟動過或已收尾過：只要確保檔案關掉
                FileStreamPool.Shutdown();
                return;
            }

            try
            {
                _cancellationTokenSource.Cancel();
                try { _signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }

                var task = _processTask;
                // CLAUDE.md：公開路徑禁 Task.Wait()，shutdown flush 是明列的唯一例外
                if (task != null)
                {
                    try { task.Wait(2000); }
                    catch (AggregateException) { /* dispatcher 自己已記錄，收尾不再往外丟 */ }
                }

                DrainBatch();   // dispatcher 收尾後可能還有殘留
                FileStreamPool.FlushAll(flushToDisk: true);
                FileStreamPool.Shutdown();
                LogRetentionCleaner.Shutdown();
                TimestampCache.Shutdown();

                var t = Interlocked.Exchange(ref _diskFlushTimer, null);
                t?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AsyncLogHandler.ShutdownCore 錯誤: {ex.Message}");
            }
        }

        /// <summary>
        /// <see cref="ShutdownCore(int)"/> 的非同步版本（排空階段不佔用呼叫端執行緒等待）。
        /// </summary>
        internal static async Task ShutdownCoreAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            await FlushAsync(timeoutMs, cancellationToken).ConfigureAwait(false);

            if (Interlocked.CompareExchange(ref _initialized, 0, 1) != 1)
            {
                FileStreamPool.Shutdown();
                return;
            }

            try
            {
                _cancellationTokenSource.Cancel();
                try { _signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }

                var task = _processTask;
                if (task != null)
                    await Task.WhenAny(task, Task.Delay(2000)).ConfigureAwait(false);

                DrainBatch();
                FileStreamPool.FlushAll(flushToDisk: true);
                FileStreamPool.Shutdown();
                LogRetentionCleaner.Shutdown();
                TimestampCache.Shutdown();

                var t = Interlocked.Exchange(ref _diskFlushTimer, null);
                t?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AsyncLogHandler.ShutdownCoreAsync 錯誤: {ex.Message}");
            }
        }

        /// <summary>
        /// ProcessExit / UnhandledException 的輕量收尾：只排空與 flush，不關檔也不停計時器。
        /// 與 <see cref="ShutdownCore(int)"/> 重複呼叫是安全的（任一順序皆可）。
        /// </summary>
        internal static void ShutdownGracefully()
        {
            try
            {
                DrainBatch();
                FileStreamPool.FlushAll(flushToDisk: true);
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
