namespace VideoProduction;

/// <summary>授权探测执行结果；拒绝和未知状态不会触发新的提交。</summary>
public sealed record MpsProbeExecutionResult(
    MpsProbePreflightResult Preflight,
    MpsProbeRecoveryEntry? Attempt,
    ProviderAsyncTaskResult? ProviderTask,
    bool Submitted);

/// <summary>把授权探测计划接入已有异步恢复协议；不包含任何供应商网络实现。</summary>
public sealed class MpsAuthorizedProbeCoordinator
{
    private readonly MpsAuthorizedProbePlan plan;
    private readonly ProviderAsyncTaskCoordinator tasks;
    private readonly Func<MpsProbeAuthorization, MpsProbeRequest, CancellationToken, Task> outboundPreflight;
    private readonly TimeSpan operationTimeout;

    /// <summary>创建协调器；外发预检和落盘回调由项目边界提供，禁止默默绕过。</summary>
    public MpsAuthorizedProbeCoordinator(MpsAuthorizedProbePlan plan,
        IProviderTaskSubmissionAdapter submission, IProviderTaskStatusAdapter status,
        Func<MpsProbeAuthorization, MpsProbeRequest, CancellationToken, Task> outboundPreflight,
        Func<CancellationToken, Task> persistBeforeSubmit,
        ProviderAsyncTaskRecoveryStore? taskRecovery = null, TimeSpan? operationTimeout = null)
    {
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        this.outboundPreflight = outboundPreflight ?? throw new ArgumentNullException(nameof(outboundPreflight));
        ArgumentNullException.ThrowIfNull(persistBeforeSubmit);
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        if (this.operationTimeout <= TimeSpan.Zero || this.operationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        var persistedSubmission = new PersistedSubmissionAdapter(submission, persistBeforeSubmit);
        tasks = new ProviderAsyncTaskCoordinator(plan.Authorization.ProviderId, persistedSubmission, status,
            taskRecovery, this.operationTimeout);
    }

    /// <summary>异步任务恢复状态，落盘回调应与计划状态一起保存。</summary>
    public ProviderAsyncTaskRecoveryStore TaskRecovery => tasks.Recovery;

    /// <summary>通过授权、外发和预算预检后只提交一次；已有意图优先恢复旧任务。</summary>
    public async Task<MpsProbeExecutionResult> ExecuteOrResumeAsync(MpsProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preflight = plan.Preflight(request, cancellationToken: cancellationToken);
        MpsProbeRecoveryEntry? attempt;
        if (preflight.Decision == MpsProbePreflightDecision.RecoveryRequired && preflight.RecoveryAttemptId is { } attemptId)
        {
            attempt = plan.Recovery.Find(attemptId, cancellationToken);
            // 不允许用不同指纹的新请求借已有恢复记录越过授权范围。
            if (attempt is null || attempt.InputFingerprint != request.InputFingerprint)
                return new MpsProbeExecutionResult(preflight, attempt, null, false);
            if (attempt.State == MpsProbeExecutionState.IntentRegistered && tasks.Recovery.Find(attempt.IdempotencyKey, cancellationToken) is null)
                return new MpsProbeExecutionResult(preflight, attempt, null, false);
            EnsureTaskRecovery(attempt, cancellationToken);
        }
        else
        {
            if (!preflight.Allowed) return new MpsProbeExecutionResult(preflight, null, null, false);
            // 项目素材、模型锁定与外发许可必须在登记意图前通过调用方的受控预检。
            await AwaitBoundedAsync(token => outboundPreflight(plan.Authorization, request, token), cancellationToken).ConfigureAwait(false);
            var admission = plan.Begin(request, cancellationToken: cancellationToken);
            preflight = admission.Preflight;
            if (!admission.Allowed) return new MpsProbeExecutionResult(preflight, null, null, false);
            attempt = admission.Attempt!;
        }

        var providerRequest = RequestFor(attempt);
        try
        {
            var result = await tasks.SubmitOrResumeAsync(providerRequest, cancellationToken).ConfigureAwait(false);
            attempt = ApplyTaskResult(attempt, result);
            return new MpsProbeExecutionResult(preflight, attempt, result, result.Submitted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消令牌不能阻止保存未知状态，可能已发生的费用必须在重开后继续占用预算。
            plan.MarkUnknown(attempt.AttemptId, "提交等待已被用户中止，必须先查询旧任务。");
            throw;
        }
        catch
        {
            plan.MarkUnknown(attempt.AttemptId, "探测调用未完成，必须先查询旧任务。");
            throw;
        }
    }

    /// <summary>有界查询已有任务，达到截止时间或取消后保留同一恢复条目。</summary>
    public async Task<MpsProbeExecutionResult> PollUntilTerminalAsync(string attemptId,
        ProviderAsyncTaskPollingOptions? options = null, CancellationToken cancellationToken = default)
    {
        var attempt = FindAttempt(attemptId, cancellationToken);
        EnsureTaskRecovery(attempt, cancellationToken);
        try
        {
            var result = await tasks.PollUntilTerminalAsync(attempt.IdempotencyKey, options, cancellationToken).ConfigureAwait(false);
            return new MpsProbeExecutionResult(RecoveryPreflight(attempt), ApplyTaskResult(attempt, result), result, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            plan.MarkUnknown(attemptId, "探测查询已被用户中止，必须先查询旧任务。");
            throw;
        }
    }

    /// <summary>请求取消旧任务；供应商确认取消前保持未知费用，绝不重新提交。</summary>
    public async Task<MpsProbeExecutionResult> CancelAsync(string attemptId, CancellationToken cancellationToken = default)
    {
        var attempt = FindAttempt(attemptId, cancellationToken);
        EnsureTaskRecovery(attempt, cancellationToken);
        try
        {
            var result = await tasks.CancelAsync(attempt.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            return new MpsProbeExecutionResult(RecoveryPreflight(attempt), ApplyTaskResult(attempt, result), result, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            plan.MarkUnknown(attemptId, "探测取消等待已被用户中止，必须先查询旧任务。");
            throw;
        }
    }

    /// <summary>把查询白名单状态映射到探测账本，不凭供应商原文推断免费或成功。</summary>
    private MpsProbeRecoveryEntry ApplyTaskResult(MpsProbeRecoveryEntry attempt, ProviderAsyncTaskResult result)
    {
        if (attempt.State is MpsProbeExecutionState.Succeeded or MpsProbeExecutionState.Failed)
            return attempt;
        if (result.Task.TaskId is { } taskId && attempt.TaskId is null)
        {
            if (attempt.State == MpsProbeExecutionState.IntentRegistered)
                attempt = plan.MarkSubmitted(attempt.AttemptId, taskId);
            else
                attempt = plan.AttachRecoveredTaskId(attempt.AttemptId, taskId);
        }
        if (result.RequiresRecoveryQuery || result.Task.Status == ProviderAdapterTaskStatus.Unknown)
            return plan.MarkUnknown(attempt.AttemptId, "探测状态未知，必须先查询旧任务。");
        if (result.Task.Status is ProviderAdapterTaskStatus.Succeeded or ProviderAdapterTaskStatus.Failed or ProviderAdapterTaskStatus.Cancelled)
        {
            if (result.Task.Price is null || string.IsNullOrWhiteSpace(result.Task.Currency))
                return plan.MarkUnknown(attempt.AttemptId, "探测费用未知，需要查询用量或人工对账。");
            var cost = MpsPriceMetadata.Confirmed(result.Task.Price.Value, result.Task.Currency, "request", "探测任务白名单费用");
            return result.Task.Status == ProviderAdapterTaskStatus.Succeeded
                ? plan.MarkSucceeded(attempt.AttemptId, cost)
                : plan.MarkFailed(attempt.AttemptId, cost, "供应商已确认探测失败或取消。");
        }
        return attempt;
    }

    /// <summary>计划状态可重建异步恢复摘要；缺少任务号时仍只保留 Unknown，不能重发。</summary>
    private void EnsureTaskRecovery(MpsProbeRecoveryEntry attempt, CancellationToken cancellationToken)
    {
        if (tasks.Recovery.Find(attempt.IdempotencyKey, cancellationToken) is not null) return;
        tasks.Recovery.Upsert(new ProviderTaskRecoveryEntry
        {
            ProviderId = attempt.ProviderId,
            AccountAlias = attempt.AccountAlias,
            ModelId = attempt.ModelId,
            Capability = attempt.Capability,
            InputFingerprint = attempt.InputFingerprint,
            IdempotencyKey = attempt.IdempotencyKey,
            TaskId = attempt.TaskId,
            Status = attempt.State switch
            {
                MpsProbeExecutionState.Succeeded => ProviderAdapterTaskStatus.Succeeded,
                MpsProbeExecutionState.Failed => ProviderAdapterTaskStatus.Failed,
                MpsProbeExecutionState.Submitted => ProviderAdapterTaskStatus.Queued,
                _ => ProviderAdapterTaskStatus.Unknown
            },
            Price = attempt.Cost.IsConfirmed ? attempt.Cost.Amount : null,
            Currency = attempt.Cost.IsConfirmed ? attempt.Cost.Currency : null,
            CreatedUtc = attempt.CreatedUtc,
            UpdatedUtc = attempt.UpdatedUtc
        }, cancellationToken);
    }

    /// <summary>只读取属于当前授权计划的探测。</summary>
    private MpsProbeRecoveryEntry FindAttempt(string attemptId, CancellationToken cancellationToken)
    {
        var entry = plan.Recovery.Find(attemptId, cancellationToken) ?? throw new InvalidDataException("探测条目不存在。");
        if (entry.PlanId != plan.PlanId) throw new InvalidDataException("探测条目不属于当前计划。");
        return entry;
    }

    /// <summary>把已登记指纹转换成统一提交请求，不携带素材正文。</summary>
    private static ProviderAdapterSubmitRequest RequestFor(MpsProbeRecoveryEntry attempt) => new(
        attempt.AccountAlias, attempt.ModelId, attempt.Capability, attempt.InputFingerprint,
        attempt.IdempotencyKey, attempt.ReservedAmount, attempt.Cost.Currency);

    /// <summary>为已有任务构造恢复提示，表明本次没有新预算登记。</summary>
    private static MpsProbePreflightResult RecoveryPreflight(MpsProbeRecoveryEntry attempt) => new(
        MpsProbePreflightDecision.RecoveryRequired, "查询或取消已有探测任务。", null, attempt.Sequence,
        attempt.ReservedAmount, attempt.AttemptId);

    /// <summary>外发预检自身也有单操作时限，即使回调忽略取消也不无限等待。</summary>
    private async Task AwaitBoundedAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(operationTimeout);
        var pending = operation(deadline.Token);
        try { await pending.WaitAsync(operationTimeout, deadline.Token).ConfigureAwait(false); }
        finally
        {
            if (!pending.IsCompleted)
            {
                deadline.Cancel();
                _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    /// <summary>在已有异步协调器保存提交意图后、实际适配器调用前完成持久化。</summary>
    private sealed class PersistedSubmissionAdapter(
        IProviderTaskSubmissionAdapter inner,
        Func<CancellationToken, Task> persist) : IProviderTaskSubmissionAdapter
    {
        /// <summary>落盘失败或取消时绝不调用真实提交适配器。</summary>
        public async Task<ProviderAdapterSubmission> SubmitAsync(ProviderAdapterSubmitRequest request,
            CancellationToken cancellationToken = default)
        {
            await persist(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await inner.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
