using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using OzaLog.Core;
using Xunit;

namespace OzaLog.Tests
{
    /// <summary>
    /// 防回歸（v3.2.0）：訊息裡的 <c>{</c> / <c>}</c> 必須原樣落檔，不可被跳脫字元汙染。
    /// </summary>
    /// <remarks>
    /// v3.2.0 以前 LOG.Log 會無條件把訊息中的大括號雙倍化（給 AppendFormat 用），
    /// 但無 args 的路徑走 StringBuilder.Append、根本不經 AppendFormat，沒人把它還原，
    /// 導致異常序列化的 JSON 以 <c>{{ "Type": ... }}</c> 落檔，下游 parser 直接吃不下。
    /// 這裡同時守住三件事：無 args 原樣輸出、有 args 仍正常代換、例外 JSON 真的 parse 得動。
    /// </remarks>
    public class BraceEscapingTests
    {
        /// <summary>
        /// 等待 dispatcher 排空（FlushIntervalMs 預設 1000ms）+ disk flush timer（100ms）
        /// </summary>
        private const int DispatcherSettleMs = 1600;

        /// <summary>
        /// 巢狀但結尾不相鄰的 JSON：可直接對整行斷言「不含 <c>}}</c>」而不誤判
        /// </summary>
        private const string SampleJson = "{\"symbol\":\"BTCUSDT\",\"depth\":{\"bid\":[1,2]},\"ok\":true}";

        private static LogItem Item(string message, object[] args, LogLevel level = LogLevel.Info, string name = "")
        {
            var thread = Thread.CurrentThread;
            return new LogItem(
                level: level,
                name: name,
                message: message,
                args: args,
                timestampTicks: TimestampCache.GetCurrentTicks(),
                threadId: thread.ManagedThreadId,
                threadName: thread.Name,
                requireImmediateFlush: false);
        }

        // ── 1. 無 args：大括號原樣呈現 ────────────────────────────────────

        [Fact]
        public void Format_WithoutArgs_KeepsBracesUndoubled()
        {
            var line = LogFormatter.Format(Item(SampleJson, null));

            Assert.EndsWith(SampleJson, line, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", line, StringComparison.Ordinal);
            Assert.DoesNotContain("}}", line, StringComparison.Ordinal);
        }

        [Fact]
        public void Format_WithoutArgs_KeepsEmptyBracePairs()
        {
            // 例外序列化最常見的形狀：空物件 "Data": {}
            var line = LogFormatter.Format(Item("\"Data\": {}", null));

            Assert.EndsWith("\"Data\": {}", line, StringComparison.Ordinal);
        }

        [Fact]
        public void Format_WithoutArgs_KeepsUserWrittenDoubleBracesAsWritten()
        {
            // 使用者真的寫了 {{ 就該原樣出現 {{，不可被再次加倍成 {{{{
            var line = LogFormatter.Format(Item("literal {{braces}}", null));

            Assert.EndsWith("literal {{braces}}", line, StringComparison.Ordinal);
        }

        // ── 2. 有 args：佔位符照常代換，字面大括號照常顯示 ────────────────

        [Fact]
        public void Format_WithArgs_SubstitutesPlaceholdersAndKeepsLiteralBraces()
        {
            var line = LogFormatter.Format(
                Item("payload {\"id\":7} sent by {0} at {1}", new object[] { "alice", "09:00" }));

            Assert.EndsWith("payload {\"id\":7} sent by alice at 09:00", line, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", line, StringComparison.Ordinal);
        }

        [Fact]
        public void Format_WithArgs_SupportsAlignmentAndFormatSections()
        {
            var line = LogFormatter.Format(
                Item("{{raw}} {0,-6}|{1:F2}", new object[] { "ab", 3.14159 }));

            // {{raw}} 是使用者寫的字面量 → 原樣輸出；{0,-6} 靠左補白；{1:F2} 取兩位小數
            Assert.EndsWith("{{raw}} ab    |3.14", line, StringComparison.Ordinal);
        }

        // ── 3. 走公開 API 的實際落檔（真正重現這個 bug 的那一組）────────

        [Fact]
        public void InfoLog_WithoutArgs_WritesBracesUndoubled()
        {
            var marker = "brace_plain_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            LOG.Info_Log(marker + " " + SampleJson);
            Thread.Sleep(DispatcherSettleMs);

            var line = FindLineContaining(marker);
            Assert.NotNull(line);
            Assert.EndsWith(marker + " " + SampleJson, line, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", line, StringComparison.Ordinal);
            Assert.DoesNotContain("}}", line, StringComparison.Ordinal);
        }

        [Fact]
        public void InfoLog_WithArgs_WritesSubstitutedPlaceholdersAndLiteralBraces()
        {
            var marker = "brace_args_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            LOG.Info_Log(marker + " cfg {\"retry\":3} user {0} at {1}", new[] { "alice", "09:00" });
            Thread.Sleep(DispatcherSettleMs);

            var line = FindLineContaining(marker);
            Assert.NotNull(line);
            Assert.EndsWith(marker + " cfg {\"retry\":3} user alice at 09:00", line, StringComparison.Ordinal);
        }

        // ── 4. 例外序列化：輸出必須是「parser 吃得下」的合法 JSON ──────────

        [Fact]
        public void ExceptionLog_Async_ProducesParsableJson()
        {
            var marker = "brace_async_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Warn 不走立即落檔，會經佇列由 dispatcher 寫入（非同步路徑）
            try { throw new InvalidOperationException(marker); }
            catch (InvalidOperationException ex) { LOG.Warn_Log(ex); }

            Thread.Sleep(DispatcherSettleMs);

            var json = ExtractExceptionJson(marker);
            Assert.NotNull(json);
            Assert.DoesNotContain("{{", json, StringComparison.Ordinal);
            Assert.DoesNotContain("}}", json, StringComparison.Ordinal);

            // 關鍵驗收：真的用 System.Text.Json 去 parse，不是比字串
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("System.InvalidOperationException", doc.RootElement.GetProperty("Type").GetString());
            Assert.Equal(marker, doc.RootElement.GetProperty("Message").GetString());
            Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("Data").ValueKind);
        }

        [Fact]
        public void ExceptionLog_Sync_ProducesParsableJson()
        {
            var marker = "brace_sync_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var name = "BraceSync" + Guid.NewGuid().ToString("N").Substring(0, 8);

            string payload;
            try { throw new InvalidOperationException(marker); }
            catch (InvalidOperationException ex) { payload = "\n" + LogSerializer.SerializeException(ex); }

            var item = Item(payload, null, LogLevel.CustomName, name);

            // LOG.Log 在 EnableAsyncLogging = false 時走的就是這個進入點
            LogText.WriteSync(in item);

            var file = Directory.GetFiles(LogFileProbe.LogRoot, name + "_Log.*", SearchOption.AllDirectories)[0];
            var content = LogFileProbe.ReadAllTextShared(file);
            Assert.False(string.IsNullOrEmpty(content));
            Assert.DoesNotContain("{{", content, StringComparison.Ordinal);

            var json = ExtractExceptionJson(marker);
            Assert.NotNull(json);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(marker, doc.RootElement.GetProperty("Message").GetString());

            // 同步模式的落檔內容必須與 dispatcher（非同步）用同一個 formatter 產出的字串一致
            Assert.Equal(LogFormatter.Format(in item), content.TrimEnd('\r', '\n'));
        }

        // ── 5. JSON 輸出格式（OutputFormat = Json）不受影響 ────────────────

        [Fact]
        public void JsonFormat_MsgFieldKeepsBracesUndoubled()
        {
            var line = JsonLogFormatter.Format(Item("payload " + SampleJson, null));

            using var doc = JsonDocument.Parse(line);
            Assert.Equal("payload " + SampleJson, doc.RootElement.GetProperty("msg").GetString());
        }

        [Fact]
        public void JsonFormat_ObjectPayloadStillLandsInDataField()
        {
            // LOG.LogObject 的形狀：header + "\n" + json；v3.2 起 json 段不再被雙倍化
            var line = JsonLogFormatter.Format(Item("header\n" + SampleJson, null));

            using var doc = JsonDocument.Parse(line);
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("BTCUSDT", data.GetProperty("symbol").GetString());
        }

        [Fact]
        public void JsonFormat_WithArgs_SubstitutesPlaceholdersAndKeepsLiteralBraces()
        {
            var line = JsonLogFormatter.Format(Item("cfg {\"a\":1} by {0}", new object[] { "bob" }));

            using var doc = JsonDocument.Parse(line);
            Assert.Equal("cfg {\"a\":1} by bob", doc.RootElement.GetProperty("msg").GetString());
        }

        // ── 輔助 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 在 logs/ 底下找出含 marker 的那一行（單行訊息用）
        /// </summary>
        private static string FindLineContaining(string marker)
        {
            if (!Directory.Exists(LogFileProbe.LogRoot)) return null;

            foreach (var file in Directory.GetFiles(LogFileProbe.LogRoot, "*", SearchOption.AllDirectories))
            {
                var content = LogFileProbe.ReadAllTextShared(file);
                if (content == null || content.IndexOf(marker, StringComparison.Ordinal) < 0) continue;

                foreach (var line in content.Split('\n'))
                {
                    if (line.IndexOf(marker, StringComparison.Ordinal) >= 0)
                        return line.TrimEnd('\r');
                }
            }
            return null;
        }

        /// <summary>
        /// 在 logs/ 底下找出含 marker 的那一段例外 JSON。
        /// 例外 JSON 由 LOG.LogObject 以 "header\n{...}" 形式寫入，
        /// WriteIndented 讓根層級的 <c>{</c> 與 <c>}</c> 各自獨佔一行，據此切出完整區段
        /// （同一個日誌檔後面還會有別筆 log，不可整串拿去 parse）。
        /// </summary>
        private static string ExtractExceptionJson(string marker)
        {
            if (!Directory.Exists(LogFileProbe.LogRoot)) return null;

            foreach (var file in Directory.GetFiles(LogFileProbe.LogRoot, "*", SearchOption.AllDirectories))
            {
                var content = LogFileProbe.ReadAllTextShared(file);
                if (content == null) continue;

                var hit = content.IndexOf(marker, StringComparison.Ordinal);
                if (hit < 0) continue;

                var start = -1;
                for (var i = hit; i >= 0; i--)
                {
                    if (content[i] == '{' && (i == 0 || content[i - 1] == '\n')) { start = i; break; }
                }
                if (start < 0) continue;

                for (var i = hit; i < content.Length; i++)
                {
                    if (content[i] == '}' && content[i - 1] == '\n')
                        return content.Substring(start, i - start + 1);
                }
            }
            return null;
        }
    }
}
