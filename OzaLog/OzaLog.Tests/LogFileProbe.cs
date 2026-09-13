using System;
using System.IO;

namespace OzaLog.Tests
{
    /// <summary>
    /// 測試輔助：直接讀取 logs/ 目錄的實際落檔內容。
    /// 檔案可能仍被 FileStreamPool 持有（FileShare.Read），故一律以 ReadWrite 共用模式開啟。
    /// </summary>
    internal static class LogFileProbe
    {
        /// <summary>
        /// logs/ 根目錄（AppDomain BaseDirectory + LogPath）
        /// </summary>
        public static string LogRoot
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LOG.GetCurrentOptions().LogPath);

        /// <summary>
        /// 統計 marker 在 logs/ 底下所有檔案中出現的總次數（用於驗證「只寫一次」）
        /// </summary>
        public static int CountOccurrences(string marker)
        {
            if (!Directory.Exists(LogRoot)) return 0;

            var total = 0;
            foreach (var file in Directory.GetFiles(LogRoot, "*", SearchOption.AllDirectories))
            {
                var content = ReadAllTextShared(file);
                if (content == null) continue;

                var index = 0;
                while (true)
                {
                    var hit = content.IndexOf(marker, index, StringComparison.Ordinal);
                    if (hit < 0) break;
                    total++;
                    index = hit + marker.Length;
                }
            }
            return total;
        }

        /// <summary>
        /// 找出某個 name 對應的日誌檔（{name}_Log.*），找不到回傳 null
        /// </summary>
        public static string FindLogFile(string name)
        {
            if (!Directory.Exists(LogRoot)) return null;
            var files = Directory.GetFiles(LogRoot, name + "_Log.*", SearchOption.AllDirectories);
            return files.Length > 0 ? files[0] : null;
        }

        /// <summary>
        /// 以共用模式讀取檔案；讀不到時回傳 null（不讓 I/O 例外中斷測試掃描）
        /// </summary>
        public static string ReadAllTextShared(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
