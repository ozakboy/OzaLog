using System;
using System.Globalization;
using System.Text;

namespace OzaLog.Core
{
    /// <summary>
    /// 日誌格式化處理器 - 將 LogItem 轉為要寫入檔案的單行字串。
    /// v3.0：格式化全部移到 dispatcher 執行緒，呼叫端不再做 string.Format / StringBuilder。
    /// v3.1：時間格式變為使用者可設定的 .NET DateTime 格式;支援 Thread ID/Name 顯示開關。
    /// v3.2：大括號跳脫改為只在真的要走 <c>AppendFormat</c> 時才做,訊息裡的 <c>{}</c> 原樣落檔。
    /// </summary>
    internal static class LogFormatter
    {
        /// <summary>
        /// 預設時間格式(對應 v3.0 行為,避免變更使用者輸出)
        /// </summary>
        private const string DefaultTimeFormat = "HH:mm:ss.fff";

        /// <summary>
        /// 格式化 LogItem 為單行檔案輸出（不含換行符；換行由 StreamWriter.WriteLine 補）
        /// 格式取決於 <see cref="LogConfiguration.LogOptions.TimeFormat"/>、
        /// <see cref="LogConfiguration.LogOptions.ShowThreadId"/>、
        /// <see cref="LogConfiguration.LogOptions.ShowThreadName"/> 設定
        /// </summary>
        public static string Format(in LogItem item)
        {
            var current = LogConfiguration.Current;
            var dt = new DateTime(item.TimestampTicks, DateTimeKind.Local);

            // 容量估計：時間戳 12-25 + thread 包裝 8-30 + message 概略長度
            var sb = new StringBuilder(128);

            AppendTimestamp(sb, dt, current.TimeFormat);
            AppendThreadSegment(sb, item.ThreadId, item.ThreadName, current.ShowThreadId, current.ShowThreadName);

            var msg = item.Message ?? string.Empty;
            var args = item.Args;
            if (args != null && args.Length > 0)
            {
                try
                {
                    // 只有真的要走 AppendFormat 時才跳脫大括號,且只跳脫「不是合法佔位符」的那些
                    sb.AppendFormat(CultureInfo.InvariantCulture, EscapeMessage(msg, args.Length), args);
                }
                catch (FormatException)
                {
                    // 保險退路:跳脫規則萬一漏判,至少原字串要進得了檔案
                    sb.Append(msg);
                }
            }
            else
            {
                // 無格式參數 → 不經過 AppendFormat,訊息原樣輸出(含其中的 {} )
                sb.Append(msg);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 跳脫訊息中「不是合法格式化佔位符」的大括號,供 <c>string.Format</c> / <c>AppendFormat</c> 使用。
        /// Escapes every brace that is not a valid format placeholder, for string.Format / AppendFormat.
        /// </summary>
        /// <param name="message">原始訊息(未跳脫)/ The raw message</param>
        /// <param name="argCount">
        /// 格式化參數個數;索引 &gt;= 此值的 <c>{N}</c> 會被當成字面量跳脫,避免 FormatException。
        /// Number of format arguments; a {N} whose index is out of range is escaped as a literal.
        /// </param>
        /// <returns>可安全交給 AppendFormat 的格式字串 / A format string safe for AppendFormat</returns>
        /// <remarks>
        /// v3.2 修正:舊版在呼叫端無條件把所有 <c>{}</c> 雙倍化,但無參數路徑走的是
        /// <c>StringBuilder.Append</c>(不經 AppendFormat),雙倍化的括號沒人還原,
        /// 導致異常序列化的 JSON 在日誌裡變成 <c>{{ "Type": ... }}</c> 而無法被 parser 讀取。
        /// 現在跳脫只發生在 AppendFormat 路徑上,且逐字元判斷:
        /// <c>{0}</c>、<c>{1,-8}</c>、<c>{2:F4}</c> 這類合法佔位符原樣保留,其餘一律跳脫。
        /// </remarks>
        public static string EscapeMessage(string message, int argCount)
        {
            if (string.IsNullOrEmpty(message)) return message;

            if (message.IndexOf('{') < 0 && message.IndexOf('}') < 0)
                return message;

            var sb = new StringBuilder(message.Length + 8);
            for (var i = 0; i < message.Length; i++)
            {
                var ch = message[i];
                if (ch == '{')
                {
                    if (TryMatchPlaceholder(message, i, argCount, out var end))
                    {
                        // 合法佔位符(含對齊 / 格式區段)→ 原樣保留讓 AppendFormat 去代換
                        sb.Append(message, i, end - i + 1);
                        i = end;
                    }
                    else
                    {
                        sb.Append("{{");
                    }
                }
                else if (ch == '}')
                {
                    sb.Append("}}");
                }
                else
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 判斷 <paramref name="start"/> 位置起是否為合法的格式化佔位符
        /// <c>{index[,alignment][:format]}</c>,是則回傳結尾的 <c>}</c> 位置。
        /// Determines whether a valid format item starts at the given position.
        /// </summary>
        private static bool TryMatchPlaceholder(string s, int start, int argCount, out int end)
        {
            end = -1;
            var i = start + 1;
            if (i >= s.Length || !IsAsciiDigit(s[i])) return false;

            // 索引區段
            var index = 0;
            while (i < s.Length && IsAsciiDigit(s[i]))
            {
                index = (index * 10) + (s[i] - '0');
                if (index > 1000000) return false;   // 明顯不是佔位符,當字面量處理
                i++;
            }
            // 索引超出 args 範圍 → 當字面量跳脫(舊行為是拋 FormatException 後整串原樣附加)
            if (index >= argCount) return false;

            // 對齊區段 ,[-]digits
            if (i < s.Length && s[i] == ',')
            {
                i++;
                if (i < s.Length && s[i] == '-') i++;
                if (i >= s.Length || !IsAsciiDigit(s[i])) return false;
                while (i < s.Length && IsAsciiDigit(s[i])) i++;
            }

            // 格式區段 :xxx(內容不得再含大括號)
            if (i < s.Length && s[i] == ':')
            {
                i++;
                while (i < s.Length && s[i] != '}' && s[i] != '{') i++;
            }

            if (i < s.Length && s[i] == '}')
            {
                end = i;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 只認 ASCII 0-9(不可用 char.IsDigit,會把其他語系數字也算進去)
        /// </summary>
        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        /// <summary>
        /// 將 DateTime 依使用者設定的格式字串輸出。
        /// 預設 <c>HH:mm:ss.fff</c> 走手寫 fast path(零配置);其他格式走 .NET ToString。
        /// </summary>
        private static void AppendTimestamp(StringBuilder sb, DateTime dt, string timeFormat)
        {
            // Fast path:預設格式手寫,避免 ToString 開銷
            if (string.IsNullOrEmpty(timeFormat) || timeFormat == DefaultTimeFormat)
            {
                AppendTwoDigit(sb, dt.Hour); sb.Append(':');
                AppendTwoDigit(sb, dt.Minute); sb.Append(':');
                AppendTwoDigit(sb, dt.Second); sb.Append('.');
                AppendThreeDigit(sb, dt.Millisecond);
                return;
            }

            try
            {
                sb.Append(dt.ToString(timeFormat, CultureInfo.InvariantCulture));
            }
            catch (FormatException)
            {
                // 使用者給了無效的格式字串 → fallback 預設格式
                AppendTwoDigit(sb, dt.Hour); sb.Append(':');
                AppendTwoDigit(sb, dt.Minute); sb.Append(':');
                AppendTwoDigit(sb, dt.Second); sb.Append('.');
                AppendThreeDigit(sb, dt.Millisecond);
            }
        }

        /// <summary>
        /// 附加 thread 區段。規則:
        ///   - 只開 ShowThreadId(或 ShowThreadName 但 Name 為 null) → "[T:12] "
        ///   - ShowThreadId 開,且 ShowThreadName 開且 Name 非 null → "[T:12/Name] "
        ///   - 只開 ShowThreadName 且 Name 非 null → "[N:Name] "
        ///   - 兩者都關 或 對應條件不成立 → 整個區段省略
        /// </summary>
        private static void AppendThreadSegment(StringBuilder sb, int threadId, string threadName,
            bool showId, bool showName)
        {
            var hasName = showName && !string.IsNullOrEmpty(threadName);

            if (showId && hasName)
            {
                sb.Append("[T:").Append(threadId).Append('/').Append(threadName).Append("] ");
            }
            else if (showId)
            {
                sb.Append("[T:").Append(threadId).Append("] ");
            }
            else if (hasName)
            {
                sb.Append("[N:").Append(threadName).Append("] ");
            }
            // 兩者都關 / name 為 null → 不輸出任何 thread 區段
        }

        private static void AppendTwoDigit(StringBuilder sb, int value)
        {
            if (value < 10) sb.Append('0');
            sb.Append(value);
        }

        private static void AppendThreeDigit(StringBuilder sb, int value)
        {
            if (value < 100) sb.Append('0');
            if (value < 10) sb.Append('0');
            sb.Append(value);
        }
    }
}
