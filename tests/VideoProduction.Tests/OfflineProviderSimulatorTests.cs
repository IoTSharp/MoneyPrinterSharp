using VideoProduction;

namespace VideoProductionTests;

/// <summary>里程碑 C 离线供应商模拟回归；全程不访问网络、不写任务记录。</summary>
public static class OfflineProviderSimulatorTests
{
    /// <summary>执行目录、权限、限流、异步状态、费用和脱敏地址断言。</summary>
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var clock = new ManualOfflineClock(DateTimeOffset.UnixEpoch);
        var options = new OfflineProviderSimulatorOptions
        {
            CatalogPageSize = 2,
            RateLimitRequests = 64,
            MaxRequests = 128,
            PollsBeforeCompletion = 2,
            MaxPolls = 8,
            ResponseFormat = OfflineResponseFormat.WrappedV2
        };
        var simulator = new OfflineProviderSimulator(options, clock);
        await CatalogIsPagedAndFormatIsVisible(simulator, cancellationToken);
        await PermissionsRemainSeparate(simulator, cancellationToken);
        await SuccessfulTaskReturnsFeeAndEphemeralUrl(simulator, cancellationToken);
        await FailedTaskReturnsErrorAndFee(simulator, cancellationToken);
        RateLimitAndRequestLimitAreBounded();
        CancellationStopsBeforeRequest(cancellationToken);
    }

    /// <summary>目录游标只推进有限页，响应格式变化由显式标记承载。</summary>
    private static async Task CatalogIsPagedAndFormatIsVisible(OfflineProviderSimulator simulator, CancellationToken ct)
    {
        var first = await simulator.ListCatalogAsync(new OfflineCatalogQuery(PageSize: 2), ct);
        Assert(first.Models.Count == 2 && first.HasMore && first.NextCursor == "1", "目录第一页或游标错误");
        Assert(first.ResponseFormat == OfflineResponseFormat.WrappedV2, "目录响应格式标记丢失");
        var second = await simulator.ListCatalogAsync(new OfflineCatalogQuery(Cursor: first.NextCursor, PageSize: 2), ct);
        Assert(second.Models.Count == 2 && !second.HasMore && second.NextCursor is null, "目录第二页边界错误");
    }

    /// <summary>未知账号返回401语义，已知账号无权模型返回403语义。</summary>
    private static async Task PermissionsRemainSeparate(OfflineProviderSimulator simulator, CancellationToken ct)
    {
        var unknown = await simulator.CheckAccessAsync("missing", "text-planning", "offline-text", cancellationToken: ct);
        Assert(!unknown.AccountKnown && !unknown.Allowed && unknown.Error == OfflineProviderErrorCode.Unauthorized, "未知账号未返回未授权状态");
        simulator.SetAccountPermissions("limited", ["offline-text"]);
        var denied = await simulator.CheckAccessAsync("limited", "video-generation", "offline-video", cancellationToken: ct);
        Assert(denied.AccountKnown && !denied.Allowed && denied.Error == OfflineProviderErrorCode.Forbidden, "模型权限未独立返回禁止状态");
    }

    /// <summary>成功任务经有限轮询完成，费用返回且签名地址不进入状态或用量记录。</summary>
    private static async Task SuccessfulTaskReturnsFeeAndEphemeralUrl(OfflineProviderSimulator simulator, CancellationToken ct)
    {
        var submission = await simulator.SubmitAsync(new OfflineSubmitRequest("demo", "text-planning", "offline-text", "sha256:fixture"), ct);
        Assert(submission.TaskId.StartsWith("offline-", StringComparison.Ordinal) && submission.EstimatedPrice == 0m, "提交任务号或估算费用错误");
        var result = await simulator.PollUntilCompletedAsync(submission.TaskId, ct);
        Assert(result.Status.Status == OfflineTaskStatusKind.Succeeded && result.Status.Price == 0m, "成功任务状态或费用错误");
        var output = await simulator.GetOutputAsync(submission.TaskId, cancellationToken: ct);
        Assert(output.SignedUrl.StartsWith("https://offline.invalid/output/", StringComparison.Ordinal) && output.SignedUrl.Contains("signature=", StringComparison.Ordinal), "签名地址格式错误");
        var status = await simulator.GetStatusAsync(submission.TaskId, cancellationToken: ct);
        Assert(!status.ToString()!.Contains("signature=", StringComparison.Ordinal), "状态响应不应包含签名地址");
        var usage = await simulator.GetUsageAsync(pageSize: 16, cancellationToken: ct);
        Assert(usage.Entries.Any(item => item.TaskId == submission.TaskId && item.Price == 0m), "用量页未返回脱敏费用");
        var repeat = await simulator.SubmitAsync(new OfflineSubmitRequest("demo", "text-planning", "offline-text", "sha256:fixture"), ct);
        Assert(repeat.TaskId == submission.TaskId && simulator.RequestCount > 0, "相同指纹未幂等复用任务号");
    }

    /// <summary>失败任务进入失败状态并保留费用观察，不能伪装成免费成功。</summary>
    private static async Task FailedTaskReturnsErrorAndFee(OfflineProviderSimulator simulator, CancellationToken ct)
    {
        var submission = await simulator.SubmitAsync(new OfflineSubmitRequest("demo", "video-generation", "offline-video", "sha256:failure", OfflineTaskOutcome.Failure, 1.25m), ct);
        try
        {
            await simulator.PollUntilCompletedAsync(submission.TaskId, ct);
            throw new InvalidOperationException("失败任务不应返回成功轮询结果");
        }
        catch (OfflineProviderException error) when (error.Code == OfflineProviderErrorCode.GenerationFailed) { }
        var usage = await simulator.GetUsageAsync(pageSize: 16, cancellationToken: ct);
        var failed = usage.Entries.Single(item => item.TaskId == submission.TaskId);
        Assert(failed.Status == OfflineTaskStatusKind.Failed && failed.Price == 1.25m, "失败任务费用未保留");
    }

    /// <summary>限流窗口和总请求次数均有明确边界，推进时钟即可恢复。</summary>
    private static void RateLimitAndRequestLimitAreBounded()
    {
        var clock = new ManualOfflineClock(DateTimeOffset.UnixEpoch);
        var simulator = new OfflineProviderSimulator(new OfflineProviderSimulatorOptions
        {
            RateLimitRequests = 1,
            RateLimitWindow = TimeSpan.FromSeconds(5),
            MaxRequests = 2
        }, clock);
        _ = simulator.ListCatalogAsync().GetAwaiter().GetResult();
        try { _ = simulator.ListCatalogAsync().GetAwaiter().GetResult(); throw new InvalidOperationException("限流未触发"); }
        catch (OfflineProviderException error) when (error.Code == OfflineProviderErrorCode.RateLimited && error.RetryAfter > TimeSpan.Zero) { }
        clock.Advance(TimeSpan.FromSeconds(5));
        _ = simulator.ListCatalogAsync().GetAwaiter().GetResult();
        clock.Advance(TimeSpan.FromSeconds(5));
        try { _ = simulator.ListCatalogAsync().GetAwaiter().GetResult(); throw new InvalidOperationException("总请求次数上限未触发"); }
        catch (OfflineProviderException error) when (error.Code == OfflineProviderErrorCode.RequestLimitExceeded) { }
    }

    /// <summary>取消令牌在请求开始前生效，且不会增加模拟请求计数。</summary>
    private static void CancellationStopsBeforeRequest(CancellationToken parent)
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(parent);
        canceled.Cancel();
        var simulator = new OfflineProviderSimulator();
        try { _ = simulator.ListCatalogAsync(cancellationToken: canceled.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("取消请求未中止"); }
        catch (OperationCanceledException) { }
        Assert(simulator.RequestCount == 0, "取消请求不应计入模拟请求次数");
    }

    /// <summary>统一断言消息，避免引入额外测试框架和网络依赖。</summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
