using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Text;
using OzaLog.Core;
using System.Xml.Linq;

namespace OzaLog
{
    /// <summary>
    /// 記錄檔
    /// </summary>
    public static partial class LOG
    {
        #region 核心日誌方法

        private static void Log(LogLevel level, string name = "", string message = "", bool writeTxt = true, bool immediateFlush = false, string[] args = null)
        {
            // v3.0：呼叫端零格式化路徑。
            // 只取得 ticks 與 threadId 就建構 struct LogItem 入隊，
            // 真正的時間戳渲染、string.Format 都延遲到 dispatcher 執行緒。
            // v3.2：大括號跳脫也移到 dispatcher（且只在有 args、真的要走 AppendFormat 時才做）。
            // 舊版在這裡無條件把 {} 雙倍化，無 args 的路徑不經 AppendFormat 還原，
            // 異常序列化的 JSON 因此以 {{ }} 落檔、下游 parser 讀不了。
            // v3.3：LOG.Shutdown() 之後一律靜默丟棄（連 Console 輸出也不做）。
            // 背景服務型套件不能因為自己已經收尾就把宿主弄掛，所以這裡是 return 不是 throw。
            if (LogLifecycle.IsShutdown) return;

            var hasArgs = args != null && args.Length > 0;

            var currentThread = Thread.CurrentThread;
            var item = new LogItem(
                level: level,
                name: name ?? string.Empty,
                message: message ?? string.Empty,
                args: hasArgs ? args : null,
                timestampTicks: TimestampCache.GetCurrentTicks(),
                threadId: currentThread.ManagedThreadId,
                threadName: currentThread.Name,
                requireImmediateFlush: immediateFlush);

            if (LogConfiguration.Current.EnableConsoleOutput)
            {
                // Console 輸出在呼叫端走（保持 v2.x 行為），避免 Console 與檔案輸出順序錯亂
                Console.WriteLine(LogFormatter.Format(in item));
            }

            if (!writeTxt) return;

            if (LogConfiguration.Current.EnableAsyncLogging)
                AsyncLogHandler.Enqueue(in item);
            else
                // 同步模式沒有 dispatcher 與定期 flush timer，必須逐筆 flush，否則內容會留在緩衝裡消失
                LogText.WriteSync(in item);
        }

        private static void LogObject<T>(LogLevel level, T obj, string name = "", string message = "", bool writeTxt = true, bool _immediateFlush = false, string[] args = null) where T : class
        {
            if (obj == null) return;

            try
            {
                string jsonString = message + "\n";
                if (level >= LogLevel.Warn && obj is Exception ex)
                {
                    jsonString += LogSerializer.SerializeException(ex);
                }
                else
                {
                    jsonString += LogSerializer.SerializeObject(obj);
                }
                Log(level, name, jsonString, writeTxt , _immediateFlush);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, name, ExceptionHandler.HandleSerializationException(ex));
            }
        }
        #endregion

        #region 公開方法 - 各種日誌級別

        #region Trace

        /// <summary>
        /// 記錄追蹤日誌
        /// </summary>
        public static void Trace_Log(string message) => Log(LogLevel.Trace, string.Empty, message);

        /// <summary>
        /// 記錄追蹤日誌，可控制是否寫入檔案
        /// </summary>
        public static void Trace_Log(string message, bool writeTxt) => Log(LogLevel.Trace, string.Empty, message, writeTxt);

        /// <summary>
        /// 記錄格式化的追蹤日誌，可控制寫入選項
        /// </summary>
        public static void Trace_Log(string message, string[] args, bool writeTxt = true, bool immediateFlush = false)
            => Log(LogLevel.Trace, string.Empty, message, writeTxt, immediateFlush, args);

        /// <summary>
        /// 記錄物件形式的追蹤日誌
        /// </summary>
        public static void Trace_Log<T>(T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Trace, obj, string.Empty, string.Empty, writeTxt, immediateFlush);

        /// <summary>
        /// 記錄帶有訊息的物件形式追蹤日誌
        /// </summary>
        public static void Trace_Log<T>(string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Trace, obj, string.Empty, message, writeTxt, immediateFlush);

        #endregion

        #region Debug
        /// <summary>
        /// 記錄調試日誌
        /// </summary>
        public static void Debug_Log(string message) => Log(LogLevel.Debug, string.Empty, message);

        /// <summary>
        /// 記錄調試日誌，可控制是否寫入檔案
        /// </summary>
        public static void Debug_Log(string message, bool writeTxt) => Log(LogLevel.Debug, string.Empty, message, writeTxt);

        /// <summary>
        /// 記錄格式化的調試日誌，可控制寫入選項
        /// </summary>
        public static void Debug_Log(string message, string[] args, bool writeTxt = true, bool immediateFlush = false)
            => Log(LogLevel.Debug, string.Empty, message, writeTxt, immediateFlush, args);

        /// <summary>
        /// 記錄物件形式的調試日誌
        /// </summary>
        public static void Debug_Log<T>(T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Debug, obj, string.Empty, string.Empty, writeTxt, immediateFlush);

        /// <summary>
        /// 記錄帶有訊息的物件形式調試日誌
        /// </summary>
        public static void Debug_Log<T>(string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Debug, obj, string.Empty, message, writeTxt, immediateFlush);

        #endregion

        #region Info
        /// <summary>
        /// 記錄資訊日誌
        /// </summary>
        public static void Info_Log(string message) => Log(LogLevel.Info, string.Empty, message);

        /// <summary>
        /// 記錄資訊日誌，可控制是否寫入檔案
        /// </summary>
        public static void Info_Log(string message, bool writeTxt) => Log(LogLevel.Info, string.Empty, message, writeTxt);

        /// <summary>
        /// 記錄格式化的資訊日誌，可控制寫入選項
        /// </summary>
        public static void Info_Log(string message, string[] args, bool writeTxt = true, bool immediateFlush = false)
            => Log(LogLevel.Info, string.Empty, message, writeTxt, immediateFlush, args);

        /// <summary>
        /// 記錄物件形式的資訊日誌
        /// </summary>
        public static void Info_Log<T>(T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Info, obj, string.Empty, string.Empty, writeTxt, immediateFlush);

        /// <summary>
        /// 記錄帶有訊息的物件形式資訊日誌
        /// </summary>
        public static void Info_Log<T>(string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Info, obj, string.Empty, message, writeTxt, immediateFlush);

        #endregion

        #region Warn
        /// <summary>
        /// 記錄警告日誌
        /// </summary>
        public static void Warn_Log(string message) => Log(LogLevel.Warn, string.Empty, message);

        /// <summary>
        /// 記錄警告日誌，可控制是否寫入檔案
        /// </summary>
        public static void Warn_Log(string message, bool writeTxt) => Log(LogLevel.Warn, string.Empty, message, writeTxt);

        /// <summary>
        /// 記錄格式化的警告日誌，可控制寫入選項
        /// </summary>
        public static void Warn_Log(string message, string[] args, bool writeTxt = true, bool immediateFlush = false)
            => Log(LogLevel.Warn, string.Empty, message, writeTxt, immediateFlush, args);

        /// <summary>
        /// 記錄物件形式的警告日誌
        /// </summary>
        public static void Warn_Log<T>(T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Warn, obj, string.Empty, string.Empty, writeTxt, immediateFlush);

        /// <summary>
        /// 記錄帶有訊息的物件形式警告日誌
        /// </summary>
        public static void Warn_Log<T>(string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Warn, obj, string.Empty, message, writeTxt, immediateFlush);

        #endregion

        #region Error
        /// <summary>
        /// 記錄錯誤日誌
        /// </summary>
        public static void Error_Log(string message) => Log(LogLevel.Error, string.Empty, message);

        /// <summary>
        /// 記錄錯誤日誌，可控制是否寫入檔案
        /// </summary>
        public static void Error_Log(string message, bool writeTxt) => Log(LogLevel.Error, string.Empty, message, writeTxt);

        /// <summary>
        /// 記錄格式化的錯誤日誌，可控制寫入選項
        /// </summary>
        public static void Error_Log(string message, string[] args, bool writeTxt = true, bool immediateFlush = false)
            => Log(LogLevel.Error, string.Empty, message, writeTxt, immediateFlush, args);

        /// <summary>
        /// 記錄物件形式的錯誤日誌
        /// </summary>
        public static void Error_Log<T>(T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Error, obj, string.Empty, string.Empty, writeTxt, immediateFlush);

        /// <summary>
        /// 記錄帶有訊息的物件形式錯誤日誌
        /// </summary>
        public static void Error_Log<T>(string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Error, obj, string.Empty, message, writeTxt, immediateFlush);

        #endregion

        #region Fatal
        /// <summary>
        /// 記錄致命錯誤日誌
        /// </summary>
        public static void Fatal_Log(string message) => Log(LogLevel.Fatal, string.Empty, message);

        /// <summary>
        /// 記錄致命錯誤日誌，可控制是否寫入檔案
        /// </summary>
        public static void Fatal_Log(string message, bool writeTxt) => Log(LogLevel.Fatal, string.Empty, message, writeTxt);

        /// <summary>
        /// 記錄格式化的致命錯誤日誌，可控制寫入選項
        /// </summary>
        public static void Fatal_Log(string message, string[] args, bool writeTxt = true, bool immediateFlush = false)
            => Log(LogLevel.Fatal, string.Empty, message, writeTxt, immediateFlush, args);

        /// <summary>
        /// 記錄物件形式的致命錯誤日誌
        /// </summary>
        public static void Fatal_Log<T>(T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Fatal, obj, string.Empty, string.Empty, writeTxt, immediateFlush);

        /// <summary>
        /// 記錄帶有訊息的物件形式致命錯誤日誌
        /// </summary>
        public static void Fatal_Log<T>(string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.Fatal, obj, string.Empty, message, writeTxt, immediateFlush);

        #endregion

        #region CustomName
        /// <summary>
        /// 記錄自定義類型日誌
        /// </summary>
        public static void CustomName_Log(string name, string message) => Log(LogLevel.CustomName, name, message);

        /// <summary>
        /// 記錄自定義類型日誌，可控制是否寫入檔案
        /// </summary>
        public static void CustomName_Log(string name, string message, bool writeTxt) => Log(LogLevel.CustomName, name, message, writeTxt);

        /// <summary>
        /// 記錄格式化的自定義類型日誌，可控制寫入選項
        /// </summary>
        public static void CustomName_Log(string name, string message, string[] args, bool writeTxt = true, bool immediateFlush = false)
            => Log(LogLevel.CustomName, name, message, writeTxt, immediateFlush, args);

        /// <summary>
        /// 記錄物件形式的自定義類型日誌
        /// </summary>
        public static void CustomName_Log<T>(string name, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.CustomName, obj, name, string.Empty, writeTxt, immediateFlush);

        /// <summary>
        /// 記錄帶有訊息的物件形式自定義類型日誌
        /// </summary>
        public static void CustomName_Log<T>(string name, string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class
            => LogObject(LogLevel.CustomName, obj, name, message, writeTxt, immediateFlush);

        #endregion

        #endregion


        #region  LOG預設檔案配置

        /// <summary>
        /// 配置日誌系統
        /// </summary>
        /// <param name="configure">配置動作</param>
        public static void Configure(Action<LogConfiguration.LogOptions> configure)
        {
            LogConfiguration.Initialize(configure);
        }

        /// <summary>
        /// 取得當前日誌配置
        /// </summary>
        public static LogConfiguration.ILogOptions GetCurrentOptions()
        {
            return LogConfiguration.GetCurrentOptions();
        }

        #endregion

        #region 生命週期 - Flush / Shutdown（v3.3.0）

        /// <summary>
        /// 是否已呼叫過 <see cref="Shutdown()"/>。為 <c>true</c> 時所有寫入方法一律靜默丟棄（不擲例外）；
        /// 再次呼叫 <see cref="Configure(Action{LogConfiguration.LogOptions})"/> 會解除此狀態並重新啟動管線。
        /// Whether <see cref="Shutdown()"/> has been called. While true every logging call is silently
        /// discarded (never throws); calling Configure again clears it and restarts the pipeline.
        /// </summary>
        public static bool IsShutdown => LogLifecycle.IsShutdown;

        /// <summary>
        /// 同步等待「已寫出的每一筆日誌都落到磁碟」，逾時上限 10 秒。
        /// 適用於宿主收尾、快照前、或任何「接下來可能被強制終止」的時點。
        /// Synchronously waits until every entry written so far has reached the disk (10 s timeout).
        /// </summary>
        /// <returns>
        /// <c>true</c> 表示佇列已排空且檔案已 flush 到磁碟；
        /// <c>false</c> 表示逾時、或已經 <see cref="Shutdown()"/>（此時仍會盡力 flush 已寫入的部分）。
        /// True when the queue drained and files were flushed to disk; false on timeout or after Shutdown.
        /// </returns>
        /// <remarks>呼叫端執行緒會一起幫忙排空佇列，不是乾等 dispatcher 的週期；本方法不擲例外。</remarks>
        public static bool Flush() => LogLifecycle.Flush(LogLifecycle.DefaultTimeoutMs);

        /// <summary>
        /// 同步等待日誌落到磁碟，自訂逾時上限。
        /// Synchronously flushes to disk with a custom timeout.
        /// </summary>
        /// <param name="timeoutMs">等待上限（毫秒）/ Timeout in milliseconds</param>
        /// <returns>同 <see cref="Flush()"/> / Same as <see cref="Flush()"/></returns>
        public static bool Flush(int timeoutMs) => LogLifecycle.Flush(timeoutMs);

        /// <summary>
        /// <see cref="Flush()"/> 的非同步版本，逾時上限 10 秒。
        /// Asynchronous counterpart of <see cref="Flush()"/> with a 10 s timeout.
        /// </summary>
        /// <param name="cancellationToken">取消權杖；取消時回傳 <c>false</c>，不擲 <see cref="OperationCanceledException"/></param>
        /// <returns>同 <see cref="Flush()"/> / Same as <see cref="Flush()"/></returns>
        public static Task<bool> FlushAsync(CancellationToken cancellationToken = default)
            => LogLifecycle.FlushAsync(LogLifecycle.DefaultTimeoutMs, cancellationToken);

        /// <summary>
        /// <see cref="Flush(int)"/> 的非同步版本。
        /// Asynchronous counterpart of <see cref="Flush(int)"/>.
        /// </summary>
        /// <param name="timeoutMs">等待上限（毫秒）/ Timeout in milliseconds</param>
        /// <param name="cancellationToken">取消權杖；取消時回傳 <c>false</c>，不擲例外</param>
        /// <returns>同 <see cref="Flush()"/> / Same as <see cref="Flush()"/></returns>
        public static Task<bool> FlushAsync(int timeoutMs, CancellationToken cancellationToken = default)
            => LogLifecycle.FlushAsync(timeoutMs, cancellationToken);

        /// <summary>
        /// 收尾：先把佇列排空並落盤，再停掉 dispatcher、定期 flush 計時器與過期清理計時器，最後關閉所有日誌檔。
        /// 之後的寫入一律靜默丟棄（不擲例外）；要恢復請再次呼叫
        /// <see cref="Configure(Action{LogConfiguration.LogOptions})"/>（此時配置回到預設值）。
        /// Shuts the logger down: drains and flushes, stops all background work, closes every file.
        /// Subsequent logging calls are silently discarded; call Configure again to restart.
        /// </summary>
        /// <returns>
        /// <c>true</c> 表示本次呼叫真的執行了收尾；<c>false</c> 表示先前已收尾過（冪等，不是錯誤）。
        /// True when this call performed the shutdown; false when it had already been shut down (idempotent).
        /// </returns>
        public static bool Shutdown() => LogLifecycle.Shutdown(LogLifecycle.DefaultTimeoutMs);

        /// <summary>
        /// 收尾，自訂排空階段的逾時上限。
        /// Shuts the logger down with a custom drain timeout.
        /// </summary>
        /// <param name="timeoutMs">排空階段的等待上限（毫秒）/ Drain timeout in milliseconds</param>
        /// <returns>同 <see cref="Shutdown()"/> / Same as <see cref="Shutdown()"/></returns>
        public static bool Shutdown(int timeoutMs) => LogLifecycle.Shutdown(timeoutMs);

        /// <summary>
        /// <see cref="Shutdown()"/> 的非同步版本，逾時上限 10 秒。
        /// Asynchronous counterpart of <see cref="Shutdown()"/> with a 10 s timeout.
        /// </summary>
        /// <param name="cancellationToken">取消權杖；取消只會縮短排空等待，收尾仍會完成，且不擲例外</param>
        /// <returns>同 <see cref="Shutdown()"/> / Same as <see cref="Shutdown()"/></returns>
        public static Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
            => LogLifecycle.ShutdownAsync(LogLifecycle.DefaultTimeoutMs, cancellationToken);

        /// <summary>
        /// <see cref="Shutdown(int)"/> 的非同步版本。
        /// Asynchronous counterpart of <see cref="Shutdown(int)"/>.
        /// </summary>
        /// <param name="timeoutMs">排空階段的等待上限（毫秒）/ Drain timeout in milliseconds</param>
        /// <param name="cancellationToken">取消權杖；取消只會縮短排空等待，收尾仍會完成，且不擲例外</param>
        /// <returns>同 <see cref="Shutdown()"/> / Same as <see cref="Shutdown()"/></returns>
        public static Task<bool> ShutdownAsync(int timeoutMs, CancellationToken cancellationToken = default)
            => LogLifecycle.ShutdownAsync(timeoutMs, cancellationToken);

        #endregion

    }
}
