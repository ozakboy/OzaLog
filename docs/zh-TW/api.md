---
title: API 參考
description: OzaLog v3.1 完整公開 API — LOG 靜態類別、LogOptions、QuoteRecord、各 enum。
---

# API 參考

> 內容來源以 [`OzaLog/OzaLog/LOG.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/LOG.cs) 為準,自動生成的 XML doc 隨 NuGet 套件以 `file.xml` 形式發佈。
>
> 所有公開型別都在 `OzaLog` 命名空間。

---

## 1. `LOG` 靜態類別

所有日誌寫入的唯一入口。不必實例化、不必 `LoggerFactory`、不必依賴注入。

```csharp
using OzaLog;

LOG.Info_Log("Hello, OzaLog!");
```

### 1.1 各 LogLevel 寫入方法

每個 `LogLevel`(Trace / Debug / Info / Warn / Error / Fatal)各提供 5 個多載。命名慣例:`<Level>_Log`。

```csharp
// 純訊息
LOG.Info_Log(string message);

// 控制是否寫檔(true=寫檔,false=只在 EnableConsoleOutput=true 時走主控台)
LOG.Info_Log(string message, bool writeTxt);

// 含 {0}/{1}/... placeholder 的格式化訊息
LOG.Info_Log(string message, string[] args, bool writeTxt = true, bool immediateFlush = false);

// 物件 — 自動序列化為 JSON
LOG.Info_Log<T>(T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;

// 物件 + 標頭訊息
LOG.Info_Log<T>(string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;
```

**把 `Info` 換成 `Trace`、`Debug`、`Warn`、`Error`、`Fatal` 即得對應級別方法。**

#### 自動立即 flush

`Error_Log` 與 `Fatal_Log` **永遠**會觸發同步 immediate flush,不管使用者傳入的 `immediateFlush` 參數。這是確保程式 crash 前 log 已落盤的關鍵機制。其他級別則尊重 `immediateFlush` 參數(預設 `false`)。

走這條路徑的項目**不入隊**,由呼叫端執行緒寫入,因此每筆只會出現一行,也不會被佇列滿時的 drop-oldest 丟棄。(v3.1.0 會同時入隊與同步寫入,導致每筆重複兩行 — v3.2.0 已修正。)

#### 物件多載的行為

- 當 `obj is Exception` 且 `level >= Warn` 時,物件透過 `ExceptionHandler.CreateSerializableException(...)` 展開,遞迴包含 `InnerException`、`Data` 字典、`StackTrace`、與反射出的非標準屬性。
- 其他情況直接用 `System.Text.Json` 序列化:`WriteIndented=false`、`DefaultIgnoreCondition=WhenWritingNull`、`Encoder=UnsafeRelaxedJsonEscaping`。

### 1.2 `CustomName_Log` — 自訂檔名分桶

把 log 路由到自訂檔名,而非級別預設檔。對交易場景特別有用(`BTC_Log.txt`、`ETH_Log.txt` 各自一檔)。

```csharp
LOG.CustomName_Log(string name, string message);
LOG.CustomName_Log(string name, string message, bool writeTxt);
LOG.CustomName_Log(string name, string message, string[] args, bool writeTxt = true, bool immediateFlush = false);
LOG.CustomName_Log<T>(string name, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;
LOG.CustomName_Log<T>(string name, string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;
```

→ 檔案路徑:`{baseDir}/{LogPath}/{yyyyMMdd}/{CustomPath}/{name}_Log.{ext}`

### 1.3 `LOG.Configure(...)` — 一次性配置

```csharp
public static void Configure(Action<LogConfiguration.LogOptions> configure);
```

**不可重入**。第二次呼叫拋 `InvalidOperationException("OzaLog 已初始化（Configure 不可重入）")`。若從未呼叫 `Configure`,首次寫 log 會自動以預設值初始化。

> v3.3.0 起有一個例外:`LOG.Shutdown()`(§1.5) 之後管線已經不在跑,`Configure` 因此再次被允許,並會重啟管線 — 配置**回到預設值**,不是上一輪的設定。

### 1.4 `LOG.GetCurrentOptions()` — 唯讀配置視圖

```csharp
public static LogConfiguration.ILogOptions GetCurrentOptions();
```

回傳一個包裝目前 `LogOptions` 的唯讀介面。用於診斷與驗證執行中設定。

### 1.5 生命週期 — `Flush` / `Shutdown` (v3.3+)

```csharp
public static bool IsShutdown { get; }

public static bool Flush();
public static bool Flush(int timeoutMs);
public static Task<bool> FlushAsync(CancellationToken cancellationToken = default);
public static Task<bool> FlushAsync(int timeoutMs, CancellationToken cancellationToken = default);

public static bool Shutdown();
public static bool Shutdown(int timeoutMs);
public static Task<bool> ShutdownAsync(CancellationToken cancellationToken = default);
public static Task<bool> ShutdownAsync(int timeoutMs, CancellationToken cancellationToken = default);
```

| 成員 | 回傳 | 意義 |
| --- | --- | --- |
| `Flush` / `FlushAsync` | `true` | 兩條 pipeline 都已排空,檔案已落盤(`fsync`) |
| | `false` | 逾時(預設 10 秒)、被取消,或已收尾 — 已寫入的部分仍會盡力 flush |
| `Shutdown` / `ShutdownAsync` | `true` | 本次呼叫真的執行了收尾 |
| | `false` | 先前已收尾 — 冪等,不是錯誤 |
| `IsShutdown` | `bool` | 目前是否處於「寫入會被丟棄」的狀態 |

**`Flush` 是精確的屏障**。呼叫端執行緒會一起幫忙排空,而不是乾等 dispatcher 的 `FlushIntervalMs` 週期,所以呼叫前寫的 1000 筆,在它回來的當下就是檔案裡的 1000 行。主 logger 與報價 pipeline 都涵蓋。

**`Shutdown` 會停掉背景工作**:先排空落盤,再停掉 dispatcher、定期 flush 計時器與過期清理計時器,並關閉所有日誌檔。之後**所有寫入一律靜默丟棄**(不擲例外、不印 Console),而 `Configure` 變成可以再次呼叫 — 那會以**預設值**重啟管線(見 §1.3)。與內建的 `ProcessExit` / `UnhandledException` 收尾不論誰先誰後都安全。

**以上方法在任何路徑都不擲例外**,包含被取消的 `CancellationToken`(回 `false`,不擲 `OperationCanceledException`)。回傳 `Task` 的多載在每個 TFM 都有,含 `netstandard2.0`。

```csharp
LOG.Info_Log("about to snapshot");
LOG.Flush();                         // 上面那行此刻已在磁碟上

// 行程收尾
await LOG.ShutdownAsync();
LOG.Info_Log("被忽略,不會擲例外");
```

---

## 2. `LOG.Quote(...)` — 報價 pipeline (v3.1+)

報價 pipeline 是**獨立**的非同步寫入管道,專為高頻 tick/quote 資料設計,與主 logger 完全分離。欄位命名對齊 Binance REST API 24hr Ticker schema。

> **前置條件**:必須在配置階段啟用 pipeline:
> ```csharp
> LOG.Configure(o => o.ConfigureQuote(q => q.Enable = true));
> ```
> 若沒有設 `Enable = true`,所有 `LOG.Quote(...)` 呼叫都靜默 no-op(也不會啟動背景執行緒)。

### 2.1 A2 核心 API — struct 多載

```csharp
public static void Quote(in QuoteRecord record);
```

零配置入隊。在呼叫端**同步**驗證 `record`,以下情況拋 `ArgumentException`:

- `Symbol` 或 `Bucket` 為 null 或空字串
- `Extras` 與 `ExtrasJson` 同時設定
- `Extras` 含有撞名內建欄位的 key(見 [§2.4](#24-extras-的保留-key))

### 2.2 A1 便利多載

內部建構 `QuoteRecord` 後委派給 struct 多載。

```csharp
// 最簡 tick — 只填 last
LOG.Quote(string symbol, string bucket, long ticks, decimal last);

// 含 bid/ask
LOG.Quote(string symbol, string bucket, long ticks,
          decimal last, decimal bid, decimal ask);

// 含 bid/ask + 買賣量
LOG.Quote(string symbol, string bucket, long ticks,
          decimal last,
          decimal bid, decimal bidQty,
          decimal ask, decimal askQty);

// 完整 ticker — 對齊 Binance REST API /api/v3/ticker/24hr
LOG.QuoteTicker(string symbol, string bucket, long ticks,
                decimal last,
                decimal? lastQty = null,
                decimal? bid = null, decimal? bidQty = null,
                decimal? ask = null, decimal? askQty = null,
                decimal? open = null, decimal? prevClose = null,
                decimal? high = null, decimal? low = null,
                decimal? volume = null, decimal? quoteVolume = null);

// 完整 ticker + Extras 字典
LOG.QuoteTicker(string symbol, string bucket, long ticks,
                decimal last,
                IReadOnlyDictionary<string, object> extras,
                /* 其餘選填欄位同上 */);
```

### 2.3 `QuoteRecord`(公開 `readonly struct`)

```csharp
public readonly struct QuoteRecord
{
    // 必填
    public readonly string Symbol;     // 例如 "BTCUSDT"
    public readonly string Bucket;     // 例如 "binance_spot"
    public readonly long   Ticks;      // 事件時間,caller 傳入
    public readonly decimal Last;      // 最後成交價

    // 選填(全部 decimal?)
    public readonly decimal? LastQty;       // 最後一筆成交數量
    public readonly decimal? Bid, BidQty;   // 最佳買價 + 量
    public readonly decimal? Ask, AskQty;   // 最佳賣價 + 量
    public readonly decimal? Open, PrevClose;
    public readonly decimal? High, Low;
    public readonly decimal? Volume;        // 累積基礎資產成交量
    public readonly decimal? QuoteVolume;   // 累積計價資產成交量

    // 自訂欄位 — 二擇一,同時設定拋例外
    public readonly IReadOnlyDictionary<string, object>? Extras;
    public readonly string? ExtrasJson;     // 預序列化的 JSON 物件字串
}
```

**欄位對照 Binance `/api/v3/ticker/24hr` 回應**:

| `QuoteRecord` | Binance JSON | 說明 |
|---|---|---|
| `Last` | `lastPrice` | 必填 |
| `LastQty` | `lastQty` | 最後一筆成交數量 |
| `Bid` / `BidQty` | `bidPrice` / `bidQty` | 最佳買單 |
| `Ask` / `AskQty` | `askPrice` / `askQty` | 最佳賣單 |
| `Open` / `PrevClose` / `High` / `Low` | `openPrice` / `prevClosePrice` / `highPrice` / `lowPrice` | 區間統計 |
| `Volume` | `volume` | 24h 基礎資產量 |
| `QuoteVolume` | `quoteVolume` | 24h 計價資產量 |

### 2.4 Extras 的保留 key

下列 key 是 schema 內建的保留欄位。放在 `Extras`(Dictionary)會在呼叫端**同步**拋 `ArgumentException`;放在 `ExtrasJson`(字串)則在 dispatcher 端拋(記錄到主控台,該筆記錄被丟棄)。

> `ts`、`symbol`、`bucket`、`last`、`lastQty`、`bid`、`bidQty`、`ask`、`askQty`、`open`、`prevClose`、`high`、`low`、`volume`、`quoteVolume`、`extras`

### 2.5 檔名規則

```
{baseDir}/{LogPath}/{yyyyMMdd}/{QuotePath}/{Bucket}_{Symbol}_Quote.{ext}
```

- **不分子目錄**:`Bucket` 變成檔名前綴,不會建立子資料夾。
- **自動 sanitize**:`Symbol` / `Bucket` 中的檔系統非法字元(`/ \ : * ? " < > |`)**僅在檔名中**會被替換為 `-`。原始字串仍會出現在檔案內容裡。
- 換日、LRU 淘汰、size-based 檔案分割(`_part2_Quote.{ext}` 等)行為跟主 logger 一致,但走獨立的 `QuoteFileStreamPool`,互不影響。

---

## 3. `LogLevel` enum

```csharp
public enum LogLevel
{
    Trace      = 0,
    Debug      = 1,
    Info       = 2,
    Warn       = 3,
    Error      = 4,
    Fatal      = 5,
    CustomName = 99,   // LOG.CustomName_Log(...) 內部使用
}
```

> v3.0 把舊的 `CostomName` 改名為 `CustomName`(拼字修正,屬於 breaking change)。`LOG.CustomName_Log(...)` 方法名稱原本就拼對。

---

## 4. `LogOutputFormat` enum (v3.1+)

決定**主 logger** 的輸出格式。透過 `LogOptions.OutputFormat` 設定。

```csharp
public enum LogOutputFormat
{
    Txt  = 0,   // 純文字,副檔名 .txt(預設)
    Log  = 1,   // 內容同 Txt,副檔名 .log
    Json = 2,   // NDJSON(每行一個 JSON 物件),副檔名 .json
}
```

### 4.1 Json 格式 schema(NDJSON)

```json
{"ts":1715587425123,"lv":"Info","nm":"","tid":12,"tn":"MainThread","msg":"hello","data":{...}}
```

| 欄位 | 型別 | 是否一定存在 | 含義 |
|---|---|---|---|
| `ts` | `long`(epoch_ms) | 是 | 事件時間戳,Unix epoch 後的毫秒數 |
| `lv` | `string` | 是 | `"Trace"` / `"Debug"` / `"Info"` / `"Warn"` / `"Error"` / `"Fatal"` / `"CustomName"` |
| `nm` | `string` | 是 | log 名稱(CustomName 的值;級別 log 則為空字串) |
| `tid` | `int` | 僅當 `ShowThreadId=true` 時 | 呼叫端執行緒的 `ManagedThreadId` |
| `tn` | `string` | 僅當 `ShowThreadName=true` 且 `Thread.Name != null` 時 | 呼叫端執行緒的 `Thread.Name` |
| `msg` | `string` | 是 | 訊息文字(即使為空字串也會輸出) |
| `data` | object | 僅當存在時 | 物件多載的 payload 解析後的 JSON(Exception 或任意物件) |

---

## 5. `QuoteOutputFormat` enum (v3.1+)

決定**報價 pipeline** 的輸出格式。透過 `LogOptions.QuoteOptions.OutputFormat` 設定。

```csharp
public enum QuoteOutputFormat
{
    Txt  = 0,   // 人類可讀 key=value,副檔名 .txt(預設)
    Log  = 1,   // 內容同 Txt,副檔名 .log
    Json = 2,   // NDJSON,副檔名 .json
}
```

### 5.1 Txt / Log 格式

```
[2026-05-13 10:23:45.123] binance_spot BTCUSDT last=60123.5 bid=60123.0 ask=60124.0 bidQty=0.5 askQty=1.2
```

- ISO 8601 時間戳前綴(給人類看)
- null 的選填欄位略過(行長變動)
- `Extras` 字典項目展開成更多 `k=v`

### 5.2 Json 格式(NDJSON)

```json
{"ts":1715587425123,"symbol":"BTCUSDT","bucket":"binance_spot","last":60123.5,"bid":60123.0,"ask":60124.0,"extras":{"funding":0.0001}}
```

- `ts` = epoch_ms(與主 logger 一致)
- 只輸出非 null 欄位
- `Extras` nested 在 `"extras"` 子物件(不平攤到 top level) — 保持乾淨的 schema 邊界
- Quote NDJSON **不含** `tid` / `tn`(報價是市場事件,非程式內部事件)

---

## 6. 唯讀配置視圖

```csharp
LogConfiguration.ILogOptions current = LOG.GetCurrentOptions();
Console.WriteLine(current.OutputFormat);             // LogOutputFormat
Console.WriteLine(current.TimeFormat);               // "HH:mm:ss.fff" 等
Console.WriteLine(current.HighPrecisionTimestamp);   // bool
Console.WriteLine(current.QuoteOptions.Enable);      // bool
```

完整屬性列表見[配置選項](./configuration.md)。

---

## 7. 例外序列化(`SerializableExceptionInfo`)

當你以 `Warn` 級別或以上呼叫 `Warn_Log<T>(ex)` / `Error_Log<T>(ex)` / `Fatal_Log<T>(ex)` 並傳入 `Exception` 物件時,runtime 會展開為:

```csharp
class SerializableExceptionInfo
{
    string Type;          // ex.GetType().FullName
    string Message;
    string Source;
    string HelpLink;
    string StackTrace;
    Dictionary<string, string> Data;             // 從 ex.Data 展開
    SerializableExceptionInfo InnerException;    // 遞迴
    Dictionary<string, string> AdditionalProperties;  // 反射撈的非標準屬性
}
```

結果以 JSON 序列化寫入 log 行(或在 Json 輸出模式下放進 `data` 欄位)。

v3.2.0 起序列化後的 JSON **原樣落檔**,可直接餵給 parser:

```text
09:07:05.123[T:12]
{
  "Type": "System.InvalidOperationException",
  "Message": "order book is empty",
  "Data": {},
  "AdditionalProperties": { "HResult": "-2146233079" }
}
```

v3.1.0 以前所有大括號都會被加倍(`{{ "Type": ... "Data": {{}} }}`),導致內容無法解析。若你當初為此寫了解析邏輯,升級時要跟著調整。

---

## 8. 版本相容性說明

- v3.1 新增內容**全部是 additive** — 沒移除或重命名任何公開 API。
- `LogOptions` 與新 `QuoteOptions` 的所有新選項都預設為 v3.0 行為 — 現有程式碼不需修改。
- 新加的 `ILogOptions` 介面成員(`OutputFormat`、`TimeFormat`、`ShowThreadId`、`ShowThreadName`、`HighPrecisionTimestamp`、`QuoteOptions`)是**唯讀**;函式庫消費者通常只透過 `LOG.GetCurrentOptions()` 讀取,所以這對典型用法不算 breaking change。
- v3.3 的新增(`Flush`、`FlushAsync`、`Shutdown`、`ShutdownAsync`、`IsShutdown`)同樣是**純新增** — 既有簽章沒動、預設值沒動。唯一的行為變更是 `Configure` 在 `Shutdown` 之後變成可以再呼叫;管線還活著時第二次呼叫仍然擲例外。
