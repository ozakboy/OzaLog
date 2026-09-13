using System;
using System.Threading;
using System.Threading.Tasks;

namespace OzaLog.Core
{
    /// <summary>
    /// 生命週期協調器(v3.3.0)。把主 logger 與報價 pipeline 兩條管線的
    /// 「確定落地」與「停掉背景工作」收斂成宿主端的單一入口。
    /// Lifecycle coordinator (v3.3.0): single host-facing entry point for flushing both
    /// pipelines to disk and stopping their background work.
    /// </summary>
    /// <remarks>
    /// 背景服務型套件的規則是不能弄掛宿主,因此:
    /// • Shutdown 之後的寫入一律靜默丟棄,不擲例外
    /// • Flush / Shutdown 本身也不擲例外,以回傳值表示結果
    /// • Shutdown 冪等(重複呼叫、或與 ProcessExit 的收尾重疊都安全)
    /// </remarks>
    internal static class LogLifecycle
    {
        /// <summary>Flush / Shutdown 的預設等待上限(毫秒)。</summary>
        internal const int DefaultTimeoutMs = 10_000;

        private static int _shutdown;

        /// <summary>是否已收尾(收尾後的寫入會被靜默丟棄)。</summary>
        internal static bool IsShutdown => Volatile.Read(ref _shutdown) != 0;

        /// <summary>
        /// 由 <c>LogConfiguration.Initialize</c> 呼叫:Shutdown 之後再次 Configure 即解除丟棄狀態,
        /// 下一筆日誌會重新啟動管線。
        /// </summary>
        internal static void Resume() => Volatile.Write(ref _shutdown, 0);

        internal static bool Flush(int timeoutMs)
        {
            if (IsShutdown) return false;

            try
            {
                var mainOk = AsyncLogHandler.Flush(timeoutMs);
                var quoteOk = QuoteLogHandler.Flush(timeoutMs);
                return mainOk && quoteOk;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LogLifecycle.Flush 錯誤: {ex.Message}");
                return false;
            }
        }

        internal static async Task<bool> FlushAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            if (IsShutdown) return false;

            try
            {
                var mainOk = await AsyncLogHandler.FlushAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                var quoteOk = await QuoteLogHandler.FlushAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                return mainOk && quoteOk;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LogLifecycle.FlushAsync 錯誤: {ex.Message}");
                return false;
            }
        }

        internal static bool Shutdown(int timeoutMs)
        {
            // 先豎旗再收尾:旗子一豎,新的寫入就不再進佇列,收尾才可能真的結束
            if (Interlocked.Exchange(ref _shutdown, 1) != 0) return false;

            try
            {
                AsyncLogHandler.ShutdownCore(timeoutMs);
                QuoteLogHandler.ShutdownCore(timeoutMs);
                GlobalExceptionCapture.Disable();
                LogConfiguration.ResetForRestart();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LogLifecycle.Shutdown 錯誤: {ex.Message}");
                return false;
            }
        }

        internal static async Task<bool> ShutdownAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _shutdown, 1) != 0) return false;

            try
            {
                await AsyncLogHandler.ShutdownCoreAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                await QuoteLogHandler.ShutdownCoreAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                GlobalExceptionCapture.Disable();
                LogConfiguration.ResetForRestart();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LogLifecycle.ShutdownAsync 錯誤: {ex.Message}");
                return false;
            }
        }
    }
}
