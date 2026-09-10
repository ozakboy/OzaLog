---
title: HFT Async Architecture
description: ConcurrentQueue + persistent FileStream pool + cached timestamp + drop-oldest backpressure — and the independent Quote pipeline (v3.1+).
---

# HFT Async Architecture

OzaLog ships two **independent** async pipelines:

1. **Main logger pipeline** — for application logs (`LOG.Info_Log`, `Error_Log`, `CustomName_Log`, …)
2. **Quote pipeline** (v3.1+) — for high-frequency market tick/quote data (`LOG.Quote`, `LOG.QuoteTicker`)

The two share **no locks, no streams, and no dispatcher threads**. They differ only in default tuning (queue size, file count, batch size).

---

## 1. Main logger pipeline

### 1.1 Write path

```
[caller threads]
       │  enqueue (struct LogItem, zero-alloc)
       ▼
[ConcurrentQueue<LogItem>]
       │  signal via SemaphoreSlim
       ▼
[single dispatcher thread]
       │  drain batch → format → AppendLine
       ▼
[FileStreamPool: per-(level, name) persistent FileStream]
       │  LRU bound = LogOptions.MaxOpenFileStreams (default 100)
       │  day rollover handled inline
       │  size-based splitting → {name}_part2_Log.{ext}
       ▼
{baseDir}/{LogPath}/{yyyyMMdd}/{TypeDirectories.*}/{name}_Log.{ext}
```

### 1.2 Caller-side cost (per `LOG.Info_Log(...)`)

- 1× volatile read for cached timestamp (~5 ns; ~30 ns if `HighPrecisionTimestamp=true`)
- 1× `LogFormatter.EscapeMessage` for `{}` escaping (skipped if message has no braces)
- 1× struct field copy to build `LogItem`
- 1× `ConcurrentQueue.Enqueue` (CAS)
- 1× `SemaphoreSlim.Release`

**No `DateTime.Now`, no `string.Format`, no heap allocation** on the caller path. All formatting is done on the dispatcher thread.

### 1.3 Backpressure: drop-oldest

When the queue has more items than `AsyncLogOptions.MaxQueueSize` (default 10000):

1. The oldest item is `TryDequeue`-d and discarded
2. A drop counter is incremented atomically
3. `LogOptions.OnDropped` callback fires (if set)

This guarantees the queue never grows unbounded — OOM cannot happen from log spam, only the *oldest* lines are sacrificed.

### 1.4 Immediate flush

`Error` and `Fatal` levels (and any call with `immediateFlush: true`) are **not enqueued**. They are written synchronously on the caller thread followed by `FileStream.Flush(flushToDisk: true)`. This guarantees crash logs reach disk before the process dies.

Because they bypass the queue entirely, these entries:

- Are written exactly once (v3.1.0 both enqueued *and* wrote them synchronously, producing two identical lines per entry — fixed in v3.1.1)
- Are never subject to drop-oldest backpressure, so they cannot be discarded
- Keep caller order within the same `(level, name)` file; ordering across files may drift slightly against asynchronous entries (the in-line timestamp is the source of truth)

> ⚠️ The predicate must be `level == Error || level == Fatal`, never `level >= Error` — `LogLevel.CustomName = 99` would match and turn every CustomName entry into a synchronous + fsync write.

### 1.4.1 Synchronous mode (`EnableAsyncLogging = false`)

Setting this to `false` starts none of the pipeline above: no queue, no dispatcher, no backpressure (so nothing is ever dropped). The caller thread formats and writes into the `FileStreamPool` directly and **flushes after every entry** (at `StreamWriter` / `FileStream` level, no forced `fsync`), so the content is readable immediately after the call returns.

- Writes are still guarded by the `FileStreamPool` lock → thread-safe
- Output formatting is **identical** to asynchronous mode (same formatter)
- In v3.1.0 nothing ever flushed this path, so the content stayed in the buffer and was lost (0-byte files) — fixed in v3.1.1
- Day rollover and size-based splitting work as usual, but **retention cleanup (`KeepDays`) does not run** — `LogRetentionCleaner` starts together with the asynchronous pipeline, so synchronous-mode hosts must prune old directories themselves

### 1.5 Disk flush timer

A `Timer` calls `FileStreamPool.FlushAll()` every `DiskFlushIntervalMs` (default 100 ms), which calls `StreamWriter.Flush()` on all open streams. The OS decides write-back timing (no forced `fsync`) — favors throughput over durability.

### 1.6 Shutdown safety

- `AppDomain.CurrentDomain.ProcessExit` → drain + flush + close all streams
- `AppDomain.CurrentDomain.UnhandledException` → same
- `LOG.Configure` can subscribe `EnableGlobalExceptionCapture = true` for additional Fatal-level logging on unhandled exceptions and unobserved Task exceptions.

---

## 2. Quote pipeline (v3.1+)

The Quote pipeline runs in **parallel** with the main logger — same architecture, separate state.

### 2.1 Why a separate pipeline?

Quote / tick data has fundamentally different characteristics from application logs:

| | Main logger | Quote pipeline |
|---|---|---|
| Throughput | ~10–1000 entries/sec | ~10,000–1,000,000 entries/sec |
| Data type | Free-form string | Structured (Symbol, Bid, Ask, …) |
| Default `MaxQueueSize` | 10,000 | 50,000 |
| Default `MaxOpenStreams` | 100 | 500 |
| Default `MaxBatchSize` | 100 | 500 |
| Severity levels | yes (Trace … Fatal) | no — all entries are equal |
| Immediate flush | Error/Fatal trigger flush | none — pure async batch |

Putting them on the same dispatcher would cause:
- Quote burst (~1M/sec) saturating the queue and dropping Error/Fatal application logs
- Quote dispatcher latency growing during application log immediate-flush

Separating them eliminates this contention entirely.

### 2.2 Write path

```
[caller threads — WebSocket consumer, REST poller, etc.]
       │  validate (Symbol/Bucket non-empty, Extras key collisions, …)
       │  enqueue (struct QuoteRecord, zero-alloc)
       ▼
[ConcurrentQueue<QuoteRecord>]                 ← QuoteOptions.MaxQueueSize
       │  signal via SemaphoreSlim
       ▼
[independent dispatcher thread]
       │  drain batch → QuoteFormatter.Format → AppendLine
       ▼
[QuoteFileStreamPool: per-(bucket, symbol) persistent FileStream]
       │  LRU bound = QuoteOptions.MaxOpenStreams (default 500)
       │  filename auto-sanitization for invalid file-system chars
       ▼
{baseDir}/{LogPath}/{yyyyMMdd}/{QuotePath}/{Bucket}_{Symbol}_Quote.{ext}
```

### 2.3 Synchronous validation on caller thread

`LOG.Quote(...)` validates the record **before** enqueueing:

- `Symbol` is null or empty → `ArgumentException` thrown on caller thread
- `Bucket` is null or empty → `ArgumentException`
- Both `Extras` and `ExtrasJson` set → `ArgumentException`
- `Extras` contains a reserved key (`bid`, `ask`, `last`, …) → `ArgumentException`

This lets callers wrap calls in `try`/`catch` and catch programmer errors immediately. Validation errors **never** silently disappear into the dispatcher.

### 2.4 Backpressure: drop-oldest with batched callback

Same drop-oldest strategy as the main logger, but the `OnDropped` callback signature differs:

```csharp
// Main logger
public Action OnDropped { get; set; }              // fires once per drop event

// Quote pipeline
public Action<long> OnDropped { get; set; }        // batched: parameter = newly dropped since last callback
```

The Quote callback receives the **delta** (number of records dropped since the last callback fired), allowing efficient metric reporting without per-record callback overhead during heavy bursts.

### 2.5 Shutdown

Same `ProcessExit` hook as the main logger — the Quote pipeline flushes and closes all its streams independently. Both pipelines flush **in parallel** (no ordering coupling), so total shutdown time is bounded by the slower of the two.

---

## 3. Why no thread-pool dispatcher?

Both pipelines use a single dedicated `Task.Run(...)` dispatcher per pipeline — not a thread-pool worker. Reasons:

- **Predictable latency**: a dedicated thread is never preempted by user code.
- **Lock-free FileStreamPool access**: only one thread writes to the pool, so `FileStream` state needs no locks during normal write path (locks are only used during shutdown / disk-flush timer / immediate-flush interleaving).
- **Cache locality**: the dispatcher thread keeps its `FileStreamPool` slots, `StreamWriter` buffers, and dictionary hot in CPU cache.

The cost: per-`(level, name)` (or per-`(bucket, symbol)`) write ordering is preserved, but **cross-key write order may be slightly reordered** (different keys may flush to disk in batched groups). For HFT tick reconstruction this is fine — timestamps in the records are the source of truth, not file ordering.

---

## 4. TimestampCache

A background `Timer` updates `volatile long _currentTicks` every 1 ms by calling `DateTime.Now.Ticks`. Callers do an atomic read of this value (~5 ns) instead of paying the `DateTime.Now` syscall cost (~80 ns on Windows due to `GetSystemTimeAsFileTime` + time-zone conversion) on every log call.

**1 ms precision floor**: if your `TimeFormat` uses precision finer than `.fff` (e.g. `.ffffff` for µs), the last digits will always be `0000` unless you opt into `HighPrecisionTimestamp = true`.

### 4.1 HighPrecisionTimestamp mode (v3.1+)

When enabled, the cache also stores `Stopwatch.GetTimestamp()` at each 1 ms update. On read, the caller computes:

```
actualTicks = cachedTicks + (Stopwatch.GetTimestamp() - cachedSwTimestamp) * (TimeSpan.TicksPerSecond / Stopwatch.Frequency)
```

This reconstructs sub-millisecond precision from the 1 ms cache without paying the `DateTime.Now` cost. Caller-side read goes from ~5 ns to ~30 ns. Use only when you need µs-level timestamps for latency analysis or tick-level time series.

---

## 5. Source of truth

- [`OzaLog/OzaLog/Core/AsyncLogHandler.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/Core/AsyncLogHandler.cs) — main pipeline dispatcher
- [`OzaLog/OzaLog/Core/FileStreamPool.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/Core/FileStreamPool.cs) — main pipeline file streams + LRU
- [`OzaLog/OzaLog/Core/QuoteLogHandler.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/Core/QuoteLogHandler.cs) — Quote pipeline dispatcher
- [`OzaLog/OzaLog/Core/QuoteFileStreamPool.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/Core/QuoteFileStreamPool.cs) — Quote pipeline file streams
- [`OzaLog/OzaLog/Core/TimestampCache.cs`](https://github.com/ozakboy/OzaLog/blob/main/OzaLog/OzaLog/Core/TimestampCache.cs) — cached timestamps
