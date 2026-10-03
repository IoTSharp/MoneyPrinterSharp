using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>功能主张、镜头、旁白、生成资产和最终片段的来源证据。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsEvidenceRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "source";
    public string SubjectId { get; set; } = "";
    public string Source { get; set; } = "";
    public string? AssetId { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? TaskId { get; set; }
    public DateTimeOffset ObservedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? Version { get; set; }
    public string? Limitations { get; set; }
}

/// <summary>证据字段的集中校验，确保成片片段可回溯且不写入签名地址。</summary>
public static class MpsEvidenceValidation
{
    private const int MaxRecords = 16_384;

    public static void Validate(MpsProjectDocument project, CancellationToken cancellationToken = default)
    {
        if (project.Evidence is null || project.Evidence.Count > MaxRecords)
            throw new InvalidDataException("项目证据记录为空或超出规模。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var assets = project.Assets.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var evidence in project.Evidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evidence is null || !ValidId(evidence.Id) || !ids.Add(evidence.Id) ||
                !ValidLabel(evidence.Kind, 32) || !ValidLabel(evidence.SubjectId, 128) ||
                !ValidLabel(evidence.Source, 2048) || evidence.ObservedUtc == default ||
                evidence.AssetId is not null && !assets.Contains(evidence.AssetId) ||
                !ValidOptionalLabel(evidence.Provider, 128) || !ValidOptionalLabel(evidence.Model, 256) ||
                !ValidOptionalLabel(evidence.TaskId, 256) || !ValidOptionalLabel(evidence.Version, 128) ||
                !ValidOptionalLabel(evidence.Limitations, 4096) || ContainsSensitiveUrl(evidence.Source))
                throw new InvalidDataException("项目证据记录无效或包含敏感地址。");
        }
    }

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool ValidLabel(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static bool ValidOptionalLabel(string? value, int max) => value is null || ValidLabel(value, max);
    private static bool ContainsSensitiveUrl(string value) => value.Contains("sig=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("signature=", StringComparison.OrdinalIgnoreCase) || value.Contains("token=", StringComparison.OrdinalIgnoreCase);
}
