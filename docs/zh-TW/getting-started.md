---
title: 快速開始
description: 安裝 OzaLog v3.1,一分鐘內寫下第一行 log。含報價 pipeline 範例。
---

# 快速開始

## 安裝

```bash
dotnet add package OzaLog --version 3.1.0
```

支援的 TargetFrameworks:`netstandard2.0`、`netstandard2.1`、`net8.0`、`net9.0`、`net10.0`。**`net8.0` / `net9.0` / `net10.0` 零 NuGet 依賴**(使用 BCL 內建的 `System.Text.Json`)。

## 1. 第一行 log

```csharp
using OzaLog;

LOG.Info_Log("Hello, OzaLog!");
```

完成 — **不需呼叫 `Configure`**。第一次寫入時會自動以預設值初始化。log 檔案會出現在:

```
{你的應用程式資料夾}/logs/{yyyyMMdd}/LogFiles/Info_Log.txt
```

## 2. 所有 LogLevel

```csharp
LOG.Trace_Log("追蹤訊息");
LOG.Debug_Log("除錯訊息");
LOG.Info_Log("一般資訊");
LOG.Warn_Log("警告");
LOG.Error_Log("錯誤");      // 自動 immediate flush
LOG.Fatal_Log("致命錯誤");   // 自動 immediate flush
```

## 3. 用 `CustomName_Log` 做 per-symbol 檔案分桶

交易場景常見用法 — 把每個商品的 log 路由到專屬檔案:

```csharp
LOG.CustomName_Log("BTC", "tick 67890.12");
LOG.CustomName_Log("ETH", "tick 3567.45");
// → BTC_Log.txt、ETH_Log.txt 位於 {logs}/{yyyyMMdd}/LogFiles/
```

## 4. 把物件以 JSON 寫入

```csharp
var quote = new { Symbol = "BTCUSDT", Price = 67890.12m, Volume = 1234m };
LOG.Info_Log(quote);
LOG.Info_Log("market snapshot", quote);  // 含標頭訊息
```

物件透過 `System.Text.Json` 自動序列化。

## 5. 寫入例外

```csharp
try { /* ... */ }
catch (Exception ex)
{
    LOG.Error_Log("operation failed", ex);
}
```

例外會被展開成結構化 JSON,含 `Type`、`Message`、`StackTrace`、`InnerException`、反射出的非標準屬性。

---

## 6. 配置(一次性,選填)

```csharp
LOG.Configure(o =>
{
    o.KeepDays = -7;
    o.SetFileSizeInMB(50);
    o.EnableConsoleOutput = true;
});
```

**`Configure` 不可重入** — 啟動時只能呼叫一次(v3.3.0 起多一個例外:`LOG.Shutdown()` 之後允許再呼叫)。完整選項列表見 [配置選項](./configuration.md)。

---

## 7. v3.1:NDJSON 輸出

```csharp
LOG.Configure(o =>
{
    o.OutputFormat = LogOutputFormat.Json;   // 輸出 NDJSON,不是純文字
});

LOG.Info_Log("price update");
```

結果(每筆 log 一行):

```json
{"ts":1715587425123,"lv":"Info","nm":"","tid":12,"msg":"price update"}
```

→ pandas / DuckDB / jq / Grafana / Elasticsearch 都原生支援 NDJSON。

---

## 8. v3.1:自訂時間格式與 thread name

```csharp
LOG.Configure(o =>
{
    o.TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";  // 自由格式 .NET DateTime 字串
    o.ShowThreadId = true;
    o.ShowThreadName = true;
});

Thread.CurrentThread.Name = "Worker-1";
LOG.Info_Log("doing work");
```

結果:

```
2026-05-14 10:23:45.123[T:7/Worker-1] doing work
```

---

## 9. v3.1:報價 pipeline(HFT tick/quote 資料)

**報價 pipeline** 是專為高頻市場資料設計的獨立非同步管道。欄位命名對齊 Binance `/api/v3/ticker/24hr` schema。

```csharp
LOG.Configure(o =>
{
    o.ConfigureQuote(q =>
    {
        q.Enable = true;                          // ⚠ 必填 — 報價 pipeline 是 opt-in
        q.OutputFormat = QuoteOutputFormat.Json;  // 輸出 NDJSON
        q.OnDropped = n => Console.WriteLine($"dropped {n} quotes");
    });
});

// 最簡 tick — 只填 last 價格
LOG.Quote("BTCUSDT", "binance_spot", DateTime.Now.Ticks, 67890.12m);

// 含 bid + ask 的盤口
LOG.Quote("ETHUSDT", "binance_spot", DateTime.Now.Ticks,
          last: 3567.45m, bid: 3567.00m, ask: 3568.00m);

// 完整 ticker
LOG.QuoteTicker("BTCUSDT", "binance_spot", DateTime.Now.Ticks,
                last: 67890.12m,
                bid: 67889.0m, bidQty: 1.2m,
                ask: 67891.0m, askQty: 0.8m,
                open: 67500m, high: 68000m, low: 67200m,
                volume: 12345.67m, quoteVolume: 838_000_000m);

// 含自訂欄位(Extras)
var extras = new Dictionary<string, object>
{
    ["funding"] = 0.0001m,
    ["openInterest"] = 12_345_678m,
};
LOG.QuoteTicker("BTCUSDT", "binance_perp", DateTime.Now.Ticks,
                last: 67890.12m, extras: extras,
                bid: 67889m, ask: 67891m);
```

輸出檔案(每對 `{bucket}_{symbol}` 一檔):

```
logs/20260514/Quotes/binance_spot_BTCUSDT_Quote.json
logs/20260514/Quotes/binance_spot_ETHUSDT_Quote.json
logs/20260514/Quotes/binance_perp_BTCUSDT_Quote.json
```

每行是一筆 NDJSON 記錄:

```json
{"ts":1715587425123,"symbol":"BTCUSDT","bucket":"binance_spot","last":67890.12,"bid":67889.0,"ask":67891.0,"extras":{"funding":0.0001}}
```

---

## 下一步

- [配置選項](./configuration.md) — 完整選項表
- [API 參考](./api.md) — 所有公開方法、struct、enum
- [HFT 非同步架構](./async-pipeline.md) — 內部 pipeline 設計

頂層頁面(請從網站導覽列進入):
- **Benchmark** — 與 ZLogger / ZeroLog / Serilog 比較
- **v2.x 升級指南** — 給原 `Ozakboy.NLOG` 使用者
- **版本歷史** — 版本變更記錄
