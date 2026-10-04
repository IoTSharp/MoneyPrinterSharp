using VideoProduction;

namespace VideoProductionTests;

/// <summary>统一价格/限制元数据与预算账本的离线回归，不访问网络、凭据或付费服务。</summary>
public static class PricingBudgetLedgerTests
{
    /// <summary>执行价格、限制、预留、对账和硬预算断言。</summary>
    public static void Run()
    {
        PriceAndLimitMetadataKeepUnknownFacts();
        LedgerReservesConsumesReleasesAndDeduplicates();
        LedgerRejectsHardBudgetAndCurrencyOverflow();
    }

    /// <summary>确认价格必须具备币种/单位/依据，估算和未知保持不同状态。</summary>
    private static void PriceAndLimitMetadataKeepUnknownFacts()
    {
        var confirmed = MpsPriceMetadata.Confirmed(1.25m, "CNY", "request", "provider-usage", "offline");
        confirmed.Validate();
        Assert(confirmed.IsConfirmed && !confirmed.IsEstimate, "完整价格应标为已确认");

        var estimate = MpsPriceMetadata.Estimate(2m, "CNY", "minute", "catalog-estimate");
        estimate.Validate();
        Assert(estimate.IsEstimate && !estimate.IsConfirmed, "目录价格应保留估算状态");

        var unknown = MpsPriceMetadata.Unknown("provider-has-not-published-price");
        unknown.Validate();
        Assert(!unknown.IsComplete && !unknown.IsConfirmed, "未知价格不能被视为零费用");

        var limits = new MpsLimitMetadata
        {
            MaxInputTokens = 8_192,
            MaxDurationSeconds = 300,
            MaxBytes = 50_000_000,
            Formats = ["mp4", "webm"],
            AspectRatios = ["16:9", "9:16"]
        };
        limits.Validate();
        Assert(limits.HasKnownLimit, "已声明限制应保留为已知事实");
        var metadata = new MpsPricingLimitMetadata { Price = unknown, Limits = limits };
        metadata.Validate();
        try
        {
            new MpsLimitMetadata { MaxBytes = 0 }.Validate();
            throw new InvalidOperationException("零字节限制不应通过校验");
        }
        catch (InvalidDataException) { }
    }

    /// <summary>同一提供商任务重复观察只计一次，并能完成预留、确认和释放。</summary>
    private static void LedgerReservesConsumesReleasesAndDeduplicates()
    {
        var ledger = new MpsBudgetLedger(10m);
        var estimate = MpsPriceMetadata.Estimate(4m, "CNY", "request", "offline-estimate");
        var first = ledger.Reserve("Moark", "task-1", 4m, estimate, "demo", "model-a");
        var repeat = ledger.Reserve("moark", "TASK-1", 4m, estimate);
        Assert(first.StableKey == repeat.StableKey && ledger.Reserved == 4m, "同一 provider+task 预留必须幂等");
        try
        {
            _ = ledger.Reserve("moark", "task-1", 5m, estimate);
            throw new InvalidOperationException("同一任务不同预留金额不应被接受");
        }
        catch (InvalidOperationException) { }

        var confirmed = ledger.Confirm("moark", "task-1", 3m,
            MpsPriceMetadata.Confirmed(3m, "CNY", "request", "provider-usage"));
        Assert(confirmed.State == MpsBudgetLineState.Confirmed && ledger.Reserved == 0m && ledger.Confirmed == 3m,
            "确认应把预留转为实际费用");
        var confirmedAgain = ledger.Reconcile("MOARK", "TASK-1", 3m);
        Assert(confirmedAgain.StableKey == confirmed.StableKey && ledger.Confirmed == 3m,
            "重复对账不能重复计费");

        var unknown = ledger.Reserve("sonnet.vip", "task-2", 2m, MpsPriceMetadata.Estimate(2m, "CNY", "request", "budget"));
        var unknownSettled = ledger.MarkUnknown("sonnet.vip", "task-2", 2m);
        Assert(unknownSettled.State == MpsBudgetLineState.Unknown && ledger.Unknown == 2m && unknown.Amount == 2m,
            "未知费用应继续占用预算");

        ledger.Reserve("moark", "unused-task", 1m, MpsPriceMetadata.Estimate(1m, "CNY", "request", "budget"));
        var released = ledger.Release("moark", "unused-task");
        Assert(released.State == MpsBudgetLineState.Released, "预留释放后应保留释放账本行");
    }

    /// <summary>越过硬上限或混入不同币种时必须拒绝，不能静默自动换算。</summary>
    private static void LedgerRejectsHardBudgetAndCurrencyOverflow()
    {
        var ledger = new MpsBudgetLedger(5m, "CNY");
        ledger.Reserve("moark", "one", 4m);
        try
        {
            _ = ledger.Confirm("moark", "one", 4m, MpsPriceMetadata.Estimate(4m, "CNY", "request", "catalog"));
            throw new InvalidOperationException("估算价格不应被记为供应商确认费用");
        }
        catch (InvalidDataException) { }
        try
        {
            _ = ledger.Reserve("moark", "two", 2m);
            throw new InvalidOperationException("超预算预留未拒绝");
        }
        catch (MpsBudgetExceededException error)
        {
            Assert(error.Available == 1m && error.Requested == 2m, "超预算异常应携带可用额度");
        }
        try
        {
            _ = ledger.Reserve("moark", "eur", 1m, MpsPriceMetadata.Estimate(1m, "EUR", "request", "offline"));
            throw new InvalidOperationException("不同币种不应静默进入预算");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("币种", StringComparison.Ordinal)) { }

        var snapshot = ledger.Snapshot();
        Assert(snapshot.Count == 1 && snapshot[0].StableKey == "moark:one", "账本快照应稳定去重并排序");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
