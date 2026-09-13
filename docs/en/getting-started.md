---
title: Getting Started
description: Install OzaLog v3.1 and write your first log line in under a minute. Includes Quote pipeline.
---

# Getting Started

## Install

```bash
dotnet add package OzaLog --version 3.1.0
```

Supported target frameworks: `netstandard2.0`, `netstandard2.1`, `net8.0`, `net9.0`, `net10.0`. **Zero NuGet dependencies on `net8.0` / `net9.0` / `net10.0`** (uses BCL built-in `System.Text.Json`).

## 1. First log

```csharp
using OzaLog;

LOG.Info_Log("Hello, OzaLog!");
```

That's it — **no `Configure` call required**. The first write auto-initializes with defaults. Log file appears at:

```
{your-app-folder}/logs/{yyyyMMdd}/LogFiles/Info_Log.txt
```

## 2. All log levels

```csharp
LOG.Trace_Log("for tracing");
LOG.Debug_Log("for debugging");
LOG.Info_Log("for general info");
LOG.Warn_Log("for warnings");
LOG.Error_Log("for errors");     // auto immediate flush
LOG.Fatal_Log("for fatal errors"); // auto immediate flush
```

## 3. Per-symbol files with `CustomName_Log`

Common in trading scenarios — route each symbol's logs to a dedicated file:

```csharp
LOG.CustomName_Log("BTC", "tick 67890.12");
LOG.CustomName_Log("ETH", "tick 3567.45");
// → BTC_Log.txt, ETH_Log.txt under {logs}/{yyyyMMdd}/LogFiles/
```

## 4. Log an object as JSON

```csharp
var quote = new { Symbol = "BTCUSDT", Price = 67890.12m, Volume = 1234m };
LOG.Info_Log(quote);
LOG.Info_Log("market snapshot", quote);  // with header message
```

The object is auto-serialized via `System.Text.Json`.

## 5. Log an exception

```csharp
try { /* ... */ }
catch (Exception ex)
{
    LOG.Error_Log("operation failed", ex);
}
```

Exceptions are expanded into a structured JSON with `Type`, `Message`, `StackTrace`, `InnerException`, reflected non-standard properties.

---

## 6. Configure (one-time, optional)

```csharp
LOG.Configure(o =>
{
    o.KeepDays = -7;
    o.SetFileSizeInMB(50);
    o.EnableConsoleOutput = true;
});
```

**`Configure` is not re-entrant** — call it exactly once at startup (v3.3.0 adds one exception: it is allowed again after `LOG.Shutdown()`). See [Configuration](./configuration.md) for the full option list.

---

## 7. v3.1: NDJSON output

```csharp
LOG.Configure(o =>
{
    o.OutputFormat = LogOutputFormat.Json;   // emit NDJSON instead of plain text
});

LOG.Info_Log("price update");
```

Result (one line per log entry):

```json
{"ts":1715587425123,"lv":"Info","nm":"","tid":12,"msg":"price update"}
```

→ pandas / DuckDB / jq / Grafana / Elasticsearch all read NDJSON natively.

---

## 8. v3.1: Custom time format & thread name

```csharp
LOG.Configure(o =>
{
    o.TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";  // free-form .NET DateTime format
    o.ShowThreadId = true;
    o.ShowThreadName = true;
});

Thread.CurrentThread.Name = "Worker-1";
LOG.Info_Log("doing work");
```

Result:

```
2026-05-14 10:23:45.123[T:7/Worker-1] doing work
```

---

## 9. v3.1: Quote pipeline (HFT tick/quote data)

The **Quote pipeline** is an independent async pipeline for high-frequency market data. Field names align with the Binance `/api/v3/ticker/24hr` schema.

```csharp
LOG.Configure(o =>
{
    o.ConfigureQuote(q =>
    {
        q.Enable = true;                          // ⚠ Required — Quote pipeline is opt-in
        q.OutputFormat = QuoteOutputFormat.Json;  // emit NDJSON
        q.OnDropped = n => Console.WriteLine($"dropped {n} quotes");
    });
});

// Minimal tick — just the last price
LOG.Quote("BTCUSDT", "binance_spot", DateTime.Now.Ticks, 67890.12m);

// Bid + ask snapshot
LOG.Quote("ETHUSDT", "binance_spot", DateTime.Now.Ticks,
          last: 3567.45m, bid: 3567.00m, ask: 3568.00m);

// Full ticker
LOG.QuoteTicker("BTCUSDT", "binance_spot", DateTime.Now.Ticks,
                last: 67890.12m,
                bid: 67889.0m, bidQty: 1.2m,
                ask: 67891.0m, askQty: 0.8m,
                open: 67500m, high: 68000m, low: 67200m,
                volume: 12345.67m, quoteVolume: 838_000_000m);

// With custom fields (Extras)
var extras = new Dictionary<string, object>
{
    ["funding"] = 0.0001m,
    ["openInterest"] = 12_345_678m,
};
LOG.QuoteTicker("BTCUSDT", "binance_perp", DateTime.Now.Ticks,
                last: 67890.12m, extras: extras,
                bid: 67889m, ask: 67891m);
```

Output files (one per `{bucket}_{symbol}` pair):

```
logs/20260514/Quotes/binance_spot_BTCUSDT_Quote.json
logs/20260514/Quotes/binance_spot_ETHUSDT_Quote.json
logs/20260514/Quotes/binance_perp_BTCUSDT_Quote.json
```

Each line is one NDJSON record:

```json
{"ts":1715587425123,"symbol":"BTCUSDT","bucket":"binance_spot","last":67890.12,"bid":67889.0,"ask":67891.0,"extras":{"funding":0.0001}}
```

---

## Next steps

- [Configuration](./configuration.md) — full options table
- [API Reference](./api.md) — every public method, struct, and enum
- [HFT Async Architecture](./async-pipeline.md) — internal pipeline design

Top-level pages (use the site navigation):
- **Benchmarks** — OzaLog vs ZLogger / ZeroLog / Serilog
- **Migration from v2.x** — for upgrading `Ozakboy.NLOG` users
- **Changelog** — version history
