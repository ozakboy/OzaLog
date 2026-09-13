# OzaLog

[![nuget](https://img.shields.io/badge/nuget-OzaLog-blue)](https://www.nuget.org/packages/OzaLog/)
[![github](https://img.shields.io/badge/github-OzaLog-blue)](https://github.com/ozakboy/OzaLog/)

[English](README.md) | [繁體中文](README_zh-TW.md)

> **免責聲明**：OzaLog 與 [NLog](https://www.nuget.org/packages/NLog)（jkowalski 維護的套件）**完全沒有關係**。本套件為獨立函式庫。
>
> **從 `Ozakboy.NLOG` v2.x 升級？** 請看[升級指南](docs/zh-TW/migration.md)。舊套件已棄用並更名為 `OzaLog`。
>
> **版本歷史**：[English](docs/en/changelog.md) · [繁體中文](docs/zh-TW/changelog.md)

極簡的 .NET 本地檔案 logging 函式庫，採用最簡單的靜態 API。不需 DI、不需 LoggerFactory，net8+ 零 NuGet 依賴。為高並發多執行緒場景設計（例如加密貨幣報價串流）。

## 為什麼選 OzaLog

- **API 極簡** — `LOG.Info_Log("hello")` 一行就能用，不需任何設定
- **單一目的** — 只做本地檔案 logging，沒有抽象層
- **零依賴** — net8+ 目標完全不需 NuGet
- **HFT 等級熱路徑** — 持久化 FileStream pool + LRU 上限 + 1ms 時間戳快取 + struct 隊列 + drop oldest 反壓
- **HFT 場景下比 NLog 與 Serilog 都快**（見下方 [Benchmarks](#benchmarks)）

## OzaLog 不是

- ❌ 不是 `Microsoft.Extensions.Logging` provider — 不支援 `ILogger<T>` 注入
- ❌ 不是結構化 logger — 不捕捉 `{Property}` placeholder
- ❌ 不是多目標 logger — 只寫檔（沒有 Console / Database / 遠端 sink）
- ❌ 不能用 XML / appsettings.json 配置 — 只支援程式內配置

如果以上任一是硬性需求，請改用 NLog 或 Serilog。

## 支援框架

- .NET 8.0 / 9.0 / 10.0（LTS + 當前）
- .NET Standard 2.0 / 2.1（舊版相容）

> **OzaLog v3.0 已移除**：.NET Framework 4.6.2、.NET 6.0、.NET 7.0（皆已 EOL）。

## 安裝

```bash
dotnet add package OzaLog
```

或使用 Package Manager Console：

```bash
Install-Package OzaLog
```

## 快速開始

```csharp
using OzaLog;

LOG.Info_Log("Hello, World!");
LOG.Error_Log("發生錯誤");
LOG.CustomName_Log("BTC", "tick: 67890.12");
```

就這樣 — 不需呼叫 Configure（預設值即可運作）。需進階配置時：

```csharp
LOG.Configure(o =>
{
    o.KeepDays = -7;                          // 保留最近 7 天的 log
    o.SetFileSizeInMB(50);                    // 單檔超過 50MB 自動分割
    o.EnableAsyncLogging = true;              // 預設 true
    o.EnableConsoleOutput = true;             // 同時輸出到 console
    o.MaxOpenFileStreams = 100;               // LRU 上限
    o.DiskFlushIntervalMs = 100;              // 定期 flush 間隔
    o.EnableGlobalExceptionCapture = false;   // 是否啟用全域意外攔截（預設 false）
    o.OnDropped = () => Interlocked.Increment(ref _dropCount);
    o.ConfigureAsync(a =>
    {
        a.MaxBatchSize = 1000;
        a.MaxQueueSize = 100_000;
        a.FlushIntervalMs = 100;
    });
});
```

## 檔案配置

```
{AppRoot}/
└── logs/
    └── 20260509/                 # yyyyMMdd 日期資料夾
        └── LogFiles/             # 類別子資料夾（可自訂）
            ├── Info_Log.txt
            ├── Error_Log.txt
            ├── BTC_Log.txt       # CustomName log 寫到這
            └── ETH_Log.txt
```

用 `options.LogPath` 改根目錄，用 `options.TypeDirectories.*Path` 給每個層級獨立資料夾。

## 寫入方法

每個層級都有這 5 個多載：

```csharp
LOG.Info_Log(string message);
LOG.Info_Log(string message, bool writeTxt);
LOG.Info_Log(string message, string[] args, bool writeTxt = true, bool immediateFlush = false);
LOG.Info_Log<T>(T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;
LOG.Info_Log<T>(string message, T obj, bool writeTxt = true, bool immediateFlush = false) where T : class;
```

可用層級：`Trace_Log` / `Debug_Log` / `Info_Log` / `Warn_Log` / `Error_Log` / `Fatal_Log`。

自訂 log 類型：

```csharp
LOG.CustomName_Log("BTC", "tick: 67890.12");        // → BTC_Log.txt
LOG.CustomName_Log("API", "外部呼叫");              // → API_Log.txt
```

## 異常記錄

```csharp
try { /* 程式碼 */ }
catch (Exception ex)
{
    LOG.Error_Log(ex);                            // 自動序列化為 JSON
    LOG.Error_Log("操作上下文", ex);              // 含自訂訊息
}
```

異常細節（Type、Message、StackTrace、InnerException、Data dictionary、額外屬性）會自動捕捉。

## 全域意外攔截（opt-in）

```csharp
LOG.Configure(o => o.EnableGlobalExceptionCapture = true);
```

訂閱 `AppDomain.UnhandledException` 與 `TaskScheduler.UnobservedTaskException`，將任何未攔截的例外以 `Fatal` 等級**同步寫入並 immediate flush**，確保 crash 前 log 落盤。

> 注意：本功能不涵蓋 WPF/WinForms UI thread 異常與 ASP.NET Core middleware 異常 — 那些需要應用程式自行 hook。

## Flush 與 Shutdown（v3.3+）

預設是非同步寫入，所以剛寫完的那筆還不在磁碟上。當行程接下來可能被強制終止（容器停止、整個程式的 `finally`、crash handler），或你正要讀剛才寫進去的檔案時，先要一道屏障：

```csharp
LOG.Flush();                 // 擋住，直到目前為止寫的每一筆都落到磁碟
await LOG.FlushAsync();      // 同上，但不佔住執行緒
```

`Flush()` 在佇列排空且檔案落盤後回 `true`，逾時回 `false`（預設 10 秒，要自訂用 `LOG.Flush(timeoutMs)`）。呼叫端執行緒會一起幫忙排空，而不是乾等 dispatcher 的週期，所以這是真的屏障、不是換個寫法的 sleep：寫 1000 筆、呼叫 `Flush()`，它回來的當下檔案就是 1000 行。

行程收尾時把 logger 關掉：

```csharp
LOG.Shutdown();              // 排空落盤、停掉背景工作與計時器、關閉所有日誌檔
await LOG.ShutdownAsync();   // 非同步版本
```

`Shutdown()` 之後**所有寫入一律靜默丟棄** — 不擲例外，也不印 Console。背景服務型的套件不能在自己收尾之後把宿主弄掛，而已經在收尾的宿主也不該為了每一行 log 加防呆。目前狀態可用 `LOG.IsShutdown` 查詢。

`Shutdown()` 冪等：第一次回 `true`，之後回 `false`（那不是錯誤）；與內建的 `ProcessExit` 收尾不論誰先誰後都安全。

要在同一個行程裡重新開始寫，呼叫 `Configure` — `Shutdown` 之後才允許，也只有那個時候允許；管線還活著時 `Configure` 維持不可重入。重啟時配置回到預設值，避免上一輪的設定殘留成看不見的隱藏狀態：

```csharp
LOG.Shutdown();
LOG.Configure(o => o.LogPath = "logs2");   // 管線重啟，IsShutdown 回到 false
```

以上方法在任何路徑都不擲例外。`FlushAsync` / `ShutdownAsync` 在每個 TFM（含 `netstandard2.0`）都回傳 `Task<bool>`；取消時回 `false`，不擲 `OperationCanceledException`。

## 已知限制

- **同一毫秒內寫入的多筆，不保證依序落檔。** 時間戳快取的解析度是 1 ms，佇列又是分批排空的，同一毫秒的兩筆在檔案裡可能任一順序。事後需要排序請開 `HighPrecisionTimestamp = true`。
- **同步模式（`EnableAsyncLogging = false`）沒有過期清理。** `KeepDays` 由背景清理器負責，而它只在非同步管線啟動；請自行清理舊的日期目錄，或改用非同步模式。
- **日誌檔以 `FileShare.ReadWrite` 開啟附加**（v3.3.0 起），外部行程可以邊跑邊讀、也可以用讀寫模式開啟。多個寫入端同時附加同一個檔案時，OzaLog 不負責協調。

## Benchmarks

對 ZLogger 2.5.10、ZeroLog 2.6.1、Serilog 4.2.0 + Sinks.File 6.0.0 比較。.NET 10.0.7、AMD Ryzen 9 9950X3D、BenchmarkDotNet 0.14：

### S1 — 單筆短訊息

| Method | Mean | Allocated |
|--------|------|-----------|
| **OzaLog** | **65.96 ns** | 151 B |
| ZLogger | 219.53 ns | 278 B |
| ZeroLog | 12.19 ns | 0 B |
| Serilog | 168.87 ns | 160 B |

### S3 — HFT 8 thread × 50 商品 × 2000 logs = 80 萬筆

| Method | Mean | Allocated |
|--------|------|-----------|
| **OzaLog** | **3,047 μs** | 3.94 MB |
| ZLogger | 4,996 μs | 5.21 KB |
| ZeroLog | 649 μs | 3.35 KB |
| Serilog | 11,092 μs | 8.27 MB |

**結論**：OzaLog 在兩個場景都**比 ZLogger 與 Serilog 快**。ZeroLog 純效能贏（用 source generator 達到真零分配），但 OzaLog 的靜態 API 更簡單。

→ 自己跑 benchmark：`dotnet run -c Release --project OzaLog.Benchmarks`

## 授權

MIT License

## 回報與支援

- GitHub Issues：[回報問題](https://github.com/ozakboy/OzaLog/issues)
- Pull Requests：[貢獻程式碼](https://github.com/ozakboy/OzaLog/pulls)
