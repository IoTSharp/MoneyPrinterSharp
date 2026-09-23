using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VideoProduction;

/// <summary>按供应商和任务标识汇总真实费用，未知价格与估算永远不混入实付合计。</summary>
public static class CostReport
{
    /// <summary>扫描有限目录并写入脱敏费用报告，兼容历史任务JSON和新台账。</summary>
    public static int Run(Arguments args, CancellationToken ct)
    {
        args.Allow("input", "output", "markdown", "provider", "used-ids", "timeout-seconds");
        var limit = args.Int("timeout-seconds", 60);
        if (limit is < 1 or > 300) throw new ArgumentException("费用扫描时限须为1至300秒。");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(limit));
        var output = Path.GetFullPath(args.Required("output"));
        var input = Path.GetFullPath(args.Required("input"));
        var files = new List<string>();
        if (File.Exists(input)) files.Add(input);
        else
        {
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((input, 0));
            var entries = 0;
            for (var directory = 0; queue.Count > 0 && directory < 128; directory++)
            {
                stop.Token.ThrowIfCancellationRequested();
                var current = queue.Dequeue();
                foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (++entries > 5000) throw new InvalidDataException("目录项目超过5000，请指定更小的记录目录。");
                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (current.Depth < 4 && !Path.GetFileName(path).StartsWith('.')) queue.Enqueue((path, current.Depth + 1));
                    }
                    else if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) && !Path.GetFullPath(path).Equals(output, StringComparison.OrdinalIgnoreCase))
                    {
                        if (files.Count >= 1000) throw new InvalidDataException("JSON记录超过1000，请缩小目录。");
                        files.Add(path);
                    }
                }
            }
            if (queue.Count > 0) throw new InvalidDataException("记录目录超过128个，拒绝输出不完整合计。");
        }
        HashSet<string>? used = null;
        if (args.Optional("used-ids") is { } usedPath)
        {
            var ids = JsonFiles.Read<List<string>>(usedPath);
            if (ids.Count > 1000) throw new InvalidDataException("成片任务ID不能超过1000项。");
            used = ids.ToHashSet(StringComparer.Ordinal);
        }
        var observations = new List<CostObservation>();
        foreach (var file in files)
        {
            stop.Token.ThrowIfCancellationRequested();
            if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("费用记录超过8MiB。");
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 32 });
            var queue = new Queue<JsonElement>();
            queue.Enqueue(doc.RootElement);
            for (var nodes = 0; queue.Count > 0 && nodes < 20000; nodes++)
            {
                stop.Token.ThrowIfCancellationRequested();
                var node = queue.Dequeue();
                if (node.ValueKind == JsonValueKind.Object)
                {
                    var observation = ReadObservation(node, args.Optional("provider") ?? "moark");
                    if (observation is not null) observations.Add(observation);
                    foreach (var property in node.EnumerateObject())
                        if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) queue.Enqueue(property.Value);
                }
                else if (node.ValueKind == JsonValueKind.Array)
                    foreach (var child in node.EnumerateArray()) queue.Enqueue(child);
                if (queue.Count > 20000 || observations.Count > 20000) throw new InvalidDataException("费用JSON节点过多。");
            }
            if (queue.Count > 0) throw new InvalidDataException("费用JSON节点过多。");
        }
        var report = Summarize(observations, used);
        JsonFiles.Write(output, report);
        if (args.Optional("markdown") is { } markdown)
        {
            var path = Path.GetFullPath(markdown);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, FormatMarkdown(report));
        }
        Console.WriteLine($"已确认 CNY {report.ConfirmedCny:0.#####}；未知 {report.UnknownCount} 项；冲突 {report.ConflictCount} 项。账单仍须与平台核对。");
        return report.ConflictCount > 0 ? 2 : 0;
    }

    /// <summary>只读取白名单字段；不传播请求正文、下载地址或原始响应。</summary>
    private static CostObservation? ReadObservation(JsonElement node, string fallbackProvider)
    {
        var task = Text(node, "task_id");
        var fingerprint = Text(node, "fingerprint");
        if (string.IsNullOrWhiteSpace(task) && string.IsNullOrWhiteSpace(fingerprint)) return null;
        var price = Number(node, "price");
        var currency = Text(node, "currency");
        if (price < 0) throw new InvalidDataException("任务费用为负数，需人工核对退款记录。");
        return new CostObservation(Text(node, "provider") ?? fallbackProvider, task, fingerprint,
            Text(node, "kind") ?? "unclassified", Text(node, "model") ?? "unknown",
            Text(node, "status") ?? "unknown", price, currency?.ToUpperInvariant(), Number(node, "estimate_cny"));
    }

    /// <summary>根据真实任务ID合并重复记录；同一任务的不同价格标为冲突而非相加。</summary>
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
        // 小计从同一份去重列表派生，不再将模型小计重复加到总数。
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

    /// <summary>读取有限长度的标量标识，不允许把链接或多行敏感正文当标识存档。</summary>
    private static string? Text(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return null;
        var text = property.GetString();
        if (text is null) return null;
        if (text.Length > 256 || text.Contains("://", StringComparison.Ordinal) || text.Any(char.IsControl)) throw new InvalidDataException("费用标量字段格式无效。");
        return text;
    }

    /// <summary>接收数值或十进制字符串，空值保持未知。</summary>
    private static decimal? Number(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number)) return number;
        if (property.ValueKind == JsonValueKind.String && decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number)) return number;
        throw new InvalidDataException("费用数值字段格式无效：" + name);
    }

    /// <summary>生成可交付摘要，明确区分已知费用和待账单确认的费用。</summary>
    private static string FormatMarkdown(CostSummary report)
    {
        var text = new StringBuilder("# 制作费用\n\n");
        text.AppendLine($"已确认人民币费用：**¥{report.ConfirmedCny:0.#####}**。这是记录中的已知金额，不代表完整平台账单。\n");
        text.AppendLine($"未知或冲突费用：{report.UnknownCount} 项；其中价格冲突 {report.ConflictCount} 项。未知任务的预算估算合计 ¥{report.UnknownEstimateCny:0.#####}，未计入已确认金额。\n");
        if (report.FinalUsedCny.HasValue || report.TrialsOrUnusedCny.HasValue) text.AppendLine($"成片使用 ¥{report.FinalUsedCny ?? 0:0.#####}；试验或未使用 ¥{report.TrialsOrUnusedCny ?? 0:0.#####}（均仅包含已知人民币费用）。\n");
        text.AppendLine("| 任务 | 类别 | 已知费用 | 状态 |\n| --- | --- | --- | --- |");
        foreach (var task in report.Tasks.Take(1000))
            text.AppendLine($"| {task.Id.Replace('|', '_')} | {task.Kind.Replace('|', '_')} | {(task.Price.HasValue ? task.Price.Value.ToString("0.#####", CultureInfo.InvariantCulture) + " " + task.Currency : task.Conflict ? "价格冲突" : "待确认")} | {string.Join(", ", task.Statuses).Replace('|', '_')} |");
        return text.ToString();
    }
}

/// <summary>一份任务费用观察；用于导入、去重和离线验证。</summary>
public sealed record CostObservation(string Provider, string? TaskId, string? Fingerprint, string Kind, string Model, string Status, decimal? Price, string? Currency, decimal? EstimateCny);

/// <summary>去重后的单一任务，不保存原始API响应。</summary>
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
