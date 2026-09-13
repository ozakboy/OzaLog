using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OzaLog.Core
{
    /// <summary>
    /// 報價 pipeline 的非同步處理器。獨立於主 logger 的 AsyncLogHandler。
    /// </summary>
    /// <remarks>
    /// v3.1+ 引入。
    /// • 預設關閉(QuoteOptions.Enable=false),使用者需在 Configure 內明確 opt-in
    /// • 啟動後建立獨立的 dispatcher Task + 獨立 disk flush timer
    /// • Backpressure:drop oldest,觸發 OnDropped callback(若有設定)
    /// • 收尾:ProcessExit 時並行於主 logger 的 flush(兩條 pipeline 互不阻塞)
    /// • v3.3.0:併入 LOG.Flush() / LOG.Shutdown() 的生命週期,並可在 Shutdown 後重啟
    /// </remarks>
    internal static class QuoteLogHandler
    {
        private static readonly ConcurrentQueue<QuoteRecord> _queue = new ConcurrentQueue<QuoteRecord>();

        // v3.3.0:Shutdown 後允許重新啟動,因此 signal / CTS 每次 Initialize 重建
        private static SemaphoreSlim _signal = new SemaphoreSlim(0);
        private static CancellationTokenSource _cts = new CancellationTokenSource();

        private static Task _processTask;
        private static int _initialized;
        private static int _exitHooksRegistered;
        private static Timer _diskFlushTimer;

        private static long _droppedCount;
        private static long _droppedCountAtLastCallback;

        // v3.3.0:已入隊但尚未寫完的筆數(理由同 AsyncLogHandler._pending)
        private static long _pending;

        public static long DroppedCount => Interlocked.Read(ref _droppedCount);

        /// <summary>
        /// 入隊。第一次呼叫時自動啟動 dispatcher(若 QuoteOptions.Enable=true)。
        /// 佇列滿時 drop oldest 並觸發 callback(若有設定)。
        /// </summary>
        public static void Enqueue(in QuoteRecord rec)
        {
            // v3.3.0:已 Shutdown 時靜默丟棄(不擲例外,不啟動背景執行緒)
            if (LogLifecycle.IsShutdown) return;

            // 若使用者未 opt-in,不啟動 dispatcher,呼叫端視為 no-op(避免意外建立背景執行緒)
            if (!LogConfiguration.Current.QuoteOptions.Enable) return;

            if (Interlocked.CompareExchange(ref _initialized, 0, 0) == 0)
                Initialize();

            var quoteOpts = LogConfiguration.Current.QuoteOptions;
            if (_queue.Count >= quoteOpts.MaxQueueSize)
            {
                if (_queue.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _pending);
                    var dropped = Interlocked.Increment(ref _droppedCount);
                    var cb = quoteOpts.OnDropped;
                    if (cb != null)
                    {
                        try
                        {
                            var newlyDropped = dropped - Interlocked.Exchange(ref _droppedCountAtLastCallback, dropped);
                            cb(newlyDropped);
                        }
                        catch
                        {
                            // callback body 出錯不能拖累生產者
                        }
                    }
                }
            }

            // 先加計數再入隊(理由同 AsyncLogHandler.Enqueue)
            Interlocked.Increment(ref _pending);
            _queue.Enqueue(rec);

            try { _signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
        }

        private static void Initialize()
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

            // 觸發其他背景元件
            _ = TimestampCache.GetCurrentTicks();

            _signal = new SemaphoreSlim(0);
            _cts = new CancellationTokenSource();

            _processTask = Task.Run(ProcessQueueAsync);

            var flushMs = LogConfiguration.Current.DiskFlushIntervalMs;
            _diskFlushTimer = new Timer(static _ => QuoteFileStreamPool.FlushAll(), null, flushMs, flushMs);

            // 行程結束的收尾只掛一次(重啟時重複掛會讓收尾跑好幾遍)
            if (Interlocked.CompareExchange(ref _exitHooksRegistered, 1, 0) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += static (s, e) => ShutdownGracefully();
                AppDomain.CurrentDomain.UnhandledException += static (s, e) => ShutdownGracefully();
            }
        }

        private static async Task ProcessQueueAsync()
        {
            // 在啟動當下取用,避免重啟後讀到新一輪的 signal / token
            var signal = _signal;
            var token = _cts.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var intervalMs = LogConfiguration.Current.QuoteOptions.FlushIntervalMs;
                    await signal.WaitAsync(TimeSpan.FromMilliseconds(intervalMs)).ConfigureAwait(false);
                    DrainBatch();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"QuoteLogHandler.ProcessQueueAsync 錯誤: {ex.Message}");
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            DrainBatch();
        }

        private static void DrainBatch()
        {
            var quoteOpts = LogConfiguration.Current.QuoteOptions;
            var batchLimit = quoteOpts.MaxBatchSize;
            var format = quoteOpts.OutputFormat;
            var processed = 0;

            while (processed < batchLimit && _queue.TryDequeue(out var rec))
            {
                try
                {
                    var line = QuoteFormatter.Format(in rec, format);
                    QuoteFileStreamPool.AppendLine(rec.Bucket, rec.Symbol, line);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"QuoteLogHandler.DrainBatch 寫入錯誤: {ex.Message}");
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
        /// 同步等待報價佇列排空並落盤。管線未啟動時直接回 true。
        /// </summary>
        internal static bool Flush(int timeoutMs)
        {
            if (Interlocked.CompareExchange(ref _initialized, 0, 0) == 0)
            {
                QuoteFileStreamPool.FlushAll(flushToDisk: true);
                return true;
            }

            var sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref _pending) > 0)
            {
                DrainBatch();
                if (Interlocked.Read(ref _pending) <= 0) break;
                if (sw.ElapsedMilliseconds >= timeoutMs)
                {
                    QuoteFileStreamPool.FlushAll(flushToDisk: true);
                    return false;
                }
                Thread.Sleep(1);
            }

            QuoteFileStreamPool.FlushAll(flushToDisk: true);
            return true;
        }

        /// <summary>
        /// <see cref="Flush(int)"/> 的非同步版本。取消時回傳 false,不擲例外。
        /// </summary>
        internal static async Task<bool> FlushAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _initialized, 0, 0) == 0)
            {
                QuoteFileStreamPool.FlushAll(flushToDisk: true);
                return true;
            }

            var sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref _pending) > 0)
            {
                DrainBatch();
                if (Interlocked.Read(ref _pending) <= 0) break;
                if (cancellationToken.IsCancellationRequested || sw.ElapsedMilliseconds >= timeoutMs)
                {
                    QuoteFileStreamPool.FlushAll(flushToDisk: true);
                    return false;
                }
                await Task.Delay(1).ConfigureAwait(false);
            }

            QuoteFileStreamPool.FlushAll(flushToDisk: true);
            return true;
        }

        /// <summary>收尾:排空 → 停 dispatcher → 落盤 → 關檔 → 停計時器。冪等。</summary>
        internal static void ShutdownCore(int timeoutMs)
        {
            Flush(timeoutMs);

            if (Interlocked.CompareExchange(ref _initialized, 0, 1) != 1)
            {
                QuoteFileStreamPool.Shutdown();
                return;
            }

            try
            {
                _cts.Cancel();
                try { _signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }

                var task = _processTask;
                if (task != null)
                {
                    try { task.Wait(2000); }
                    catch (AggregateException) { /* dispatcher 自己已記錄,收尾不再往外丟 */ }
                }

                DrainBatch();
                QuoteFileStreamPool.FlushAll(flushToDisk: true);
                QuoteFileStreamPool.Shutdown();

                var t = Interlocked.Exchange(ref _diskFlushTimer, null);
                t?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"QuoteLogHandler.ShutdownCore 錯誤: {ex.Message}");
            }
        }

        /// <summary><see cref="ShutdownCore(int)"/> 的非同步版本。</summary>
        internal static async Task ShutdownCoreAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            await FlushAsync(timeoutMs, cancellationToken).ConfigureAwait(false);

            if (Interlocked.CompareExchange(ref _initialized, 0, 1) != 1)
            {
                QuoteFileStreamPool.Shutdown();
                return;
            }

            try
            {
                _cts.Cancel();
                try { _signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }

                var task = _processTask;
                if (task != null)
                    await Task.WhenAny(task, Task.Delay(2000)).ConfigureAwait(false);

                DrainBatch();
                QuoteFileStreamPool.FlushAll(flushToDisk: true);
                QuoteFileStreamPool.Shutdown();

                var t = Interlocked.Exchange(ref _diskFlushTimer, null);
                t?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"QuoteLogHandler.ShutdownCoreAsync 錯誤: {ex.Message}");
            }
        }

        internal static void ShutdownGracefully()
        {
            try
            {
                DrainBatch();
                QuoteFileStreamPool.FlushAll(flushToDisk: true);
            }
            catch
            {
                // 收尾時的錯誤吞掉
            }
        }
    }
}
