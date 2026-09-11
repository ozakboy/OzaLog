using OzaLog;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

// Windows cmd 預設 OEM encoding 會把中文輸出成亂碼，強制改 UTF-8
Console.OutputEncoding = Encoding.UTF8;

// ═══════════════════════════════════════════════════════════════════════════
// OzaLog v3.1 — Console smoke / 壓測 程式
// 涵蓋 v3.0 所有功能 + v3.1 新功能:
//   v3.0: 所有 LogLevel、格式化、物件序列化、異常記錄、CustomName、
//         HFT 多執行緒、LRU、queue 飽和 drop、immediateFlush
//   v3.1: 自訂 TimeFormat、ShowThreadId/Name、HighPrecisionTimestamp、
//         OutputFormat (Txt/Log/Json)、Quote pipeline (所有多載 + 錯誤情境)
//
// 使用方式:
//   dotnet run --project OzaLog.Test -- [main-fmt] [quote-fmt] [write-mode]
//     main-fmt:   txt | log | json   (預設 txt)
//     quote-fmt:  txt | log | json   (預設 json)
//     write-mode: async | sync       (預設 async;sync = EnableAsyncLogging=false)
//
//   範例:
//     dotnet run --project OzaLog.Test                   → 主 txt,報價 json,非同步
//     dotnet run --project OzaLog.Test -- json json      → 全 JSON
//     dotnet run --project OzaLog.Test -- log txt        → 主 log,報價 txt
//     dotnet run --project OzaLog.Test -- txt json sync  → 主 logger 走同步寫入路徑
// ═══════════════════════════════════════════════════════════════════════════

// ─── 命令列引數 ──────────────────────────────────────────────────────────
var mainFormat = args.Length >= 1 ? ParseLogFormat(args[0]) : LogOutputFormat.Txt;
var quoteFormat = args.Length >= 2 ? ParseQuoteFormat(args[1]) : QuoteOutputFormat.Json;
var asyncLogging = args.Length < 3 || ParseWriteMode(args[2]);

Header("OzaLog v3.2.0 console smoke test");
Console.WriteLine($"  PID:           {Environment.ProcessId}");
Console.WriteLine($"  BaseDir:       {AppContext.BaseDirectory}");
Console.WriteLine($"  Runtime:       {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"  Main format:   {mainFormat}");
Console.WriteLine($"  Quote format:  {quoteFormat}");
Console.WriteLine($"  Write mode:    {(asyncLogging ? "async(dispatcher)" : "sync(呼叫端直接寫入)")}");
Console.WriteLine();

// ─── v3.1 設定:涵蓋所有新選項 ────────────────────────────────────────────
long droppedCount = 0;
long quoteDroppedCount = 0;
LOG.Configure(o =>
{
    // ── v3.0 既有選項 ──
    o.KeepDays = -7;
    o.SetFileSizeInMB(10);
    o.EnableAsyncLogging = asyncLogging;
    o.EnableConsoleOutput = false;
    o.MaxOpenFileStreams = 50;              // 故意低於後續 100 商品數,測 LRU
    o.DiskFlushIntervalMs = 100;
    o.EnableGlobalExceptionCapture = false;
    o.OnDropped = () => Interlocked.Increment(ref droppedCount);
    o.ConfigureAsync(a =>
    {
        a.MaxBatchSize = 500;
        a.MaxQueueSize = 5000;
        a.FlushIntervalMs = 100;
    });

    // ── v3.1 新增選項 ──
    o.OutputFormat = mainFormat;
    o.TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";  // 自訂格式(超出預設 HH:mm:ss.fff)
    o.ShowThreadId = true;
    o.ShowThreadName = true;                   // 顯示 thread name (若有設定)
    o.HighPrecisionTimestamp = true;           // 啟用 µs 精度 (Stopwatch hybrid)

    o.ConfigureQuote(q =>
    {
        q.Enable = true;                       // ⚠ 必填:報價 pipeline 預設 OFF
        q.OutputFormat = quoteFormat;
        q.QuotePath = "Quotes";
        q.MaxOpenStreams = 200;
        q.MaxQueueSize = 50_000;
        q.FlushIntervalMs = 100;
        q.MaxBatchSize = 500;
        q.OnDropped = (n) => Interlocked.Add(ref quoteDroppedCount, n);
    });
});

// ─── 1. 各 LogLevel 基本寫入 ─────────────────────────────────────────────
Header("1. 各 LogLevel 基本寫入");
LOG.Trace_Log("trace 級別測試");
LOG.Debug_Log("debug 級別測試");
LOG.Info_Log("info  級別測試");
LOG.Warn_Log("warn  級別測試");
LOG.Error_Log("error 級別測試");
LOG.Fatal_Log("fatal 級別測試");
Tick("已寫入 6 種 LogLevel");

// ─── 2. 格式化參數 ───────────────────────────────────────────────────────
Header("2. 格式化參數 {N} placeholder");
LOG.Info_Log("user {0} performed {1}", new[] { "alice", "login" });
LOG.Warn_Log("retry {0}/{1} failed: {2}", new[] { "3", "5", "timeout" });
Tick("含 {N} placeholder 的訊息已格式化寫入");

// ─── 3. 物件序列化（JSON）─────────────────────────────────────────────
Header("3. 物件序列化 JSON");
var quoteObj = new { Symbol = "BTC/USDT", Price = 67890.12m, Volume = 1234, Best5 = new[] { 67889m, 67888m, 67887m } };
LOG.Info_Log(quoteObj);
LOG.Info_Log("含訊息的物件 log", quoteObj);
Tick("匿名物件已序列化為 JSON(JSON 模式下會展開為 data 欄位)");

// ─── 4. 異常記錄 ─────────────────────────────────────────────────────────
Header("4. 異常記錄");
try { throw new InvalidOperationException("scenario 4a 一般異常 demo"); }
catch (Exception ex) { LOG.Error_Log(ex); }

try { throw new ErrorMessageException("scenario 4b 自訂異常 demo"); }
catch (ErrorMessageException ex) { LOG.Warn_Log(ex); }

try
{
    try { throw new InvalidOperationException("最內層"); }
    catch (Exception inner) { throw new ErrorMessageException("中層包裝", inner); }
}
catch (ErrorMessageException ex)
{
    LOG.Error_Log("scenario 4c 巢狀異常 demo", ex);
}

try { throw new ArgumentNullException("priceFeed", "報價串流為 null"); }
catch (ArgumentNullException ex) { LOG.Fatal_Log(ex); }

Tick("4 種異常情境已記錄為 JSON 結構");

// ─── 5. CustomName 寫入 ─────────────────────────────────────────────────
Header("5. CustomName(每商品獨立檔案)");
LOG.CustomName_Log("BTC", "scenario 5: BTC tick 67890.12");
LOG.CustomName_Log("ETH", "scenario 5: ETH tick  3567.45");
LOG.CustomName_Log("SOL", "scenario 5: SOL tick   234.78");
LOG.CustomName_Log("API", "scenario 5: 外部呼叫 GET /price");
Tick("4 個 CustomName log 已寫入各自檔案");

// ─── 6. HFT 多執行緒 × 多商品壓測 ─────────────────────────────────────
Header("6. HFT 多執行緒 × 多商品壓測(主 log)");
const int productCount = 100;
const int threadCount = 8;
const int logsPerThread = 2000;
var products = Enumerable.Range(0, productCount).Select(i => $"PROD{i:D3}").ToArray();
var totalLogs = 0L;

Console.WriteLine($"  配置: {threadCount} threads × {logsPerThread} logs each, {productCount} 商品輪寫");
Console.WriteLine($"  目標總筆數: {threadCount * logsPerThread:N0}");
Console.WriteLine($"  MaxOpenFileStreams = 50 → 預期會看到 LRU 關閉冷檔");

var sw = Stopwatch.StartNew();
var threads = new List<Thread>();
for (int t = 0; t < threadCount; t++)
{
    var threadIdx = t;
    var th = new Thread(() =>
    {
        var rng = new Random(threadIdx * 1000 + 7);
        for (int i = 0; i < logsPerThread; i++)
        {
            var p = products[rng.Next(products.Length)];
            LOG.CustomName_Log(p, $"t{threadIdx} i{i} bid:{rng.NextDouble() * 100:F4} ask:{rng.NextDouble() * 100:F4}");
            Interlocked.Increment(ref totalLogs);
        }
    })
    { IsBackground = true, Name = $"hft-{t}" };
    threads.Add(th);
    th.Start();
}
foreach (var th in threads) th.Join();
sw.Stop();

var rate = totalLogs * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds);
Console.WriteLine($"  實際寫入: {totalLogs:N0} 筆");
Console.WriteLine($"  耗時:     {sw.ElapsedMilliseconds:N0} ms");
Console.WriteLine($"  吞吐:     {rate:N0} logs/sec(呼叫端入隊速率)");
Console.WriteLine($"  drop 計數: {droppedCount:N0}");

// ─── 7. queue 飽和 drop oldest 壓測 ─────────────────────────────────────
Header("7. queue 飽和 drop oldest 壓測(主 log)");
var dropBefore = droppedCount;
Console.WriteLine($"  queue 容量 5000;連續 fire 50000 筆,不等 dispatcher");
sw.Restart();
for (int i = 0; i < 50_000; i++)
    LOG.Info_Log($"burst {i}");
sw.Stop();
var enqueueRate = 50_000 * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds);
Console.WriteLine($"  純 enqueue 50K 筆耗時: {sw.ElapsedMilliseconds} ms({enqueueRate:N0} logs/sec)");
Thread.Sleep(800);
var dropAfter = droppedCount;
Console.WriteLine($"  本批 drop 數:        {dropAfter - dropBefore:N0}");
Console.WriteLine($"  累計 drop 數:        {dropAfter:N0}");

// ─── 8. immediateFlush 路徑 ──────────────────────────────────────────────
Header("8. immediateFlush 路徑");
LOG.Error_Log("scenario 8: Error 自動 immediate flush(>= Error 級別)");
LOG.Info_Log("scenario 8: 手動 immediate flush", new[] { "manual" }, true, true);
LOG.CustomName_Log("CRITICAL", "scenario 8: CustomName immediate", new[] { "true" }, true, true);
Tick("3 筆 immediateFlush log 已立即寫入並 flush 到磁碟");

// ─── 9. v3.1 設定資訊顯示 ────────────────────────────────────────────────
Header("9. v3.1 設定資訊顯示");
var currentOpts = LOG.GetCurrentOptions();
Console.WriteLine($"  OutputFormat:           {currentOpts.OutputFormat}");
Console.WriteLine($"  TimeFormat:             \"{currentOpts.TimeFormat}\"");
Console.WriteLine($"  ShowThreadId:           {currentOpts.ShowThreadId}");
Console.WriteLine($"  ShowThreadName:         {currentOpts.ShowThreadName}");
Console.WriteLine($"  HighPrecisionTimestamp: {currentOpts.HighPrecisionTimestamp}");
Console.WriteLine($"  Quote.Enable:           {currentOpts.QuoteOptions.Enable}");
Console.WriteLine($"  Quote.OutputFormat:     {currentOpts.QuoteOptions.OutputFormat}");
Console.WriteLine($"  Quote.QuotePath:        {currentOpts.QuoteOptions.QuotePath}");
Console.WriteLine($"  Quote.MaxOpenStreams:   {currentOpts.QuoteOptions.MaxOpenStreams}");
Console.WriteLine($"  Quote.MaxQueueSize:     {currentOpts.QuoteOptions.MaxQueueSize}");
Tick("v3.1 全選項已透過 ReadOnly wrapper 對外可讀");

// ─── 10. v3.1 自訂 TimeFormat / ThreadName 驗證 ─────────────────────────
Header("10. v3.1 自訂 TimeFormat / ThreadName 驗證");
// 設定 thread name 後寫 log,可在輸出檔案看到 [T:tid/Name]
Thread.CurrentThread.Name = "MainThread";
LOG.Info_Log("scenario 10: 在 MainThread 上寫入,應在輸出看到 [T:tid/MainThread]");

var threadWithName = new Thread(() =>
{
    LOG.Info_Log("scenario 10: 在 ConsumerThread 上寫入");
})
{ IsBackground = false, Name = "ConsumerThread" };
threadWithName.Start();
threadWithName.Join();
Tick("已寫入兩筆 log,輸出檔應包含 thread name 區段");

// ─── 11. v3.1 Quote API - A1 強型別便利多載 ─────────────────────────────
Header("11. v3.1 Quote API - A1 強型別便利多載");

var binance = "binance_spot";
var okx = "okx_swap";
var perp = "binance_perp";
var nowTicks = DateTime.Now.Ticks;

// (a) 最簡 tick:只填 Last
LOG.Quote("BTCUSDT", binance, nowTicks, 67890.12m);

// (b) 含 bid/ask
LOG.Quote("ETHUSDT", binance, nowTicks + 1, 3567.45m, bid: 3567.00m, ask: 3568.00m);

// (c) 含 bid/ask + 買賣量
LOG.Quote("SOLUSDT", binance, nowTicks + 2, 234.78m, 234.50m, 1.5m, 235.00m, 2.3m);

// (d) 完整 QuoteTicker(對齊 Binance 24hr Ticker)
LOG.QuoteTicker("BTCUSDT", binance, nowTicks + 3,
    last: 67890.12m, lastQty: 0.05m,
    bid: 67889.0m, bidQty: 1.2m,
    ask: 67891.0m, askQty: 0.8m,
    open: 67500.0m, prevClose: 67450.0m,
    high: 68000.0m, low: 67200.0m,
    volume: 12345.67m, quoteVolume: 838_000_000m);

// (e) QuoteTicker + Extras Dictionary
var extras = new Dictionary<string, object>
{
    ["funding"] = 0.0001m,
    ["openInterest"] = 12_345_678m,
    ["mark"] = 67889.5m,
    ["indexPrice"] = 67888.0m,
};
LOG.QuoteTicker("BTCUSDT", perp, nowTicks + 4,
    last: 67890.12m,
    extras: extras,
    bid: 67889.0m, ask: 67891.0m,
    volume: 12345.67m);

Tick("A1 五種多載已寫入(tick / quote / quote+sizes / ticker / ticker+extras)");

// ─── 12. v3.1 Quote API - A2 struct 多載 ────────────────────────────────
Header("12. v3.1 Quote API - A2 struct 多載");

// (a) 純 struct + 完整欄位
var rec1 = new QuoteRecord(
    symbol: "BTCUSDT",
    bucket: okx,
    ticks: nowTicks + 10,
    last: 67889.5m,
    lastQty: 0.01m,
    bid: 67889.0m, bidQty: 5m,
    ask: 67890.0m, askQty: 3m,
    open: 67500m, prevClose: 67450m,
    high: 68000m, low: 67200m,
    volume: 9876.5m, quoteVolume: 670_000_000m);
LOG.Quote(in rec1);

// (b) struct + ExtrasJson(預序列化字串)
var rec2 = new QuoteRecord(
    symbol: "ETHUSDT",
    bucket: perp,
    ticks: nowTicks + 11,
    last: 3567.45m,
    bid: 3567.00m, ask: 3568.00m,
    extrasJson: "{\"funding\":0.00015,\"mark\":3567.3,\"basis\":-0.05}");
LOG.Quote(in rec2);

// (c) struct + 多種 Extras 型別測試
var richExtras = new Dictionary<string, object>
{
    ["funding"] = 0.0001m,
    ["isBacked"] = true,
    ["count24h"] = 12345L,
    ["mark"] = 67889.5,             // double
    ["lastUpdateUtc"] = DateTime.UtcNow,
    ["source"] = "rest-api-v3",
};
var rec3 = new QuoteRecord(
    symbol: "BTCUSDT",
    bucket: perp,
    ticks: nowTicks + 12,
    last: 67890.5m,
    extras: richExtras);
LOG.Quote(in rec3);

Tick("A2 struct 多載已寫入(完整欄位 / ExtrasJson / 多型別 Extras)");

// ─── 13. v3.1 Quote API - 檔名 sanitize ─────────────────────────────────
Header("13. v3.1 Quote API - 檔名 sanitize(Symbol 含特殊字元)");

// 含 / 的 symbol → 應自動換成 -,檔名應為 binance_spot_BTC-USDT_Quote.{ext}
LOG.Quote("BTC/USDT", binance, nowTicks + 20, 67890.12m);
LOG.Quote("ETH/USDT", binance, nowTicks + 21, 3567.45m);

// 含 : 的 symbol(部分 broker 風格)
LOG.Quote("BTC:USDT", binance, nowTicks + 22, 67890.12m);

// 含 \ 的 bucket
LOG.Quote("LTCUSDT", "broker\\v2", nowTicks + 23, 95.5m);

// 含多個非法字元
LOG.Quote("XRP/USDT:PERP", "binance|spot", nowTicks + 24, 0.55m);

Tick("5 筆含特殊字元的 symbol/bucket 已寫入(檔名應已自動 sanitize 成 '-')");

// ─── 14. v3.1 Quote API - 錯誤情境(try/catch 驗證同步拋例外)──────────
Header("14. v3.1 Quote API - 錯誤情境驗證");

// (a) 空 Symbol → ArgumentException
try
{
    LOG.Quote("", binance, nowTicks + 30, 1m);
    Console.WriteLine("  ✗ 預期會拋例外但沒拋!");
}
catch (ArgumentException ex)
{
    Console.WriteLine($"  ✓ 空 Symbol → ArgumentException: {ex.Message}");
}

// (b) null Symbol → ArgumentException
try
{
    LOG.Quote(null!, binance, nowTicks + 31, 1m);
    Console.WriteLine("  ✗ 預期會拋例外但沒拋!");
}
catch (ArgumentException ex)
{
    Console.WriteLine($"  ✓ null Symbol → ArgumentException: {ex.Message}");
}

// (c) 空 Bucket → ArgumentException
try
{
    LOG.Quote("BTCUSDT", "", nowTicks + 32, 1m);
    Console.WriteLine("  ✗ 預期會拋例外但沒拋!");
}
catch (ArgumentException ex)
{
    Console.WriteLine($"  ✓ 空 Bucket → ArgumentException: {ex.Message}");
}

// (d) Extras + ExtrasJson 同時設定 → ArgumentException
try
{
    var bad = new QuoteRecord("BTCUSDT", binance, nowTicks + 33, 1m,
        extras: new Dictionary<string, object> { ["a"] = 1 },
        extrasJson: "{\"b\":2}");
    LOG.Quote(in bad);
    Console.WriteLine("  ✗ 預期會拋例外但沒拋!");
}
catch (ArgumentException ex)
{
    Console.WriteLine($"  ✓ Extras+ExtrasJson 同時設定 → ArgumentException: {ex.Message}");
}

// (e) Extras key 撞名 'bid' → ArgumentException
try
{
    var bad = new QuoteRecord("BTCUSDT", binance, nowTicks + 34, 1m,
        extras: new Dictionary<string, object> { ["bid"] = 999m });
    LOG.Quote(in bad);
    Console.WriteLine("  ✗ 預期會拋例外但沒拋!");
}
catch (ArgumentException ex)
{
    Console.WriteLine($"  ✓ Extras key 撞名 'bid' → ArgumentException: {ex.Message}");
}

// (f) Extras key 撞名 'last' → ArgumentException
try
{
    var bad = new QuoteRecord("BTCUSDT", binance, nowTicks + 35, 1m,
        extras: new Dictionary<string, object> { ["last"] = 999m });
    LOG.Quote(in bad);
    Console.WriteLine("  ✗ 預期會拋例外但沒拋!");
}
catch (ArgumentException ex)
{
    Console.WriteLine($"  ✓ Extras key 撞名 'last' → ArgumentException: {ex.Message}");
}

Tick("6 種錯誤情境全部依預期拋出 ArgumentException(呼叫端可同步攔截)");

// ─── 15. v3.1 Quote pipeline HFT 多執行緒壓測 ───────────────────────────
Header("15. v3.1 Quote pipeline HFT 壓測");
const int quoteSymbols = 50;
const int quoteBuckets = 3;
const int quoteThreads = 6;
const int quotesPerThread = 5000;
var symbols = Enumerable.Range(0, quoteSymbols).Select(i => $"SYM{i:D3}").ToArray();
var buckets = new[] { "binance_spot", "okx_swap", "bybit_futures" };
var totalQuotes = 0L;

Console.WriteLine($"  配置: {quoteThreads} threads × {quotesPerThread} quotes each");
Console.WriteLine($"  {quoteSymbols} symbols × {quoteBuckets} buckets = {quoteSymbols * quoteBuckets} 檔案維度");
Console.WriteLine($"  目標總筆數: {quoteThreads * quotesPerThread:N0}");

sw.Restart();
var qThreads = new List<Thread>();
for (int t = 0; t < quoteThreads; t++)
{
    var threadIdx = t;
    var th = new Thread(() =>
    {
        var rng = new Random(threadIdx * 1000 + 13);
        for (int i = 0; i < quotesPerThread; i++)
        {
            var sym = symbols[rng.Next(symbols.Length)];
            var buc = buckets[rng.Next(buckets.Length)];
            var lastPx = 100m + (decimal)rng.NextDouble() * 1000m;
            var spread = (decimal)rng.NextDouble() * 0.5m;
            LOG.Quote(sym, buc, DateTime.Now.Ticks, lastPx,
                bid: lastPx - spread,
                ask: lastPx + spread);
            Interlocked.Increment(ref totalQuotes);
        }
    })
    { IsBackground = true, Name = $"quote-{t}" };
    qThreads.Add(th);
    th.Start();
}
foreach (var th in qThreads) th.Join();
sw.Stop();

var qRate = totalQuotes * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds);
Console.WriteLine($"  實際寫入: {totalQuotes:N0} 筆");
Console.WriteLine($"  耗時:     {sw.ElapsedMilliseconds:N0} ms");
Console.WriteLine($"  吞吐:     {qRate:N0} quotes/sec");
Console.WriteLine($"  Quote drop 計數: {quoteDroppedCount:N0}");

// ─── 16. v3.1 Quote pipeline drop oldest 壓測 ───────────────────────────
Header("16. v3.1 Quote pipeline drop oldest 壓測");
var qDropBefore = quoteDroppedCount;
Console.WriteLine("  queue 容量 50000;連續 fire 100000 筆,不等 dispatcher");
sw.Restart();
for (int i = 0; i < 100_000; i++)
    LOG.Quote("BURST", "stress", DateTime.Now.Ticks, 1m + i);
sw.Stop();
Console.WriteLine($"  純 enqueue 100K 筆耗時: {sw.ElapsedMilliseconds} ms");
Thread.Sleep(1500);
var qDropAfter = quoteDroppedCount;
Console.WriteLine($"  本批 drop 數: {qDropAfter - qDropBefore:N0}");
Console.WriteLine($"  累計 drop 數: {qDropAfter:N0}");
Console.WriteLine("  ✓ 預期:超出 queue 部分被 drop oldest,callback 應該收到通知");

// ─── 等 dispatcher 處理完 ────────────────────────────────────────────────
Header("等待 dispatcher flush 最後 batch");
Thread.Sleep(2500);

// ─── 終端報告 ────────────────────────────────────────────────────────────
Header("最終統計");
var logsRoot = Path.Combine(AppContext.BaseDirectory, LOG.GetCurrentOptions().LogPath);
if (Directory.Exists(logsRoot))
{
    var dateDirs = Directory.GetDirectories(logsRoot);
    var mainExt = MainExtensionOf(mainFormat);
    var quoteExt = QuoteExtensionOf(quoteFormat);
    var allMainFiles = dateDirs.SelectMany(d =>
        Directory.GetFiles(d, $"*_Log.{mainExt}", SearchOption.AllDirectories)).ToArray();
    var allQuoteFiles = dateDirs.SelectMany(d =>
        Directory.GetFiles(d, $"*_Quote.{quoteExt}", SearchOption.AllDirectories)).ToArray();

    long mainSize = 0, quoteSize = 0;
    foreach (var f in allMainFiles) { try { mainSize += new FileInfo(f).Length; } catch { } }
    foreach (var f in allQuoteFiles) { try { quoteSize += new FileInfo(f).Length; } catch { } }

    Console.WriteLine($"  log 根目錄:           {logsRoot}");
    Console.WriteLine($"  日期子目錄數:         {dateDirs.Length}");
    Console.WriteLine();
    Console.WriteLine($"  [主 logger,*.{mainExt}]");
    Console.WriteLine($"    檔案數:    {allMainFiles.Length}");
    Console.WriteLine($"    總大小:    {mainSize / 1024.0:N2} KB ({mainSize / 1024.0 / 1024.0:N2} MB)");
    Console.WriteLine($"    drop:      {droppedCount:N0}");
    Console.WriteLine();
    Console.WriteLine($"  [報價 pipeline,*.{quoteExt}]");
    Console.WriteLine($"    檔案數:    {allQuoteFiles.Length}");
    Console.WriteLine($"    總大小:    {quoteSize / 1024.0:N2} KB ({quoteSize / 1024.0 / 1024.0:N2} MB)");
    Console.WriteLine($"    drop:      {quoteDroppedCount:N0}");
    Console.WriteLine();

    // Top 大檔(主 + 報價)
    var allFiles = allMainFiles.Concat(allQuoteFiles)
        .Select(f => new { Path = f, Size = new FileInfo(f).Length })
        .OrderByDescending(x => x.Size)
        .Take(5)
        .ToArray();
    if (allFiles.Length > 0)
    {
        Console.WriteLine("  Top 5 大檔:");
        foreach (var f in allFiles)
            Console.WriteLine($"    {f.Size,12:N0} bytes  {Path.GetRelativePath(logsRoot, f.Path)}");
    }
    Console.WriteLine();

    // 抽樣顯示主 log
    var sampleMainFile = allMainFiles.FirstOrDefault();
    if (sampleMainFile != null)
    {
        Console.WriteLine($"  [主 log 抽樣 {Path.GetRelativePath(logsRoot, sampleMainFile)}]");
        DumpFirstLines(sampleMainFile, 3, "    ");
        Console.WriteLine();
    }

    // 抽樣顯示報價 log
    var sampleQuoteFile = allQuoteFiles.FirstOrDefault();
    if (sampleQuoteFile != null)
    {
        Console.WriteLine($"  [報價 log 抽樣 {Path.GetRelativePath(logsRoot, sampleQuoteFile)}]");
        DumpFirstLines(sampleQuoteFile, 3, "    ");
        Console.WriteLine();
    }

    // 驗證 sanitize 是否生效:檔名中不該有 / \ : 等字元
    Console.WriteLine("  [檔名 sanitize 驗證]");
    var hasIllegalChar = false;
    foreach (var f in allQuoteFiles)
    {
        var name = Path.GetFileName(f);
        if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
        {
            Console.WriteLine($"    ✗ 含非法字元: {name}");
            hasIllegalChar = true;
        }
    }
    if (!hasIllegalChar)
        Console.WriteLine("    ✓ 所有報價檔名都不含檔系統非法字元");

    // 找出 sanitize 後的特定檔案
    var sanitizeTargets = allQuoteFiles.Where(f =>
        Path.GetFileName(f).Contains("BTC-USDT") ||
        Path.GetFileName(f).Contains("XRP-USDT-PERP") ||
        Path.GetFileName(f).Contains("broker-v2")).ToArray();
    if (sanitizeTargets.Length > 0)
    {
        Console.WriteLine("    ✓ Sanitize 範例檔:");
        foreach (var f in sanitizeTargets.Take(5))
            Console.WriteLine($"      {Path.GetFileName(f)}");
    }
}
else
{
    Console.WriteLine("  ⚠ logs 目錄未生成!");
}

Console.WriteLine();
Console.WriteLine("按 Enter 結束(讓 dispatcher 處理完所有未寫入 log)...");
Console.ReadLine();

// ═══════════════════════════════════════════════════════════════════════════

[MethodImpl(MethodImplOptions.NoInlining)]
static void Header(string title)
{
    Console.WriteLine();
    var bar = new string('-', Math.Max(40, title.Length + 6));
    Console.WriteLine(bar);
    Console.WriteLine($"  {title}");
    Console.WriteLine(bar);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void Tick(string note) => Console.WriteLine($"  v {note}");

static LogOutputFormat ParseLogFormat(string s) => s.ToLowerInvariant() switch
{
    "txt" => LogOutputFormat.Txt,
    "log" => LogOutputFormat.Log,
    "json" => LogOutputFormat.Json,
    _ => throw new ArgumentException($"未知 main-format '{s}'(允許 txt|log|json)"),
};

// 回傳 true 代表非同步(dispatcher)模式,false 代表同步寫入模式
static bool ParseWriteMode(string s) => s.ToLowerInvariant() switch
{
    "async" => true,
    "sync" => false,
    _ => throw new ArgumentException($"未知 write-mode '{s}'(允許 async|sync)"),
};

static QuoteOutputFormat ParseQuoteFormat(string s) => s.ToLowerInvariant() switch
{
    "txt" => QuoteOutputFormat.Txt,
    "log" => QuoteOutputFormat.Log,
    "json" => QuoteOutputFormat.Json,
    _ => throw new ArgumentException($"未知 quote-format '{s}'(允許 txt|log|json)"),
};

static string MainExtensionOf(LogOutputFormat f) => f switch
{
    LogOutputFormat.Txt => "txt",
    LogOutputFormat.Log => "log",
    LogOutputFormat.Json => "json",
    _ => "txt",
};

static string QuoteExtensionOf(QuoteOutputFormat f) => f switch
{
    QuoteOutputFormat.Txt => "txt",
    QuoteOutputFormat.Log => "log",
    QuoteOutputFormat.Json => "json",
    _ => "txt",
};

static void DumpFirstLines(string path, int n, string indent)
{
    try
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        for (int i = 0; i < n; i++)
        {
            var line = sr.ReadLine();
            if (line == null) break;
            // 超長行截斷顯示
            if (line.Length > 200) line = line.Substring(0, 200) + "...";
            Console.WriteLine($"{indent}{line}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{indent}(無法讀取:{ex.Message})");
    }
}
