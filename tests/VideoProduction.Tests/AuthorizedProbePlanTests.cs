using System.Text.Json;
using VideoProduction;

public static class AuthorizedProbePlanTests
{
    private const string Summary = "一条本地合成图片的最小能力探测";

    /// <summary>只使用内存假适配器验证授权、预算、次数、取消和重启恢复。</summary>
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        PreflightRejectsUnauthorizedUnknownAndOverLimit();
        CostOverrunsAndSnapshotScopeStayBlocked();
        RestoredBudgetAndTaskIdentityCannotBeForged();
        await ConcurrencyRegistersOnlyOneIntent(cancellationToken);
        await StableTaskIdAndPersistenceArePreserved(cancellationToken);
        await TimeoutAndCancellationNeverResubmit(cancellationToken);
        await UnknownFinalCostNeverResubmits(cancellationToken);
        await OutboundAndPersistenceFailuresNeverSubmit(cancellationToken);
    }

    /// <summary>恢复已有账本时验证账号、模型、金额和状态，不通过伪造序号绕过次数上限。</summary>
    private static void RestoredBudgetAndTaskIdentityCannotBeForged()
    {
        var ledger = new MpsBudgetLedger(2m);
        var plan = Plan(ledger);
        var first = plan.Begin(Request()).Attempt!;
        plan.MarkSubmitted(first.AttemptId, "restore-identity-task");
        var sameLedger = MpsAuthorizedProbePlan.ReopenJson(plan.ExportJson(), ledger);
        Assert(sameLedger.Recovery.ToSnapshot().Entries.Single().TaskId == "restore-identity-task" && ledger.EntryCount == 1,
            "兼容既有预算行应继续复用同一任务而非重复预留");

        var wrongAccount = new MpsBudgetLedger(2m);
        wrongAccount.Reserve("offline", "restore-identity-task", .5m, Estimate(.5m), "another-account", "offline-image");
        ExpectInvalid(() => MpsAuthorizedProbePlan.ReopenJson(plan.ExportJson(), wrongAccount));
        var wrongModel = new MpsBudgetLedger(2m);
        wrongModel.Reserve("offline", "restore-identity-task", .5m, Estimate(.5m), "lab", "another-model");
        ExpectInvalid(() => MpsAuthorizedProbePlan.ReopenJson(plan.ExportJson(), wrongModel));
        var wrongAmount = new MpsBudgetLedger(2m);
        wrongAmount.Reserve("offline", "restore-identity-task", .1m, Estimate(.1m), "lab", "offline-image");
        ExpectInvalid(() => MpsAuthorizedProbePlan.ReopenJson(plan.ExportJson(), wrongAmount));
        var wrongState = new MpsBudgetLedger(2m);
        wrongState.Reserve("offline", "restore-identity-task", .5m, Estimate(.5m), "lab", "offline-image");
        wrongState.Release("offline", "restore-identity-task");
        ExpectInvalid(() => MpsAuthorizedProbePlan.ReopenJson(plan.ExportJson(), wrongState));

        var stored = plan.Recovery.Find(first.AttemptId)!;
        stored.TaskId = null;
        ExpectInvalid(() => plan.Recovery.Upsert(stored));
        stored.TaskId = "replacement-task";
        ExpectInvalid(() => plan.Recovery.Upsert(stored));
        Assert(plan.Recovery.Find(first.AttemptId)!.TaskId == "restore-identity-task", "拒绝更新后不能丢失原任务号");

        var discontinuous = plan.ToSnapshot();
        discontinuous.Recovery.Entries.Single().Sequence = 2;
        ExpectInvalid(() => discontinuous.Validate());
        plan.MarkSucceeded(first.AttemptId, Confirmed(.2m));
        var second = plan.Begin(Request(Estimate(.2m), "sha256:other")).Attempt!;
        plan.MarkSubmitted(second.AttemptId, "restore-second-task");
        plan.MarkSucceeded(second.AttemptId, Confirmed(.2m));
        var overCount = plan.ToSnapshot();
        overCount.Authorization.MaxRequests = 1;
        ExpectInvalid(() => overCount.Validate());
        var terminalMismatch = new MpsBudgetLedger(2m);
        terminalMismatch.Reserve("offline", "restore-identity-task", .5m, Estimate(.5m), "lab", "offline-image");
        terminalMismatch.Confirm("offline", "restore-identity-task", .3m, Confirmed(.3m));
        ExpectInvalid(() => MpsAuthorizedProbePlan.ReopenJson(plan.ExportJson(), terminalMismatch));
    }

    /// <summary>供应商意外超额和恶意恢复范围不能被当成授权内成功。</summary>
    private static void CostOverrunsAndSnapshotScopeStayBlocked()
    {
        var ledger = new MpsBudgetLedger(1m);
        var plan = Plan(ledger);
        var attempt = plan.Begin(Request()).Attempt!;
        plan.MarkSubmitted(attempt.AttemptId, "overrun-task");
        var overrun = plan.MarkSucceeded(attempt.AttemptId, Confirmed(.8m));
        Assert(overrun.State == MpsProbeExecutionState.Unknown && overrun.Cost.Amount == .8m && ledger.Unknown == .8m,
            "实际费用超出单次授权时必须保留未知和实付观察");
        var reopenedLedger = new MpsBudgetLedger(1m);
        var reopened = MpsAuthorizedProbePlan.ReopenJson(plan.ExportJson(), reopenedLedger);
        Assert(reopenedLedger.Unknown == .8m && reopened.Preflight(Request()).Decision == MpsProbePreflightDecision.RecoveryRequired,
            "费用超限重开后必须继续占用预算并阻止重复调用");
        var higher = plan.MarkSucceeded(attempt.AttemptId, Confirmed(.9m));
        Assert(higher.State == MpsProbeExecutionState.Unknown && ledger.Unknown == .9m, "重复更高费用观察应增加同一未知占用");
        var beyondBudget = plan.MarkSucceeded(attempt.AttemptId, Confirmed(1.2m));
        Assert(beyondBudget.Cost.Amount == 1.2m && ledger.Unknown == .9m && ledger.EntryCount == 1,
            "无法容纳更高实付时保留当前未知占用并记录超额观察");
        var malicious = plan.ToSnapshot();
        malicious.Recovery.Entries[0].AccountAlias = "different-account";
        ExpectInvalid(() => malicious.Validate());
        var normal = Plan();
        var normalAttempt = normal.Begin(Request()).Attempt!;
        normal.MarkSubmitted(normalAttempt.AttemptId, "done-task");
        normal.MarkSucceeded(normalAttempt.AttemptId, Confirmed(.2m));
        ExpectInvalidOperation(() => normal.MarkFailed(normalAttempt.AttemptId, Confirmed(.2m)));
        ExpectInvalidOperation(() => normal.MarkUnknown(normalAttempt.AttemptId, "后来查询未知"));
        var notGranted = Authorization();
        notGranted.Granted = false;
        var frozen = new MpsAuthorizedProbePlan(notGranted, new MpsBudgetLedger(1m));
        notGranted.Granted = true;
        frozen.Authorization.Granted = true;
        Assert(frozen.Preflight(Request()).Decision == MpsProbePreflightDecision.NotAuthorized, "授权对象副本不能改变计划内已保存范围");
    }

    /// <summary>授权金额与次数是硬边界，未知费用不能伪装成免费。</summary>
    private static void PreflightRejectsUnauthorizedUnknownAndOverLimit()
    {
        var auth = Authorization();
        auth.Granted = false;
        var denied = new MpsAuthorizedProbePlan(auth, new MpsBudgetLedger(1m));
        Assert(denied.Preflight(Request()).Decision == MpsProbePreflightDecision.NotAuthorized, "未授权计划应被预检拒绝");
        var plan = Plan();
        Assert(plan.Preflight(Request(MpsPriceMetadata.Unknown("未知价格"))).Decision == MpsProbePreflightDecision.UnknownCost,
            "未知价格应拒绝调用");
        Assert(plan.Preflight(Request(Estimate(.6m))).Decision == MpsProbePreflightDecision.SingleLimitExceeded,
            "单次金额超过授权应拒绝");
        Assert(plan.Preflight(Request(MpsPriceMetadata.Estimate(.1m, "USD", "request", "离线估算"))).Decision == MpsProbePreflightDecision.InvalidRequest,
            "不同币种不能暗中换算");
        Assert(plan.Preflight(Request(), DateTimeOffset.UtcNow.AddHours(2)).Decision == MpsProbePreflightDecision.Expired,
            "过期授权不能调用");
        Assert(plan.Recovery.ToSnapshot().Entries.Count == 0, "拒绝预检不能登记提交意图");

        var freeAuth = Authorization();
        freeAuth.Level = MpsMeasurementLevel.FreeMinimalCall;
        var freePlan = new MpsAuthorizedProbePlan(freeAuth, new MpsBudgetLedger(1m));
        Assert(freePlan.Preflight(Request(Estimate(0m))).Decision == MpsProbePreflightDecision.UnknownCost, "免费调用应要求确认的零成本");
        Assert(freePlan.Preflight(Request(Confirmed(0m))).Allowed, "确认零成本的免费探测应通过预检");

        var first = plan.Begin(Request(), cancellationToken: default).Attempt!;
        plan.MarkSubmitted(first.AttemptId, "first-task");
        plan.MarkSucceeded(first.AttemptId, Confirmed(.5m));
        var second = plan.Begin(Request(Estimate(.5m), "sha256:second")).Attempt!;
        plan.MarkSubmitted(second.AttemptId, "second-task");
        plan.MarkSucceeded(second.AttemptId, Confirmed(.5m));
        Assert(plan.Preflight(Request(Estimate(.1m), "sha256:third")).Decision == MpsProbePreflightDecision.RequestLimitExceeded,
            "次数上限不能因任务已经结束而清零");

        var cumulativeAuth = Authorization();
        cumulativeAuth.MaxRequests = 4;
        cumulativeAuth.CumulativeLimitAmount = .6m;
        var cumulative = new MpsAuthorizedProbePlan(cumulativeAuth, new MpsBudgetLedger(2m));
        var used = cumulative.Begin(Request()).Attempt!;
        cumulative.MarkSubmitted(used.AttemptId, "used-task");
        cumulative.MarkSucceeded(used.AttemptId, Confirmed(.5m));
        Assert(cumulative.Preflight(Request(Estimate(.2m), "sha256:another")).Decision == MpsProbePreflightDecision.CumulativeLimitExceeded,
            "累计金额不能超过授权");
    }

    /// <summary>两个计划实例共享恢复仓库时只有一次意图和一次预算预留。</summary>
    private static async Task ConcurrencyRegistersOnlyOneIntent(CancellationToken cancellationToken)
    {
        var auth = Authorization();
        var recovery = new MpsProbeRecoveryStore();
        var ledger = new MpsBudgetLedger(1m);
        var one = new MpsAuthorizedProbePlan(auth, ledger, recovery);
        var two = new MpsAuthorizedProbePlan(auth, ledger, recovery);
        var results = await Task.WhenAll(
            Task.Run(() => one.Begin(Request(), cancellationToken: cancellationToken), cancellationToken),
            Task.Run(() => two.Begin(Request(), cancellationToken: cancellationToken), cancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        Assert(results.Count(item => item.Allowed) == 1 && ledger.EntryCount == 1 && ledger.Reserved == .5m,
            "并发预检/登记不能产生两个提交意图或重复占用预算");
    }

    /// <summary>提交前落盘包含恢复意图，成功后预算只按 provider+task_id 保留一行。</summary>
    private static async Task StableTaskIdAndPersistenceArePreserved(CancellationToken cancellationToken)
    {
        var ledger = new MpsBudgetLedger(1m);
        var plan = Plan(ledger);
        using var adapter = new FakeAdapter();
        MpsAuthorizedProbeCoordinator? coordinator = null;
        var persisted = false;
        coordinator = Coordinator(plan, adapter, persist: ct =>
        {
            ct.ThrowIfCancellationRequested();
            Assert(plan.Recovery.ToSnapshot(ct).Entries.Single().State == MpsProbeExecutionState.IntentRegistered &&
                coordinator!.TaskRecovery.ToSnapshot(ct).Tasks.Single().TaskId is null, "实际调用前必须先落盘两个恢复意图");
            persisted = true;
            return Task.CompletedTask;
        });
        var queued = await coordinator.ExecuteOrResumeAsync(Request(), cancellationToken);
        Assert(persisted && queued.Submitted && adapter.SubmitCount == 1 && queued.Attempt!.TaskId == "offline-probe-task",
            "授权探测未执行或没有保留稳定任务号");
        var completed = await coordinator.PollUntilTerminalAsync(queued.Attempt!.AttemptId,
            new ProviderAsyncTaskPollingOptions(2, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)), cancellationToken);
        Assert(completed.Attempt!.State == MpsProbeExecutionState.Succeeded && ledger.Confirmed == .2m && ledger.EntryCount == 1 &&
            ledger.Snapshot().Single().TaskId == "offline-probe-task", "供应商任务费用应只按稳定任务号确认一行");
        ledger.Reconcile("offline", "offline-probe-task", .2m, Confirmed(.2m));
        Assert(ledger.EntryCount == 1 && ledger.Confirmed == .2m, "再次供应商用量对账不能重复收费");

        var reopenedLedger = new MpsBudgetLedger(1m);
        var reopened = MpsAuthorizedProbePlan.ReopenJson(plan.ExportJson(cancellationToken), reopenedLedger, cancellationToken);
        var retry = await Coordinator(reopened, adapter).ExecuteOrResumeAsync(Request(), cancellationToken);
        Assert(!retry.Submitted && adapter.SubmitCount == 1 && reopenedLedger.EntryCount == 1 && reopenedLedger.Confirmed == .2m,
            "成功任务重开后不能重复提交或重新计费");
        ExpectJson(() => MpsAuthorizedProbePlan.ReopenJson(plan.ExportJson().TrimEnd('}') + ",\"credential\":\"forbidden\"}", new MpsBudgetLedger(1m)));
    }

    /// <summary>不协作适配器超时和用户取消后只保留未知恢复状态。</summary>
    private static async Task TimeoutAndCancellationNeverResubmit(CancellationToken cancellationToken)
    {
        using var timeoutAdapter = new FakeAdapter { HangSubmission = true };
        var timeoutLedger = new MpsBudgetLedger(1m);
        var timeoutPlan = Plan(timeoutLedger);
        var timeout = Coordinator(timeoutPlan, timeoutAdapter, TimeSpan.FromMilliseconds(60));
        var result = await timeout.ExecuteOrResumeAsync(Request(), cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        Assert(result.Attempt!.State == MpsProbeExecutionState.Unknown && timeoutLedger.Unknown == .5m && timeoutAdapter.LastToken.IsCancellationRequested,
            "超时不能释放预算或伪装免费");
        var reopenedLedger = new MpsBudgetLedger(1m);
        var reopened = MpsAuthorizedProbePlan.ReopenJson(timeoutPlan.ExportJson(cancellationToken), reopenedLedger, cancellationToken);
        var resumed = Coordinator(reopened, timeoutAdapter, TimeSpan.FromMilliseconds(60));
        var retry = await resumed.ExecuteOrResumeAsync(Request(), cancellationToken);
        Assert(!retry.Submitted && timeoutAdapter.SubmitCount == 1 && reopenedLedger.Unknown == .5m, "提交超时重开后不能盲目重提");

        using var cancelAdapter = new FakeAdapter { HangSubmission = true };
        var cancelPlan = Plan();
        var cancel = Coordinator(cancelPlan, cancelAdapter);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        canceled.CancelAfter(TimeSpan.FromMilliseconds(60));
        try
        {
            await cancel.ExecuteOrResumeAsync(Request(), canceled.Token).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            throw new InvalidOperationException("用户取消没有停止等待");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        var cancelReopened = MpsAuthorizedProbePlan.ReopenJson(cancelPlan.ExportJson(cancellationToken), new MpsBudgetLedger(1m), cancellationToken);
        var canceledRetry = await Coordinator(cancelReopened, cancelAdapter).ExecuteOrResumeAsync(Request(), cancellationToken);
        Assert(!canceledRetry.Submitted && cancelAdapter.SubmitCount == 1, "用户取消后重开不能重新提交");
    }

    /// <summary>终态缺价格仍保留 Unknown；找回旧任务后查询和对账不会重提。</summary>
    private static async Task UnknownFinalCostNeverResubmits(CancellationToken cancellationToken)
    {
        using var adapter = new FakeAdapter { UnknownFinalCost = true };
        var ledger = new MpsBudgetLedger(1m);
        var plan = Plan(ledger);
        var coordinator = Coordinator(plan, adapter);
        var queued = await coordinator.ExecuteOrResumeAsync(Request(), cancellationToken);
        var unknown = await coordinator.PollUntilTerminalAsync(queued.Attempt!.AttemptId,
            new ProviderAsyncTaskPollingOptions(2, TimeSpan.FromSeconds(2)), cancellationToken);
        Assert(unknown.Attempt!.State == MpsProbeExecutionState.Unknown && ledger.Unknown == .5m, "终态未知费用应阻断新调用并占用预算");
        var newRequest = await coordinator.ExecuteOrResumeAsync(Request(Estimate(.1m), "sha256:unrelated"), cancellationToken);
        Assert(!newRequest.Submitted && adapter.SubmitCount == 1, "旧任务未知时不得自动提交新指纹任务");
        plan.ResolveRecovered(unknown.Attempt.AttemptId, MpsProbeExecutionState.Succeeded, Confirmed(.2m), "offline-probe-task");
        Assert(ledger.EntryCount == 1 && ledger.Confirmed == .2m && ledger.Unknown == 0m, "恢复对账应保留同一供应商任务号");
    }

    /// <summary>外发许可失败和持久化失败都阻止适配器提交。</summary>
    private static async Task OutboundAndPersistenceFailuresNeverSubmit(CancellationToken cancellationToken)
    {
        using var adapter = new FakeAdapter();
        var plan = Plan();
        var denied = new MpsAuthorizedProbeCoordinator(plan, adapter, adapter,
            (_, _, _) => throw new InvalidOperationException("离线素材授权拒绝"), _ => Task.CompletedTask);
        try { await denied.ExecuteOrResumeAsync(Request(), cancellationToken); throw new InvalidOperationException("外发拒绝未生效"); }
        catch (InvalidOperationException error) when (error.Message == "离线素材授权拒绝") { }
        Assert(adapter.SubmitCount == 0 && plan.Recovery.ToSnapshot().Entries.Count == 0, "外发拒绝不能登记供应商调用");
        var persistencePlan = Plan();
        var persisted = Coordinator(persistencePlan, adapter, persist: _ => throw new IOException("离线落盘失败"));
        try { await persisted.ExecuteOrResumeAsync(Request(), cancellationToken); throw new InvalidOperationException("落盘失败没有阻止提交"); }
        catch (IOException) { }
        Assert(adapter.SubmitCount == 0 && persistencePlan.Recovery.ToSnapshot().Entries.Single().State == MpsProbeExecutionState.Unknown,
            "落盘失败应停止提交并保留保守恢复边界");
    }

    /// <summary>创建一小时内的离线测试授权，不访问任何真实账号。</summary>
    private static MpsProbeAuthorization Authorization() => MpsProbeAuthorization.GrantFor(
        "offline-project", "offline", "lab", "offline-image", MpsCapabilityKind.ImageGeneration,
        MpsMeasurementLevel.BillableMinimalCall, Summary, .5m, 1m, 2,
        DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));

    private static MpsAuthorizedProbePlan Plan(MpsBudgetLedger? ledger = null) => new(Authorization(), ledger ?? new MpsBudgetLedger(1m));
    private static MpsPriceMetadata Estimate(decimal amount) => MpsPriceMetadata.Estimate(amount, "CNY", "request", "离线预算上限");
    private static MpsPriceMetadata Confirmed(decimal amount) => MpsPriceMetadata.Confirmed(amount, "CNY", "request", "离线确认费用");
    private static MpsProbeRequest Request(MpsPriceMetadata? cost = null, string fingerprint = "sha256:probe") => new()
    {
        InputFingerprint = fingerprint, RequestSummary = Summary, Cost = cost ?? Estimate(.5m)
    };

    private static MpsAuthorizedProbeCoordinator Coordinator(MpsAuthorizedProbePlan plan, FakeAdapter adapter,
        TimeSpan? timeout = null, Func<CancellationToken, Task>? persist = null) => new(plan, adapter, adapter,
        (_, _, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, persist ?? (_ => Task.CompletedTask),
        operationTimeout: timeout);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectJson(Action action)
    {
        try { action(); throw new InvalidOperationException("探测计划未知字段没有拒绝"); }
        catch (JsonException) { }
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); throw new InvalidOperationException("无效探测范围未拒绝"); }
        catch (InvalidDataException) { }
    }

    private static void ExpectInvalidOperation(Action action)
    {
        try { action(); throw new InvalidDataException("探测非法终态迁移未拒绝"); }
        catch (InvalidOperationException) { }
    }

    /// <summary>内存假适配器；仅创建一个可取消的未完成任务，Dispose 负责释放。</summary>
    private sealed class FakeAdapter : IProviderTaskSubmissionAdapter, IProviderTaskStatusAdapter, IDisposable
    {
        private readonly TaskCompletionSource<ProviderAdapterSubmission> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HangSubmission { get; init; }
        public bool UnknownFinalCost { get; init; }
        public int SubmitCount { get; private set; }
        public CancellationToken LastToken { get; private set; }

        /// <summary>返回固定任务号或故意忽略取消，以验证主协调器等待边界。</summary>
        public Task<ProviderAdapterSubmission> SubmitAsync(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            LastToken = cancellationToken;
            return HangSubmission ? pending.Task : Task.FromResult(new ProviderAdapterSubmission(
                "offline-probe-task", ProviderAdapterTaskStatus.Queued, null, null, DateTimeOffset.UtcNow));
        }

        /// <summary>状态查询不访问网络，只返回本地终态和可选未知费用。</summary>
        public Task<ProviderAdapterTaskState> GetStatusAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderAdapterTaskState(query.TaskId, ProviderAdapterTaskStatus.Succeeded,
                UnknownFinalCost ? null : .2m, UnknownFinalCost ? null : "CNY", ProviderAdapterErrorCategory.Unknown, DateTimeOffset.UtcNow));
        }

        /// <summary>取消返回已确认取消及未知费用，不能误记免费。</summary>
        public Task<ProviderAdapterCancellation> CancelAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderAdapterCancellation(query.TaskId, ProviderAdapterTaskStatus.Cancelled,
                ProviderAdapterErrorCategory.Cancelled, DateTimeOffset.UtcNow));
        }

        /// <summary>释放本测试创建的唯一未完成任务，不终止任何共享进程。</summary>
        public void Dispose() => pending.TrySetCanceled();
    }
}
