using System;
using System.Threading.Tasks;
using OzaLog.Core;
using Xunit;

namespace OzaLog.Tests
{
    /// <summary>
    /// v3.3.0 新增的宿主端生命週期 API：<c>LOG.Flush</c> / <c>LOG.FlushAsync</c> /
    /// <c>LOG.Shutdown</c> / <c>LOG.ShutdownAsync</c> / <c>LOG.IsShutdown</c>。
    /// </summary>
    /// <remarks>
    /// 這個類別會操作行程級的全域狀態（收尾整條管線），因此：
    /// • 組件層級已關閉平行測試（見 AssemblyInfo.cs）
    /// • 每個會收尾的測試都在 finally 裡用 Configure 把系統復原，讓後續測試不受影響
    /// </remarks>
    public class LifecycleTests
    {
        private const int EntryCount = 1000;

        /// <summary>收尾後把系統復原成可寫入狀態（Shutdown 之後才允許再次 Configure）。</summary>
        private static void RestoreLogging()
        {
            if (LOG.IsShutdown)
                LOG.Configure(_ => { });
        }

        [Fact]
        public void FlushReturnsOnlyAfterEveryQueuedEntryHasLanded()
        {
            var name = "Flush" + Guid.NewGuid().ToString("N").Substring(0, 8);

            for (var i = 0; i < EntryCount; i++)
                LOG.CustomName_Log(name, "flush regression " + i.ToString());

            Assert.True(LOG.Flush(), "Flush 應在佇列排空後回 true");

            var file = LogFileProbe.FindLogFile(name);
            Assert.NotNull(file);

            var lines = LogFileProbe.ReadAllTextShared(file)
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            // Flush 回來的當下就必須是 1000 行：不補 sleep、不重讀
            Assert.Equal(EntryCount, lines.Length);
        }

        [Fact]
        public async Task FlushAsyncReturnsOnlyAfterEveryQueuedEntryHasLanded()
        {
            var name = "FlushAsync" + Guid.NewGuid().ToString("N").Substring(0, 8);

            for (var i = 0; i < EntryCount; i++)
                LOG.CustomName_Log(name, "flush async regression " + i.ToString());

            Assert.True(await LOG.FlushAsync());

            var file = LogFileProbe.FindLogFile(name);
            Assert.NotNull(file);

            var lines = LogFileProbe.ReadAllTextShared(file)
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(EntryCount, lines.Length);
        }

        [Fact]
        public void WritesAfterShutdownAreSilentlyDiscarded()
        {
            var marker = "after_shutdown_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                Assert.True(LOG.Shutdown());
                Assert.True(LOG.IsShutdown);

                // 收尾之後的寫入不可擲例外（背景服務型套件不能把宿主弄掛）
                var ex = Record.Exception(() =>
                {
                    LOG.Trace_Log(marker);
                    LOG.Info_Log(marker);
                    LOG.Warn_Log(marker);
                    LOG.Error_Log(marker);
                    LOG.Fatal_Log(marker);
                    LOG.CustomName_Log("AfterShutdown", marker);
                    LOG.Info_Log(marker, args: null, writeTxt: true, immediateFlush: true);
                    LOG.Error_Log("with object", new InvalidOperationException(marker));
                });
                Assert.Null(ex);

                // 靜默丟棄：內容不會出現在任何日誌檔
                Assert.Equal(0, LogFileProbe.CountOccurrences(marker));

                // 已收尾時 Flush 明確回 false（而不是假裝成功）
                Assert.False(LOG.Flush());
            }
            finally
            {
                RestoreLogging();
            }
        }

        [Fact]
        public void ShutdownIsIdempotentAlongsideTheProcessExitCleanup()
        {
            try
            {
                LOG.Info_Log("idempotent shutdown probe");

                Assert.True(LOG.Shutdown());    // 第一次真的收尾
                Assert.False(LOG.Shutdown());   // 第二次冪等，回 false 不是錯誤

                // 模擬 ProcessExit / UnhandledException 已註冊的收尾在 Shutdown 之後才被觸發
                var ex = Record.Exception(() =>
                {
                    AsyncLogHandler.ShutdownGracefully();
                    QuoteLogHandler.ShutdownGracefully();
                    LOG.Shutdown();
                });

                Assert.Null(ex);
                Assert.True(LOG.IsShutdown);
            }
            finally
            {
                RestoreLogging();
            }
        }

        [Fact]
        public async Task ShutdownAsyncIsIdempotent()
        {
            try
            {
                LOG.Info_Log("idempotent async shutdown probe");

                Assert.True(await LOG.ShutdownAsync());
                Assert.False(await LOG.ShutdownAsync());
                Assert.True(LOG.IsShutdown);
            }
            finally
            {
                RestoreLogging();
            }
        }

        [Fact]
        public void ConfigureStaysNonReentrantUntilShutdown()
        {
            try
            {
                // 先收尾把狀態歸零，這樣不論前面哪個測試先跑，下一行的 Configure 一定是「第一次」
                LOG.Shutdown();
                LOG.Configure(_ => { });

                // 管線還活著時，Configure 仍然不可重入（v3.0 起的 by design 行為不變）
                Assert.Throws<InvalidOperationException>(() => LOG.Configure(_ => { }));

                Assert.True(LOG.Shutdown());

                // 收尾之後才允許再次 Configure
                var ex = Record.Exception(() => LOG.Configure(_ => { }));
                Assert.Null(ex);
                Assert.False(LOG.IsShutdown);
            }
            finally
            {
                RestoreLogging();
            }
        }

        [Fact]
        public void LoggingResumesAfterConfigureFollowingShutdown()
        {
            var marker = "resumed_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                LOG.Shutdown();
                LOG.Configure(_ => { });

                LOG.Info_Log("resume probe " + marker);
                Assert.True(LOG.Flush());

                Assert.Equal(1, LogFileProbe.CountOccurrences(marker));
            }
            finally
            {
                RestoreLogging();
            }
        }
    }
}
