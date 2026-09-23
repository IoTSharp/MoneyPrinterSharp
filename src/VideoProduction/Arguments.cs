using System.Globalization;

namespace VideoProduction;

/// <summary>只接受显式选项，避免拼接命令行以及默默忽略拼写错误。</summary>
public sealed class Arguments
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    /// <summary>将双短横线选项解析为键值，拒绝重复键。</summary>
    public Arguments(string[] args)
    {
        if (args.Length > 120) throw new ArgumentException("参数数量超过120项。");
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"无法识别参数：{args[i]}");
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            if (!values.TryAdd(key, value)) throw new ArgumentException($"重复选项：{key}");
        }
    }

    /// <summary>读取必需的选项值。</summary>
    public string Required(string key) => values.TryGetValue(key, out var value) && value != "true" ? value : throw new ArgumentException($"需要 --{key}。");

    /// <summary>读取可选字符串值。</summary>
    public string? Optional(string key) => values.GetValueOrDefault(key);

    /// <summary>判断是否明确设置开关。</summary>
    public bool Has(string key) => values.ContainsKey(key);

    /// <summary>使用固定区域解析整数值。</summary>
    public int Int(string key, int fallback) => values.TryGetValue(key, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

    /// <summary>使用固定区域解析秒数等浮点值。</summary>
    public double Number(string key, double fallback) => values.TryGetValue(key, out var value) ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;

    /// <summary>校验当前子命令允许的参数集合。</summary>
    public void Allow(params string[] keys)
    {
        var known = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var key in values.Keys)
            if (!known.Contains(key)) throw new ArgumentException($"不支持 --{key}。");
    }
}
