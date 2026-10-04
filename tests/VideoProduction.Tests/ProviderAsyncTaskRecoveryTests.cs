using System.Diagnostics;
using System.Text.Json;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>异步任务协议和重开边界的离线回归；不访问网络、凭据或付费服务。</summary>
public static class ProviderAsyncTaskRecoveryTests
{
    /// <summary>覆盖幂等任务号、六种状态、超时先查旧任务、有限轮询、取消和脱敏快照。</summary>
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        StableIdempotencyAndTerminalStatuses(cancellationToken);
        await ExistingTaskIsQueriedBeforeSubmit(cancellationToken);
        await PollingIsBoundedAndCancellable(cancellationToken);
        await UncooperativeOperationsRespectTimeouts(cancellationToken);
        await PollingDeadlineCoversQueryAndDelay(cancellationToken);
        await CancelledSubmissionSurvivesReopen(cancellationToken);
        await SnapshotRoundTripDoesNotPersistSensitiveFields(cancellationToken);
    }

    private static void StableIdempotencyAndTerminalStatuses(CancellationToken cancellationToken)
    {
        var request = Request("sha256:stable");
        var key1 = ProviderAsyncTaskCoordinator.BuildIdempotencyKey("offline", request);
        var key2 = ProviderAsyncTaskCoordinator.BuildIdempotencyKey("offline", request);
        Assert(key1 == key2 && key1.StartsWith("mps-", StringComparison.Ordinal), "幂等键必须稳定");

        var adapter = new FakeTaskAdapter();
        var coordinator = new ProviderAsyncTaskCoordinator("offline", adapter, adapter);
        var first = coordinator.SubmitOrResumeAsync(request, cancellationToken).GetAwaiter().GetResult();
        var second = coordinator.SubmitOrResumeAsync(request, cancellationToken).GetAwaiter().GetResult();
        Assert(first.Task.TaskId == second.Task.TaskId && adapter.SubmitCount == 1 && adapter.StatusCount == 1,
            "已有任务号必须先查询而不是重复提交");
        Assert(first.Task.Status == ProviderAdapterTaskStatus.Queued && second.Task.Status == ProviderAdapterTaskStatus.Running,
            "Queued/Running 状态未按协议返回");

        var success = coordinator.PollUntilTerminalAsync(first.Task.IdempotencyKey,
            new ProviderAsyncTaskPollingOptions(MaxPolls: 4), cancellationToken).GetAwaiter().GetResult();
        Assert(success.Task.Status == ProviderAdapterTaskStatus.Succeeded, "Succeeded 状态未恢复");

        var failedAdapter = new FakeTaskAdapter(FakeOutcome.Failed);
        var failedCoordinator = new ProviderAsyncTaskCoordinator("offline", failedAdapter, failedAdapter);
        var failed = failedCoordinator.SubmitOrResumeAsync(Request("sha256:failed"), cancellationToken).GetAwaiter().GetResult();
        var failedResult = failedCoordinator.PollUntilTerminalAsync(failed.Task.IdempotencyKey, new ProviderAsyncTaskPollingOptions(4), cancellationToken).GetAwaiter().GetResult();
        Assert(failedResult.Task.Status == ProviderAdapterTaskStatus.Failed, "Failed 状态未恢复");

        var cancelAdapter = new FakeTaskAdapter(FakeOutcome.NeverTerminal);
        var cancelCoordinator = new ProviderAsyncTaskCoordinator("offline", cancelAdapter, cancelAdapter);
        var pending = cancelCoordinator.SubmitOrResumeAsync(Request("sha256:cancel"), cancellationToken).GetAwaiter().GetResult();
        var cancelled = cancelCoordinator.CancelAsync(pending.Task.IdempotencyKey, cancellationToken).GetAwaiter().GetResult();
        Assert(cancelled.Task.Status == ProviderAdapterTaskStatus.Cancelled, "Cancelled 状态未恢复");

        var unknownAdapter = new FakeTaskAdapter(FakeOutcome.Unknown);
        var unknownCoordinator = new ProviderAsyncTaskCoordinator("offline", unknownAdapter, unknownAdapter);
        var unknown = unknownCoordinator.SubmitOrResumeAsync(Request("sha256:unknown"), cancellationToken).GetAwaiter().GetResult();
        var unknownAgain = unknownCoordinator.SubmitOrResumeAsync(Request("sha256:unknown"), cancellationToken).GetAwaiter().GetResult();
        Assert(unknownAgain.Task.Status == ProviderAdapterTaskStatus.Unknown, "Unknown 状态必须保留人工恢复边界");

        var timeoutAdapter = new FakeTaskAdapter(FakeOutcome.SubmissionTimeout);
        var timeoutCoordinator = new ProviderAsyncTaskCoordinator("offline", timeoutAdapter, timeoutAdapter);
        var timedOut = timeoutCoordinator.SubmitOrResumeAsync(Request("sha256:timeout"), cancellationToken).GetAwaiter().GetResult();
        var retried = timeoutCoordinator.SubmitOrResumeAsync(Request("sha256:timeout"), cancellationToken).GetAwaiter().GetResult();
        Assert(timedOut.RequiresRecoveryQuery && retried.RequiresRecoveryQuery && timeoutAdapter.SubmitCount == 1,
            "提交超时后重开必须先查询旧任务，不能盲目重发");
    }

    private static async Task ExistingTaskIsQueriedBeforeSubmit(CancellationToken cancellationToken)
    {
        var request = Request("sha256:old");
        var key = ProviderAsyncTaskCoordinator.BuildIdempotencyKey("offline", request);
        var now = DateTimeOffset.UtcNow;
        var store = new ProviderAsyncTaskRecoveryStore();
        store.Upsert(new ProviderTaskRecoveryEntry
        {
            ProviderId = "offline", AccountAlias = request.AccountAlias, ModelId = request.ModelId,
            Capability = request.Capability, InputFingerprint = request.InputFingerprint, IdempotencyKey = key,
            TaskId = "old-task", Status = ProviderAdapterTaskStatus.Unknown, CreatedUtc = now, UpdatedUtc = now
        }, cancellationToken);
        var adapter = new FakeTaskAdapter(FakeOutcome.Success);
        var coordinator = new ProviderAsyncTaskCoordinator("offline", adapter, adapter, store);
        var recovered = await coordinator.SubmitOrResumeAsync(request, cancellationToken);
        Assert(recovered.ReusedExistingTask && !recovered.Submitted && adapter.SubmitCount == 0 && adapter.StatusCount == 1,
            "重开时应先查询旧任务，不能重复提交");
        Assert(recovered.Task.Status == ProviderAdapterTaskStatus.Running, "旧任务查询结果未保存");
    }

    private static async Task PollingIsBoundedAndCancellable(CancellationToken cancellationToken)
    {
        var adapter = new FakeTaskAdapter(FakeOutcome.NeverTerminal);
        var coordinator = new ProviderAsyncTaskCoordinator("offline", adapter, adapter);
        var submitted = await coordinator.SubmitOrResumeAsync(Request("sha256:bound"), cancellationToken);
        var result = await coordinator.PollUntilTerminalAsync(submitted.Task.IdempotencyKey,
            new ProviderAsyncTaskPollingOptions(MaxPolls: 3), cancellationToken);
        Assert(result.Task.Status == ProviderAdapterTaskStatus.Unknown && adapter.StatusCount == 3,
            "轮询必须受次数边界限制并保留 Unknown");

        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        canceled.Cancel();
        try
        {
            await coordinator.PollUntilTerminalAsync(submitted.Task.IdempotencyKey, new ProviderAsyncTaskPollingOptions(3), canceled.Token);
            throw new InvalidOperationException("取消令牌未停止轮询");
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>不响应取消的适配器也必须有限返回，提交/查询/取消超时均要求查询旧任务。</summary>
    private static async Task UncooperativeOperationsRespectTimeouts(CancellationToken cancellationToken)
    {
        using var adapter = new HangingTaskAdapter { HangSubmit = true };
        var timeout = TimeSpan.FromMilliseconds(80);
        var coordinator = new ProviderAsyncTaskCoordinator("offline", adapter, adapter, null, timeout);
        var request = Request("sha256:operation-timeout");
        var result = await coordinator.SubmitOrResumeAsync(request, cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        Assert(result.Task.Status == ProviderAdapterTaskStatus.Unknown && result.RequiresRecoveryQuery && adapter.LastToken.IsCancellationRequested,
            "忽略取消的提交必须在单操作时限内返回未知并取消适配器令牌");
        var reopened = ProviderAsyncTaskRecoveryStore.ReopenJson(coordinator.Recovery.ExportJson(cancellationToken), cancellationToken);
        var resumed = new ProviderAsyncTaskCoordinator("offline", adapter, adapter, reopened, timeout);
        var retry = await resumed.SubmitOrResumeAsync(request, cancellationToken);
        Assert(retry.RequiresRecoveryQuery && adapter.SubmitCount == 1, "提交超时重开后不可重复提交");

        reopened.AttachTaskId(result.Task.IdempotencyKey, "hanging-old-task", cancellationToken);
        adapter.HangStatus = true;
        var queried = await resumed.SubmitOrResumeAsync(request, cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        Assert(queried.Task.Status == ProviderAdapterTaskStatus.Unknown && queried.RequiresRecoveryQuery &&
               adapter.LastToken.IsCancellationRequested && adapter.SubmitCount == 1, "状态查询必须受单操作时限限制且不重新提交");
        adapter.HangCancel = true;
        var cancelled = await resumed.CancelAsync(result.Task.IdempotencyKey, cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        Assert(cancelled.Task.Status == ProviderAdapterTaskStatus.Unknown && cancelled.RequiresRecoveryQuery && adapter.LastToken.IsCancellationRequested,
            "取消超时不能宣称供应商已取消，必须保留恢复查询");

        try
        {
            _ = new ProviderAsyncTaskCoordinator("offline", adapter, adapter, null, TimeSpan.FromMinutes(6));
            throw new InvalidOperationException("过长单操作时限未拒绝");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    /// <summary>整体轮询时限必须覆盖不返回的查询及查询之间的长延迟。</summary>
    private static async Task PollingDeadlineCoversQueryAndDelay(CancellationToken cancellationToken)
    {
        using var adapter = new HangingTaskAdapter { HangStatus = true };
        var coordinator = new ProviderAsyncTaskCoordinator("offline", adapter, adapter, null, TimeSpan.FromSeconds(2));
        var submitted = await coordinator.SubmitOrResumeAsync(Request("sha256:deadline-query"), cancellationToken);
        var queryResult = await coordinator.PollUntilTerminalAsync(submitted.Task.IdempotencyKey,
            new ProviderAsyncTaskPollingOptions(4, TimeSpan.FromMilliseconds(100)), cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
        Assert(queryResult.RequiresRecoveryQuery && queryResult.Task.ErrorCategory == ProviderAdapterErrorCategory.Timeout && adapter.StatusCount == 1,
            "轮询总时限必须提前停止长查询，不能等待单操作时限");

        adapter.HangStatus = false;
        var timer = Stopwatch.StartNew();
        var delayed = await coordinator.PollUntilTerminalAsync(submitted.Task.IdempotencyKey,
            new ProviderAsyncTaskPollingOptions(4, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2)), cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
        Assert(timer.Elapsed < TimeSpan.FromSeconds(1) && delayed.RequiresRecoveryQuery && adapter.StatusCount == 2,
            "轮询总时限必须包含延迟，且到期后不得启动下一次查询");
    }

    /// <summary>提交等待被用户中止后仍保留未知记录，重开不会盲目重发。</summary>
    private static async Task CancelledSubmissionSurvivesReopen(CancellationToken cancellationToken)
    {
        using var adapter = new HangingTaskAdapter { HangSubmit = true };
        var coordinator = new ProviderAsyncTaskCoordinator("offline", adapter, adapter, null, TimeSpan.FromSeconds(2));
        var request = Request("sha256:user-cancel-submit");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(80));
        try
        {
            await coordinator.SubmitOrResumeAsync(request, cancelled.Token).WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            throw new InvalidOperationException("用户中止未抛出取消");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        var reopened = ProviderAsyncTaskRecoveryStore.ReopenJson(coordinator.Recovery.ExportJson(cancellationToken), cancellationToken);
        var key = ProviderAsyncTaskCoordinator.BuildIdempotencyKey("offline", request);
        Assert(reopened.Find(key, cancellationToken)?.Status == ProviderAdapterTaskStatus.Unknown && adapter.LastToken.IsCancellationRequested,
            "用户中止不能丢失可能已提交的任务记录");
        var resumed = new ProviderAsyncTaskCoordinator("offline", adapter, adapter, reopened);
        var result = await resumed.SubmitOrResumeAsync(request, cancellationToken);
        Assert(result.RequiresRecoveryQuery && adapter.SubmitCount == 1, "中止后重开不得重新提交付费任务");

        reopened.AttachTaskId(key, "cancelled-submit-task", cancellationToken);
        adapter.HangStatus = true;
        using var queryCancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        queryCancelled.CancelAfter(TimeSpan.FromMilliseconds(80));
        try
        {
            await resumed.SubmitOrResumeAsync(request, queryCancelled.Token).WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            throw new InvalidOperationException("查询中止未抛出取消");
        }
        catch (OperationCanceledException) when (queryCancelled.IsCancellationRequested) { }
        Assert(reopened.Find(key, cancellationToken)?.TaskId == "cancelled-submit-task" && adapter.SubmitCount == 1,
            "查询中止必须保留已有任务号且无额外提交");

        adapter.HangCancel = true;
        using var cancelWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancelWait.CancelAfter(TimeSpan.FromMilliseconds(80));
        try
        {
            await resumed.CancelAsync(key, cancelWait.Token).WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            throw new InvalidOperationException("取消请求等待中止未抛出取消");
        }
        catch (OperationCanceledException) when (cancelWait.IsCancellationRequested) { }
        Assert(reopened.Find(key, cancellationToken)?.TaskId == "cancelled-submit-task" &&
               reopened.Find(key, cancellationToken)?.Status == ProviderAdapterTaskStatus.Unknown,
            "取消请求等待中止不能伪装为供应商已取消或丢失任务号");

        adapter.HangStatus = false;
        using var pollCancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pollCancelled.CancelAfter(TimeSpan.FromMilliseconds(80));
        try
        {
            await resumed.PollUntilTerminalAsync(key,
                new ProviderAsyncTaskPollingOptions(4, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)), pollCancelled.Token)
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            throw new InvalidOperationException("轮询延迟中止未抛出取消");
        }
        catch (OperationCanceledException) when (pollCancelled.IsCancellationRequested) { }
        Assert(reopened.Find(key, cancellationToken)?.TaskId == "cancelled-submit-task" && adapter.SubmitCount == 1,
            "轮询中止必须保留同一任务以供恢复");
    }

    private static async Task SnapshotRoundTripDoesNotPersistSensitiveFields(CancellationToken cancellationToken)
    {
        var store = new ProviderAsyncTaskRecoveryStore();
        var now = DateTimeOffset.UtcNow;
        store.Upsert(new ProviderTaskRecoveryEntry
        {
            ProviderId = "offline", AccountAlias = "demo", ModelId = "offline-text", Capability = MpsCapabilityKind.TextPlanning,
            InputFingerprint = "sha256:safe", IdempotencyKey = "mps-safe", TaskId = "task-safe",
            Status = ProviderAdapterTaskStatus.Succeeded, Price = 0.2m, Currency = "CNY", CreatedUtc = now, UpdatedUtc = now
        }, cancellationToken);
        var json = store.ExportJson(cancellationToken);
        Assert(!json.Contains("secret", StringComparison.OrdinalIgnoreCase) && !json.Contains("signed_url", StringComparison.OrdinalIgnoreCase) &&
               !json.Contains("authorization", StringComparison.OrdinalIgnoreCase) && !json.Contains("raw_response", StringComparison.OrdinalIgnoreCase),
            "恢复快照不得落盘敏感字段");
        var reopened = ProviderAsyncTaskRecoveryStore.ReopenJson(json, cancellationToken);
        var task = reopened.Find("mps-safe", cancellationToken);
        Assert(task?.TaskId == "task-safe" && task.Status == ProviderAdapterTaskStatus.Succeeded, "恢复快照 JSON 往返失败");
        var temporary = Path.Combine(Path.GetTempPath(), "mps-recovery-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(temporary, "tasks.json");
        try
        {
            await store.SaveAsync(path, cancellationToken);
            var loaded = ProviderAsyncTaskRecoveryStore.Load(path, cancellationToken);
            Assert(loaded.Find("mps-safe", cancellationToken)?.TaskId == "task-safe", "恢复文件原子保存或读取失败");
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
        try
        {
            _ = ProviderAsyncTaskRecoveryStore.ReopenJson(json.TrimEnd('}') + ",\"extra\":true}", cancellationToken);
            throw new InvalidOperationException("恢复快照未知字段未拒绝");
        }
        catch (JsonException) { }
    }

    private static ProviderAdapterSubmitRequest Request(string fingerprint) =>
        new("demo", "offline-text", MpsCapabilityKind.TextPlanning, fingerprint);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private enum FakeOutcome { Success, Failed, NeverTerminal, Unknown, SubmissionTimeout }

    /// <summary>只使用三个内存任务模拟不协作适配器；测试结束后全部取消，不启动线程或外部进程。</summary>
    private sealed class HangingTaskAdapter : IProviderTaskSubmissionAdapter, IProviderTaskStatusAdapter, IDisposable
    {
        private readonly TaskCompletionSource<ProviderAdapterSubmission> submission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ProviderAdapterTaskState> status = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ProviderAdapterCancellation> cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HangSubmit { get; set; }
        public bool HangStatus { get; set; }
        public bool HangCancel { get; set; }
        public int SubmitCount { get; private set; }
        public int StatusCount { get; private set; }
        public CancellationToken LastToken { get; private set; }

        /// <summary>提交可故意忽略取消令牌，以验证协调器自己的等待边界。</summary>
        public Task<ProviderAdapterSubmission> SubmitAsync(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            LastToken = cancellationToken;
            return HangSubmit ? submission.Task : Task.FromResult(new ProviderAdapterSubmission("hanging-task",
                ProviderAdapterTaskStatus.Queued, null, null, DateTimeOffset.UtcNow));
        }

        /// <summary>查询仅返回内存 Running 状态或一个不完成的任务。</summary>
        public Task<ProviderAdapterTaskState> GetStatusAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
        {
            StatusCount++;
            LastToken = cancellationToken;
            return HangStatus ? status.Task : Task.FromResult(new ProviderAdapterTaskState(query.TaskId,
                ProviderAdapterTaskStatus.Running, null, null, ProviderAdapterErrorCategory.Unknown, DateTimeOffset.UtcNow));
        }

        /// <summary>取消可故意不返回，验证等待超时不会被误记为供应商已取消。</summary>
        public Task<ProviderAdapterCancellation> CancelAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            return HangCancel ? cancellation.Task : Task.FromResult(new ProviderAdapterCancellation(query.TaskId,
                ProviderAdapterTaskStatus.Cancelled, ProviderAdapterErrorCategory.Cancelled, DateTimeOffset.UtcNow));
        }

        /// <summary>释放本测试创建的三个内存未完成任务。</summary>
        public void Dispose()
        {
            submission.TrySetCanceled();
            status.TrySetCanceled();
            cancellation.TrySetCanceled();
        }
    }

    private sealed class FakeTaskAdapter : IProviderTaskSubmissionAdapter, IProviderTaskStatusAdapter
    {
        private readonly FakeOutcome outcome;
        private int statusCalls;
        private string? taskId;

        public FakeTaskAdapter(FakeOutcome outcome = FakeOutcome.Success) => this.outcome = outcome;
        public int SubmitCount { get; private set; }
        public int StatusCount => statusCalls;

        public Task<ProviderAdapterSubmission> SubmitAsync(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmitCount++;
            if (outcome == FakeOutcome.SubmissionTimeout) throw new TimeoutException("offline timeout");
            taskId ??= "fake-task-" + SubmitCount;
            return Task.FromResult(new ProviderAdapterSubmission(taskId, ProviderAdapterTaskStatus.Queued, 0.2m, "CNY", DateTimeOffset.UtcNow));
        }

        public Task<ProviderAdapterTaskState> GetStatusAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statusCalls++;
            var state = outcome switch
            {
                FakeOutcome.Success when statusCalls == 1 => ProviderAdapterTaskStatus.Running,
                FakeOutcome.Success => ProviderAdapterTaskStatus.Succeeded,
                FakeOutcome.Failed => ProviderAdapterTaskStatus.Failed,
                FakeOutcome.Unknown => ProviderAdapterTaskStatus.Unknown,
                _ => ProviderAdapterTaskStatus.Running
            };
            return Task.FromResult(new ProviderAdapterTaskState(query.TaskId, state, state is ProviderAdapterTaskStatus.Succeeded or ProviderAdapterTaskStatus.Failed ? 0.2m : null,
                state is ProviderAdapterTaskStatus.Succeeded or ProviderAdapterTaskStatus.Failed ? "CNY" : null,
                state == ProviderAdapterTaskStatus.Failed ? ProviderAdapterErrorCategory.Unavailable : ProviderAdapterErrorCategory.Unknown,
                DateTimeOffset.UtcNow));
        }

        public Task<ProviderAdapterCancellation> CancelAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderAdapterCancellation(query.TaskId, ProviderAdapterTaskStatus.Cancelled,
                ProviderAdapterErrorCategory.Cancelled, DateTimeOffset.UtcNow));
        }
    }
}
