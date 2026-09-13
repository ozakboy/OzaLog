using Xunit;

// v3.3.0：關閉跨測試類別的平行執行。
// LifecycleTests 會呼叫 LOG.Shutdown()，那是行程級的全域狀態——
// 與其他測試類別同時跑的話，別人的日誌會在寫到一半時被收尾掉，失敗還無法重現。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
