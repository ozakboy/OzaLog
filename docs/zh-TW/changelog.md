---
title: 版本歷史
description: OzaLog 所有重要變更紀錄。
---

# 版本更新記錄

本檔案記錄 **OzaLog**（前身為 **Ozakboy.NLOG**）套件的所有重要變更。
版本號遵循 [語意化版本（SemVer）](https://semver.org/lang/zh-TW/)。

---

## [3.3.0] - 2026-09-14

> 宿主端的生命週期控制:`LOG.Flush()` / `LOG.Shutdown()` 與各自的非同步版本,外加「跑著的時候日誌檔真的打得開」。本版全部是**新增** — 既有簽章沒動、預設值沒動 — 從 3.2.0 升上來不需要改任何程式碼。
>
> 為什麼這件事重要:寫入是非同步的,在此之前想知道「我寫的那筆到底落地了沒」,除了睡一下再賭一把之外沒有別的辦法。接下來可能被強制終止的宿主(容器停止、crash handler、整個程式的 `finally`)沒有屏障可等,而想把 logger 關掉的人也沒有辦法停掉背景工作。

### 新增功能

**`LOG.Flush()` — 真的屏障,不是換個寫法的 sleep**
- `LOG.Flush()` / `LOG.Flush(int timeoutMs)` 擋住呼叫端,直到目前為止寫的每一筆都落到磁碟才回 `true`;逾時(預設 10 秒)或已收尾時回 `false`。
- 呼叫端執行緒會**一起幫忙排空佇列**,而不是乾等 dispatcher 的 `FlushIntervalMs` 週期,所以保證是精確的:寫 1000 筆、呼叫 `Flush()`,它回來的當下檔案就是 1000 行 — 不必補 sleep、不必重讀、不必寫重試迴圈。
- `LOG.FlushAsync(CancellationToken)` / `LOG.FlushAsync(int timeoutMs, CancellationToken)` 在**每個** TFM(含 `netstandard2.0`)都回傳 `Task<bool>`;取消時回 `false`,不擲 `OperationCanceledException`。
- 兩條 pipeline(主 logger 與報價)都會被排空,且都強制 `fsync` — 宿主會呼叫 `Flush` 的理由就是「接下來可能被砍」,只把緩衝交給 OS 是不夠的。

**`LOG.Shutdown()` — 停掉背景工作**
- `LOG.Shutdown()` / `LOG.Shutdown(int timeoutMs)` / `LOG.ShutdownAsync(...)`:先排空落盤,再停掉 dispatcher、定期 flush 計時器與過期清理計時器,最後關閉所有日誌檔。
- **冪等**。第一次回 `true`,之後回 `false` — 那是回報,不是錯誤。與內建的 `ProcessExit` / `UnhandledException` 收尾不論誰先誰後都安全,所以明確呼叫 `Shutdown()` 之後又正常結束行程,不會把收尾跑兩遍,也不會撞上關到一半的 stream。
- **收尾之後的寫入一律靜默丟棄** — 不擲例外,連 Console 都不印。背景服務型的套件不能在自己收尾之後把宿主弄掛,而已經在收尾的宿主也不該為了每一行 log 加防呆。目前狀態可用 `LOG.IsShutdown` 查詢。

**`Shutdown` 之後允許再次 `Configure`**
- 管線還活著時 `LOG.Configure(...)` 維持不可重入(v3.0 起的行為不變 — 它存在的理由是不讓人在執行中途換設定),但**收尾之後現在允許再呼叫**:會重啟管線並解除丟棄狀態。那條限制本來就只在「還有東西在跑」的前提下才有意義。
- 重啟時配置**回到預設值**,避免上一輪的設定殘留成沒人設定過、也沒人看得見的隱藏狀態。

### 問題修正

**執行中的日誌檔,外部工具打不開**
- 檔案以 `FileShare.Read` 開啟,而那個模式只允許**以唯讀方式**開檔的讀取者。`tail`、編輯器與多數日誌檢視工具都是以 `FileAccess.ReadWrite` 開檔,一律吃到共用違規 — 實務上就是*跑著的時候看不到日誌*,而那幾乎是本地檔案 logger 的存在意義。
- 現在改以 `FileShare.ReadWrite` 開啟(主 logger 與報價 pipeline 都是)。寫入端仍維持自己的附加用 handle;這一改只是放寬其他行程能對該檔做什麼。

### 技術改進

- **移除專案檔的 `DocumentationFile` 屬性**。它把五個 TFM 釘死在專案目錄下的同一個 `file.xml`(五個建置搶著寫同一個檔),而且打包出來的檔名是 `lib/<tfm>/file.xml` — IntelliSense 不認這個名字,它只找 `<AssemblyName>.xml`。**也就是說,整套中英雙語 XML 註解在消費者端一直是看不到的。** 只留 `GenerateDocumentationFile` 之後,每個 TFM 各自產生 `OzaLog.xml`,套件現在帶的是 `lib/<tfm>/OzaLog.xml`,IntelliSense 讀得到。repo 裡那份過時的 `file.xml` 一併移除。
- 兩條 pipeline 改用**未完成筆數(pending counter)**追蹤,不再用「佇列空了」推論「寫完了」。佇列空不等於工作做完 — dispatcher 可能剛把一筆取出來、還沒寫進檔案 — 只看佇列的 `Flush` 會提早一筆回來。計數器在入隊**之前**遞增,理由相同。
- dispatcher 的 semaphore 與 `CancellationTokenSource` 改為每次 `Initialize` 重建,收尾之後才能真的重啟;`ProcessExit` / `UnhandledException` 的 handler **每個行程只掛一次**,重啟不會疊出好幾份重複的收尾。
- `FileStreamPool.FlushAll` 與 `QuoteFileStreamPool.FlushAll` 新增 `flushToDisk` 多載(無參數版本行為不變)。定期計時器仍傳 `false` — 把緩衝交給 OS、吞吐優先;`Flush` / `Shutdown` 傳 `true`,強制 `fsync`。
- 新增內部協調器 `LogLifecycle` — 收尾旗標與兩條 pipeline 的先後順序集中在這一處,`LOG` 本身維持薄的門面。
- 新增 xUnit 測試:`LifecycleTests`(連寫 1000 筆後 `Flush` 回來時檔案剛好 1000 行;`Shutdown` 之後的寫入不擲例外且不留痕跡;`Shutdown` 冪等,且之後才觸發的 `ProcessExit` 收尾不互相干擾;`Configure` 在收尾前仍不可重入;重啟後恢復寫入)與 `FileShareTests`(寫入端持有檔案時,外部可唯讀開啟、也可讀寫開啟,且之後寫入端仍能繼續寫)。組件層級關閉跨類別平行測試 — 收尾是行程級的全域狀態,不關的話會把其他測試類別寫到一半掐斷。**81 個測試全綠**(3.2.0 為 73 個)。
- `net8.0` / `net9.0` / `net10.0` 的零 NuGet 相依維持不變;五個 TFM 的建置驗證:0 錯誤、0 警告。

### 已知限制

- **同一毫秒內寫入的多筆,不保證依序落檔。** 時間戳快取的解析度是 1 ms,佇列又是分批排空的;事後需要排序請開 `HighPrecisionTimestamp = true`。這一項不打算改:1 ms 快取正是呼叫端成本壓在幾奈秒的原因。
- **同步模式(`EnableAsyncLogging = false`)仍然沒有過期清理** — `KeepDays` 由背景清理器負責,而它只在非同步管線啟動。此限制自 3.2.0 延續。

---

## [3.2.0] - 2026-09-11

> 三個修正,改的都是**實際落到日誌檔裡的內容**:`Error` / `Fatal` 每筆重複寫入、同步模式(`EnableAsyncLogging = false`)完全寫不出內容、訊息裡的大括號被加倍導致異常 JSON 無法解析。公開 API 簽章與預設配置值完全沒動,所以不是 Major;但因為日誌內容會變,發為 Minor 而非 Patch — Patch 常被自動升級工具直接套用,Minor 才會讓人在升級時多看一眼這份說明。
>
> **升級前先確認三件事**:① 有在數錯誤筆數 → 舊數字偏高;② 有在用同步模式 → 舊日誌是空的;③ 有在解析日誌裡的 JSON → 大括號不再加倍,解析邏輯要跟著調整。

### 問題修正

**`Error` / `Fatal` / `immediateFlush` 每筆寫入兩次**
- `AsyncLogHandler.Enqueue` 會先把項目放進佇列(dispatcher 稍後寫一次),再在呼叫端同步寫一次以確保 crash 前落盤 — 同一筆 log 因此在檔案裡出現**兩行完全相同的內容**。影響 `Error_Log`、`Fatal_Log` 的全部多載,以及任何級別帶 `immediateFlush: true` 的呼叫;`Trace` / `Debug` / `Info` / `Warn` / `CustomName` 的一般呼叫不受影響。
- **請注意:如果你之前用日誌統計錯誤筆數,3.2.0 以前的數字偏高**。倍率不是穩定的兩倍,而是在 1~2 倍之間浮動 — 佇列裡的那份副本在飽和時可能被 drop-oldest 背壓丟掉,所以壓力越大、實際重複率越低。以錯誤率設定的告警閾值需要重新校正。
- 修正後這些項目改為**只走呼叫端同步寫入、不再入隊**,立即落檔的效能特性完全保留(仍是寫入後立刻 `Flush(flushToDisk: true)`)。附帶效果:自動 flush 的項目不再經過佇列,不可能被 drop-oldest 背壓丟棄。

**同步模式(`EnableAsyncLogging = false`)寫出 0 bytes 檔案**
- 同步路徑寫進 `StreamWriter`(`AutoFlush = false`)之後沒有任何人負責 flush:dispatcher、100ms 定期 flush timer、`ProcessExit` 收尾三者都掛在 `AsyncLogHandler.Initialize`,而同步模式從未走到那裡。結果是日誌檔建得出來、程式不報錯,但內容留在緩衝裡隨行程結束**全部消失**。
- **請注意:如果你之前用同步模式,你的日誌檔一直是空的。**
- 修正後同步模式逐筆 flush(`StreamWriter` / `FileStream` 層級,不強制 `fsync`,與非同步模式的定期 flush 行為一致),寫完立刻讀得到;寫入仍由 `FileStreamPool` 的 lock 保護,維持線程安全。同步與非同步模式產生的內容格式完全相同。
- 佇列滿時的背壓自 v3.0 起就是 drop-oldest(不是降級為同步寫),因此不受本 bug 影響。
- 同步模式的 `KeepDays` 過期清理**仍未生效**(清理器掛在非同步管線上)。目前請自行清理舊的日期目錄,或改用非同步模式;這一項需要多開一個背景 Timer,留待下一版處理。

**訊息裡的大括號被加倍,異常 JSON 無法解析**
- 落檔的異常 JSON 長這樣:`{{ "Type": "System.InvalidOperationException", "Data": {{}} }}` — 所有大括號都變兩倍,**下游 parser 直接吃不下**,想拿 Error log 做結構化分析就會卡在這裡。
- 成因:`LOG.Log` 在呼叫端無條件把訊息裡的 `{` `}` 雙倍化,目的是讓 `AppendFormat` 不要把它們當成格式化佔位符。但沒帶 `args` 的路徑走的是 `StringBuilder.Append`(**根本不經過 `AppendFormat`**),雙倍化的括號沒人還原,就這樣進了檔案。異常與物件多載都沒有 `args`,所以每一筆都中。
- **請注意:如果你之前在解析日誌裡的 JSON,大括號不再是加倍的**,解析邏輯(或當初為此寫的 regex / 取代)要跟著調整。
- 修正後訊息裡的大括號**原樣落檔**,不論有沒有帶 `args`。文字模式與 JSON 模式(`OutputFormat = Json`)、同步與非同步模式的輸出一致。
- 順帶修好字面大括號與佔位符混用的情況:`LOG.Info_Log("cfg {\"retry\":3} user {0}", new[] { "alice" })` 以前會因 `FormatException` 退回原字串(`{0}` 沒被代換),現在會正確輸出 `cfg {"retry":3} user alice`。

### 技術改進

- 大括號跳脫從呼叫端移到 formatter,且**只在真的要走 `AppendFormat` 時**(有 `args`)才做。逐字元判斷:`{0}`、`{1,-8}`、`{2:F4}` 這類合法格式項原樣保留,其餘一律跳脫 — 包含索引超出 `args` 範圍的 `{5}`(以前會拋 `FormatException`,現在當字面量輸出)。附帶效果:呼叫端少做一次 `Regex.IsMatch` 與兩次 `string.Replace`,更貼近 v3.0「呼叫端零格式化」的設計。
- `FileStreamPool.Flush` 新增 `flushToDisk` 參數(預設 `true`,既有呼叫行為不變);同步模式以 `false` 呼叫,避免逐筆 `fsync`。
- 新增內部方法 `LogText.WriteSync`(同步模式專用:寫入 + flush);`LogText.Add_LogText`(v2.x 相容入口)一併改走此路徑。
- **移除 `Microsoft.SourceLink.GitHub` 套件參照**。.NET 8 起 SourceLink 已內建於 SDK,只靠 `PublishRepositoryUrl` + `EmbedUntrackedSources` 就能產生完全相同的 nuspec `<repository ... branch=... commit=... />`,五個 TFM(含 `netstandard2.0` / `netstandard2.1`)的 PDB 也都帶有完整的 source link 對應 — 移除前後的 nuspec 逐字元相同。此舉同時清掉其傳遞相依 `Microsoft.Build.Tasks.Git` 的弱點公告(NU1902)。建置期變更,消費者不受影響。
- 補齊 `LogConfiguration` 剩餘 16 個公開成員的 XML 文件註解(中英雙語)。函式庫現在在五個 TFM 上都是**零警告**建置。
- 新增 xUnit 測試:`BraceEscapingTests`(無 `args` / 有 `args` 的大括號行為、文字與 JSON 兩種輸出格式、同步與非同步兩條路徑;異常 JSON 以 `System.Text.Json` **實際 parse** 驗證,而非比對字串)、`DuplicateWriteTests`(Error / Fatal / `immediateFlush` 各只寫一次,其餘級別行為不變)、`SyncModeWriteTests`(同步寫入立即可讀、與非同步模式輸出同一行、多執行緒不掉行)。`AutoFlushLevelTests`(防 `CustomName = 99` 誤中自動 flush)維持通過,共 73 個測試全綠。
- `OzaLog.Test` smoke 程式新增第三個 CLI 引數 `write-mode`(`async` / `sync`,預設 `async`),可實測同步模式輸出。
- 跨 5 個 TargetFrameworks(`netstandard2.0` / `netstandard2.1` / `net8.0` / `net9.0` / `net10.0`)的建置驗證:0 錯誤、0 警告。

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
