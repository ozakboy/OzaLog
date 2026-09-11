---
title: Changelog
description: All notable changes to OzaLog.
---

# Changelog

This file tracks all notable changes to the **OzaLog** package (formerly **Ozakboy.NLOG**).
Version numbers follow [Semantic Versioning](https://semver.org/).

---

## [3.2.0] - 2026-09-11

> Three fixes, all of them changing **what actually lands in your log files**: every `Error` / `Fatal` entry was written twice, synchronous mode (`EnableAsyncLogging = false`) produced empty files, and curly braces in a message were doubled, leaving serialized exceptions as unparsable JSON. No public API signature changes and no default value changes, so this is not a major release — but because the log content changes it ships as a minor rather than a patch: patches tend to be applied by automated upgrade tooling, while a minor makes people glance at these notes first.
>
> **Check three things before upgrading:** (1) counting errors from your logs → the old numbers were inflated; (2) using synchronous mode → your old logs are empty; (3) parsing JSON out of your logs → braces are no longer doubled, adjust your parsing.

### Fixed

**`Error` / `Fatal` / `immediateFlush` entries were written twice**
- `AsyncLogHandler.Enqueue` enqueued the item (written later by the dispatcher) *and* wrote it synchronously on the caller thread to guarantee it reached disk before a crash — so the same log entry appeared as **two identical lines** in the file. This affected every `Error_Log` / `Fatal_Log` overload and any call at any level with `immediateFlush: true`; ordinary `Trace` / `Debug` / `Info` / `Warn` / `CustomName` calls were not affected.
- **Note: if you have been counting errors from your logs, every number before 3.2.0 was inflated.** Not a clean 2x either — it fluctuated between 1x and 2x, because the queued copy could still be discarded by drop-oldest backpressure when the queue saturated, so the heavier the load the lower the actual duplication rate. Alert thresholds based on error rates need to be recalibrated.
- These entries now take **only** the synchronous caller-thread write and are no longer enqueued. The immediate-flush performance characteristic is fully preserved (still `Flush(flushToDisk: true)` right after the write). Side benefit: auto-flush entries no longer pass through the queue, so drop-oldest backpressure can never discard them.

**Synchronous mode (`EnableAsyncLogging = false`) produced 0-byte files**
- The synchronous path wrote into a `StreamWriter` with `AutoFlush = false` and nothing ever flushed it: the dispatcher, the 100 ms periodic disk-flush timer and the `ProcessExit` shutdown hook are all started by `AsyncLogHandler.Initialize`, which synchronous mode never reaches. The log file was created and no error was raised, but the content stayed in the buffer and **was lost when the process exited**.
- **Note: if you have been using synchronous mode, your log files have been empty all along.**
- Synchronous mode now flushes after every entry (at `StreamWriter` / `FileStream` level, no forced `fsync` — same behavior as the periodic flush in async mode), so the content is readable right away. Writes are still guarded by the `FileStreamPool` lock, so the path remains thread-safe. Synchronous and asynchronous mode produce byte-identical formatting for the same entry.
- Queue-full backpressure has been drop-oldest since v3.0 (not a downgrade to synchronous writing), so that path was not affected by this bug.
- `KeepDays` retention cleanup **still does not run in synchronous mode** (the cleaner is started by the async pipeline). Clean old date directories yourself for now, or use asynchronous mode; fixing it needs an extra background timer and is deferred to the next release.

**Curly braces were doubled, leaving exception JSON unparsable**
- Serialized exceptions landed in the file like this: `{{ "Type": "System.InvalidOperationException", "Data": {{}} }}` — every brace doubled, so **no parser could read it**, which blocked any structured analysis of your error logs.
- Cause: `LOG.Log` unconditionally doubled every `{` and `}` in the message on the caller thread so that `AppendFormat` would not mistake them for format placeholders. But the no-arguments path appends the message with `StringBuilder.Append` and **never goes through `AppendFormat`**, so nothing ever undid the doubling. Exception and object overloads never carry arguments, so every single one was affected.
- **Note: if you have been parsing JSON out of your logs, braces are no longer doubled** — adjust any parsing (or the regex / replace you wrote to work around it).
- Braces in a message now reach the file **exactly as written**, with or without arguments. Text mode and JSON mode (`OutputFormat = Json`), synchronous and asynchronous mode all agree.
- This also fixes mixing literal braces with placeholders: `LOG.Info_Log("cfg {\"retry\":3} user {0}", new[] { "alice" })` used to hit a `FormatException` and fall back to the raw message with `{0}` unsubstituted; it now correctly produces `cfg {"retry":3} user alice`.

### Technical
- Brace escaping moved from the caller thread into the formatter, and now happens **only on the `AppendFormat` path** (when arguments are present). It scans character by character: valid format items (`{0}`, `{1,-8}`, `{2:F4}`) are preserved, everything else is escaped — including an index beyond the argument range such as `{5}`, which used to throw `FormatException` and is now emitted as a literal. Side benefit: the caller thread no longer pays for one `Regex.IsMatch` and two `string.Replace` calls per entry, which is closer to the v3.0 "zero formatting on the caller" design.
- `FileStreamPool.Flush` gained a `flushToDisk` parameter (default `true`, existing callers unchanged); synchronous mode passes `false` to avoid an `fsync` per entry.
- New internal method `LogText.WriteSync` (synchronous mode: write + flush); `LogText.Add_LogText` (v2.x compatibility entry point) now routes through it as well.
- **Removed the `Microsoft.SourceLink.GitHub` package reference.** SourceLink has shipped in the SDK since .NET 8; `PublishRepositoryUrl` + `EmbedUntrackedSources` alone produce an identical nuspec `<repository ... branch=... commit=... />`, and the PDBs of all five target frameworks (`netstandard2.0` / `netstandard2.1` included) carry full source-link mappings — the nuspec is byte-identical before and after removal. This also clears the NU1902 vulnerability advisory on its transitive `Microsoft.Build.Tasks.Git` dependency. Build-time change only; consumers are unaffected.
- Added bilingual XML documentation for the remaining 16 public members of `LogConfiguration`. The library now builds with **0 warnings** on all five target frameworks.
- New xUnit tests: `BraceEscapingTests` (brace behavior with and without arguments, both output formats, both write paths; exception JSON verified by **actually parsing it with `System.Text.Json`** rather than comparing strings), `DuplicateWriteTests` (Error / Fatal / `immediateFlush` written exactly once, other levels unchanged) and `SyncModeWriteTests` (sync write readable immediately, same line as the async dispatcher, no lost lines under concurrency). `AutoFlushLevelTests` (guards against `LogLevel.CustomName = 99` being treated as auto-flush) still passes — 73 tests green.
- The `OzaLog.Test` smoke program gained a third CLI argument `write-mode` (`async` / `sync`, default `async`) so synchronous output can be inspected directly.
- Build verified across all 5 TargetFrameworks (`netstandard2.0` / `netstandard2.1` / `net8.0` / `net9.0` / `net10.0`) with 0 errors and 0 warnings.

---

## [3.1.0] - 2026-05-14

> Three new capabilities: customizable time/thread display, configurable output format (txt/log/json), and a dedicated **Quote** pipeline for high-frequency tick/quote data with Binance-aligned schema. All additions are backward compatible — defaults preserve v3.0 behavior.

### Added

**Customizable Time & Thread Display**
- `LogOptions.TimeFormat` (default `"HH:mm:ss.fff"`) — free-form .NET DateTime format string for the message prefix. Falls back to default on parse failure.
- `LogOptions.ShowThreadId` (default `true`) and `LogOptions.ShowThreadName` (default `false`) — independently toggle thread ID / name in the prefix. When `ShowThreadName=true` but the calling thread has no name (`Thread.Name == null`), the entire thread segment is omitted.
- `LogOptions.HighPrecisionTimestamp` (default `false`) — opt-in `Stopwatch`-hybrid mode that reconstructs µs-level precision from the 1ms cache; raises caller-side ticks read cost from ~5ns to ~30ns.

**Multiple Output Formats**
- `LogOptions.OutputFormat` (default `LogOutputFormat.Txt`) — global format selector: `Txt` / `Log` (same content, different extension) / `Json` (NDJSON with fixed schema `{ts, lv, nm, tid?, tn?, msg, data?}`).
- JSON timestamps emit as epoch_ms integers. Field names use short forms (`lv`, `nm`, `tid`, `tn`) for compactness.

**Quote (Tick/Ticker) Pipeline**
- `LOG.Quote(...)` and `LOG.QuoteTicker(...)` — public API for high-frequency quote/ticker data with field names aligned to **Binance REST 24hr Ticker** schema (`Last`, `LastQty`, `Bid`, `BidQty`, `Ask`, `AskQty`, `Open`, `PrevClose`, `High`, `Low`, `Volume`, `QuoteVolume`).
- `QuoteRecord` (public `readonly struct`) — A2 core API for zero-allocation enqueue. Convenience A1 overloads for the common cases (tick only / bid+ask / full ticker / ticker+extras).
- `QuoteOptions` (opt-in via `opt.ConfigureQuote(q => q.Enable = true)`, default off) — independent async pipeline with its own dispatcher, queue, and `FileStreamPool`. Configurable `OutputFormat` (Txt/Log/Json), `MaxOpenStreams` (default 500), `MaxQueueSize` (default 50000), `MaxBatchSize`, `FlushIntervalMs`, `OnDropped(long)` callback.
- `QuoteRecord.Extras` (`IReadOnlyDictionary<string, object>`) for flexible attributes and `QuoteRecord.ExtrasJson` (raw pre-serialized JSON string for the zero-overhead path) — mutually exclusive; setting both throws `ArgumentException` at the call site.
- File naming: `{baseDir}/{LogPath}/{yyyyMMdd}/{QuotePath}/{Bucket}_{Symbol}_Quote.{ext}` — no nested subdirectories.
- Symbol/bucket sanitization: file-system-invalid characters (`/ \ : * ? " < > |`) are automatically replaced with `-` in filenames; the original symbol/bucket text is preserved in the file content.

**Tests**
- Four new xUnit test files covering custom time formats, NDJSON formatting, Quote schema/error scenarios, and filename sanitization (48 tests total, all passing).
- `OzaLog.Test/Program.cs` rewritten to a comprehensive v3.1 smoke-test covering every API surface and error path in a single linear run, with optional CLI args for format selection (`txt`/`log`/`json`).

### Improved

- `LogItem` carries `ThreadName` so the dispatcher thread can render the calling thread's name (previously unavailable post-enqueue).
- All Quote API overloads funnel through `LOG.Quote(in QuoteRecord)` for centralized validation. Errors (null/empty Symbol or Bucket, `Extras`/`ExtrasJson` both set, `Extras` key colliding with a reserved field) throw `ArgumentException` **synchronously** on the calling thread — not deferred to the dispatcher.
- `LogFormatter` retains a fast path for the default `HH:mm:ss.fff` format (hand-written, zero-allocation); other formats route through `DateTime.ToString` with `FormatException` fallback to default.
- `FileStreamPool` supports per-output extension (`.txt` / `.log` / `.json`) with corresponding part-detection logic for size-based file splitting.

### Technical

- `System.Text.Json`: bumped from `8.0.5` → `9.0.16` for `netstandard2.0` / `netstandard2.1` targets (`net8.0` / `net9.0` / `net10.0` still use the BCL built-in — zero NuGet dependencies).
- `Microsoft.SourceLink.GitHub`: bumped from `8.0.0` → `10.0.300` (build-only, `PrivateAssets=all`, no consumer impact).
- New internal types: `JsonLogFormatter`, `QuoteFormatter`, `QuoteFileStreamPool`, `QuoteLogHandler`. Quote pipeline runs entirely in parallel with the main `AsyncLogHandler` — they share no locks or stream pools.
- Build verified across all 5 TargetFrameworks (`netstandard2.0` / `netstandard2.1` / `net8.0` / `net9.0` / `net10.0`) with 0 errors.

---

## [3.0.1] - 2026-05-09

> Metadata + repository improvements release. **No library code changes** — the OzaLog assembly is byte-identical to v3.0.0 (Deterministic build).

### Improved
- **NuGet package metadata refreshed**: cleaner `Description` (highlights `LOG.Info_Log("...")` API + HFT pipeline + zero dependencies + crypto tick stream use case), updated `PackageTags` (added `ozalog`, `hft`, `high-performance`, `zero-dependency`; removed misleading `nlog` tag), polished `Title`.
- `PackageReleaseNotes` now uses absolute GitHub URLs for cross-references (NuGet doesn't render relative paths).

### Technical
- **New project website**: Nuxt 4 + @nuxt/content + Tailwind CSS, deployed to GitHub Pages → <https://ozakboy.github.io/OzaLog/>
- **Repository documentation restructured**: all user-facing docs moved to `docs/{en,zh-TW}/` bilingual tree (`changelog.md`, `migration.md`, plus templates for `getting-started.md`, `configuration.md`, `api.md`, `async-pipeline.md`, `benchmarks.md`).
- GitHub Actions auto-deploys the site on push to main.
- Sponsor page added with USDT (BEP20) wallet + Binance Pay QR.
- `uplog` release flow extended: now also creates GitHub Release and pushes to NuGet.org automatically.

### Notes
- For migration from v2.x see [migration guide](./migration.md).

---

## [3.0.0] - 2026-05-09

### Breaking Changes
- **Package renamed**: `Ozakboy.NLOG` → `OzaLog`. The previous package on NuGet is deprecated and points here. See [migration guide](./migration.md) for the upgrade guide.
- **Namespace renamed**: `ozakboy.LOG` → `OzaLog`. All `using` statements in consumer code must be updated.
- **Removed TargetFrameworks**: dropped `.NET Framework 4.6.2`, `net6.0`, `net7.0` (all EOL). Now supports `netstandard2.0`, `netstandard2.1`, `net8.0`, `net9.0`, `net10.0`.
- **Enum typo fixed**: `LogLevel.CostomName` → `LogLevel.CustomName`. The public method `LOG.CustomName_Log(...)` was already correctly named — only the underlying enum value was renamed.

### Added
- HFT-grade async pipeline: `ConcurrentQueue<struct LogItem>` + persistent FileStream pool + 1ms cached timestamp + drop-oldest backpressure.
- `LogOptions.EnableGlobalExceptionCapture` (default `false`) — opt-in subscription to `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`, auto-logs at Fatal level with synchronous immediate flush.
- `LogOptions.MaxOpenFileStreams` (default `100`) — LRU upper bound; exceeding closes the least recently written stream.
- `LogOptions.DiskFlushIntervalMs` (default `100`) — periodic `Flush()` interval for persistent FileStreams.
- `LogOptions.OnDropped` (default `null`) — callback invoked when the async queue drops the oldest item under backpressure.
- `OzaLog.Tests/` — xUnit test project covering concurrency, LRU, day rollover, backpressure, GlobalExceptionCapture toggle, and format correctness.
- `OzaLog.Benchmarks/` — BenchmarkDotNet project comparing OzaLog with ZLogger, ZeroLog, and Serilog.
- `MIGRATION.md` — upgrade guide from `Ozakboy.NLOG` v2.x.

### Improved
- Zero NuGet dependencies on `net8.0` / `net9.0` / `net10.0` (System.Text.Json is built into the BCL on these targets).
- Formatting work moved off the calling thread — callers only enqueue the raw tuple `(level, name, message, args, ticks, threadId)`; no `string.Format` on the hot path.
- Persistent FileStreams eliminate per-batch open/close, reducing syscall cost to near-zero.
- Day rollover handled inline in the dispatcher (compares cached ticks date vs. stream date).
- Expired-log cleanup moved to a background timer (was on the hot path in v2.x).

### Fixed
- Double-format bug in `LOG.cs` where `Console.WriteLine(formattedMessage, args)` could throw `FormatException` if the formatted message coincidentally contained `{0}`-style tokens.
- Auto-flush level selection — `Error` and `Fatal` now correctly trigger immediate flush regardless of the caller's `immediateFlush` argument.

### Technical
- `LogItem` changed from class to `readonly struct` — zero GC on the hot path.
- New `Core/TimestampCache.cs` — background timer updates `volatile long _currentTicks` every 1ms; callers only read.
- New `Core/FileStreamPool.cs` — persistent FileStreams keyed by `(level, name)` with LRU eviction.
- New `Core/LogRetentionCleaner.cs` — background expired-log cleanup, off the hot path.
- New `Core/GlobalExceptionCapture.cs` — opt-in global exception subscription.
- Build verified across all 5 TargetFrameworks with 0 warnings / 0 errors.

---

## [2.1.0] - 2024

### Added
- Added support for .NET 8.0
- Introduced async logging with configurable batch processing
- Added customizable directory structure for different log levels
- Added support for custom log types (`CustomName_Log`)
- Added console output support

### Improved
- Enhanced file management with automatic log rotation
- Enhanced exception handling and serialization
- Improved configuration system with more options
- Better handling of file paths across operating systems

### Technical
- Improved thread safety and performance
- Implemented intelligent file size management

---

> History prior to 2.1.0 is not fully tracked here. See git history and the NuGet package page for details.
