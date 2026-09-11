using System;
using System.IO;
using System.Threading;
using OzaLog.Core;
using Xunit;

namespace OzaLog.Tests
{
    /// <summary>
    /// 防回歸（v3.2.0）：EnableAsyncLogging = false 的同步寫入路徑必須真的落檔。
    /// </summary>
    /// <remarks>
    /// 為何不透過 LOG.Configure 切成同步模式測試：LogConfiguration.Initialize 不可重入（by design），
    /// 全域只能設定一次，同一個測試 process 內無法在同步／非同步之間切換，
    /// 硬切會讓其他測試（例如 BasicLoggingTests / DuplicateWriteTests）跟著改變模式而失去意義。
    /// 因此這裡直接驅動 LOG.Log 在同步模式下所走的那一個內部進入點。
    /// </remarks>
    public class SyncModeWriteTests
    {
        private static LogItem BuildItem(string name, string message)
        {
            var thread = Thread.CurrentThread;
            return new LogItem(
                level: LogLevel.CustomName,
                name: name,
                message: message,
                args: null,
                timestampTicks: TimestampCache.GetCurrentTicks(),
                threadId: thread.ManagedThreadId,
                threadName: thread.Name,
                requireImmediateFlush: false);
        }

        private static string FindLogFile(string name)
        {
            if (!Directory.Exists(LogFileProbe.LogRoot)) return null;
            var files = Directory.GetFiles(LogFileProbe.LogRoot, name + "_Log.*", SearchOption.AllDirectories);
            return files.Length > 0 ? files[0] : null;
        }

        [Fact]
        public void SyncWrite_ContentIsReadableImmediately()
        {
            var name = "SyncMode" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var item = BuildItem(name, "sync mode must not be swallowed by the writer buffer");

            // LOG.Log 在 EnableAsyncLogging = false 時走的就是這個進入點
            LogText.WriteSync(in item);

            var file = FindLogFile(name);
            Assert.NotNull(file);

            var content = LogFileProbe.ReadAllTextShared(file);
            Assert.False(string.IsNullOrEmpty(content), $"同步模式寫入後檔案不可為空：{file}");
            Assert.Contains("sync mode must not be swallowed by the writer buffer", content, StringComparison.Ordinal);
        }

        [Fact]
        public void SyncWrite_ProducesSameLineAsAsyncDispatcher()
        {
            var name = "SyncFmt" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var item = BuildItem(name, "same line in both modes");

            // 非同步模式由 dispatcher 呼叫 LogText.Write，兩者共用同一個 formatter
            var expectedLine = LogFormatter.Format(in item);

            LogText.WriteSync(in item);

            var file = FindLogFile(name);
            Assert.NotNull(file);

            var content = LogFileProbe.ReadAllTextShared(file);
            Assert.Equal(expectedLine, content.TrimEnd('\r', '\n'));
        }

        [Fact]
        public void SyncWrite_IsThreadSafe()
        {
            var name = "SyncMt" + Guid.NewGuid().ToString("N").Substring(0, 8);
            const int threadCount = 8;
            const int perThread = 50;

            var threads = new Thread[threadCount];
            for (var t = 0; t < threadCount; t++)
            {
                var threadIndex = t;
                threads[t] = new Thread(() =>
                {
                    for (var i = 0; i < perThread; i++)
                    {
                        var item = BuildItem(name, $"mt {threadIndex}-{i}");
                        LogText.WriteSync(in item);
                    }
                });
            }

            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();

            var file = FindLogFile(name);
            Assert.NotNull(file);

            var lines = LogFileProbe.ReadAllTextShared(file)
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(threadCount * perThread, lines.Length);
        }
    }
}
