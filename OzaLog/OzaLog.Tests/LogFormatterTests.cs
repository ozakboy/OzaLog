using System;
using OzaLog.Core;
using Xunit;

namespace OzaLog.Tests
{
    public class LogFormatterTests
    {
        [Fact]
        public void Format_ProducesExpectedTimestampLayout()
        {
            var ticks = new DateTime(2026, 5, 8, 9, 7, 5, 123, DateTimeKind.Local).Ticks;
            var item = new LogItem(LogLevel.Info, "", "hello", null, ticks, 42, null, false);
            var line = LogFormatter.Format(in item);

            Assert.StartsWith("09:07:05.123[T:42] ", line);
            Assert.EndsWith("hello", line);
        }

        [Fact]
        public void Format_AppliesArgsFormatting()
        {
            var ticks = DateTime.Now.Ticks;
            var item = new LogItem(LogLevel.Info, "", "user {0} did {1}", new object[] { "alice", "login" }, ticks, 1, null, false);
            var line = LogFormatter.Format(in item);

            Assert.Contains("user alice did login", line);
        }

        [Fact]
        public void Format_GracefullyHandlesBadFormatString()
        {
            var ticks = DateTime.Now.Ticks;
            // {2} 超出 args 範圍 → 應 fallback 為原字串而非拋例外
            var item = new LogItem(LogLevel.Info, "", "broken {2}", new object[] { "x" }, ticks, 1, null, false);
            var line = LogFormatter.Format(in item);

            Assert.Contains("broken {2}", line);
        }

        [Fact]
        public void EscapeMessage_LeavesNumericPlaceholdersAlone()
        {
            Assert.Equal("user {0} did {1}", LogFormatter.EscapeMessage("user {0} did {1}", 2));
        }

        [Fact]
        public void EscapeMessage_DoublesNonPlaceholderBraces()
        {
            Assert.Equal("plain {{text}}", LogFormatter.EscapeMessage("plain {text}", 1));
        }

        [Fact]
        public void EscapeMessage_EscapesOutOfRangePlaceholder()
        {
            // {2} 超出 args 範圍 → 當字面量跳脫,不讓 AppendFormat 拋 FormatException
            Assert.Equal("broken {{2}}", LogFormatter.EscapeMessage("broken {2}", 1));
        }

        [Fact]
        public void EscapeMessage_KeepsAlignmentAndFormatSections()
        {
            Assert.Equal("{0,-8}|{1:F4}", LogFormatter.EscapeMessage("{0,-8}|{1:F4}", 2));
        }
    }
}
