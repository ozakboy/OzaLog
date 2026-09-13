---
title: API Reference
description: Complete public API of OzaLog v3.1 — LOG static class, LogOptions, QuoteRecord, enums.
---

# API Reference

> Source of truth: [`OzaLog/OzaLog/LOG.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/LOG.cs), generated XML doc shipped in the NuGet package as `file.xml`.
>
> All public types live in the `OzaLog` namespace.

---

## 1. `LOG` static class

The single entry point for all logging. No instantiation, no `LoggerFactory`, no dependency injection.

```csharp
using OzaLog;

LOG.Info_Log("Hello, OzaLog!");
```

### 1.1 LogLevel methods

For each `LogLevel` value (Trace / Debug / Info / Warn / Error / Fatal), five overloads are provided. Naming convention: `<Level>_Log`.

```csharp
// String message
LOG.Info_Log(string message);

// Toggle file write (true=write, false=console-only when EnableConsoleOutput=true)
LOG.Info_Log(string message, bool writeTxt);

// Formatted message with {0}/{1}/... placeholders
LOG.Info_Log(string message, string[] args, bool writeTxt = true, bool immediateFlush = false);

// Object — automatically serialized to JSON
LOG.Info_Log<T>(T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;

// Object with header message
LOG.Info_Log<T>(string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;
```

**Replace `Info` with `Trace`, `Debug`, `Warn`, `Error`, or `Fatal` for the corresponding level.**

#### Automatic immediate flush

`Error_Log` and `Fatal_Log` **always** trigger synchronous immediate flush regardless of the `immediateFlush` argument. This ensures crash logs reach disk before the process dies. Other levels respect the `immediateFlush` argument (default `false`).

Entries taking this path are **not enqueued** — the caller thread writes them, so each produces exactly one line and none can be discarded by drop-oldest backpressure when the queue is full. (v3.1.0 both enqueued and wrote them synchronously, duplicating every such entry — fixed in v3.2.0.)

#### Object overload behavior

- When `obj is Exception` and `level >= Warn`, the object is serialized via `ExceptionHandler.CreateSerializableException(...)`, which recursively expands `InnerException`, `Data` dictionary, `StackTrace`, and reflected non-standard properties.
- Otherwise serialization goes through `System.Text.Json` with `WriteIndented=false`, `DefaultIgnoreCondition=WhenWritingNull`, `Encoder=UnsafeRelaxedJsonEscaping`.

### 1.2 `CustomName_Log` — per-bucket file

Routes the log line to a custom filename instead of the level-based default. Useful for per-symbol files in trading (`BTC_Log.txt`, `ETH_Log.txt`).

```csharp
LOG.CustomName_Log(string name, string message);
LOG.CustomName_Log(string name, string message, bool writeTxt);
LOG.CustomName_Log(string name, string message, string[] args, bool writeTxt = true, bool immediateFlush = false);
LOG.CustomName_Log<T>(string name, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;
LOG.CustomName_Log<T>(string name, string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;
```

→ File path: `{baseDir}/{LogPath}/{yyyyMMdd}/{CustomPath}/{name}_Log.{ext}`

### 1.3 `LOG.Configure(...)` — one-time configuration

```csharp
public static void Configure(Action<LogConfiguration.LogOptions> configure);
```

**Not re-entrant.** Second call throws `InvalidOperationException("OzaLog 已初始化（Configure 不可重入）")`. If `Configure` is never called, the first log write auto-initializes with default settings.

> Since v3.3.0 there is one exception: after `LOG.Shutdown()` (§1.5) the pipeline is no longer running, so `Configure` is permitted again and restarts it — with the options **reset to their defaults**, not the ones from the previous round.

### 1.4 `LOG.GetCurrentOptions()` — read-only config view

```csharp
public static LogConfiguration.ILogOptions GetCurrentOptions();
```

Returns a read-only wrapper around the live `LogOptions`. Useful for diagnostics and verifying the active configuration.

### 1.5 Lifecycle — `Flush` / `Shutdown` (v3.3+)

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

| Member | Returns | Meaning |
| --- | --- | --- |
| `Flush` / `FlushAsync` | `true` | Both pipelines drained and the files were flushed to disk (`fsync`) |
| | `false` | Timed out (default 10 s), cancelled, or the logger is already shut down — the already-written part is still flushed |
| `Shutdown` / `ShutdownAsync` | `true` | This call performed the shutdown |
| | `false` | Already shut down — idempotent, not an error |
| `IsShutdown` | `bool` | Whether logging calls are currently being discarded |

**`Flush` is an exact barrier.** The calling thread helps drain the queue instead of waiting out the dispatcher's `FlushIntervalMs`, so 1000 entries written before the call are 1000 lines in the file when it returns. Both the main logger and the Quote pipeline are covered.

**`Shutdown` stops the background work**: it flushes, then stops the dispatcher, the periodic disk-flush timer and the retention-cleanup timer, and closes every open log file. Afterwards **every logging call is silently discarded** — no exception, no console output — and `Configure` becomes callable again, which restarts the pipeline with the **default** options (see §1.3). Interleaving with the built-in `ProcessExit` / `UnhandledException` cleanup is safe in either order.

**None of these methods throw**, on any path — including a cancelled `CancellationToken`, which returns `false` rather than raising `OperationCanceledException`. The `Task`-returning overloads are available on every target framework, `netstandard2.0` included.

```csharp
LOG.Info_Log("about to snapshot");
LOG.Flush();                         // the line above is on disk now

// at the end of the process
await LOG.ShutdownAsync();
LOG.Info_Log("ignored, no exception");
```

---

## 2. `LOG.Quote(...)` — Quote pipeline (v3.1+)

The Quote pipeline is an **independent** async pipeline for high-frequency tick/quote data, separate from the main logger. Field names align with the Binance REST API 24hr Ticker schema.

> **Prerequisite**: enable the pipeline at configuration time:
> ```csharp
> LOG.Configure(o => o.ConfigureQuote(q => q.Enable = true));
> ```
> Without `Enable = true`, all `LOG.Quote(...)` calls are silent no-ops (no background thread is started).

### 2.1 A2 core API — struct overload

```csharp
public static void Quote(in QuoteRecord record);
```

Zero-allocation enqueue. Validates `record` synchronously on the calling thread and throws `ArgumentException` for:

- `Symbol` or `Bucket` is null or empty
- `Extras` and `ExtrasJson` both set
- `Extras` contains a key matching a reserved field (see [§2.4](#24-reserved-extras-keys))

### 2.2 A1 convenience overloads

Internally construct a `QuoteRecord` and delegate to the struct overload.

```csharp
// Minimal tick — last price only
LOG.Quote(string symbol, string bucket, long ticks, decimal last);

// With bid/ask
LOG.Quote(string symbol, string bucket, long ticks,
          decimal last, decimal bid, decimal ask);

// With bid/ask + sizes
LOG.Quote(string symbol, string bucket, long ticks,
          decimal last,
          decimal bid, decimal bidQty,
          decimal ask, decimal askQty);

// Full ticker — aligned with Binance REST API /api/v3/ticker/24hr
LOG.QuoteTicker(string symbol, string bucket, long ticks,
                decimal last,
                decimal? lastQty = null,
                decimal? bid = null, decimal? bidQty = null,
                decimal? ask = null, decimal? askQty = null,
                decimal? open = null, decimal? prevClose = null,
                decimal? high = null, decimal? low = null,
                decimal? volume = null, decimal? quoteVolume = null);

// Full ticker with custom Extras dictionary
LOG.QuoteTicker(string symbol, string bucket, long ticks,
                decimal last,
                IReadOnlyDictionary<string, object> extras,
                /* same optional fields as above */);
```

### 2.3 `QuoteRecord` (public `readonly struct`)

```csharp
public readonly struct QuoteRecord
{
    // Required
    public readonly string Symbol;     // e.g. "BTCUSDT"
    public readonly string Bucket;     // e.g. "binance_spot"
    public readonly long   Ticks;      // event time, caller-supplied
    public readonly decimal Last;      // last trade price

    // Optional (all decimal?)
    public readonly decimal? LastQty;       // qty of last trade
    public readonly decimal? Bid, BidQty;   // best bid + qty
    public readonly decimal? Ask, AskQty;   // best ask + qty
    public readonly decimal? Open, PrevClose;
    public readonly decimal? High, Low;
    public readonly decimal? Volume;        // cumulative base asset volume
    public readonly decimal? QuoteVolume;   // cumulative quote asset volume

    // Custom fields — mutually exclusive
    public readonly IReadOnlyDictionary<string, object>? Extras;
    public readonly string? ExtrasJson;     // pre-serialized JSON object string
}
```

**Field-name mapping to Binance `/api/v3/ticker/24hr` response**:

| `QuoteRecord` | Binance JSON | Note |
|---|---|---|
| `Last` | `lastPrice` | required |
| `LastQty` | `lastQty` | quantity of the most recent trade |
| `Bid` / `BidQty` | `bidPrice` / `bidQty` | best bid quote |
| `Ask` / `AskQty` | `askPrice` / `askQty` | best ask quote |
| `Open` / `PrevClose` / `High` / `Low` | `openPrice` / `prevClosePrice` / `highPrice` / `lowPrice` | session stats |
| `Volume` | `volume` | 24h base asset volume |
| `QuoteVolume` | `quoteVolume` | 24h quote asset volume |

### 2.4 Reserved Extras keys

The following keys are reserved by the built-in schema. Putting them in `Extras` (Dictionary) throws `ArgumentException` synchronously. Putting them in `ExtrasJson` (string) throws asynchronously inside the dispatcher (logged to console; record is dropped).

> `ts`, `symbol`, `bucket`, `last`, `lastQty`, `bid`, `bidQty`, `ask`, `askQty`, `open`, `prevClose`, `high`, `low`, `volume`, `quoteVolume`, `extras`

### 2.5 Filename rules

```
{baseDir}/{LogPath}/{yyyyMMdd}/{QuotePath}/{Bucket}_{Symbol}_Quote.{ext}
```

- **No nested subdirectories**: `Bucket` becomes a filename prefix, not a folder.
- **Auto-sanitization**: file-system-invalid characters (`/ \ : * ? " < > |`) in `Symbol` / `Bucket` are replaced with `-` **in the filename only**. The original strings are preserved in the file content.
- Day rollover, LRU eviction, and size-based splitting (`_part2_Quote.{ext}` etc.) all work the same as the main logger but with their own independent `QuoteFileStreamPool`.

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
    CustomName = 99,   // used internally by LOG.CustomName_Log(...)
}
```

> v3.0 renamed `CostomName` → `CustomName` (typo fix, breaking change). `LOG.CustomName_Log(...)` method was always spelled correctly.

---

## 4. `LogOutputFormat` enum (v3.1+)

Selects the **main logger** output format. Set via `LogOptions.OutputFormat`.

```csharp
public enum LogOutputFormat
{
    Txt  = 0,   // human-readable text, .txt extension (default)
    Log  = 1,   // same content as Txt, .log extension
    Json = 2,   // NDJSON (one JSON object per line), .json extension
}
```

### 4.1 Json format schema (NDJSON)

```json
{"ts":1715587425123,"lv":"Info","nm":"","tid":12,"tn":"MainThread","msg":"hello","data":{...}}
```

| Field | Type | Always present? | Meaning |
|---|---|---|---|
| `ts` | `long` (epoch_ms) | yes | event timestamp in milliseconds since Unix epoch |
| `lv` | `string` | yes | `"Trace"` / `"Debug"` / `"Info"` / `"Warn"` / `"Error"` / `"Fatal"` / `"CustomName"` |
| `nm` | `string` | yes | log name (CustomName value, or empty for level-based logs) |
| `tid` | `int` | iff `ShowThreadId=true` | calling thread's `ManagedThreadId` |
| `tn` | `string` | iff `ShowThreadName=true` AND `Thread.Name != null` | calling thread's `Thread.Name` |
| `msg` | `string` | yes | message text (always emitted even if empty) |
| `data` | object | iff present | parsed JSON of the object-overload payload (Exception or arbitrary object) |

---

## 5. `QuoteOutputFormat` enum (v3.1+)

Selects the **Quote pipeline** output format. Set via `LogOptions.QuoteOptions.OutputFormat`.

```csharp
public enum QuoteOutputFormat
{
    Txt  = 0,   // human-readable key=value, .txt extension (default)
    Log  = 1,   // same content as Txt, .log extension
    Json = 2,   // NDJSON, .json extension
}
```

### 5.1 Txt / Log format

```
[2026-05-13 10:23:45.123] binance_spot BTCUSDT last=60123.5 bid=60123.0 ask=60124.0 bidQty=0.5 askQty=1.2
```

- ISO 8601 timestamp prefix (human-readable)
- Null optional fields are skipped (variable line length)
- `Extras` dictionary entries are appended as more `k=v` pairs

### 5.2 Json format (NDJSON)

```json
{"ts":1715587425123,"symbol":"BTCUSDT","bucket":"binance_spot","last":60123.5,"bid":60123.0,"ask":60124.0,"extras":{"funding":0.0001}}
```

- `ts` = epoch_ms (consistent with main logger)
- Only non-null fields are emitted
- `Extras` is nested under `"extras"` (not flattened into top level) — keeps a clean schema boundary
- Quote NDJSON **never** includes `tid` / `tn` (Quote represents market events, not program-internal events)

---

## 6. Read-only configuration view

```csharp
LogConfiguration.ILogOptions current = LOG.GetCurrentOptions();
Console.WriteLine(current.OutputFormat);             // LogOutputFormat
Console.WriteLine(current.TimeFormat);               // "HH:mm:ss.fff" etc.
Console.WriteLine(current.HighPrecisionTimestamp);   // bool
Console.WriteLine(current.QuoteOptions.Enable);      // bool
```

See [Configuration](./configuration.md) for the full list of properties.

---

## 7. Exception serialization (`SerializableExceptionInfo`)

When you log an `Exception` at `Warn` level or higher via `Warn_Log<T>(ex)` / `Error_Log<T>(ex)` / `Fatal_Log<T>(ex)`, the runtime expands it into:

```csharp
class SerializableExceptionInfo
{
    string Type;          // ex.GetType().FullName
    string Message;
    string Source;
    string HelpLink;
    string StackTrace;
    Dictionary<string, string> Data;             // expanded from ex.Data
    SerializableExceptionInfo InnerException;    // recursive
    Dictionary<string, string> AdditionalProperties;  // reflected non-standard props
}
```

The result is JSON-serialized into the log line (or the `data` field in Json output mode).

Since v3.2.0 the serialized JSON reaches the file **exactly as produced**, so it can be handed straight to a parser:

```text
09:07:05.123[T:12]
{
  "Type": "System.InvalidOperationException",
  "Message": "order book is empty",
  "Data": {},
  "AdditionalProperties": { "HResult": "-2146233079" }
}
```

Up to v3.1.0 every brace was doubled (`{{ "Type": ... "Data": {{}} }}`), which made the payload unparsable. If you built parsing around the doubled form, adjust it when upgrading.

---

## 8. Versioning notes

- v3.1 additions are **strictly additive** — no public API was removed or renamed.
- All new options on `LogOptions` and the new `QuoteOptions` default to v3.0 behavior — existing code continues to work unchanged.
- The new `ILogOptions` interface members (`OutputFormat`, `TimeFormat`, `ShowThreadId`, `ShowThreadName`, `HighPrecisionTimestamp`, `QuoteOptions`) are **read-only**; library consumers normally only read `LOG.GetCurrentOptions()`, so this is not a breaking change for typical use.
- v3.3 additions (`Flush`, `FlushAsync`, `Shutdown`, `ShutdownAsync`, `IsShutdown`) are **strictly additive** as well — no existing signature changed and no default value changed. The one behavioral change is that `Configure` is now permitted after `Shutdown`; it still throws on a second call while the pipeline is alive.
