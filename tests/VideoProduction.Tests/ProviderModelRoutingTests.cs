using VideoProduction;

namespace VideoProductionTests;

/// <summary>模型推荐与人工锁定的离线回归，不访问供应商或凭据。</summary>
public static class ProviderModelRoutingTests
{
    /// <summary>执行推荐、预算和锁定暂停断言。</summary>
    public static void Run()
    {
        var candidates = new List<ProviderModelCandidate>
        {
            Candidate("moark", "primary", "text-good", MpsModelEvidenceStatus.Measured, 90, 80, 0.2m),
            Candidate("sonnet.vip", "backup", "text-good", MpsModelEvidenceStatus.AccountCallable, 95, 90, 0.1m),
            Candidate("moark", "primary", "unknown-capability", MpsModelEvidenceStatus.Measured, 100, 100, 0m, MpsCapabilityKind.Unknown),
            Candidate("moark", "limited", "text-good", MpsModelEvidenceStatus.Measured, 100, 100, 0.01m, availability: ProviderAdapterAvailability.Unknown)
        };
        var request = new ProviderModelRecommendationRequest(MpsCapabilityKind.TextPlanning, candidates, 0.15m, "CNY");
        var result = ProviderModelRouter.Recommend(request);
        Assert(result.IsSelected && result.Candidate!.ProviderId == "sonnet.vip", "推荐应遵守硬预算并选择可用候选");
        var unknownPrice = candidates[0] with { EstimatedPrice = null, Currency = null };
        var noFree = ProviderModelRouter.Recommend(new ProviderModelRecommendationRequest(
            MpsCapabilityKind.TextPlanning, [unknownPrice], 0.15m, "CNY"));
        Assert(!noFree.IsSelected && noFree.RequiresUserChoice, "未知费用不能当作免费突破硬预算");

        var modelLock = new ProviderModelLock("project:demo", MpsCapabilityKind.TextPlanning, "moark", "primary", "text-good");
        var locked = ProviderModelLockResolver.Resolve(modelLock, candidates);
        Assert(locked.IsUsable, "可用模型锁定应解析成功");
        var unavailable = ProviderModelLockResolver.Resolve(modelLock, [candidates[3] with { ProviderId = "moark", AccountAlias = "primary" }]);
        Assert(unavailable.Paused && unavailable.Reason.Contains("不可用", StringComparison.Ordinal), "锁定模型不可用时必须暂停");
        var missing = ProviderModelLockResolver.Resolve(modelLock, [candidates[1]]);
        Assert(missing.Paused && missing.Reason.Contains("用户选择", StringComparison.Ordinal), "锁定模型缺失时必须交由用户选择");
        try
        {
            _ = ProviderModelRouter.Recommend(new ProviderModelRecommendationRequest(MpsCapabilityKind.Unknown, candidates));
            throw new InvalidOperationException("未知能力不应进入推荐");
        }
        catch (InvalidDataException) { }
        try
        {
            _ = ProviderModelRouter.Recommend(new ProviderModelRecommendationRequest(MpsCapabilityKind.TextPlanning, candidates, MaxCandidates: 0));
            throw new InvalidOperationException("候选数量上限无效时不应进入推荐");
        }
        catch (InvalidDataException) { }
    }

    private static ProviderModelCandidate Candidate(
        string provider,
        string account,
        string model,
        MpsModelEvidenceStatus evidence,
        double quality,
        double speed,
        decimal price,
        MpsCapabilityKind capability = MpsCapabilityKind.TextPlanning,
        ProviderAdapterAvailability availability = ProviderAdapterAvailability.Available)
    {
        var descriptor = new MpsModelDescriptor
        {
            ModelId = model,
            DisplayName = model,
            Modalities = [MpsModelModality.Text],
            Capabilities = [capability],
            EvidenceStatus = evidence,
            ExecutionMode = MpsModelExecutionMode.Synchronous,
            Source = provider,
            ObservedUtc = DateTimeOffset.UtcNow,
            Price = new MpsModelPrice { Amount = price, Currency = "CNY", Unit = "request", Basis = "offline" }
        };
        return new ProviderModelCandidate(provider, account, descriptor, availability, quality, speed, price, "CNY");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
