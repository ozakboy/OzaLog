# OzaLog

[![nuget](https://img.shields.io/badge/nuget-OzaLog-blue)](https://www.nuget.org/packages/OzaLog/)
[![github](https://img.shields.io/badge/github-OzaLog-blue)](https://github.com/ozakboy/OzaLog/)

[English](README.md) | [繁體中文](README_zh-TW.md)

> **Disclaimer:** OzaLog is **not related** to [NLog](https://www.nuget.org/packages/NLog) (jkowalski's package). This is a separate, independent library.
>
> **Migrating from `Ozakboy.NLOG` v2.x?** See [Migration Guide](docs/en/migration.md). The previous package has been deprecated and renamed to `OzaLog`.
>
> **Changelog**: [English](docs/en/changelog.md) · [繁體中文](docs/zh-TW/changelog.md)

A lean, lightweight .NET local file logging library with the simplest possible static API. No DI, no LoggerFactory, zero NuGet dependencies on net8+. Designed for high-throughput multi-threaded scenarios such as cryptocurrency tick streams.

## Why OzaLog

- **Simplest possible API** — `LOG.Info_Log("hello")` works without any setup
- **Single purpose** — local file logging only, no abstractions
- **Zero dependencies** — net8+ targets have no NuGet dependencies
- **HFT-tuned hot path** — persistent FileStream pool with LRU eviction, cached timestamp, struct-based queue, drop-oldest backpressure
- **Faster than NLog and Serilog** in HFT-style multi-thread scenarios (see [Benchmarks](#benchmarks) below)

## What OzaLog is NOT

- ❌ Not a `Microsoft.Extensions.Logging` provider — does not integrate with `ILogger<T>` DI
- ❌ Not a structured logger — no `{Property}` placeholder capture
- ❌ Not a multi-target logger — file output only (no Console / Database / remote sinks)
- ❌ Not configurable via XML / appsettings.json — programmatic configuration only

If any of the above is a hard requirement, use NLog or Serilog instead.

## Supported Frameworks

- .NET 8.0 / 9.0 / 10.0 (LTS + current)
- .NET Standard 2.0 / 2.1 (legacy compatibility)

> **Dropped in OzaLog v3.0**: .NET Framework 4.6.2, .NET 6.0, .NET 7.0 (all EOL).

## Installation

```bash
dotnet add package OzaLog
```

Or via Package Manager Console:

```bash
Install-Package OzaLog
```

## Quick Start

```csharp
using OzaLog;

LOG.Info_Log("Hello, World!");
LOG.Error_Log("Something went wrong");
LOG.CustomName_Log("BTC", "tick: 67890.12");
```

That's it — no Configure call required (defaults work). For advanced configuration:

```csharp
LOG.Configure(o =>
{
    o.KeepDays = -7;                          // keep last 7 days of logs
    o.SetFileSizeInMB(50);                    // split files at 50 MB
    o.EnableAsyncLogging = true;              // default true
    o.EnableConsoleOutput = true;             // also write to console
    o.MaxOpenFileStreams = 100;               // LRU upper bound
    o.DiskFlushIntervalMs = 100;              // periodic flush
    o.EnableGlobalExceptionCapture = false;   // opt-in: auto-log unhandled exceptions
    o.OnDropped = () => Interlocked.Increment(ref _dropCount);
    o.ConfigureAsync(a =>
    {
        a.MaxBatchSize = 1000;
        a.MaxQueueSize = 100_000;
        a.FlushIntervalMs = 100;
    });
});
```

## Log File Layout

```
{AppRoot}/
└── logs/
    └── 20260509/                 # yyyyMMdd date folder
        └── LogFiles/             # type subfolder (configurable)
            ├── Info_Log.txt
            ├── Error_Log.txt
            ├── BTC_Log.txt       # CustomName logs go here
            └── ETH_Log.txt
```

Use `options.LogPath` to change the root, and `options.TypeDirectories.*Path` to give each level its own folder.

## Logging Methods

Every level has the same 5 overloads:

```csharp
LOG.Info_Log(string message);
LOG.Info_Log(string message, bool writeTxt);
LOG.Info_Log(string message, string[] args, bool writeTxt = true, bool immediateFlush = false);
LOG.Info_Log<T>(T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;
LOG.Info_Log<T>(string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;
```

Available levels: `Trace_Log` / `Debug_Log` / `Info_Log` / `Warn_Log` / `Error_Log` / `Fatal_Log`.

For custom log buckets:

```csharp
LOG.CustomName_Log("BTC", "tick: 67890.12");        // → BTC_Log.txt
LOG.CustomName_Log("API", "external call");         // → API_Log.txt
```

## Exception Logging

```csharp
try { /* code */ }
catch (Exception ex)
{
    LOG.Error_Log(ex);                            // serialized as JSON
    LOG.Error_Log("operation context", ex);       // with custom message
}
```

Exception details (Type, Message, StackTrace, InnerException, Data dictionary, additional properties) are automatically captured.

## Global Exception Capture (opt-in)

```csharp
LOG.Configure(o => o.EnableGlobalExceptionCapture = true);
```

Subscribes to `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` and logs them as `Fatal` with synchronous flush to ensure crash logs land on disk.

> Note: This does not cover WPF/WinForms UI thread exceptions or ASP.NET Core middleware exceptions — those need to be hooked separately by the application.

## Flush & Shutdown (v3.3+)

Logging is asynchronous by default, so an entry you just wrote is not on disk yet. Whenever the process is about to be killed — a container stop, a `finally` around the whole app, a crash handler — or you are about to read the file you just wrote to, ask for a barrier:

```csharp
LOG.Flush();                 // blocks until everything written so far is on disk
await LOG.FlushAsync();      // same, without blocking the thread
```

`Flush()` returns `true` once the queue has drained and the files have been flushed to disk, `false` on timeout (10 s by default — pass your own with `LOG.Flush(timeoutMs)`). The calling thread helps drain the queue rather than waiting out the dispatcher's interval, so this is a real barrier and not a sleep: write 1000 entries, call `Flush()`, and the file has 1000 lines by the time it returns.

At the end of the process, shut the logger down:

```csharp
LOG.Shutdown();              // flush, stop the background worker and timers, close every file
await LOG.ShutdownAsync();   // async counterpart
```

After `Shutdown()`, **every logging call is silently discarded** — no exception, no console output. A logger must not take its host down on the way out, and a host that is already shutting down should not have to guard every log statement. `LOG.IsShutdown` reports the current state.

`Shutdown()` is idempotent: the first call returns `true`, later calls return `false` (which is not an error), and it interleaves safely with the built-in `ProcessExit` cleanup in either order.

To log again in the same process, call `Configure` — allowed after `Shutdown`, and only then; while the pipeline is alive `Configure` stays non-reentrant. The options reset to their defaults on restart, so the previous round's settings cannot linger as invisible state:

```csharp
LOG.Shutdown();
LOG.Configure(o => o.LogPath = "logs2");   // pipeline restarts, IsShutdown is false again
```

None of these methods throw, on any path. `FlushAsync` / `ShutdownAsync` return `Task<bool>` on every target framework, `netstandard2.0` included, and a cancelled token returns `false` instead of throwing `OperationCanceledException`.

## Known Limitations

- **Entries written within the same millisecond are not guaranteed to land in file order.** The timestamp cache has 1 ms resolution and the queue is drained in batches, so two entries sharing a millisecond may appear in either order. Set `HighPrecisionTimestamp = true` if you need to reorder entries after the fact.
- **Synchronous mode (`EnableAsyncLogging = false`) has no retention cleanup.** `KeepDays` is enforced by a background cleaner that only the async pipeline starts. Clean old date directories yourself, or use asynchronous mode.
- **Log files are opened for append with `FileShare.ReadWrite`** (since v3.3.0), so other processes can read — and open read-write — a file while it is being written. Other writers appending to the same file at the same time are not coordinated by OzaLog.

## Benchmarks

Measured against ZLogger 2.5.10, ZeroLog 2.6.1, Serilog 4.2.0 + Sinks.File 6.0.0 on .NET 10.0.7 (AMD Ryzen 9 9950X3D, BenchmarkDotNet 0.14):

### S1 — Single short message

| Method | Mean | Allocated |
|--------|------|-----------|
| **OzaLog** | **65.96 ns** | 151 B |
| ZLogger | 219.53 ns | 278 B |
| ZeroLog | 12.19 ns | 0 B |
| Serilog | 168.87 ns | 160 B |

### S3 — HFT 8 thread × 50 products × 2000 logs = 800 K writes

| Method | Mean | Allocated |
|--------|------|-----------|
| **OzaLog** | **3,047 μs** | 3.94 MB |
| ZLogger | 4,996 μs | 5.21 KB |
| ZeroLog | 649 μs | 3.35 KB |
| Serilog | 11,092 μs | 8.27 MB |

**Verdict**: OzaLog is **faster than ZLogger and Serilog** in both scenarios. ZeroLog wins on raw speed (it uses source generators for true zero-allocation), but OzaLog's static API is simpler.

→ Run benchmarks yourself: `dotnet run -c Release --project OzaLog.Benchmarks`

## License

MIT License

## Support

- GitHub Issues: [Report Issues](https://github.com/ozakboy/OzaLog/issues)
- Pull Requests: [Contribute Code](https://github.com/ozakboy/OzaLog/pulls)
