using System;
using System.IO;
using Xunit;

namespace OzaLog.Tests
{
    /// <summary>
    /// 防回歸（v3.3.0）：執行期的日誌檔必須能被外部工具開起來看。
    /// </summary>
    /// <remarks>
    /// v3.2.0 以前寫入端以 <c>FileShare.Read</c> 開檔，只允許「自身共用模式含 Write 的唯讀開啟」；
    /// tail / 編輯器 / 監看工具普遍以 <c>FileAccess.ReadWrite</c> 開檔，一律吃到共用違規，
    /// 實務上等同「跑著的時候看不到日誌」。v3.3.0 放寬為 <c>FileShare.ReadWrite</c>。
    /// </remarks>
    public class FileShareTests
    {
        [Fact]
        public void LogFileIsReadableWhileTheWriterStillHoldsIt()
        {
            var name = "Share" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var marker = "file share probe " + name;

            LOG.CustomName_Log(name, marker);
            Assert.True(LOG.Flush(), "Flush 應在佇列排空後回 true");

            var file = LogFileProbe.FindLogFile(name);
            Assert.NotNull(file);

            // 1) 唯讀開啟（tail 類工具的最低需求）
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                Assert.Contains(marker, reader.ReadToEnd(), StringComparison.Ordinal);
            }

            // 2) 以 ReadWrite 存取開啟 —— 這一項在寫入端為 FileShare.Read 時會直接 IOException：
            //    要求的存取含 Write，而既有寫入 handle 的共用模式只允許 Read。
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                Assert.True(fs.Length > 0);
            }

            // 讀完之後寫入端仍應能繼續寫（確認前面的開啟沒有把檔案卡住）
            LOG.CustomName_Log(name, marker + " after external read");
            Assert.True(LOG.Flush());

            var content = LogFileProbe.ReadAllTextShared(file);
            Assert.Contains("after external read", content, StringComparison.Ordinal);
        }
    }
}
