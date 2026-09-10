---
title: HFT 非同步架構
description: ConcurrentQueue + 持久化 FileStream 池 + 快取時間戳 + drop-oldest 背壓 — 以及 v3.1+ 獨立的報價 pipeline。
---

# HFT 非同步架構

OzaLog 內含兩條**獨立**的非同步管線:

1. **主 logger pipeline** — 應用程式日誌(`LOG.Info_Log`、`Error_Log`、`CustomName_Log`、…)
2. **報價 pipeline**(v3.1+)— 高頻市場 tick/quote 資料(`LOG.Quote`、`LOG.QuoteTicker`)

兩條管線**不共享 lock、不共享 stream、不共享 dispatcher 執行緒**。只有預設參數調校不同(佇列大小、檔案數上限、批次大小)。

---

## 1. 主 logger pipeline

### 1.1 寫入路徑

```
[呼叫端執行緒]
       │  入隊(struct LogItem,零配置)
       ▼
[ConcurrentQueue<LogItem>]
       │  透過 SemaphoreSlim signal
       ▼
[單一 dispatcher 執行緒]
       │  排出批次 → 格式化 → AppendLine
       ▼
[FileStreamPool: per-(level, name) 持久化 FileStream]
       │  LRU 上限 = LogOptions.MaxOpenFileStreams(預設 100)
       │  換日內聯處理
       │  size-based 分割 → {name}_part2_Log.{ext}
       ▼
{baseDir}/{LogPath}/{yyyyMMdd}/{TypeDirectories.*}/{name}_Log.{ext}
```

### 1.2 呼叫端成本(每次 `LOG.Info_Log(...)`)

- 1× volatile read 讀快取時間戳(~5 ns;`HighPrecisionTimestamp=true` 時 ~30 ns)
- 1× `LogFormatter.EscapeMessage` 跳脫 `{}`(訊息無大括號則跳過)
- 1× struct field copy 建構 `LogItem`
- 1× `ConcurrentQueue.Enqueue`(CAS)
- 1× `SemaphoreSlim.Release`

**呼叫端路徑無 `DateTime.Now`、無 `string.Format`、無 heap 配置**。所有格式化都在 dispatcher 執行緒做。

### 1.3 背壓:drop-oldest

當佇列項目數超過 `AsyncLogOptions.MaxQueueSize`(預設 10000):

1. 最舊的項目 `TryDequeue` 丟掉
2. drop 計數器 atomic 遞增
3. `LogOptions.OnDropped` callback 觸發(如有設定)

這保證佇列永遠不會無限成長 — log 暴增不會造成 OOM,只是犧牲*最舊*的那幾筆。

### 1.4 Immediate flush

`Error` 與 `Fatal` 級別(以及任何 `immediateFlush: true` 的呼叫)**不入隊**,直接在呼叫端執行緒同步寫入 + `FileStream.Flush(flushToDisk: true)`。這保證 crash log 在程式死掉前落盤。

因為完全繞過佇列,這些項目:

- 每筆只寫入一次(v3.1.0 會同時入隊又同步寫,導致每筆出現兩行 — 已於 v3.1.1 修正)
- 不受 drop-oldest 背壓影響,不可能被丟棄
- 寫入順序相對於**同一個 `(level, name)` 檔案**仍是呼叫順序;跨檔案的先後可能與非同步項目輕微錯位(以行內時間戳為準)

> ⚠️ 判斷式必須寫 `level == Error || level == Fatal`,不可寫 `level >= Error` — `LogLevel.CustomName = 99` 會誤中,讓每筆 CustomName 都變成同步 + fsync。

### 1.4.1 同步模式(`EnableAsyncLogging = false`)

設為 `false` 時完全不啟動上面這條管線:沒有佇列、沒有 dispatcher、沒有背壓(因此不會 drop 任何一筆)。呼叫端執行緒直接格式化並寫入 `FileStreamPool`,**每筆寫完立即 flush**(`StreamWriter` / `FileStream` 層級,不強制 `fsync`),所以寫完馬上讀得到。

- 寫入仍由 `FileStreamPool` 的 lock 保護 → 多執行緒安全
- 輸出格式與非同步模式**完全相同**(共用同一個 formatter)
- v3.1.0 的同步模式因為沒有人 flush,內容會全部留在緩衝裡消失(檔案 0 bytes)— 已於 v3.1.1 修正
- 換日與 size 分割照常運作;但**過期清理(`KeepDays`)不會執行** — `LogRetentionCleaner` 隨非同步管線一起啟動,同步模式下需自行清理舊目錄

### 1.5 Disk flush timer

`Timer` 每 `DiskFlushIntervalMs`(預設 100 ms)呼叫一次 `FileStreamPool.FlushAll()`,對所有開啟的 stream 呼叫 `StreamWriter.Flush()`。OS 決定 write-back 時機(不強制 `fsync`)— 偏好吞吐勝過耐久性。

### 1.6 收尾保證

- `AppDomain.CurrentDomain.ProcessExit` → drain + flush + 關閉所有 stream
- `AppDomain.CurrentDomain.UnhandledException` → 同上
- `LOG.Configure` 可訂閱 `EnableGlobalExceptionCapture = true`,在 unhandled exception 與 unobserved Task exception 時額外做 Fatal 級別寫入。

---

## 2. 報價 pipeline (v3.1+)

報價 pipeline 與主 logger **平行**運作 — 相同架構,獨立狀態。

### 2.1 為什麼要分開做?

報價 / tick 資料與應用程式日誌特性根本不同:

| | 主 logger | 報價 pipeline |
|---|---|---|
| 吞吐量 | ~10–1000 筆/秒 | ~10,000–1,000,000 筆/秒 |
| 資料型態 | 自由文字 | 結構化(Symbol、Bid、Ask、…) |
| 預設 `MaxQueueSize` | 10,000 | 50,000 |
| 預設 `MaxOpenStreams` | 100 | 500 |
| 預設 `MaxBatchSize` | 100 | 500 |
| 嚴重性級別 | 有(Trace … Fatal) | 無 — 所有筆數等價 |
| Immediate flush | Error/Fatal 觸發 | 無 — 純非同步批次 |

若放同一個 dispatcher 會出現:
- 報價暴增(~1M/秒)塞爆佇列 → drop 掉 Error/Fatal 應用程式 log
- 應用程式 log immediate-flush 時,報價 dispatcher 延遲拉長

分開做就根本沒這種衝突。

### 2.2 寫入路徑

```
[呼叫端執行緒 — WebSocket consumer、REST poller 等]
       │  驗證(Symbol/Bucket 非空、Extras key 撞名、…)
       │  入隊(struct QuoteRecord,零配置)
       ▼
[ConcurrentQueue<QuoteRecord>]                ← QuoteOptions.MaxQueueSize
       │  透過 SemaphoreSlim signal
       ▼
[獨立 dispatcher 執行緒]
       │  排出批次 → QuoteFormatter.Format → AppendLine
       ▼
[QuoteFileStreamPool: per-(bucket, symbol) 持久化 FileStream]
       │  LRU 上限 = QuoteOptions.MaxOpenStreams(預設 500)
       │  檔系統非法字元自動 sanitize
       ▼
{baseDir}/{LogPath}/{yyyyMMdd}/{QuotePath}/{Bucket}_{Symbol}_Quote.{ext}
```

### 2.3 呼叫端的同步驗證

`LOG.Quote(...)` 在入隊**之前**驗證 record:

- `Symbol` 為 null 或空 → 在呼叫端拋 `ArgumentException`
- `Bucket` 為 null 或空 → `ArgumentException`
- 同時設定 `Extras` 與 `ExtrasJson` → `ArgumentException`
- `Extras` 含保留 key(`bid`、`ask`、`last`、…)→ `ArgumentException`

這讓呼叫端可以用 `try`/`catch` 攔截 programmer error。驗證失敗**絕不**會被靜默吞進 dispatcher。

### 2.4 背壓:drop-oldest 加上批次 callback

drop-oldest 策略與主 logger 相同,但 `OnDropped` callback 簽章不同:

```csharp
// 主 logger
public Action OnDropped { get; set; }              // 每次 drop 觸發一次

// 報價 pipeline
public Action<long> OnDropped { get; set; }        // 批次:參數 = 距上次 callback 新丟棄的數量
```

報價的 callback 收到的是**自上次 callback 以來新丟棄的筆數**(非累計),讓你可以在劇烈暴增時做高效的 metric 上報,不必每筆都呼叫 callback。

### 2.5 收尾

跟主 logger 一樣 hook `ProcessExit` — 報價 pipeline 獨立 flush 並關閉自己的 stream。兩條 pipeline **平行** flush(無順序耦合),總收尾時間取兩者較慢的那條。

---

## 3. 為何不用 thread-pool dispatcher?

兩條 pipeline 各自用一個獨立 `Task.Run(...)` dispatcher,**不**走 thread-pool worker。理由:

- **可預測的延遲**:獨立執行緒不會被使用者程式碼搶占。
- **無鎖 FileStreamPool 存取**:只有一個執行緒寫入 pool,所以 `FileStream` 狀態在正常寫入路徑無需 lock(只在 shutdown / disk-flush timer / immediate-flush 交織時用 lock)。
- **快取局部性**:dispatcher 執行緒讓自己的 `FileStreamPool` slot、`StreamWriter` buffer、dictionary 保持在 CPU cache 熱區。

代價:每個 `(level, name)`(或 `(bucket, symbol)`)的寫入順序有保證,但**跨 key 的寫入順序可能輕微錯位**(不同 key 可能被批次分組 flush)。這在 HFT tick 重建是可接受的 — 記錄裡的時間戳才是事實來源,不是檔案順序。

---

## 4. TimestampCache

背景 `Timer` 每 1 ms 呼叫 `DateTime.Now.Ticks` 更新 `volatile long _currentTicks`。呼叫端做一次 atomic read(~5 ns)而非每次 log 都付 `DateTime.Now` syscall 成本(~80 ns on Windows,因為 `GetSystemTimeAsFileTime` + 時區轉換)。

**1 ms 精度上限**:若 `TimeFormat` 用了比 `.fff` 更精細的精度(例如 `.ffffff` 取 µs),最後幾位數字永遠是 `0000`,除非你 opt-in `HighPrecisionTimestamp = true`。

### 4.1 HighPrecisionTimestamp 模式 (v3.1+)

啟用後,cache 在每 1 ms 更新時同時記錄 `Stopwatch.GetTimestamp()`。讀取時,呼叫端計算:

```
actualTicks = cachedTicks + (Stopwatch.GetTimestamp() - cachedSwTimestamp) * (TimeSpan.TicksPerSecond / Stopwatch.Frequency)
```

這從 1 ms cache 重建出 sub-millisecond 精度,不必付 `DateTime.Now` 成本。呼叫端 read 從 ~5 ns 增加到 ~30 ns。只在你需要 µs 級時間戳做 latency 分析或 tick-level 時序時才啟用。

---

## 5. 內容來源

- [`OzaLog/OzaLog/Core/AsyncLogHandler.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/Core/AsyncLogHandler.cs) — 主 pipeline dispatcher
- [`OzaLog/OzaLog/Core/FileStreamPool.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/Core/FileStreamPool.cs) — 主 pipeline FileStream + LRU
- [`OzaLog/OzaLog/Core/QuoteLogHandler.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/Core/QuoteLogHandler.cs) — 報價 pipeline dispatcher
- [`OzaLog/OzaLog/Core/QuoteFileStreamPool.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/Core/QuoteFileStreamPool.cs) — 報價 pipeline FileStream
- [`OzaLog/OzaLog/Core/TimestampCache.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/Core/TimestampCache.cs) — 快取時間戳
