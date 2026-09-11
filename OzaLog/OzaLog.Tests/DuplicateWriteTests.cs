using System;
using System.Threading;
using Xunit;

namespace OzaLog.Tests
{
    /// <summary>
    /// 防回歸（v3.2.0）：Error / Fatal 與 immediateFlush 的立即落檔路徑不可造成重複寫入。
    /// v3.1.0 的 AsyncLogHandler.Enqueue 會先入隊、再同步寫一次，dispatcher 之後又寫一次 → 每筆兩行。
    /// </summary>
    public class DuplicateWriteTests
    {
        /// <summary>
        /// 等待 dispatcher 排空（FlushIntervalMs 預設 1000ms）+ disk flush timer（100ms）。
        /// 若有重複寫入，等待結束時第二筆必然已落檔。
        /// </summary>
        private const int DispatcherSettleMs = 1600;

        [Fact]
        public void ErrorLog_WritesExactlyOnce()
        {
            var marker = "dup_error_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            LOG.Error_Log("duplicate write regression " + marker);
            Thread.Sleep(DispatcherSettleMs);

            Assert.Equal(1, LogFileProbe.CountOccurrences(marker));
        }

        [Fact]
        public void FatalLog_WritesExactlyOnce()
        {
            var marker = "dup_fatal_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            LOG.Fatal_Log("duplicate write regression " + marker);
            Thread.Sleep(DispatcherSettleMs);

            Assert.Equal(1, LogFileProbe.CountOccurrences(marker));
        }

        [Fact]
        public void ImmediateFlushOverload_WritesExactlyOnce()
        {
            var marker = "dup_flush_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // immediateFlush: true 走的是與 Error/Fatal 相同的立即落檔路徑
            LOG.Info_Log("duplicate write regression " + marker, args: null, writeTxt: true, immediateFlush: true);
            Thread.Sleep(DispatcherSettleMs);

            Assert.Equal(1, LogFileProbe.CountOccurrences(marker));
        }

        [Theory]
        [InlineData("trace")]
        [InlineData("debug")]
        [InlineData("info")]
        [InlineData("warn")]
        [InlineData("custom")]
        public void NonAutoFlushLevels_StillWriteExactlyOnce(string kind)
        {
            var marker = "dup_" + kind + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var message = "duplicate write regression " + marker;

            switch (kind)
            {
                case "trace": LOG.Trace_Log(message); break;
                case "debug": LOG.Debug_Log(message); break;
                case "info": LOG.Info_Log(message); break;
                case "warn": LOG.Warn_Log(message); break;
                default: LOG.CustomName_Log("DupRegression", message); break;
            }

            Thread.Sleep(DispatcherSettleMs);

            Assert.Equal(1, LogFileProbe.CountOccurrences(marker));
        }
    }
}
