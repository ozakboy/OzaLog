using OzaLog.Core;
using System;
using static OzaLog.LogConfiguration;

namespace OzaLog
{
    /// <summary>
    /// LOG配置類別 - 提供日誌系統的全局配置管理
    /// Log Configuration Class - Provides global configuration management for the logging system
    /// </summary>
    public static class LogConfiguration
    {
        /// <summary>
        /// 儲存當前的日誌配置選項
        /// Stores the current logging configuration options
        /// </summary>
        private static readonly LogOptions _currentOptions = new LogOptions();
        /// <summary>
        /// 標記日誌系統是否已經初始化
        /// Flag indicating whether the logging system has been initialized
        /// </summary>
        private static bool _isInitialized = false;
        /// <summary>
        /// 取得日誌系統當前的配置
        /// Gets the current configuration of the logging system
        /// </summary>
        public static ILogOptions Current => new ReadOnlyLogOptions(_currentOptions);
        /// <summary>
        /// 日誌配置選項介面 - 定義可供外部讀取的配置項目
        /// Log Options Interface - Defines configuration items available for external reading
        /// </summary>
        public interface ILogOptions
        {
            /// <summary>
            /// 日誌保留天數 - 定義日誌檔案的保存期限
            /// Log Retention Days - Defines how long log files are kept
            /// </summary>
            int KeepDays { get; }

            /// <summary>
            /// 單一日誌檔的大小上限(bytes),超過就自動切下一個 part 檔,預設 50MB。
            /// 設得過小會讓同一天的日誌碎成大量 part 檔,增加開檔次數並拖慢寫入。
            /// Max size of a single log file in bytes (default 50MB); exceeding it starts a new part file.
            /// Setting it too small fragments a day into many part files and slows writing down.
            /// </summary>
            long MaxFileSize { get; }

            /// <summary>
            /// 日誌根目錄,相對於 <c>AppDomain.CurrentDomain.BaseDirectory</c>(不是行程的目前目錄),
            /// 預設 <c>"logs"</c>。實際路徑為 <c>{BaseDirectory}/{LogPath}/{yyyyMMdd}/{類型目錄}/</c>。
            /// Log root directory, resolved against AppDomain.CurrentDomain.BaseDirectory (not the
            /// current working directory); default "logs".
            /// </summary>
            string LogPath { get; }

            /// <summary>
            /// 各日誌級別的子目錄設定(唯讀視圖)。未個別指定的級別一律落在
            /// <see cref="ILogTypeDirectories.DirectoryPath"/>。
            /// Per-level subdirectory settings (read-only view); levels left unset fall back to DirectoryPath.
            /// </summary>
            ILogTypeDirectories TypeDirectories { get; }

            /// <summary>
            /// 是否啟用非同步寫入(預設 <c>true</c>)。<c>true</c> 時呼叫端只入隊,由 dispatcher 執行緒批次落檔;
            /// <c>false</c> 時由呼叫端直接寫入並逐筆 flush(延遲較低但吞吐大幅下降),
            /// 且同步模式沒有背景清理器,過期目錄需自行清理。
            /// Whether asynchronous logging is enabled (default true). When false, entries are written and
            /// flushed on the calling thread — lower latency, far lower throughput, and no background retention cleanup.
            /// </summary>
            bool EnableAsyncLogging { get; }

            /// <summary>
            /// 是否同時把日誌輸出到主控台(預設 <c>true</c>)。輸出發生在呼叫端執行緒,
            /// 高頻情境下 <c>Console.WriteLine</c> 會成為瓶頸,壓測與正式環境建議關閉。
            /// Whether to also write to the console (default true). The write happens on the calling thread,
            /// so Console.WriteLine becomes the bottleneck under high throughput — turn it off in production.
            /// </summary>
            bool EnableConsoleOutput { get; }

            /// <summary>
            /// 非同步管線的批次 / 佇列 / 定時 flush 設定(唯讀視圖);
            /// <see cref="EnableAsyncLogging"/> 為 <c>false</c> 時整組不生效。
            /// Read-only view of the async pipeline settings; ignored when EnableAsyncLogging is false.
            /// </summary>
            IAsyncLogOptions AsyncOptions { get; }

            /// <summary>
            /// v3.0+ 是否啟用全域意外攔截（預設 false）
            /// AppDomain.UnhandledException + TaskScheduler.UnobservedTaskException 自動 log Fatal
            /// </summary>
            bool EnableGlobalExceptionCapture { get; }

            /// <summary>
            /// v3.0+ FileStreamPool 同時保持開啟的檔案上限（LRU 自動關閉冷檔，預設 100）
            /// </summary>
            int MaxOpenFileStreams { get; }

            /// <summary>
            /// v3.0+ 持久化 FileStream 定期 flush 至磁碟的間隔（毫秒，預設 100ms）
            /// </summary>
            int DiskFlushIntervalMs { get; }

            /// <summary>
            /// v3.0+ queue 滿時 drop oldest 的通知 callback（純通知，使用者通常只計數）
            /// </summary>
            Action OnDropped { get; }

            /// <summary>
            /// v3.1+ 主 logger 的輸出格式（Txt / Log / Json，預設 Txt）
            /// </summary>
            LogOutputFormat OutputFormat { get; }

            /// <summary>
            /// v3.1+ 訊息前綴的時間字串格式(自由格式,預設 <c>"HH:mm:ss.fff"</c>)
            /// JSON 模式下此設定不適用(JSON 走 epoch_ms)
            /// </summary>
            string TimeFormat { get; }

            /// <summary>
            /// v3.1+ 是否在訊息前綴顯示 Thread ID(預設 true)
            /// </summary>
            bool ShowThreadId { get; }

            /// <summary>
            /// v3.1+ 是否在訊息前綴顯示 Thread Name(預設 false)
            /// 若該執行緒無名稱(Thread.Name == null),thread 區塊整個省略
            /// </summary>
            bool ShowThreadName { get; }

            /// <summary>
            /// v3.1+ 是否啟用高精度時間戳(Stopwatch hybrid,提供 µs 級精度)
            /// 預設 false;啟用後呼叫端讀 ticks 從 ~5ns 增加到 ~30ns,但能拿到真實 µs 精度
            /// </summary>
            bool HighPrecisionTimestamp { get; }

            /// <summary>
            /// v3.1+ 報價 pipeline 的唯讀設定視圖
            /// </summary>
            IQuoteOptions QuoteOptions { get; }
        }

        private class ReadOnlyLogOptions : ILogOptions
        {
            private readonly LogOptions _options;

            public ReadOnlyLogOptions(LogOptions options)
            {
                _options = options;
            }

            public int KeepDays => _options.KeepDays;
            public long MaxFileSize => _options.MaxFileSize;
            public string LogPath => _options.LogPath;
            public bool EnableAsyncLogging => _options.EnableAsyncLogging;
            public bool EnableConsoleOutput => _options.EnableConsoleOutput;
            public ILogTypeDirectories TypeDirectories => new ReadOnlyLogTypeDirectories(_options.TypeDirectories);
            public IAsyncLogOptions AsyncOptions => new ReadOnlyAsyncLogOptions(_options.AsyncOptions);
            public bool EnableGlobalExceptionCapture => _options.EnableGlobalExceptionCapture;
            public int MaxOpenFileStreams => _options.MaxOpenFileStreams;
            public int DiskFlushIntervalMs => _options.DiskFlushIntervalMs;
            public Action OnDropped => _options.OnDropped;
            public LogOutputFormat OutputFormat => _options.OutputFormat;
            public string TimeFormat => _options.TimeFormat;
            public bool ShowThreadId => _options.ShowThreadId;
            public bool ShowThreadName => _options.ShowThreadName;
            public bool HighPrecisionTimestamp => _options.HighPrecisionTimestamp;
            public IQuoteOptions QuoteOptions => new ReadOnlyQuoteOptions(_options.QuoteOptions);
        }

        /// <summary>
        /// 日誌類型目錄的唯讀視圖 - 對外揭露各級別實際使用的子目錄名稱。
        /// Read-only view of the per-level log directories.
        /// </summary>
        /// <remarks>
        /// 這些值都是「日期目錄底下的子目錄名稱」,不是完整路徑;
        /// 完整路徑為 <c>{BaseDirectory}/{LogPath}/{yyyyMMdd}/{此處的目錄名}/</c>。
        /// </remarks>
        public interface ILogTypeDirectories
        {
            /// <summary>
            /// 所有級別的預設子目錄名稱(預設 <c>"LogFiles"</c>)。
            /// 各級別未個別指定時一律落在這裡,亦即全部級別混在同一個目錄。
            /// Default subdirectory for every level (default "LogFiles"); levels without an explicit
            /// directory all land here.
            /// </summary>
            string DirectoryPath { get; }

            /// <summary>
            /// Trace 級別的子目錄名稱;為 <c>null</c> 或未設定時退回 <see cref="DirectoryPath"/>(預設未設定)。
            /// Subdirectory for Trace; falls back to DirectoryPath when null (default: not set).
            /// </summary>
            string TracePath { get; }

            /// <summary>
            /// Debug 級別的子目錄名稱;為 <c>null</c> 或未設定時退回 <see cref="DirectoryPath"/>(預設未設定)。
            /// Subdirectory for Debug; falls back to DirectoryPath when null (default: not set).
            /// </summary>
            string DebugPath { get; }

            /// <summary>
            /// Info 級別的子目錄名稱;為 <c>null</c> 或未設定時退回 <see cref="DirectoryPath"/>(預設未設定)。
            /// Subdirectory for Info; falls back to DirectoryPath when null (default: not set).
            /// </summary>
            string InfoPath { get; }

            /// <summary>
            /// Warn 級別的子目錄名稱;為 <c>null</c> 或未設定時退回 <see cref="DirectoryPath"/>(預設未設定)。
            /// Subdirectory for Warn; falls back to DirectoryPath when null (default: not set).
            /// </summary>
            string WarnPath { get; }

            /// <summary>
            /// Error 級別的子目錄名稱;為 <c>null</c> 或未設定時退回 <see cref="DirectoryPath"/>(預設未設定)。
            /// 想把錯誤獨立出來給監控系統掃描時,設定這一項最直接。
            /// Subdirectory for Error; falls back to DirectoryPath when null (default: not set).
            /// </summary>
            string ErrorPath { get; }

            /// <summary>
            /// Fatal 級別的子目錄名稱;為 <c>null</c> 或未設定時退回 <see cref="DirectoryPath"/>(預設未設定)。
            /// Subdirectory for Fatal; falls back to DirectoryPath when null (default: not set).
            /// </summary>
            string FatalPath { get; }

            /// <summary>
            /// CustomName 級別的子目錄名稱;為 <c>null</c> 或未設定時退回 <see cref="DirectoryPath"/>(預設未設定)。
            /// 每個 CustomName 各自一個檔案,全部放在這個目錄下。
            /// Subdirectory for CustomName entries; falls back to DirectoryPath when null (default: not set).
            /// </summary>
            string CustomPath { get; }

            /// <summary>
            /// 取得指定級別實際要用的子目錄名稱(已套用退回 <see cref="DirectoryPath"/> 的規則)。
            /// Resolves the subdirectory actually used by a level, with the DirectoryPath fallback applied.
            /// </summary>
            /// <param name="logLevel">日誌級別 / The log level</param>
            /// <returns>
            /// 該級別的子目錄名稱;未列舉到的級別一律比照 CustomName 處理。
            /// The subdirectory for that level; unlisted levels are treated as CustomName.
            /// </returns>
            string GetPathForType(LogLevel logLevel);
        }

        private class ReadOnlyLogTypeDirectories : ILogTypeDirectories
        {
            private readonly LogTypeDirectories _TypeDirectories;
            public ReadOnlyLogTypeDirectories(LogTypeDirectories TypeDirectories)
            {
                _TypeDirectories = TypeDirectories;
            }
            public string DirectoryPath => _TypeDirectories.DirectoryPath;
            public string TracePath =>  _TypeDirectories.TracePath;
            public string DebugPath =>  _TypeDirectories.DebugPath;
            public string InfoPath =>  _TypeDirectories.InfoPath;
            public string WarnPath =>  _TypeDirectories.WarnPath;
            public string ErrorPath =>  _TypeDirectories.ErrorPath;
            public string FatalPath =>  _TypeDirectories.FatalPath;
            public string CustomPath =>  _TypeDirectories.CustomPath;

            /// <summary>
            /// 取得指定日誌類型的目錄路徑
            /// </summary>
            /// <param name="logLevel">日誌類型</param>
            /// <returns>目錄路徑</returns>
            public string GetPathForType(LogLevel logLevel)
            {

                switch (logLevel)
                {
                    case LogLevel.Trace:
                        return TracePath ?? DirectoryPath;
                    case LogLevel.Debug:
                        return DebugPath ?? DirectoryPath;
                    case LogLevel.Info:
                        return InfoPath ?? DirectoryPath;
                    case LogLevel.Warn:
                        return WarnPath ?? DirectoryPath;
                    case LogLevel.Error:
                        return ErrorPath ?? DirectoryPath;
                    case LogLevel.Fatal:
                        return FatalPath ?? DirectoryPath;
                    case LogLevel.CustomName:
                        return CustomPath ?? DirectoryPath;
                    default:
                        return CustomPath ?? DirectoryPath;
                }
            }
        }

        /// <summary>
        /// 異步日誌配置接口
        /// </summary>
        public interface IAsyncLogOptions
        {
            /// <summary>
            /// 批次處理的最大日誌數量
            /// </summary>
            int MaxBatchSize { get; }

            /// <summary>
            /// 隊列的最大容量
            /// </summary>
            int MaxQueueSize { get; }

            /// <summary>
            /// 定期寫入的時間間隔（毫秒）
            /// </summary>
            int FlushIntervalMs { get; }
        }

        private class ReadOnlyAsyncLogOptions : IAsyncLogOptions
        {
            private readonly AsyncLogOptions _options;

            public ReadOnlyAsyncLogOptions(AsyncLogOptions options)
            {
                _options = options;
            }

            public int MaxBatchSize => _options.MaxBatchSize;
            public int MaxQueueSize => _options.MaxQueueSize;
            public int FlushIntervalMs => _options.FlushIntervalMs;
        }

        /// <summary>
        /// 異步日誌配置選項
        /// </summary>
        public class AsyncLogOptions
        {
            private int _maxBatchSize = 100;
            private int _maxQueueSize = 10000;
            private int _flushIntervalMs = 1000;

            /// <summary>
            /// 批次處理的最大日誌數量
            /// 默認值：100
            /// 最小值：1，最大值：1000
            /// </summary>
            public int MaxBatchSize
            {
                get => _maxBatchSize;
                set => _maxBatchSize = Math.Max(1, Math.Min(1000, value));
            }

            /// <summary>
            /// 隊列的最大容量
            /// 默認值：10000
            /// 最小值：1000，最大值：100000
            /// </summary>
            public int MaxQueueSize
            {
                get => _maxQueueSize;
                set => _maxQueueSize = Math.Max(1000, Math.Min(100000, value));
            }

            /// <summary>
            /// 定期寫入的時間間隔（毫秒）
            /// 默認值：1000ms
            /// 最小值：100ms，最大值：10000ms
            /// </summary>
            public int FlushIntervalMs
            {
                get => _flushIntervalMs;
                set => _flushIntervalMs = Math.Max(10, Math.Min(10000, value));
            }
        }


        /// <summary>
        /// 日誌配置選項
        /// </summary>
        public class LogOptions
        {
            /// <summary>
            /// 日誌檔案保存天數設定，預設為 -3 天（保存最近3天的日誌）
            /// 請設定為負數，例如 -7 表示保存最近 7 天的日誌
            /// （請使用負數）
            /// </summary>
            public int KeepDays { get; set; } = -3;

            /// <summary>
            /// 單個日誌檔案的最大大小限制，超過此大小將自動分割檔案
            /// 預設為 50MB (50 * 1024 * 1024 bytes)
            /// </summary>
            public long MaxFileSize { get; set; } = 50 * 1024 * 1024;

            /// <summary>
            /// 日誌檔案根目錄
            /// 默認為 logs
            /// </summary>
            public string LogPath { get; set; } = "logs";

            /// <summary>
            /// 日誌類型目錄設定
            /// 可為每種日誌類型配置不同的子目錄
            /// </summary>
            public LogTypeDirectories TypeDirectories { get; set; } = new LogTypeDirectories();

            /// <summary>
            /// 是否啟用異步寫入
            /// </summary>
            public bool EnableAsyncLogging { get; set; } = true;

            /// <summary>
            /// 是否在控制台輸出
            /// </summary>
            public bool EnableConsoleOutput { get; set; } = true;

            /// <summary>
            /// v3.0+ 是否啟用全域意外攔截
            /// 訂閱 AppDomain.UnhandledException 與 TaskScheduler.UnobservedTaskException
            /// 任何未攔截的例外都會自動以 Fatal 級別寫入 log
            /// 預設 false（避免函式庫副作用，使用者明確 opt-in）
            /// </summary>
            public bool EnableGlobalExceptionCapture { get; set; } = false;

            private int _maxOpenFileStreams = 100;
            /// <summary>
            /// v3.0+ FileStreamPool 同時保持開啟的檔案上限
            /// 超過時關閉最久未寫入（LRU），預設 100
            /// 範圍 [4, 4096]
            /// </summary>
            public int MaxOpenFileStreams
            {
                get => _maxOpenFileStreams;
                set => _maxOpenFileStreams = Math.Max(4, Math.Min(4096, value));
            }

            private int _diskFlushIntervalMs = 100;
            /// <summary>
            /// v3.0+ 持久化 FileStream 定期 flush 至磁碟的間隔（ms）
            /// 太小會增加 syscall 成本，太大會在 crash 時丟失更多資料
            /// 預設 100ms，範圍 [10, 10000]
            /// </summary>
            public int DiskFlushIntervalMs
            {
                get => _diskFlushIntervalMs;
                set => _diskFlushIntervalMs = Math.Max(10, Math.Min(10000, value));
            }

            /// <summary>
            /// v3.0+ queue 滿時 drop oldest 後觸發的 callback
            /// 通常用於計數丟棄量，請保持 callback body 極輕量（會在 dispatcher 執行緒呼叫）
            /// </summary>
            public Action OnDropped { get; set; }

            /// <summary>
            /// 設定檔案大小的便捷方法（以 MB 為單位）
            /// </summary>
            /// <param name="megabytes">檔案大小（MB）</param>
            public void SetFileSizeInMB(int megabytes)
            {
                MaxFileSize = megabytes * 1024L * 1024L;
            }

            /// <summary>
            /// 異步日誌配置
            /// </summary>
            public AsyncLogOptions AsyncOptions { get; set; } = new AsyncLogOptions();

            /// <summary>
            /// 設定異步日誌配置
            /// </summary>
            /// <param name="configure">配置動作</param>
            public void ConfigureAsync(Action<AsyncLogOptions> configure)
            {
                configure?.Invoke(AsyncOptions);
            }

            /// <summary>
            /// v3.1+ 主 logger 的輸出格式(全域,只能擇一)
            /// Txt 與 Log 內容相同只差副檔名;Json 走 NDJSON 結構化輸出
            /// </summary>
            public LogOutputFormat OutputFormat { get; set; } = LogOutputFormat.Txt;

            private string _timeFormat = "HH:mm:ss.fff";
            /// <summary>
            /// v3.1+ 文字模式下訊息前綴的時間字串格式(自由格式 .NET DateTime format)
            /// 預設 <c>"HH:mm:ss.fff"</c>;若包含 <c>fffff</c> 以上的精度,須搭配
            /// <see cref="HighPrecisionTimestamp"/> = true 才能拿到真實精度,否則最後數位是 0
            /// JSON 模式下此設定不適用(JSON 一律走 epoch_ms 整數時間戳)
            /// </summary>
            public string TimeFormat
            {
                get => _timeFormat;
                set => _timeFormat = string.IsNullOrEmpty(value) ? "HH:mm:ss.fff" : value;
            }

            /// <summary>
            /// v3.1+ 是否在訊息前綴顯示 Thread ID(預設 true)
            /// 文字模式:控制 <c>[T:12]</c> 或 <c>[T:12/Name]</c> 區段是否輸出
            /// JSON 模式:控制 <c>tid</c> 欄位是否輸出
            /// </summary>
            public bool ShowThreadId { get; set; } = true;

            /// <summary>
            /// v3.1+ 是否在訊息前綴顯示 Thread Name(預設 false)
            /// 若該執行緒無名稱(Thread.Name == null),thread 區塊整個省略
            /// JSON 模式:控制 <c>tn</c> 欄位是否輸出
            /// </summary>
            public bool ShowThreadName { get; set; } = false;

            /// <summary>
            /// v3.1+ 是否啟用高精度時間戳(Stopwatch hybrid)
            /// 預設 false;1ms cache 解析度上限
            /// 啟用後:呼叫端讀 ticks 從 ~5ns 增加到 ~30ns,但能拿到真實 µs 精度
            /// 適用情境:需要 latency 分析、tick-level 時序排查
            /// </summary>
            public bool HighPrecisionTimestamp { get; set; } = false;

            /// <summary>
            /// v3.1+ 報價 pipeline 設定
            /// </summary>
            public QuoteOptions QuoteOptions { get; set; } = new QuoteOptions();

            /// <summary>
            /// v3.1+ 設定報價 pipeline。預設 <c>QuoteOptions.Enable</c> 為 false,需在此 callback 內明確開啟
            /// </summary>
            /// <param name="configure">配置動作</param>
            public void ConfigureQuote(Action<QuoteOptions> configure)
            {
                configure?.Invoke(QuoteOptions);
            }
        }

        /// <summary>
        /// 日誌類型目錄配置
        /// 用於配置不同類型日誌的存放目錄
        /// </summary>
        public class LogTypeDirectories
        {
            /// <summary>
            /// 所有日誌類型的預設目錄
            /// 預設為 LogFiles
            /// </summary>
            public string DirectoryPath { get; set; } = "LogFiles";

            /// <summary>
            /// 追蹤日誌目錄，若為空則使用 DirectoryPath
            /// </summary>
            public string TracePath { get; set; }

            /// <summary>
            /// 除錯日誌目錄，若為空則使用 DirectoryPath
            /// </summary>
            public string DebugPath { get; set; }

            /// <summary>
            /// 一般資訊日誌目錄，若為空則使用 DirectoryPath
            /// </summary>
            public string InfoPath { get; set; }

            /// <summary>
            /// 警告日誌目錄，若為空則使用 DirectoryPath
            /// </summary>
            public string WarnPath { get; set; }

            /// <summary>
            /// 錯誤日誌目錄，若為空則使用 DirectoryPath
            /// </summary>
            public string ErrorPath { get; set; }

            /// <summary>
            /// 致命錯誤日誌目錄，若為空則使用 DirectoryPath
            /// </summary>
            public string FatalPath { get; set; }

            /// <summary>
            /// 自定義日誌目錄，若為空則使用 DirectoryPath
            /// </summary>
            public string CustomPath { get; set; }

      
        }

        /// <summary>
        /// 配置日誌系統
        /// </summary>
        /// <param name="configure">配置動作</param>
        public static void Initialize(Action<LogOptions> configure)
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException("OzaLog 已初始化（Configure 不可重入）");
            }
            configure?.Invoke(_currentOptions);
            _isInitialized = true;

            // v3.0：啟用全域意外攔截（如果使用者明確 opt-in）
            if (_currentOptions.EnableGlobalExceptionCapture)
            {
                Core.GlobalExceptionCapture.Enable();
            }
        }

        /// <summary>
        /// 取得當前配置
        /// </summary>
        public static ILogOptions GetCurrentOptions()
        {
            return Current;
        }

        /// <summary>
        /// 確保配置已初始化
        /// </summary>
        internal static void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                Initialize(_ => { }); // 使用預設配置
            }
        }
    }
}