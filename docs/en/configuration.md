---
title: Configuration
description: Complete reference of LogOptions, AsyncLogOptions, QuoteOptions exposed by LOG.Configure() (v3.1).
---

# Configuration

All configuration is set **once** at process start via `LOG.Configure(...)`. The call is **not re-entrant** — a second call throws `InvalidOperationException`. If you skip it entirely, defaults apply on first log write.

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

## 1. `LogOptions` — main logger

### 1.1 File output

| Option | Type | Default | Description |
|---|---|---|---|
| `KeepDays` | `int` | `-3` | **Must be negative.** Number of days of logs to keep. Older dated subdirectories are deleted by `LogRetentionCleaner` running every 60 s in the background. |
| `MaxFileSize` | `long` | `50 * 1024 * 1024` (50 MB) | File size threshold for size-based splitting. When a file exceeds this, the next write opens `{name}_part2_Log.{ext}`. |
| `SetFileSizeInMB(int megabytes)` | method | — | Convenience setter for `MaxFileSize`. |
| `LogPath` | `string` | `"logs"` | Root directory under `AppDomain.CurrentDomain.BaseDirectory`. Full path becomes `{baseDir}/{LogPath}/{yyyyMMdd}/{TypeDirectories.*}/{name}_Log.{ext}`. |
| `TypeDirectories` | `LogTypeDirectories` | see §1.5 | Per-LogLevel subdirectory configuration. |

### 1.2 Output format (v3.1+)

| Option | Type | Default | Description |
|---|---|---|---|
| `OutputFormat` | `LogOutputFormat` | `Txt` | Global selector: `Txt` / `Log` (same content, different extension) / `Json` (NDJSON). See [API §4](./api.md#4-logoutputformat-enum-v31). |
| `TimeFormat` | `string` | `"HH:mm:ss.fff"` | Free-form .NET `DateTime` format string for the message prefix in `Txt`/`Log` modes. **Not used in `Json` mode** (which always emits epoch_ms). Invalid format strings fall back to default. |
| `ShowThreadId` | `bool` | `true` | Toggle `[T:tid]` prefix in `Txt`/`Log`; toggle `tid` field in `Json`. |
| `ShowThreadName` | `bool` | `false` | Toggle thread name display. When the calling thread has no `Thread.Name`, the entire thread segment is omitted regardless. In `Txt`/`Log` mode the output combines as `[T:tid/Name]` if both `ShowThreadId` and `ShowThreadName` are true. |
| `HighPrecisionTimestamp` | `bool` | `false` | Opt-in `Stopwatch`-hybrid mode for µs-level precision. Raises caller-side ticks read from ~5 ns to ~30 ns. Useful only if your `TimeFormat` uses precision finer than `.fff`. |

### 1.3 Async pipeline behavior

| Option | Type | Default | Description |
|---|---|---|---|
| `EnableAsyncLogging` | `bool` | `true` | If `false`, calls format and write synchronously on the caller thread and flush right away — no queue, no dispatcher, no backpressure (nothing is ever dropped), and the entry is readable as soon as the call returns. Slower for HFT but simpler to reason about; still thread-safe, and the output format is identical to async mode. Note that retention cleanup (`KeepDays`) does not run in this mode. Fixed in v3.2.0 — earlier versions never flushed this path and produced empty files. |
| `EnableConsoleOutput` | `bool` | `true` | If `true`, every log line is also written to `Console.WriteLine` on the caller thread. |
| `MaxOpenFileStreams` | `int` | `100` (range `[4, 4096]`) | LRU upper bound for the persistent `FileStreamPool`. When exceeded, the least-recently-written stream is closed. |
| `DiskFlushIntervalMs` | `int` | `100` (range `[10, 10000]`) | Period for `FileStreamPool.FlushAll()` — buffered writes flushed to disk (but not `fsync`-ed; OS decides write-back). |
| `OnDropped` | `Action` | `null` | Callback fired **once per drop event** when the queue is full and the oldest entry is dropped. Body must be light (called on dispatcher thread). |

### 1.4 Global exception capture (opt-in)

| Option | Type | Default | Description |
|---|---|---|---|
| `EnableGlobalExceptionCapture` | `bool` | `false` | If `true`, subscribes to `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` and logs them at Fatal level with synchronous immediate flush. Off by default to avoid clashing with the host app's existing handlers. |

> Does **not** intercept WPF `DispatcherUnhandledException`, WinForms `ThreadException`, or ASP.NET Core middleware exceptions — library can't reach those framework objects. Host app must hook those itself.

### 1.5 `LogTypeDirectories` — per-level subdirectories

```csharp
o.TypeDirectories.DirectoryPath = "LogFiles";   // default if level-specific path is null
o.TypeDirectories.ErrorPath     = "ErrorLogs";  // route Error level to a separate folder
o.TypeDirectories.FatalPath     = "FatalLogs";
// TracePath / DebugPath / InfoPath / WarnPath / CustomPath also available
```

If a level-specific path is `null`, it falls back to `DirectoryPath`.

### 1.6 `AsyncLogOptions` — dispatcher tuning

Configured via `o.ConfigureAsync(a => { ... })`.

| Option | Type | Default | Range | Description |
|---|---|---|---|---|
| `MaxBatchSize` | `int` | `100` | `[1, 1000]` | Max items drained by the dispatcher per wake-up. |
| `MaxQueueSize` | `int` | `10000` | `[1000, 100000]` | Queue capacity. When exceeded, drop oldest + `OnDropped` callback fires. |
| `FlushIntervalMs` | `int` | `1000` | `[10, 10000]` | Dispatcher wake-up interval. The semaphore wait times out at this interval if no signals arrive. |

---

## 2. `QuoteOptions` — Quote pipeline (v3.1+)

Configured via `o.ConfigureQuote(q => { ... })`. **Default is `Enable = false`** — the Quote pipeline must be explicitly opted into.

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

| Option | Type | Default | Range | Description |
|---|---|---|---|---|
| `Enable` | `bool` | `false` | — | **Required.** When `false`, all `LOG.Quote(...)` calls are silent no-ops and no background thread is started. |
| `OutputFormat` | `QuoteOutputFormat` | `Txt` | — | `Txt` / `Log` / `Json`. Independent from main logger's `OutputFormat`. |
| `QuotePath` | `string` | `"Quotes"` | — | Subdirectory under `{LogPath}/{yyyyMMdd}/` for quote files. |
| `MaxOpenStreams` | `int` | `500` | `[4, 4096]` | LRU upper bound for the independent `QuoteFileStreamPool`. Higher than main logger because crypto/equities symbol counts run to thousands. |
| `MaxQueueSize` | `int` | `50000` | `[1000, 1_000_000]` | Quote queue capacity. Drop-oldest on overflow. Higher than main logger because quote throughput is typically 100×–1000× higher. |
| `MaxBatchSize` | `int` | `500` | `[1, 10000]` | Max records drained per dispatcher wake-up. |
| `FlushIntervalMs` | `int` | `100` | `[10, 10000]` | Dispatcher wake-up interval. |
| `OnDropped` | `Action<long>` | `null` | — | Callback invoked when drops happen. Parameter is the **newly-dropped count** since the last callback invocation (not cumulative). |

> The Quote pipeline reuses the main logger's `KeepDays` and `MaxFileSize` settings for retention and file splitting. Quote files are cleaned up by the same `LogRetentionCleaner`.

---

## 3. Read-back via `LOG.GetCurrentOptions()`

After `Configure`, you can inspect the live configuration via the read-only view:

```csharp
var current = LOG.GetCurrentOptions();

// Main logger
Console.WriteLine(current.OutputFormat);                  // Json
Console.WriteLine(current.TimeFormat);                    // "HH:mm:ss.fff"
Console.WriteLine(current.ShowThreadId);                  // true
Console.WriteLine(current.ShowThreadName);                // false
Console.WriteLine(current.HighPrecisionTimestamp);        // false
Console.WriteLine(current.MaxOpenFileStreams);            // 100
Console.WriteLine(current.AsyncOptions.MaxQueueSize);     // 10000

// Quote pipeline
Console.WriteLine(current.QuoteOptions.Enable);           // true
Console.WriteLine(current.QuoteOptions.OutputFormat);     // Json
Console.WriteLine(current.QuoteOptions.QuotePath);        // "Quotes"
Console.WriteLine(current.QuoteOptions.MaxOpenStreams);   // 500
```

All read-back values are wrapped in `ReadOnlyLogOptions` / `ReadOnlyQuoteOptions` — consumers cannot mutate the live config through this view.

---

## 4. Defaults summary (when `LOG.Configure(...)` is **not** called)

| Group | Setting | Default |
|---|---|---|
| File | `KeepDays` | `-3` |
| File | `MaxFileSize` | 50 MB |
| File | `LogPath` | `"logs"` |
| File | `TypeDirectories.DirectoryPath` | `"LogFiles"` |
| Format | `OutputFormat` | `Txt` |
| Format | `TimeFormat` | `"HH:mm:ss.fff"` |
| Format | `ShowThreadId` | `true` |
| Format | `ShowThreadName` | `false` |
| Format | `HighPrecisionTimestamp` | `false` |
| Async | `EnableAsyncLogging` | `true` |
| Async | `EnableConsoleOutput` | `true` |
| Async | `MaxOpenFileStreams` | `100` |
| Async | `DiskFlushIntervalMs` | `100` |
| Async | `AsyncOptions.MaxBatchSize` | `100` |
| Async | `AsyncOptions.MaxQueueSize` | `10000` |
| Async | `AsyncOptions.FlushIntervalMs` | `1000` |
| Quote | `QuoteOptions.Enable` | **`false` (opt-in)** |
| Quote | `QuoteOptions.OutputFormat` | `Txt` |
| Quote | `QuoteOptions.QuotePath` | `"Quotes"` |
| Quote | `QuoteOptions.MaxOpenStreams` | `500` |
| Quote | `QuoteOptions.MaxQueueSize` | `50000` |
| Misc | `EnableGlobalExceptionCapture` | `false` |

→ For a one-page API summary see [API Reference](./api.md).
