using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace OzaLog.Core
{
    /// <summary>
    /// 報價 pipeline 的持久化 FileStream 池。獨立於主 logger 的 FileStreamPool。
    /// 鍵為 <c>(bucket, symbol)</c>;LRU 上限由 <see cref="QuoteOptions.MaxOpenStreams"/> 控制。
    /// </summary>
    /// <remarks>
    /// 與主 FileStreamPool 的差異:
    /// • 鍵維度不同(bucket+symbol vs level+name)
    /// • 路徑不同:<c>{baseDir}/{LogPath}/{yyyyMMdd}/{QuotePath}/{bucket}_{symbol}_Quote.{ext}</c>
    /// • 不分子目錄(使用者拍板的 Q3D-1 a)
    /// • 檔名特殊字元自動 sanitize(Q4-1)
    /// </remarks>
    internal static class QuoteFileStreamPool
    {
        private sealed class Slot
        {
            public string Key;
            public string Bucket;
            public string Symbol;
            public StreamWriter Writer;
            public FileStream Stream;
            public string DirectoryPath;
            public int CurrentDateInt;
            public int PartNumber;
            public long CurrentSize;
            public DateTime LastAccess;
        }

        // 檔名禁用字元(Windows 最嚴格集) + 全部會被替換成 '-'
        private static readonly char[] _invalidFileChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, LinkedListNode<Slot>> _index = new Dictionary<string, LinkedListNode<Slot>>(StringComparer.Ordinal);
        private static readonly LinkedList<Slot> _lru = new LinkedList<Slot>();

        /// <summary>
        /// 寫入一行至 (bucket, symbol) 對應檔案,自動處理建立、換日、大小分割、LRU。
        /// </summary>
        public static void AppendLine(string bucket, string symbol, string formattedLine)
        {
            ValidateRequired(bucket, symbol);

            var safeBucket = Sanitize(bucket);
            var safeSymbol = Sanitize(symbol);
            var nowTicks = TimestampCache.GetCurrentTicks();
            var nowDate = new DateTime(nowTicks);
            var dateInt = nowDate.Year * 10000 + nowDate.Month * 100 + nowDate.Day;
            var key = safeBucket + "|" + safeSymbol;

            lock (_gate)
            {
                var slot = GetOrCreateSlot(key, safeBucket, safeSymbol, dateInt, nowDate);

                if (slot.CurrentDateInt != dateInt)
                {
                    CloseSlot(slot);
                    slot = OpenSlot(key, safeBucket, safeSymbol, dateInt, nowDate);
                }

                var lineBytes = Encoding.UTF8.GetByteCount(formattedLine) + Environment.NewLine.Length;
                var maxSize = LogConfiguration.Current.MaxFileSize;
                if (slot.CurrentSize + lineBytes > maxSize && slot.CurrentSize > 0)
                {
                    CloseSlot(slot);
                    slot = OpenSlot(key, safeBucket, safeSymbol, dateInt, nowDate, forcePart: slot.PartNumber + 1);
                }

                slot.Writer.WriteLine(formattedLine);
                slot.CurrentSize += lineBytes;
                slot.LastAccess = nowDate;

                EnforceLruBound();
            }
        }

        /// <summary>
        /// flush 全部開啟的 stream(由 dispatcher 的定期 timer 呼叫)
        /// </summary>
        public static void FlushAll()
        {
            FlushAll(flushToDisk: false);
        }

        /// <summary>
        /// flush 全部開啟的 stream。
        /// </summary>
        /// <param name="flushToDisk">是否強制 fsync(v3.3.0 的 LOG.Flush / LOG.Shutdown 傳 true)</param>
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
                        Console.WriteLine($"QuoteFileStreamPool.FlushAll 錯誤: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>shutdown:flush + 關閉所有 stream</summary>
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

        // =========================================================

        private static void ValidateRequired(string bucket, string symbol)
        {
            if (string.IsNullOrEmpty(bucket))
                throw new ArgumentException("QuoteRecord.Bucket 不可為 null 或空字串", nameof(bucket));
            if (string.IsNullOrEmpty(symbol))
                throw new ArgumentException("QuoteRecord.Symbol 不可為 null 或空字串", nameof(symbol));
        }

        /// <summary>
        /// 把使用者傳入的 bucket / symbol 中含的檔系統非法字元換成 '-'
        /// </summary>
        internal static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            if (input.IndexOfAny(_invalidFileChars) < 0) return input;

            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                if (Array.IndexOf(_invalidFileChars, ch) >= 0) sb.Append('-');
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private static Slot GetOrCreateSlot(string key, string bucket, string symbol, int dateInt, DateTime now)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value;
            }
            return OpenSlot(key, bucket, symbol, dateInt, now);
        }

        private static Slot OpenSlot(string key, string bucket, string symbol, int dateInt, DateTime now, int forcePart = -1)
        {
            var dirPath = ComputeDirectoryPath(now);
            if (!Directory.Exists(dirPath))
                Directory.CreateDirectory(dirPath);

            var ext = GetExtension(LogConfiguration.Current.QuoteOptions.OutputFormat);
            var partNumber = forcePart >= 0 ? forcePart : DetectExistingPart(dirPath, bucket, symbol, ext);
            var prefix = bucket + "_" + symbol;
            var fileName = partNumber == 0
                ? $"{prefix}_Quote.{ext}"
                : $"{prefix}_part{partNumber}_Quote.{ext}";
            var filePath = Path.Combine(dirPath, fileName);

            var existingSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0L;

            // v3.3.0:與主 logger 一致改用 FileShare.ReadWrite,讓外部工具可以邊跑邊看(理由見 FileStreamPool.OpenSlot)
            var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 4096, useAsync: false);
            var writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = false };

            var slot = new Slot
            {
                Key = key,
                Bucket = bucket,
                Symbol = symbol,
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

        private static int DetectExistingPart(string dirPath, string bucket, string symbol, string ext)
        {
            try
            {
                var prefix = bucket + "_" + symbol;
                var suffix = "_Quote." + ext;
                var files = Directory.GetFiles(dirPath, prefix + "*" + suffix);
                int maxPart = 0;
                bool foundBase = false;
                foreach (var f in files)
                {
                    var fileName = Path.GetFileName(f);
                    if (fileName == prefix + suffix)
                    {
                        foundBase = true;
                        continue;
                    }
                    var partPrefix = prefix + "_part";
                    if (fileName.StartsWith(partPrefix, StringComparison.Ordinal) && fileName.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        var middle = fileName.Substring(partPrefix.Length, fileName.Length - partPrefix.Length - suffix.Length);
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

        private static string ComputeDirectoryPath(DateTime now)
        {
            var current = LogConfiguration.Current;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(
                baseDir,
                current.LogPath,
                now.ToString("yyyyMMdd"),
                current.QuoteOptions.QuotePath);
        }

        private static string GetExtension(QuoteOutputFormat format)
        {
            switch (format)
            {
                case QuoteOutputFormat.Json: return "json";
                case QuoteOutputFormat.Log: return "log";
                case QuoteOutputFormat.Txt:
                default: return "txt";
            }
        }

        private static void CloseSlot(Slot slot)
        {
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
            var max = LogConfiguration.Current.QuoteOptions.MaxOpenStreams;
            if (max <= 0) return;
            while (_index.Count > max && _lru.Last != null)
            {
                var oldest = _lru.Last.Value;
                CloseSlot(oldest);
            }
        }
    }
}
