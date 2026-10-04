using System.Text.Json;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>跨预算与任务恢复的并发、身份及脱敏回归，所有适配器只使用内存对象。</summary>
public static class ProviderSafetyRegressionTests
{
    /// <summary>固定执行六组回归；异步等待有五秒边界并接受统一取消令牌。</summary>
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await ConcurrentBudgetReservationsRespectOneLimit(cancellationToken);
        BudgetBindingAndReconciliationKeepOneLine();
        await ConcurrentCoordinatorsSubmitOnlyOnce(cancellationToken);
        await ExplicitIdempotencyKeyCannotChangeIdentity(cancellationToken);
        TaskNumbersAreScopedToProvider(cancellationToken);
        await MismatchedResponsesAndRawMessagesStayOutOfRecovery(cancellationToken);
        await InvalidFeesNeverPolluteRecovery(cancellationToken);
        RecoveredTaskIdSurvivesOlderIntent(cancellationToken);
        MultiCurrencyAndSignedPriceSourcesAreRejected();
    }

    /// <summary>十二个请求争用同一个硬预算时最多五个获得预留，失败不留下账本行。</summary>
    private static async Task ConcurrentBudgetReservationsRespectOneLimit(CancellationToken cancellationToken)
    {
        var ledger = new MpsBudgetLedger(10m);
        var attempts = Enumerable.Range(0, 12).Select(index => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { ledger.Reserve("offline", "budget-" + index, 2m); return true; }
            catch (MpsBudgetExceededException) { return false; }
        }, cancellationToken)).ToArray();
        var outcomes = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert(outcomes.Count(value => value) == 5 && ledger.Reserved == 10m && ledger.EntryCount == 5 && ledger.Remaining == 0,
            "并发预算预留突破硬上限或留下重复行");
    }

    /// <summary>本地意图原子绑定到供应商任务号，未知转确认和重复用量均只占一行。</summary>
    private static void BudgetBindingAndReconciliationKeepOneLine()
    {
        var ledger = new MpsBudgetLedger(8m);
        ledger.Reserve("offline", "local-intent", 4m);
        ledger.BindTaskId("offline", "local-intent", "stable-task");
        ledger.BindTaskId("offline", "local-intent", "stable-task");
        ledger.MarkUnknown("offline", "local-intent");
        ledger.MarkUnknown("offline", "stable-task", 5m);
        ExpectFailure<InvalidOperationException>(() => ledger.MarkUnknown("offline", "stable-task", 4m));
        ledger.Reconcile("offline", "stable-task", 3m, MpsPriceMetadata.Confirmed(3m, "CNY", "request", "offline-bill"));
        ledger.Reconcile("offline", "local-intent", 3m);
        Assert(ledger.Snapshot().Single().TaskId == "stable-task" && ledger.Confirmed == 3m && ledger.Unknown == 0,
            "意图绑定及对账不应产生重复费用");
        ExpectFailure<InvalidOperationException>(() => ledger.BindTaskId("offline", "local-intent", "different-task"));

        var other = new MpsBudgetLedger(8m);
        other.Reserve("offline", "first", 2m);
        other.Reserve("offline", "second", 2m);
        ExpectFailure<InvalidOperationException>(() => other.BindTaskId("offline", "first", "second"));
        other.BindTaskId("offline", "first", "bound");
        ExpectFailure<InvalidOperationException>(() => other.BindTaskId("offline", "bound", "rebound"));
        Assert(other.Reserved == 4m && other.EntryCount == 2, "绑定冲突不得释放或增加预算");
    }

    /// <summary>两个协调器同时发现同一请求时，原子提交意图保证 transport 只调用一次。</summary>
    private static async Task ConcurrentCoordinatorsSubmitOnlyOnce(CancellationToken cancellationToken)
    {
        using var adapter = new ControlledAdapter();
        var store = new ProviderAsyncTaskRecoveryStore();
        var first = new ProviderAsyncTaskCoordinator("offline", adapter, adapter, store, TimeSpan.FromSeconds(2));
        var second = new ProviderAsyncTaskCoordinator("offline", adapter, adapter, store, TimeSpan.FromSeconds(2));
        var request = Request("concurrent");
        var pending = first.SubmitOrResumeAsync(request, cancellationToken);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        var duplicate = await second.SubmitOrResumeAsync(request, cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        Assert(!duplicate.Submitted && duplicate.RequiresRecoveryQuery && adapter.SubmitCount == 1,
            "已有未返回提交意图不得触发第二次请求");
        adapter.CompleteSubmission();
        var submitted = await pending.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        var reopened = ProviderAsyncTaskRecoveryStore.ReopenJson(store.ExportJson(cancellationToken), cancellationToken);
        var resumed = await new ProviderAsyncTaskCoordinator("offline", adapter, adapter, reopened)
            .SubmitOrResumeAsync(request, cancellationToken);
        Assert(submitted.Submitted && resumed.ReusedExistingTask && adapter.SubmitCount == 1 && adapter.QueryCount == 1,
            "重启后应查询相同任务，不能重复提交");
    }

    /// <summary>人为指定的同一幂等键不能改写请求范围或绕过指纹预检。</summary>
    private static async Task ExplicitIdempotencyKeyCannotChangeIdentity(CancellationToken cancellationToken)
    {
        using var adapter = new ControlledAdapter();
        adapter.CompleteSubmission();
        var coordinator = new ProviderAsyncTaskCoordinator("offline", adapter, adapter);
        var request = Request("identity") with { IdempotencyKey = "explicit-key" };
        await coordinator.SubmitOrResumeAsync(request, cancellationToken);
        try
        {
            await coordinator.SubmitOrResumeAsync(request with { AccountAlias = "other" }, cancellationToken);
            throw new InvalidOperationException("同一幂等键接受不同账号");
        }
        catch (InvalidDataException) { }
        try
        {
            await coordinator.SubmitOrResumeAsync(request with { InputFingerprint = "https://invalid.example/?token=fixture" }, cancellationToken);
            throw new InvalidOperationException("显式键绕过输入预检");
        }
        catch (InvalidDataException) { }
        Assert(adapter.SubmitCount == 1 && adapter.QueryCount == 0, "身份变化不能查询其他账号或重新提交");
    }

    /// <summary>不同提供商可返回相同编号，但同一提供商不可把同号绑定到两个意图。</summary>
    private static void TaskNumbersAreScopedToProvider(CancellationToken cancellationToken)
    {
        var store = new ProviderAsyncTaskRecoveryStore();
        var first = ProviderTaskRecoveryEntry.FromRequest("offline", Request("scope-a"), "scope-a");
        first.TaskId = "same-task";
        store.Upsert(first, cancellationToken);
        var other = ProviderTaskRecoveryEntry.FromRequest("other", Request("scope-b"), "scope-b");
        other.TaskId = "same-task";
        store.Upsert(other, cancellationToken);
        Assert(ProviderAsyncTaskRecoveryStore.ReopenJson(store.ExportJson(cancellationToken), cancellationToken)
            .ToSnapshot(cancellationToken).Tasks.Count == 2, "恢复去重必须保留不同提供商同名任务");
        var duplicate = ProviderTaskRecoveryEntry.FromRequest("offline", Request("scope-c"), "scope-c");
        duplicate.TaskId = "same-task";
        ExpectFailure<InvalidDataException>(() => store.Upsert(duplicate, cancellationToken));
    }

    /// <summary>错号响应变为未知，供应商原始消息和签名地址不进入恢复文件。</summary>
    private static async Task MismatchedResponsesAndRawMessagesStayOutOfRecovery(CancellationToken cancellationToken)
    {
        using var adapter = new ControlledAdapter { WrongTaskId = true };
        adapter.CompleteSubmission();
        var coordinator = new ProviderAsyncTaskCoordinator("offline", adapter, adapter);
        var request = Request("redaction");
        var submitted = await coordinator.SubmitOrResumeAsync(request, cancellationToken);
        var queried = await coordinator.SubmitOrResumeAsync(request, cancellationToken);
        Assert(queried.Task.Status == ProviderAdapterTaskStatus.Unknown && queried.RequiresRecoveryQuery &&
            queried.Task.TaskId == submitted.Task.TaskId && queried.Task.ErrorCategory == ProviderAdapterErrorCategory.ResponseFormat,
            "错号查询不能污染既有任务状态或费用");
        var cancelled = await coordinator.CancelAsync(submitted.Task.IdempotencyKey, cancellationToken);
        Assert(cancelled.Task.Status == ProviderAdapterTaskStatus.Unknown && cancelled.RequiresRecoveryQuery,
            "错号取消不得宣称旧任务已取消");
        var json = coordinator.Recovery.ExportJson(cancellationToken);
        Assert(!json.Contains("fixture-secret", StringComparison.Ordinal) && !json.Contains("https://", StringComparison.Ordinal) &&
            !json.Contains("raw_response", StringComparison.Ordinal), "原始供应商消息泄漏到恢复 JSON");
        ExpectFailure<JsonException>(() => ProviderAsyncTaskRecoveryStore.ReopenJson(json.TrimEnd('}') + ",\"raw_response\":\"fixture\"}", cancellationToken));
    }

    /// <summary>币种保持独立，不能把美元静默计成人民币；价格来源拒绝签名地址。</summary>
    private static void MultiCurrencyAndSignedPriceSourcesAreRejected()
    {
        var cny = new MpsBudgetLedger(10m, "CNY");
        var usd = new MpsBudgetLedger(10m, "USD");
        usd.Reserve("offline", "usd-task", 1m, MpsPriceMetadata.Estimate(1m, "USD", "request", "offline"));
        ExpectFailure<InvalidOperationException>(() => cny.Reconcile("offline", "usd-task", 1m,
            MpsPriceMetadata.Confirmed(1m, "USD", "request", "offline")));
        Assert(cny.EntryCount == 0 && usd.Reserved == 1m, "多币种不能相互污染账本");
        cny.Reserve("offline", "repeat", 1m);
        ExpectFailure<InvalidOperationException>(() => cny.Reserve("offline", "repeat", 1m,
            MpsPriceMetadata.Estimate(1m, "USD", "request", "offline")));
        ExpectFailure<InvalidDataException>(() => MpsPriceMetadata.Confirmed(1m, "CNY", "request", "offline",
            "https://invalid.example/artifact?signature=fixture").Validate());
        ExpectFailure<InvalidDataException>(() => cny.Reserve("offline", "https://invalid.example/?signature=fixture", 1m));
        MpsPriceMetadata.Estimate(1m, "CNY", "request", "公开目录", "https://invalid.example/docs").Validate();
    }

    /// <summary>非空恶意币种和负价均在修改恢复状态前被拒绝，未知费用也不允许承载原文。</summary>
    private static async Task InvalidFeesNeverPolluteRecovery(CancellationToken cancellationToken)
    {
        using var badCurrency = new ControlledAdapter { InvalidCurrency = true };
        badCurrency.CompleteSubmission();
        var coordinator = new ProviderAsyncTaskCoordinator("offline", badCurrency, badCurrency);
        var request = Request("bad-currency");
        await coordinator.SubmitOrResumeAsync(request, cancellationToken);
        var result = await coordinator.SubmitOrResumeAsync(request, cancellationToken);
        Assert(result.RequiresRecoveryQuery && result.Task.Price is null && result.Task.Currency is null,
            "非法币种必须返回未知并保留未经污染的费用字段");
        Assert(!coordinator.Recovery.ExportJson(cancellationToken).Contains("fixture-secret", StringComparison.Ordinal),
            "null 费用下的恶意币种不能泄漏");

        using var badPrice = new ControlledAdapter { InvalidPrice = true };
        badPrice.CompleteSubmission();
        var priceCoordinator = new ProviderAsyncTaskCoordinator("offline", badPrice, badPrice);
        await priceCoordinator.SubmitOrResumeAsync(Request("bad-price"), cancellationToken);
        var priceResult = await priceCoordinator.SubmitOrResumeAsync(Request("bad-price"), cancellationToken);
        Assert(priceResult.RequiresRecoveryQuery && priceResult.Task.Price is null, "负价不得使 unknown 保存再次抛出或污染账本");

        using var badSubmission = new ControlledAdapter();
        badSubmission.CompleteSubmission("https://invalid.example/?token=fixture-secret");
        var submissionCoordinator = new ProviderAsyncTaskCoordinator("offline", badSubmission, badSubmission);
        var submitted = await submissionCoordinator.SubmitOrResumeAsync(Request("bad-submission-currency"), cancellationToken);
        Assert(submitted.RequiresRecoveryQuery && submitted.Task.Currency is null && submitted.Task.TaskId == "stable-task",
            "非法费用响应应保留安全任务号以便查询，不能写入币种原文");
    }

    /// <summary>提交等待期间人工找回任务号，旧无号快照更新不能丢掉它。</summary>
    private static void RecoveredTaskIdSurvivesOlderIntent(CancellationToken cancellationToken)
    {
        var store = new ProviderAsyncTaskRecoveryStore();
        var intent = ProviderTaskRecoveryEntry.FromRequest("offline", Request("attached"), "attached-key");
        store.Upsert(intent, cancellationToken);
        store.AttachTaskId("attached-key", "found-task", cancellationToken);
        store.Upsert(intent, cancellationToken);
        Assert(store.Find("attached-key", cancellationToken)?.TaskId == "found-task", "旧意图更新抹掉人工找回任务号");
    }

    private static ProviderAdapterSubmitRequest Request(string fingerprint) =>
        new("demo", "offline-model", MpsCapabilityKind.ImageGeneration, "sha256:" + fingerprint);

    /// <summary>只接受预期拒绝异常，避免测试自身断言被误判为成功。</summary>
    private static void ExpectFailure<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("未拒绝：" + typeof(T).Name);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>可控制一次内存提交，结束时取消未完成任务，不启动外部进程。</summary>
    private sealed class ControlledAdapter : IProviderTaskSubmissionAdapter, IProviderTaskStatusAdapter, IDisposable
    {
        private readonly TaskCompletionSource<ProviderAdapterSubmission> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SubmitCount { get; private set; }
        public int QueryCount { get; private set; }
        public bool WrongTaskId { get; init; }
        public bool InvalidCurrency { get; init; }
        public bool InvalidPrice { get; init; }

        /// <summary>仅返回受控内存任务，以构造两个协调器重叠等待的窗口。</summary>
        public Task<ProviderAdapterSubmission> SubmitAsync(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmitCount++;
            Started.TrySetResult();
            return response.Task;
        }

        /// <summary>结束唯一提交等待；调用前完成也可模拟同步供应商。</summary>
        public void CompleteSubmission(string? currency = null) => response.TrySetResult(new ProviderAdapterSubmission("stable-task",
            ProviderAdapterTaskStatus.Queued, null, currency, DateTimeOffset.UtcNow));

        /// <summary>查询可故意返回其他任务及敏感原文，检验调用方拒绝与白名单保存。</summary>
        public Task<ProviderAdapterTaskState> GetStatusAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCount++;
            return Task.FromResult(new ProviderAdapterTaskState(WrongTaskId ? "wrong-task" : query.TaskId,
                ProviderAdapterTaskStatus.Succeeded, InvalidCurrency ? null : InvalidPrice ? -1m : 1m,
                InvalidCurrency ? "https://invalid.example/?token=fixture-secret" : "CNY", ProviderAdapterErrorCategory.Unknown, DateTimeOffset.UtcNow,
                "fixture-secret https://invalid.example/?signature=fixture raw_response"));
        }

        /// <summary>错号取消不代表旧任务取消成功。</summary>
        public Task<ProviderAdapterCancellation> CancelAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderAdapterCancellation(WrongTaskId ? "wrong-task" : query.TaskId,
                ProviderAdapterTaskStatus.Cancelled, ProviderAdapterErrorCategory.Cancelled, DateTimeOffset.UtcNow));
        }

        /// <summary>回收本测试唯一的未完成内存任务。</summary>
        public void Dispose() => response.TrySetCanceled();
    }
}
