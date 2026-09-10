using System;
using System.Threading;

namespace OzaLog.Core
{
    /// <summary>
    /// 日誌寫入入口 - v3.0 改為 FileStreamPool 的薄包裝，
    /// 不再持有單一全域 lock，不再每筆 open/close 檔案。
    /// Log file writer - thin wrapper over FileStreamPool in v3.0.
    /// </summary>
    internal static class LogText
    {
        /// <summary>
        /// 同步寫入單筆 log。v3.0 由 dispatcher 執行緒呼叫，或在 EnableAsyncLogging=false 時由呼叫端直接呼叫。
        /// v3.1+：依 <c>LogConfiguration.Current.OutputFormat</c> 路由到對應 formatter。
        /// </summary>
        internal static void Write(in LogItem item)
        {
            try
            {
                var format = LogConfiguration.Current.OutputFormat;
                var line = format == LogOutputFormat.Json
                    ? JsonLogFormatter.Format(in item)
                    : LogFormatter.Format(in item);

                FileStreamPool.AppendLine(item.Level, item.Name, line);
                if (item.RequireImmediateFlush)
                    FileStreamPool.Flush(item.Level, item.Name);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy/MM/dd HH:mm:ss}>>LogText.Write 錯誤: {ex.Message}");
            }
        }

        /// <summary>
        /// 同步模式（<c>EnableAsyncLogging = false</c>）專用寫入 - 寫入後立即 flush，內容當下就讀得到。
        /// Synchronous-mode write - appends then flushes immediately so the content is readable right away.
        /// </summary>
        /// <remarks>
        /// v3.1.1 修正：同步模式沒有 dispatcher、沒有 100ms 定期 flush timer、也沒有 ProcessExit 收尾
        /// （這三者都掛在 <c>AsyncLogHandler.Initialize</c>，同步模式從未觸發），
        /// 因此若沿用 <see cref="Write(in LogItem)"/>，內容會一直留在 StreamWriter 緩衝裡，
        /// 最後隨行程結束而全部消失（檔案建得出來但 0 bytes）。
        /// 這裡只做 StreamWriter / FileStream 層級的 flush（不強制 fsync），與非同步模式的定期 flush 行為一致。
        /// </remarks>
        internal static void WriteSync(in LogItem item)
        {
            Write(in item);

            // RequireImmediateFlush 的項目 Write 內已 flush（且已 fsync），不重複做
            if (item.RequireImmediateFlush) return;

            FileStreamPool.Flush(item.Level, item.Name, flushToDisk: false);
        }

        /// <summary>
        /// v2.x 相容入口 - 同步寫入路徑（EnableAsyncLogging=false 時呼叫）。
        /// 內部會建構 LogItem 並呼叫 <see cref="WriteSync(in LogItem)"/>（同步模式需逐筆 flush）。
        /// </summary>
        internal static void Add_LogText(LogLevel level, string name, string message, object[] args)
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
                requireImmediateFlush: false);
            WriteSync(in item);
        }
    }
}
