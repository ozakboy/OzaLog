using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OzaLog.Core
{
    /// <summary>
    /// JSON(NDJSON)模式的主 logger 格式化器。
    /// 每筆 log 輸出一行 JSON 物件,schema 固定:
    /// <c>{"ts":epoch_ms,"lv":string,"nm":string,"tid":int?,"tn":string?,"msg":string,"data":object?}</c>
    /// </summary>
    /// <remarks>
    /// v3.1+ 引入。當 <c>LogOptions.OutputFormat == LogOutputFormat.Json</c> 時取代 LogFormatter。
    /// 注意:JSON 模式下 <c>TimeFormat</c> 設定不適用,時間一律走 epoch_ms 整數。
    /// 短欄位名(<c>lv</c>/<c>nm</c>/<c>tid</c>/<c>tn</c>)為使用者於需求 2 拍板。
    /// </remarks>
    internal static class JsonLogFormatter
    {
        // Unix epoch (1970-01-01 UTC) 的 .NET ticks
        private const long UnixEpochTicks = 621355968000000000L;

        private static readonly JsonWriterOptions _writerOptions = new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// 將 LogItem 格式化為 NDJSON 行(不含換行符)
        /// </summary>
        public static string Format(in LogItem item)
        {
            var current = LogConfiguration.Current;
            using var stream = new MemoryStream(256);
            using (var writer = new Utf8JsonWriter(stream, _writerOptions))
            {
                writer.WriteStartObject();

                // ts: epoch_ms。TimestampTicks 是 local time ticks,先轉 UTC ticks 再算 epoch
                var utcTicks = new DateTime(item.TimestampTicks, DateTimeKind.Local).ToUniversalTime().Ticks;
                var epochMs = (utcTicks - UnixEpochTicks) / TimeSpan.TicksPerMillisecond;
                writer.WriteNumber("ts", epochMs);

                writer.WriteString("lv", LevelToString(item.Level));
                writer.WriteString("nm", item.Name ?? string.Empty);

                if (current.ShowThreadId)
                {
                    writer.WriteNumber("tid", item.ThreadId);
                }
                if (current.ShowThreadName && !string.IsNullOrEmpty(item.ThreadName))
                {
                    writer.WriteString("tn", item.ThreadName);
                }

                // msg 永遠存在(使用者拍板)。若 args 已展開過則用展開後字串。
                var msg = ApplyArgs(item.Message, item.Args);
                writer.WriteString("msg", msg);

                // data 欄位:本 formatter 不直接 serialize 任意物件
                // 物件多載走 LOG.LogObject 已把序列化字串塞進 message;
                // 這裡若偵測到 message 含有可解析 JSON,嘗試以 raw JSON 寫入 data 欄位
                // 失敗則保持單純(只有 msg)。
                // 此設計避免重複序列化,且讓 Exception/Object 多載自然落入 data。
                TryWriteDataFromMessage(writer, msg);

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }

        /// <summary>
        /// 把 args 展開到 message(等價 LogFormatter 的行為,但回傳純字串而非 StringBuilder)
        /// </summary>
        private static string ApplyArgs(string message, object[] args)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            if (args == null || args.Length == 0) return message;

            try
            {
                // 與 LogFormatter 同一套規則:只跳脫「不是合法佔位符」的大括號
                return string.Format(CultureInfo.InvariantCulture,
                    LogFormatter.EscapeMessage(message, args.Length), args);
            }
            catch (FormatException)
            {
                return message;
            }
        }

        /// <summary>
        /// 若 message 是 "header\n{...json...}" 格式(LOG.LogObject 的輸出形式),
        /// 把 \n 之後的 JSON 部分提取出來,當作 data 欄位寫入。失敗則不寫 data。
        /// </summary>
        /// <remarks>
        /// v3.2 起呼叫端不再把 <c>{</c>/<c>}</c> 雙倍化,message 裡的 JSON 就是原樣的合法 JSON,
        /// 直接 parse 即可(舊版必須先反跳脫 <c>{{</c>/<c>}}</c>,該退路已隨跳脫時機修正而移除)。
        /// </remarks>
        private static void TryWriteDataFromMessage(Utf8JsonWriter writer, string fullMsg)
        {
            if (string.IsNullOrEmpty(fullMsg)) return;

            // LOG.LogObject 的格式:message + "\n" + json
            var nlIdx = fullMsg.IndexOf('\n');
            if (nlIdx < 0 || nlIdx == fullMsg.Length - 1) return;

            var jsonPart = fullMsg.Substring(nlIdx + 1).TrimStart();
            if (jsonPart.Length == 0) return;

            // 快速判斷:首字必為 { 或 [
            var firstCh = jsonPart[0];
            if (firstCh != '{' && firstCh != '[') return;

            TryParseAndWrite(writer, jsonPart);
        }

        private static bool TryParseAndWrite(Utf8JsonWriter writer, string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                writer.WritePropertyName("data");
                doc.RootElement.WriteTo(writer);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string LevelToString(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Trace: return "Trace";
                case LogLevel.Debug: return "Debug";
                case LogLevel.Info: return "Info";
                case LogLevel.Warn: return "Warn";
                case LogLevel.Error: return "Error";
                case LogLevel.Fatal: return "Fatal";
                case LogLevel.CustomName: return "CustomName";
                default: return level.ToString();
            }
        }
    }
}
