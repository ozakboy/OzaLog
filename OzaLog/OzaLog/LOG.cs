using System;
using System.Threading;
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
            // 只取得 ticks 與 threadId、跳脫 message 中 {} 後就建構 struct LogItem 入隊。
            // 真正的時間戳渲染、string.Format 都延遲到 dispatcher 執行緒。
            var escapedMessage = LogFormatter.EscapeMessage(message);
            var hasArgs = args != null && args.Length > 0;

            var currentThread = Thread.CurrentThread;
            var item = new LogItem(
                level: level,
                name: name ?? string.Empty,
                message: escapedMessage,
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

    }
}
