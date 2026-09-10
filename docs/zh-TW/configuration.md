---
title: 配置選項
description: OzaLog v3.1 完整配置參考 — LogOptions、AsyncLogOptions、QuoteOptions(透過 LOG.Configure() 設定)。
---

# 配置選項

所有配置都在程式啟動時透過 `LOG.Configure(...)` **一次性**設定。此呼叫**不可重入** — 第二次呼叫拋 `InvalidOperationException`。若完全省略,首次寫 log 會自動以預設值初始化。

```csharp
using OzaLog;

LOG.Configure(o =>
{
    o.KeepDays = -7;
    o.SetFileSizeInMB(50);
    o.OutputFormat = LogOutputFormat.Json;
    o.ConfigureQuote(q => q.Enable = true);
});
```

---

## 1. `LogOptions` — 主 logger

### 1.1 檔案輸出

| 選項 | 型別 | 預設 | 說明 |
|---|---|---|---|
| `KeepDays` | `int` | `-3` | **必須是負數**。保留幾天份的 log。超過的日期子目錄會被背景 `LogRetentionCleaner` 每 60 秒掃一次刪除。 |
| `MaxFileSize` | `long` | `50 * 1024 * 1024` (50 MB) | 檔案 size-based 分割閾值。超過後下一筆寫到 `{name}_part2_Log.{ext}`。 |
| `SetFileSizeInMB(int megabytes)` | method | — | `MaxFileSize` 的便利 setter。 |
| `LogPath` | `string` | `"logs"` | 位於 `AppDomain.CurrentDomain.BaseDirectory` 之下的根目錄。完整路徑為 `{baseDir}/{LogPath}/{yyyyMMdd}/{TypeDirectories.*}/{name}_Log.{ext}`。 |
| `TypeDirectories` | `LogTypeDirectories` | 見 §1.5 | 各 LogLevel 對應的子目錄設定。 |

### 1.2 輸出格式 (v3.1+)

| 選項 | 型別 | 預設 | 說明 |
|---|---|---|---|
| `OutputFormat` | `LogOutputFormat` | `Txt` | 全域選擇:`Txt` / `Log`(內容相同,副檔名不同) / `Json`(NDJSON)。詳見 [API §4](./api.md#4-logoutputformat-enum-v31)。 |
| `TimeFormat` | `string` | `"HH:mm:ss.fff"` | 自由格式的 .NET `DateTime` 字串,用於 `Txt`/`Log` 模式的訊息前綴。**`Json` 模式下不適用**(JSON 一律走 epoch_ms)。無效格式自動 fallback 為預設。 |
| `ShowThreadId` | `bool` | `true` | 切換 `Txt`/`Log` 中的 `[T:tid]` 前綴;切換 `Json` 中的 `tid` 欄位。 |
| `ShowThreadName` | `bool` | `false` | 切換 thread name 顯示。若呼叫端執行緒無 `Thread.Name`,整個 thread 區段一律省略。在 `Txt`/`Log` 模式下,若 `ShowThreadId` 與 `ShowThreadName` 都為 `true`,輸出組合為 `[T:tid/Name]`。 |
| `HighPrecisionTimestamp` | `bool` | `false` | Opt-in `Stopwatch`-hybrid 模式,提供 µs 級精度。呼叫端 ticks 讀取從 ~5 ns 增加到 ~30 ns。只有當你的 `TimeFormat` 用了比 `.fff` 更精細的精度時才有意義。 |

### 1.3 非同步管線行為

| 選項 | 型別 | 預設 | 說明 |
|---|---|---|---|
| `EnableAsyncLogging` | `bool` | `true` | 若 `false`,呼叫端執行緒直接格式化、寫入並立即 flush — 無佇列、無 dispatcher、無背壓(不會丟任何一筆),呼叫返回後內容立刻讀得到。HFT 場景較慢但邏輯較直觀;仍為線程安全,輸出格式與非同步模式完全相同。注意此模式下過期清理(`KeepDays`)不會執行。v3.1.1 修正 — 舊版本此路徑從未 flush,會寫出空檔案。 |
| `EnableConsoleOutput` | `bool` | `true` | 若 `true`,每筆 log 也會在呼叫端執行緒 `Console.WriteLine` 一次。 |
| `MaxOpenFileStreams` | `int` | `100`(範圍 `[4, 4096]`) | 持久化 `FileStreamPool` 的 LRU 上限。超過時關閉最久未寫入的 stream。 |
| `DiskFlushIntervalMs` | `int` | `100`(範圍 `[10, 10000]`) | `FileStreamPool.FlushAll()` 的呼叫週期 — 緩衝寫入落盤(但不做 `fsync`,由 OS 決定 write-back 時機)。 |
| `OnDropped` | `Action` | `null` | 佇列滿並 drop oldest 時觸發**每筆一次**的 callback。Body 必須極輕量(在 dispatcher 執行緒呼叫)。 |

### 1.4 全域意外攔截(opt-in)

| 選項 | 型別 | 預設 | 說明 |
|---|---|---|---|
| `EnableGlobalExceptionCapture` | `bool` | `false` | 若 `true`,訂閱 `AppDomain.UnhandledException` 與 `TaskScheduler.UnobservedTaskException`,以 Fatal 級別同步 + immediate flush 寫入。預設關閉以避免與宿主應用既有 handler 衝突。 |

> **不會**攔截 WPF `DispatcherUnhandledException`、WinForms `ThreadException`、ASP.NET Core middleware 例外 — 函式庫層級無法存取這些 framework 物件。宿主應用需自行 hook。

### 1.5 `LogTypeDirectories` — 各級別子目錄

```csharp
o.TypeDirectories.DirectoryPath = "LogFiles";   // 級別專屬路徑為 null 時的預設
o.TypeDirectories.ErrorPath     = "ErrorLogs";  // Error 級別走獨立資料夾
o.TypeDirectories.FatalPath     = "FatalLogs";
// 另有 TracePath / DebugPath / InfoPath / WarnPath / CustomPath
```

級別專屬路徑為 `null` 時,fallback 為 `DirectoryPath`。

### 1.6 `AsyncLogOptions` — dispatcher 調校

透過 `o.ConfigureAsync(a => { ... })` 設定。

| 選項 | 型別 | 預設 | 範圍 | 說明 |
|---|---|---|---|---|
| `MaxBatchSize` | `int` | `100` | `[1, 1000]` | dispatcher 每次喚醒最多排出的項目數。 |
| `MaxQueueSize` | `int` | `10000` | `[1000, 100000]` | 佇列容量。超過時 drop oldest + 觸發 `OnDropped` callback。 |
| `FlushIntervalMs` | `int` | `1000` | `[10, 10000]` | dispatcher 喚醒間隔。沒有 signal 進來時,semaphore wait 等到此間隔逾時。 |

---

## 2. `QuoteOptions` — 報價 pipeline (v3.1+)

透過 `o.ConfigureQuote(q => { ... })` 設定。**預設 `Enable = false`** — 報價 pipeline 必須明確 opt-in。

```csharp
LOG.Configure(o =>
{
    o.ConfigureQuote(q =>
    {
        q.Enable = true;
        q.OutputFormat = QuoteOutputFormat.Json;
        q.QuotePath = "Quotes";
        q.MaxOpenStreams = 500;
        q.MaxQueueSize = 50_000;
        q.MaxBatchSize = 500;
        q.FlushIntervalMs = 100;
        q.OnDropped = droppedCount => MyMetrics.RecordDrops(droppedCount);
    });
});
```

| 選項 | 型別 | 預設 | 範圍 | 說明 |
|---|---|---|---|---|
| `Enable` | `bool` | `false` | — | **必填**。若 `false`,所有 `LOG.Quote(...)` 呼叫都靜默 no-op,也不啟動背景執行緒。 |
| `OutputFormat` | `QuoteOutputFormat` | `Txt` | — | `Txt` / `Log` / `Json`。獨立於主 logger 的 `OutputFormat`。 |
| `QuotePath` | `string` | `"Quotes"` | — | 報價檔案在 `{LogPath}/{yyyyMMdd}/` 下的子目錄。 |
| `MaxOpenStreams` | `int` | `500` | `[4, 4096]` | 獨立 `QuoteFileStreamPool` 的 LRU 上限。比主 logger 高,因為加密貨幣/股票的 symbol 數量動輒上千。 |
| `MaxQueueSize` | `int` | `50000` | `[1000, 1_000_000]` | 報價佇列容量。Drop-oldest on overflow。比主 logger 高,因為報價吞吐量通常是普通 log 的 100×–1000×。 |
| `MaxBatchSize` | `int` | `500` | `[1, 10000]` | dispatcher 每次喚醒最多排出的記錄數。 |
| `FlushIntervalMs` | `int` | `100` | `[10, 10000]` | dispatcher 喚醒間隔。 |
| `OnDropped` | `Action<long>` | `null` | — | drop 發生時觸發。參數是**自上次 callback 以來新丟棄的筆數**(非累計)。 |

> 報價 pipeline 沿用主 logger 的 `KeepDays` 與 `MaxFileSize` 進行保留與檔案分割。報價檔案會被同一個 `LogRetentionCleaner` 清理。

---

## 3. 透過 `LOG.GetCurrentOptions()` 讀回

`Configure` 後可透過唯讀視圖檢視執行中設定:

```csharp
var current = LOG.GetCurrentOptions();

// 主 logger
Console.WriteLine(current.OutputFormat);                  // Json
Console.WriteLine(current.TimeFormat);                    // "HH:mm:ss.fff"
Console.WriteLine(current.ShowThreadId);                  // true
Console.WriteLine(current.ShowThreadName);                // false
Console.WriteLine(current.HighPrecisionTimestamp);        // false
Console.WriteLine(current.MaxOpenFileStreams);            // 100
Console.WriteLine(current.AsyncOptions.MaxQueueSize);     // 10000

// 報價 pipeline
Console.WriteLine(current.QuoteOptions.Enable);           // true
Console.WriteLine(current.QuoteOptions.OutputFormat);     // Json
Console.WriteLine(current.QuoteOptions.QuotePath);        // "Quotes"
Console.WriteLine(current.QuoteOptions.MaxOpenStreams);   // 500
```

所有讀回的值都包在 `ReadOnlyLogOptions` / `ReadOnlyQuoteOptions` 內 — 消費者無法透過此視圖修改執行中設定。

---

## 4. 預設值總表(未呼叫 `LOG.Configure(...)` 時)

| 類別 | 設定 | 預設 |
|---|---|---|
| 檔案 | `KeepDays` | `-3` |
| 檔案 | `MaxFileSize` | 50 MB |
| 檔案 | `LogPath` | `"logs"` |
| 檔案 | `TypeDirectories.DirectoryPath` | `"LogFiles"` |
| 格式 | `OutputFormat` | `Txt` |
| 格式 | `TimeFormat` | `"HH:mm:ss.fff"` |
| 格式 | `ShowThreadId` | `true` |
| 格式 | `ShowThreadName` | `false` |
| 格式 | `HighPrecisionTimestamp` | `false` |
| 異步 | `EnableAsyncLogging` | `true` |
| 異步 | `EnableConsoleOutput` | `true` |
| 異步 | `MaxOpenFileStreams` | `100` |
| 異步 | `DiskFlushIntervalMs` | `100` |
| 異步 | `AsyncOptions.MaxBatchSize` | `100` |
| 異步 | `AsyncOptions.MaxQueueSize` | `10000` |
| 異步 | `AsyncOptions.FlushIntervalMs` | `1000` |
| 報價 | `QuoteOptions.Enable` | **`false`(opt-in)** |
| 報價 | `QuoteOptions.OutputFormat` | `Txt` |
| 報價 | `QuoteOptions.QuotePath` | `"Quotes"` |
| 報價 | `QuoteOptions.MaxOpenStreams` | `500` |
| 報價 | `QuoteOptions.MaxQueueSize` | `50000` |
| 其他 | `EnableGlobalExceptionCapture` | `false` |

→ 一頁式 API 摘要見 [API 參考](./api.md)。
