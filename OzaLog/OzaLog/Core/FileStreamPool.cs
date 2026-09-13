using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace OzaLog.Core
{
    /// <summary>
    /// 持久化檔案流池 - 維護 per-(level, name) 的 FileStream，
    /// 配合 LRU 上限自動關閉冷檔、換日自動切換目錄、超過大小自動分割檔案。
    /// Persistent FileStream pool with LRU eviction, day rollover, and size-based splitting.
    /// </summary>
    /// <remarks>
    /// v3.0 引入：取代 v2.x 每批次 open/close 的高 syscall 開銷。
    /// 單一 dispatcher 執行緒呼叫 AppendLine，不需 lock 細粒度同步，
    /// 但 periodic flush timer 與 shutdown 路徑會與 dispatcher 競爭，故仍以 lock 保護。
    /// </remarks>
    internal static class FileStreamPool
    {
        private sealed class Slot
        {
            public string Key;
            public string Name;
            public LogLevel Level;
            public StreamWriter Writer;
            public FileStream Stream;
            public string DirectoryPath;
            public int CurrentDateInt;   // yyyyMMdd as int for cheap compare
            public int PartNumber;
            public long CurrentSize;
            public DateTime LastAccess;
        }

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, LinkedListNode<Slot>> _index = new Dictionary<string, LinkedListNode<Slot>>(StringComparer.Ordinal);
        private static readonly LinkedList<Slot> _lru = new LinkedList<Slot>();   // first = newest, last = oldest

        /// <summary>
        /// 寫入一行至對應檔案，自動處理建立、換日、大小分割、LRU。
        /// </summary>
        public static void AppendLine(LogLevel level, string name, string formattedLine)
        {
            var nameKey = string.IsNullOrEmpty(name) ? level.ToString() : name;
            var nowTicks = TimestampCache.GetCurrentTicks();
            var nowDate = new DateTime(nowTicks);
            var dateInt = nowDate.Year * 10000 + nowDate.Month * 100 + nowDate.Day;
            var key = ((int)level).ToString() + "|" + nameKey;

            lock (_gate)
            {
                var slot = GetOrCreateSlot(key, level, nameKey, dateInt, nowDate);

                // 換日：dateInt 變了，關閉舊 stream 重開新目錄
                if (slot.CurrentDateInt != dateInt)
                {
                    CloseSlot(slot);
                    slot = OpenSlot(key, level, nameKey, dateInt, nowDate);
                }

                // 寫入前先用估算大小判斷是否需要分割（避免每寫一行都 .Length 打 syscall）
                var lineBytes = Encoding.UTF8.GetByteCount(formattedLine) + Environment.NewLine.Length;
                var maxSize = LogConfiguration.Current.MaxFileSize;
                if (slot.CurrentSize + lineBytes > maxSize && slot.CurrentSize > 0)
                {
                    CloseSlot(slot);
                    slot = OpenSlot(key, level, nameKey, dateInt, nowDate, forcePart: slot.PartNumber + 1);
                }

                slot.Writer.WriteLine(formattedLine);
                slot.CurrentSize += lineBytes;
                slot.LastAccess = nowDate;

                EnforceLruBound();
            }
        }

        /// <summary>
        /// 強制 flush 指定 (level, name) 的緩衝（用於 crash log / immediateFlush / 同步模式逐筆落檔）
        /// Flushes the buffer of one (level, name) stream.
        /// </summary>
        /// <param name="level">日誌級別 / Log level</param>
        /// <param name="name">日誌名稱（CustomName 用）/ Log name</param>
        /// <param name="flushToDisk">
        /// 是否強制寫入實體磁碟（fsync，預設 true）；false 時只把緩衝交給 OS，
        /// 與 <see cref="FlushAll()"/> 的定期 flush 行為一致（效能優先）。
        /// Whether to force an fsync (default true); false only hands the buffer to the OS.
        /// </param>
        public static void Flush(LogLevel level, string name, bool flushToDisk = true)
        {
            var nameKey = string.IsNullOrEmpty(name) ? level.ToString() : name;
            var key = ((int)level).ToString() + "|" + nameKey;

            lock (_gate)
            {
                if (_index.TryGetValue(key, out var node))
                {
                    try
                    {
                        node.Value.Writer?.Flush();
                        if (flushToDisk)
                            node.Value.Stream?.Flush(flushToDisk: true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FileStreamPool.Flush 錯誤: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// flush 全部開啟的 stream（由 100ms 定期 timer 呼叫）。
        /// 不強制 fsync，讓 OS 決定何時落盤，效能優先。
        /// </summary>
        public static void FlushAll()
        {
            FlushAll(flushToDisk: false);
        }

        /// <summary>
        /// flush 全部開啟的 stream。
        /// Flushes every open stream.
        /// </summary>
        /// <param name="flushToDisk">
        /// 是否強制寫入實體磁碟（fsync）。定期 timer 傳 <c>false</c>；
        /// v3.3.0 的 <c>LOG.Flush()</c> / <c>LOG.Shutdown()</c> 傳 <c>true</c>，
        /// 因為宿主呼叫它的理由就是「接下來可能被強制終止，現在就要確定落地」。
        /// Whether to force an fsync; the periodic timer passes false, LOG.Flush/Shutdown pass true.
        /// </param>
        public static void FlushAll(bool flushToDisk)
        {
            lock (_gate)
            {
                foreach (var node in _index.Values)
                {
                    try
                    {
                        node.Value.Writer?.Flush();
                        if (flushToDisk)
                            node.Value.Stream?.Flush(flushToDisk: true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FileStreamPool.FlushAll 錯誤: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// shutdown：flush + 關閉所有 stream
        /// </summary>
        public static void Shutdown()
        {
            lock (_gate)
            {
                foreach (var node in _index.Values)
                {
                    CloseSlotRaw(node.Value);
                }
                _index.Clear();
                _lru.Clear();
            }
        }

        // =========================================================================

        private static Slot GetOrCreateSlot(string key, LogLevel level, string name, int dateInt, DateTime now)
        {
            if (_index.TryGetValue(key, out var node))
            {
                // LRU: 移至 first（最新）
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value;
            }

            return OpenSlot(key, level, name, dateInt, now);
        }

        private static Slot OpenSlot(string key, LogLevel level, string name, int dateInt, DateTime now, int forcePart = -1)
        {
            var dirPath = ComputeDirectoryPath(level, now);
            if (!Directory.Exists(dirPath))
                Directory.CreateDirectory(dirPath);

            var ext = GetExtension(LogConfiguration.Current.OutputFormat);
            var partNumber = forcePart >= 0 ? forcePart : DetectExistingPart(dirPath, name, ext);
            var fileName = partNumber == 0
                ? $"{name}_Log.{ext}"
                : $"{name}_part{partNumber}_Log.{ext}";
            var filePath = Path.Combine(dirPath, fileName);

            var existingSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0L;

            // v3.3.0：共用模式由 FileShare.Read 放寬為 FileShare.ReadWrite。
            // FileShare.Read 只允許「以唯讀方式、且自身 share 含 Write」的開啟者；
            // tail / 編輯器 / 監看工具常以 FileAccess.ReadWrite 開檔，在舊設定下一律吃到共用違規，
            // 對「邊跑邊看日誌」是實際的可用性阻礙。放寬後外部工具怎麼開都讀得到。
            var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 4096, useAsync: false);
            var writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = false };

            var slot = new Slot
            {
                Key = key,
                Name = name,
                Level = level,
                Writer = writer,
                Stream = fs,
                DirectoryPath = dirPath,
                CurrentDateInt = dateInt,
                PartNumber = partNumber,
                CurrentSize = existingSize,
                LastAccess = now,
            };

            var node = new LinkedListNode<Slot>(slot);
            _index[key] = node;
            _lru.AddFirst(node);

            return slot;
        }

        private static int DetectExistingPart(string dirPath, string name, string ext)
        {
            // 沿用 v2.x 的命名慣例：{name}_Log.{ext}（part=0） / {name}_part{N}_Log.{ext}
            try
            {
                var suffix = "_Log." + ext;
                var files = Directory.GetFiles(dirPath, name + "*" + suffix);
                int maxPart = 0;
                bool foundBase = false;
                foreach (var f in files)
                {
                    var fileName = Path.GetFileName(f);
                    if (fileName == name + suffix)
                    {
                        foundBase = true;
                        continue;
                    }
                    // 解析 {name}_part{N}_Log.{ext}
                    var prefix = name + "_part";
                    if (fileName.StartsWith(prefix, StringComparison.Ordinal) && fileName.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        var middle = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
                        if (int.TryParse(middle, out var n) && n > maxPart)
                            maxPart = n;
                    }
                }
                if (maxPart > 0) return maxPart;
                if (foundBase) return 0;
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string GetExtension(LogOutputFormat format)
        {
            switch (format)
            {
                case LogOutputFormat.Json: return "json";
                case LogOutputFormat.Log: return "log";
                case LogOutputFormat.Txt:
                default: return "txt";
            }
        }

        private static string ComputeDirectoryPath(LogLevel level, DateTime now)
        {
            var current = LogConfiguration.Current;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(
                baseDir,
                current.LogPath,
                now.ToString("yyyyMMdd"),
                current.TypeDirectories.GetPathForType(level));
        }

        private static void CloseSlot(Slot slot)
        {
            // index/lru 也要清
            if (_index.TryGetValue(slot.Key, out var node))
            {
                _lru.Remove(node);
                _index.Remove(slot.Key);
            }
            CloseSlotRaw(slot);
        }

        private static void CloseSlotRaw(Slot slot)
        {
            try { slot.Writer?.Flush(); } catch { }
            try { slot.Writer?.Dispose(); } catch { }
            try { slot.Stream?.Dispose(); } catch { }
            slot.Writer = null;
            slot.Stream = null;
        }

        private static void EnforceLruBound()
        {
            var max = LogConfiguration.Current.MaxOpenFileStreams;
            if (max <= 0) return;
            while (_index.Count > max && _lru.Last != null)
            {
                var oldest = _lru.Last.Value;
                CloseSlot(oldest);
            }
        }
    }
}
