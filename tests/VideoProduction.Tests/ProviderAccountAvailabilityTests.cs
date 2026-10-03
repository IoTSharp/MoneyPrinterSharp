using VideoProduction;

namespace VideoProductionTests;

/// <summary>验证账号存在、权限、余额、配额和区域事实彼此隔离。</summary>
public static class ProviderAccountAvailabilityTests
{
    /// <summary>401、403、余额不足及未知状态不得伪装为模型不存在。</summary>
    public static void Run()
    {
        var observed = new DateTimeOffset(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);
        var unauthorized = ProviderAccountAvailability.FromAdapterState("offline", new ProviderAdapterAccountState(
            "account-a", ProviderAdapterAvailability.Unavailable, ProviderAdapterErrorCategory.Authentication, observed));
        Assert(unauthorized.AccountExistence == ProviderAccountFactState.Unknown, "401 不应断言账号不存在");
        Assert(unauthorized.ModelExistence == ProviderAccountFactState.Unknown, "401 不应断言模型不存在");
        Assert(unauthorized.Permission == ProviderAccountFactState.Unknown, "401 不应伪装成权限拒绝");

        var forbidden = ProviderAccountAvailability.FromAdapterState("offline", new ProviderAdapterAccountState(
            "account-a", ProviderAdapterAvailability.Unavailable, ProviderAdapterErrorCategory.Permission, observed), "model-a");
        Assert(forbidden.AccountExistence == ProviderAccountFactState.Confirmed && forbidden.Permission == ProviderAccountFactState.Rejected,
            "403 应记录账号存在和权限拒绝");
        Assert(forbidden.ModelExistence == ProviderAccountFactState.Unknown, "403 不应断言模型不存在");

        var noBalance = ProviderAccountAvailability.FromAdapterState("offline", new ProviderAdapterAccountState(
            "account-a", ProviderAdapterAvailability.Unavailable, ProviderAdapterErrorCategory.Billing, observed), "model-a");
        Assert(noBalance.AccountExistence == ProviderAccountFactState.Confirmed && noBalance.Balance == ProviderAccountFactState.Rejected,
            "余额不足应独立记录");
        Assert(noBalance.ModelExistence == ProviderAccountFactState.Unknown, "余额不足不应断言模型不存在");

        var modelMissing = noBalance.WithModelExistence(ProviderAccountFactState.Rejected);
        Assert(modelMissing.ModelExistence == ProviderAccountFactState.Rejected && modelMissing.Balance == ProviderAccountFactState.Rejected,
            "模型存在性应能在明确目录事实后单独更新");

        var store = new ProviderAccountAvailabilityStore(maxEntries: 1);
        store.Record(forbidden);
        store.Record(modelMissing);
        Assert(!store.TryGet("offline", "account-a", "model-a", out var retained) || retained == modelMissing,
            "同键状态更新应保留最新事实");
        Assert(store.Snapshot().Count == 1, "账号状态表应遵守条目上限");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
