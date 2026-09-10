---
title: 版本歷史
description: OzaLog 所有重要變更紀錄。
---

# 版本更新記錄

本檔案記錄 **OzaLog**（前身為 **Ozakboy.NLOG**）套件的所有重要變更。
版本號遵循 [語意化版本（SemVer）](https://semver.org/lang/zh-TW/)。

---

## [3.1.1] - 2026-09-11

> 兩個影響日誌可信度的修正:`Error` / `Fatal` 每筆重複寫入兩次,以及同步模式(`EnableAsyncLogging = false`)完全寫不出內容。無 API 變更、無預設值變更。

### 問題修正

**`Error` / `Fatal` / `immediateFlush` 每筆寫入兩次**
- `AsyncLogHandler.Enqueue` 會先把項目放進佇列(dispatcher 稍後寫一次),再在呼叫端同步寫一次以確保 crash 前落盤 — 同一筆 log 因此在檔案裡出現**兩行完全相同的內容**。影響 `Error_Log`、`Fatal_Log` 的全部多載,以及任何級別帶 `immediateFlush: true` 的呼叫;`Trace` / `Debug` / `Info` / `Warn` / `CustomName` 的一般呼叫不受影響。
- **請注意:如果你之前用日誌統計錯誤筆數,3.1.1 以前的數字是實際值的兩倍**;以錯誤率設定的告警閾值需要重新校正。
- 修正後這些項目改為**只走呼叫端同步寫入、不再入隊**,立即落檔的效能特性完全保留(仍是寫入後立刻 `Flush(flushToDisk: true)`)。附帶效果:自動 flush 的項目不再經過佇列,不可能被 drop-oldest 背壓丟棄。

**同步模式(`EnableAsyncLogging = false`)寫出 0 bytes 檔案**
- 同步路徑寫進 `StreamWriter`(`AutoFlush = false`)之後沒有任何人負責 flush:dispatcher、100ms 定期 flush timer、`ProcessExit` 收尾三者都掛在 `AsyncLogHandler.Initialize`,而同步模式從未走到那裡。結果是日誌檔建得出來、程式不報錯,但內容留在緩衝裡隨行程結束**全部消失**。
- **請注意:如果你之前用同步模式,你的日誌檔一直是空的。**
- 修正後同步模式逐筆 flush(`StreamWriter` / `FileStream` 層級,不強制 `fsync`,與非同步模式的定期 flush 行為一致),寫完立刻讀得到;寫入仍由 `FileStreamPool` 的 lock 保護,維持線程安全。同步與非同步模式產生的內容格式完全相同。
- 佇列滿時的背壓自 v3.0 起就是 drop-oldest(不是降級為同步寫),因此不受本 bug 影響。

### 技術改進

- `FileStreamPool.Flush` 新增 `flushToDisk` 參數(預設 `true`,既有呼叫行為不變);同步模式以 `false` 呼叫,避免逐筆 `fsync`。
- 新增內部方法 `LogText.WriteSync`(同步模式專用:寫入 + flush);`LogText.Add_LogText`(v2.x 相容入口)一併改走此路徑。
- 新增 xUnit 測試:`DuplicateWriteTests`(Error / Fatal / `immediateFlush` 各只寫一次,其餘級別行為不變)、`SyncModeWriteTests`(同步寫入立即可讀、與非同步模式輸出同一行、多執行緒不掉行)。`AutoFlushLevelTests`(防 `CustomName = 99` 誤中自動 flush)維持通過。
- `OzaLog.Test` smoke 程式新增第三個 CLI 引數 `write-mode`(`async` / `sync`,預設 `async`),可實測同步模式輸出。
- 跨 5 個 TargetFrameworks(`netstandard2.0` / `netstandard2.1` / `net8.0` / `net9.0` / `net10.0`)的建置驗證:0 錯誤。

---

## [3.1.0] - 2026-05-14

> 三個新增能力:可自訂時間/執行緒顯示、可選輸出格式(txt/log/json)、以及對齊 Binance schema 的獨立報價(Quote)pipeline。所有新增向下相容 — 預設值維持 v3.0 行為。

### 新增功能

**自訂時間 / 執行緒顯示**
- `LogOptions.TimeFormat`(預設 `"HH:mm:ss.fff"`)— 自由格式的 .NET DateTime 字串。Parse 失敗時自動 fallback 預設格式。
- `LogOptions.ShowThreadId`(預設 `true`)與 `LogOptions.ShowThreadName`(預設 `false`)— 獨立切換訊息前綴的 thread ID / name 區段。當 `ShowThreadName=true` 但執行緒無名稱(`Thread.Name == null`)時整個 thread 區段省略。
- `LogOptions.HighPrecisionTimestamp`(預設 `false`)— opt-in `Stopwatch`-hybrid 模式,從 1ms cache 重建出 µs 級精度;呼叫端 ticks 讀取成本從 ~5ns 增加到 ~30ns。

**多輸出格式**
- `LogOptions.OutputFormat`(預設 `LogOutputFormat.Txt`)— 全域格式選擇:`Txt` / `Log`(內容相同只差副檔名) / `Json`(NDJSON 固定 schema `{ts, lv, nm, tid?, tn?, msg, data?}`)。
- JSON 時間戳輸出為 epoch_ms 整數。欄位名採短形式(`lv`、`nm`、`tid`、`tn`)以節省空間。

**報價(Tick/Ticker)pipeline**
- `LOG.Quote(...)` 與 `LOG.QuoteTicker(...)` 公開 API,欄位命名對齊 **Binance REST 24hr Ticker** schema(`Last`、`LastQty`、`Bid`、`BidQty`、`Ask`、`AskQty`、`Open`、`PrevClose`、`High`、`Low`、`Volume`、`QuoteVolume`)。
- `QuoteRecord`(公開 `readonly struct`)— A2 核心 API,零配置入隊。A1 便利多載涵蓋常見情境(僅 tick / bid+ask / 完整 ticker / ticker+extras)。
- `QuoteOptions`(預設 OFF,使用者需在 `opt.ConfigureQuote(q => q.Enable = true)` 內 opt-in)— 獨立非同步 pipeline,自帶 dispatcher、佇列、`FileStreamPool`。可配置 `OutputFormat`(Txt/Log/Json)、`MaxOpenStreams`(預設 500)、`MaxQueueSize`(預設 50000)、`MaxBatchSize`、`FlushIntervalMs`、`OnDropped(long)` callback。
- `QuoteRecord.Extras`(`IReadOnlyDictionary<string, object>`,反射友善)與 `QuoteRecord.ExtrasJson`(預先序列化的 JSON 字串,零開銷路徑)— 二擇一,同時設定會在呼叫端拋 `ArgumentException`。
- 檔名規則:`{baseDir}/{LogPath}/{yyyyMMdd}/{QuotePath}/{Bucket}_{Symbol}_Quote.{ext}` — 不分子目錄。
- Symbol / Bucket sanitize:檔系統非法字元(`/ \ : * ? " < > |`)在檔名中自動替換為 `-`;檔案內容保留原始字串。

**測試**
- 四個新 xUnit 測試檔案,涵蓋自訂時間格式、NDJSON 格式化、Quote schema / 錯誤情境、檔名 sanitize(總計 48 個測試全通過)。
- `OzaLog.Test/Program.cs` 重寫為 v3.1 完整 smoke-test,一次線性執行涵蓋所有公開 API 與錯誤情境,並支援 CLI 引數切換格式(`txt` / `log` / `json`)。

### 功能優化

- `LogItem` 加入 `ThreadName` 欄位,dispatcher 執行緒可渲染呼叫端的執行緒名稱(原本入隊後讀不到)。
- 所有 Quote API 多載統一走 `LOG.Quote(in QuoteRecord)` 集中驗證。錯誤(Symbol / Bucket 為 null 或空、`Extras` 與 `ExtrasJson` 同時設定、`Extras` key 撞名內建欄位)在呼叫端**同步**拋 `ArgumentException`,不會延遲到 dispatcher。
- `LogFormatter` 保留預設 `HH:mm:ss.fff` 格式的 fast path(手寫,零配置);其他格式走 `DateTime.ToString`,FormatException 時 fallback 預設。
- `FileStreamPool` 支援動態副檔名(`.txt` / `.log` / `.json`)並對應更新 size-based 檔案分割的 part 偵測邏輯。

### 技術改進

- `System.Text.Json`:`netstandard2.0` / `netstandard2.1` 從 `8.0.5` 升至 `9.0.16`(`net8.0` / `net9.0` / `net10.0` 仍用 BCL 內建,維持零 NuGet 依賴)。
- `Microsoft.SourceLink.GitHub`:從 `8.0.0` 升至 `10.0.300`(build-only,`PrivateAssets=all`,不影響消費者)。
- 新增內部類別:`JsonLogFormatter`、`QuoteFormatter`、`QuoteFileStreamPool`、`QuoteLogHandler`。Quote pipeline 與主 `AsyncLogHandler` 完全平行,不共享任何 lock 或 stream pool。
- 跨 5 個 TargetFrameworks(`netstandard2.0` / `netstandard2.1` / `net8.0` / `net9.0` / `net10.0`)的建置驗證:0 錯誤。

---

## [3.0.1] - 2026-05-09

> 元資料與 repo 改善版本。**函式庫程式碼無變更** —— OzaLog 組件與 v3.0.0 二進位等同 (Deterministic build)。

### 功能優化
- **NuGet 套件元資料翻新**: `Description` 更精煉 (突出 `LOG.Info_Log("...")` API + HFT pipeline + 零依賴 + 加密貨幣報價串流場景)、更新 `PackageTags` (新增 `ozalog`、`hft`、`high-performance`、`zero-dependency`;移除誤導的 `nlog` tag)、調整 `Title`。
- `PackageReleaseNotes` 改用完整 GitHub URL (NuGet 不解析相對路徑)。

### 技術改進
- **新建專案介紹網站**: Nuxt 4 + @nuxt/content + Tailwind CSS,部署至 GitHub Pages → <https://ozakboy.github.io/OzaLog/>
- **Repo 文件結構重整**: 所有對外文件搬到 `docs/{en,zh-TW}/` 雙語樹 (`changelog.md`、`migration.md`,並含 5 個子頁模板 `getting-started.md` / `configuration.md` / `api.md` / `async-pipeline.md` / `benchmarks.md`)。
- GitHub Actions 在 push 至 main 時自動部署網站。
- 贊助頁新增 USDT (BEP20) 錢包 + Binance Pay QR。
- `uplog` 發佈流程擴充: 現在會自動建立 GitHub Release 並推送到 NuGet.org。

### 備註
- v2.x 升級指南見 [升級指南](./migration.md)。

---

## [3.0.0] - 2026-05-09

### 破壞性變更
- **套件改名**：`Ozakboy.NLOG` → `OzaLog`。NuGet 上原套件標 deprecated 並指向此處。升級指南請見[升級指南](./migration.md)。
- **命名空間改名**：`ozakboy.LOG` → `OzaLog`。使用端程式碼的 `using` 須同步更新。
- **移除 TargetFramework**：砍掉 `.NET Framework 4.6.2`、`net6.0`、`net7.0`（皆 EOL）。現支援 `netstandard2.0`、`netstandard2.1`、`net8.0`、`net9.0`、`net10.0`。
- **Enum 拼字修正**：`LogLevel.CostomName` → `LogLevel.CustomName`。公開方法 `LOG.CustomName_Log(...)` 原本就拼對,僅修正底層 enum 值名稱。

### 新增功能
- HFT 級異步管線:`ConcurrentQueue<struct LogItem>` + 持久化 FileStream 池 + 1ms 快取時間戳 + drop-oldest 壓力控制。
- `LogOptions.EnableGlobalExceptionCapture`(預設 `false`)— 可選訂閱 `AppDomain.UnhandledException` 與 `TaskScheduler.UnobservedTaskException`,以 Fatal 級別同步 + 立即 flush 寫入。
- `LogOptions.MaxOpenFileStreams`(預設 `100`)— LRU 上限;超出時關閉最久未寫入的 stream。
- `LogOptions.DiskFlushIntervalMs`(預設 `100`)— 持久化 FileStream 定期 `Flush()` 落盤間隔。
- `LogOptions.OnDropped`(預設 `null`)— 異步隊列在背壓下丟棄最舊項目時觸發的 callback。
- `OzaLog.Tests/` — xUnit 測試專案,涵蓋並發、LRU、換日、backpressure、GlobalExceptionCapture 切換、格式正確性。
- `OzaLog.Benchmarks/` — BenchmarkDotNet 專案,對 ZLogger / ZeroLog / Serilog 做比較。
- `MIGRATION.md` — 自 `Ozakboy.NLOG` v2.x 的升級指南。

### 功能優化
- `net8.0` / `net9.0` / `net10.0` 零 NuGet 依賴(System.Text.Json 已內建於 BCL)。
- 格式化工作移出呼叫執行緒——呼叫端只入隊原始 `(level, name, message, args, ticks, threadId)`,hot path 不做 `string.Format`。
- 持久化 FileStream 消除每批次開關,syscall 成本趨近於零。
- 換日處理移至 dispatcher 內聯(比對快取 ticks 日期與 stream 日期)。
- 過期 log 清理移至背景定時任務(v2.x 在 hot path 上)。

### 問題修正
- `LOG.cs` 中 `Console.WriteLine(formattedMessage, args)` 雙重格式化 bug(若格式化後訊息恰好含 `{0}` 等 token 會丟 `FormatException`)。
- 自動 flush 級別判定 bug:`Error` 與 `Fatal` 現正確觸發立即 flush,不依賴呼叫端 `immediateFlush` 參數。

### 技術改進
- 內部 `LogItem` 改為 `readonly struct`(原為 class)— hot path 零 GC。
- 新增 `Core/TimestampCache.cs` — 背景定時器每 1ms 更新 `volatile long _currentTicks`,呼叫端只讀。
- 新增 `Core/FileStreamPool.cs` — 以 `(level, name)` 為 key 的持久化 FileStream 池,含 LRU 淘汰。
- 新增 `Core/LogRetentionCleaner.cs` — 背景過期 log 清理,脫離 hot path。
- 新增 `Core/GlobalExceptionCapture.cs` — 可選的全域意外攔截。
- 跨 5 個 TargetFrameworks 的建置驗證:0 警告 / 0 錯誤。

---

## [2.1.0] - 2024

### 新增功能
- 新增 .NET 8.0 支援
- 新增異步日誌寫入機制（含可配置的批次處理）
- 新增不同日誌級別的可自訂目錄結構
- 新增自訂日誌類型支援（`CustomName_Log`）
- 新增主控台輸出開關

### 功能優化
- 強化檔案管理，加入自動分割大檔機制
- 強化異常處理與序列化
- 強化配置系統，提供更多選項與便捷方法
- 改善跨作業系統的檔案路徑處理

### 技術改進
- 改善線程安全與整體效能
- 智慧型檔案大小管理

---

> 早期版本（< 2.1.0）的歷史記錄未完整保留。詳細變更可參考 git 歷史與 NuGet 套件頁面。
