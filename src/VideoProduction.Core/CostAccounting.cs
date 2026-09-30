using System.Diagnostics;

namespace VideoProduction;

/// <summary>按供应商与任务标识去重费用，未知金额和估算不混入实付合计。</summary>
public static class CostAccounting
{
    /// <summary>按稳定任务号合并观察；金额冲突标记为待核对。</summary>
    public static CostSummary Summarize(IEnumerable<CostObservation> source, HashSet<string>? used = null)
    {
        var records = source.Take(20001).ToList();
        if (records.Count > 20000) throw new InvalidDataException("费用观察记录超过20000条。");
        var result = new CostSummary();
        var timer = Stopwatch.StartNew();
        foreach (var group in records.GroupBy(r => r.Provider + ":" + (r.TaskId ?? "fingerprint:" + r.Fingerprint), StringComparer.Ordinal))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("费用汇总超过30秒。");
            var priced = group.Where(r => r.Price.HasValue && !string.IsNullOrWhiteSpace(r.Currency)).Select(r => (r.Price, r.Currency)).Distinct().ToList();
            var first = group.First();
            var entry = new CostEntry
            {
                Id = group.Key, TaskId = first.TaskId, Provider = first.Provider,
                Kind = group.Select(r => r.Kind).FirstOrDefault(k => k != "unclassified") ?? "unclassified",
                Model = group.Select(r => r.Model).FirstOrDefault(m => m != "unknown") ?? "unknown",
                Observations = group.Count(), UsedInFinal = used is null ? null : used.Contains(group.Key) || first.TaskId is { } id && used.Contains(id),
                Statuses = group.Select(r => r.Status).Distinct().Order(StringComparer.Ordinal).ToArray(),
                EstimateCny = group.Max(r => r.EstimateCny) ?? 0,
                Conflict = priced.Count > 1,
                Price = priced.Count == 1 ? priced[0].Price : null,
                Currency = priced.Count == 1 ? priced[0].Currency : null
            };
            result.Tasks.Add(entry);
        }
        // 所有小计来自同一份去重任务，避免重复累计。
        foreach (var entry in result.Tasks)
        {
            if (entry.Conflict) result.ConflictCount++;
            if (!entry.Price.HasValue) { result.UnknownCount++; result.UnknownEstimateCny += entry.EstimateCny; continue; }
            var currency = entry.Currency!;
            result.ConfirmedByCurrency[currency] = result.ConfirmedByCurrency.GetValueOrDefault(currency) + entry.Price.Value;
            if (currency == "CNY")
            {
                result.ConfirmedCny += entry.Price.Value;
                if (entry.UsedInFinal == true) result.FinalUsedCny = (result.FinalUsedCny ?? 0) + entry.Price.Value;
                if (entry.UsedInFinal == false) result.TrialsOrUnusedCny = (result.TrialsOrUnusedCny ?? 0) + entry.Price.Value;
            }
        }
        return result;
    }
}

/// <summary>一份任务费用观察；用于导入、去重和离线验证。</summary>
public sealed record CostObservation(string Provider, string? TaskId, string? Fingerprint, string Kind, string Model, string Status, decimal? Price, string? Currency, decimal? EstimateCny);

/// <summary>去重后的单一任务，不保存原始 API 响应。</summary>
public sealed class CostEntry
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? TaskId { get; set; }
    public string Kind { get; set; } = "";
    public string Model { get; set; } = "";
    public string[] Statuses { get; set; } = [];
    public int Observations { get; set; }
    public decimal? Price { get; set; }
    public string? Currency { get; set; }
    public decimal EstimateCny { get; set; }
    public bool Conflict { get; set; }
    public bool? UsedInFinal { get; set; }
}

/// <summary>实际费用与未知预算分离的汇总结果。</summary>
public sealed class CostSummary
{
    public decimal ConfirmedCny { get; set; }
    public Dictionary<string, decimal> ConfirmedByCurrency { get; set; } = [];
    public int UnknownCount { get; set; }
    public int ConflictCount { get; set; }
    public decimal UnknownEstimateCny { get; set; }
    public decimal? FinalUsedCny { get; set; }
    public decimal? TrialsOrUnusedCny { get; set; }
    public List<CostEntry> Tasks { get; set; } = [];
}
