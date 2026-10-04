using System.Text.Json;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>能力实测的四个证据层级；层级越高并不自动代表当前账号可用。</summary>
[JsonConverter(typeof(MpsMeasurementLevelJsonConverter))]
public enum MpsMeasurementLevel
{
    ReadOnlyMetadata,
    FreeMinimalCall,
    BillableMinimalCall,
    RealMaterialSample
}

/// <summary>一次实测的结论；未知和阻断状态不能被展示为通过。</summary>
[JsonConverter(typeof(MpsMeasurementOutcomeJsonConverter))]
public enum MpsMeasurementOutcome
{
    Unknown,
    Passed,
    Failed,
    Blocked,
    TimedOut,
    Cancelled
}

/// <summary>以小写蛇形序列化实测层级，拒绝数字枚举值。</summary>
public sealed class MpsMeasurementLevelJsonConverter : JsonStringEnumConverter
{
    /// <summary>固定证据文件中的层级名称。</summary>
    public MpsMeasurementLevelJsonConverter() : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) { }
}

/// <summary>以小写蛇形序列化实测结论。</summary>
public sealed class MpsMeasurementOutcomeJsonConverter : JsonStringEnumConverter
{
    /// <summary>固定证据文件中的结论名称。</summary>
    public MpsMeasurementOutcomeJsonConverter() : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) { }
}

/// <summary>模型能力的一条可追溯实测证据；只保存摘要，不保存请求正文或供应商原始响应。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsMeasurementEvidence
{
    public int SchemaVersion { get; set; } = 1;
    public string EvidenceId { get; set; } = Guid.NewGuid().ToString("N");
    public string ProviderId { get; set; } = string.Empty;
    public string? AccountAlias { get; set; }
    public string? ModelId { get; set; }
    public MpsCapabilityKind Capability { get; set; }
    public MpsMeasurementLevel Level { get; set; }
    public MpsMeasurementOutcome Outcome { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? SourceVersion { get; set; }
    public DateTimeOffset ObservedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresUtc { get; set; }
    public MpsPriceMetadata Cost { get; set; } = MpsPriceMetadata.Unknown("not-observed");
    public int RequestCount { get; set; }
    public string? InputFingerprint { get; set; }
    public string? TaskId { get; set; }
    public string? Limitations { get; set; }
    public string InvalidationConditions { get; set; } = "账号、模型版本或价格限制变化时失效。";

    /// <summary>检查证据在指定时间是否仍可使用；失效条件由调用方显示给用户。</summary>
    public bool IsActiveAt(DateTimeOffset nowUtc) => Outcome == MpsMeasurementOutcome.Passed && nowUtc >= ObservedUtc &&
        (ExpiresUtc is null || nowUtc < ExpiresUtc.Value);

    /// <summary>校验证据字段、成本层级和脱敏边界。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || !Enum.IsDefined(Capability) || Capability == MpsCapabilityKind.Unknown ||
            !Enum.IsDefined(Level) || !Enum.IsDefined(Outcome) || ObservedUtc == default ||
            ExpiresUtc is { } expiry && expiry <= ObservedUtc || RequestCount is < 0 or > 128)
            throw new InvalidDataException("实测证据基础字段无效。");

        ValidateText(EvidenceId, 128, "证据标识", requireValue: true);
        ValidateText(ProviderId, 128, "提供商", requireValue: true);
        ValidateOptionalText(AccountAlias, 128, "账号");
        ValidateOptionalText(ModelId, 256, "模型");
        ValidateText(Source, 2048, "来源", requireValue: true);
        ValidateOptionalText(SourceVersion, 128, "来源版本");
        ValidateOptionalText(InputFingerprint, 512, "输入指纹");
        ValidateOptionalText(TaskId, 256, "任务号");
        ValidateOptionalText(Limitations, 4096, "限制");
        ValidateText(InvalidationConditions, 2048, "失效条件", requireValue: true);
        if (Cost is null) throw new InvalidDataException("实测证据成本字段不能为空。");
        Cost.Validate();
        if (ContainsSensitiveMaterial(Cost.Source) || ContainsSensitiveMaterial(Cost.Basis))
            throw new InvalidDataException("实测成本不能包含秘密或签名地址。");
        if (ContainsSensitiveMaterial(Source) || ContainsSensitiveMaterial(Limitations) ||
            ContainsSensitiveMaterial(InvalidationConditions) || ContainsSensitiveMaterial(InputFingerprint))
            throw new InvalidDataException("实测证据不能包含签名地址或秘密字段。");

        if (Level == MpsMeasurementLevel.ReadOnlyMetadata)
        {
            if (RequestCount != 0 || TaskId is not null || InputFingerprint is not null)
                throw new InvalidDataException("只读元数据证据不能携带任务或调用字段。");
        }
        else
        {
            if (RequestCount is < 1 or > 128 || InputFingerprint is null || AccountAlias is null || ModelId is null)
                throw new InvalidDataException("调用类实测必须记录次数和脱敏输入指纹。");
            if (Outcome == MpsMeasurementOutcome.Passed && !Cost.IsKnown)
                throw new InvalidDataException("通过的调用类实测必须有已知或估算成本。");
        }

        if (Level == MpsMeasurementLevel.FreeMinimalCall && Outcome == MpsMeasurementOutcome.Passed &&
            (!Cost.IsConfirmed || Cost.Amount != 0m))
            throw new InvalidDataException("免费最小调用通过证据必须由确认的零成本支持。");
        if (TaskId is not null && TaskId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidDataException("实测任务号无效。");
        if (Outcome == MpsMeasurementOutcome.Passed && ExpiresUtc is null && string.IsNullOrWhiteSpace(InvalidationConditions))
            throw new InvalidDataException("通过证据必须记录过期时间或失效条件。");
    }

    /// <summary>构造只读目录证据；不产生供应商调用。</summary>
    public static MpsMeasurementEvidence ReadOnly(string providerId, MpsCapabilityKind capability,
        string source, DateTimeOffset observedUtc, DateTimeOffset? expiresUtc = null, string? sourceVersion = null) => new()
    {
        ProviderId = providerId,
        Capability = capability,
        Level = MpsMeasurementLevel.ReadOnlyMetadata,
        Outcome = MpsMeasurementOutcome.Passed,
        Source = source,
        SourceVersion = sourceVersion,
        ObservedUtc = observedUtc,
        ExpiresUtc = expiresUtc,
        Cost = MpsPriceMetadata.Unknown("目录未提供实付成本")
    };

    /// <summary>构造调用实测证据；调用方必须显式传入成本状态。</summary>
    public static MpsMeasurementEvidence Call(string providerId, string accountAlias, string modelId,
        MpsCapabilityKind capability, MpsMeasurementLevel level, MpsMeasurementOutcome outcome,
        string source, string inputFingerprint, MpsPriceMetadata cost, DateTimeOffset observedUtc,
        string? taskId = null, DateTimeOffset? expiresUtc = null, string? limitations = null) => new()
    {
        ProviderId = providerId,
        AccountAlias = accountAlias,
        ModelId = modelId,
        Capability = capability,
        Level = level,
        Outcome = outcome,
        Source = source,
        InputFingerprint = inputFingerprint,
        Cost = cost,
        ObservedUtc = observedUtc,
        ExpiresUtc = expiresUtc,
        TaskId = taskId,
        RequestCount = 1,
        Limitations = limitations
    };

    private static void ValidateText(string? value, int max, string name, bool requireValue)
    {
        if ((requireValue && string.IsNullOrWhiteSpace(value)) || value is not null &&
            (value.Length > max || value.Any(char.IsControl) || ContainsSensitiveMaterial(value)))
            throw new InvalidDataException($"{name}无效。");
    }

    private static void ValidateOptionalText(string? value, int max, string name) => ValidateText(value, max, name, false);

    private static bool ContainsSensitiveMaterial(string? value) => value is not null &&
        (value.Contains("sig=", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("signature=", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("bearer ", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("secret", StringComparison.OrdinalIgnoreCase));
}

/// <summary>项目内实测证据仓库；条目和序列化大小均有界。</summary>
public sealed class MpsMeasurementEvidenceStore
{
    public const int MaxEntries = 4096;
    private readonly object gate = new();
    private readonly Dictionary<string, MpsMeasurementEvidence> entries = new(StringComparer.Ordinal);

    /// <summary>添加或替换同一证据标识，拒绝不同证据互相覆盖。</summary>
    public void Upsert(MpsMeasurementEvidence evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate(cancellationToken);
        lock (gate)
        {
            if (!entries.ContainsKey(evidence.EvidenceId) && entries.Count >= MaxEntries)
                throw new InvalidDataException("实测证据条目超过上限。");
            entries[evidence.EvidenceId] = Clone(evidence);
        }
    }

    /// <summary>按证据标识返回脱敏副本。</summary>
    public MpsMeasurementEvidence? Find(string evidenceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(evidenceId)) throw new ArgumentException("证据标识不能为空。", nameof(evidenceId));
        lock (gate) return entries.TryGetValue(evidenceId, out var item) ? Clone(item) : null;
    }

    /// <summary>按稳定顺序返回证据副本。</summary>
    public IReadOnlyList<MpsMeasurementEvidence> Snapshot(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return entries.Values.OrderBy(item => item.EvidenceId, StringComparer.Ordinal).Select(Clone).ToArray();
    }

    /// <summary>导出仅包含证据摘要的 JSON。</summary>
    public string ExportJson(CancellationToken cancellationToken = default)
    {
        var snapshot = new MpsMeasurementEvidenceSnapshot { Entries = Snapshot(cancellationToken).ToList() };
        snapshot.Validate(cancellationToken);
        return JsonSerializer.Serialize(snapshot, JsonFiles.Options);
    }

    /// <summary>从本地 JSON 恢复证据，不访问网络或凭据。</summary>
    public static MpsMeasurementEvidenceStore ReopenJson(string json, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("实测证据 JSON 为空或超过大小上限。");
        var snapshot = JsonSerializer.Deserialize<MpsMeasurementEvidenceSnapshot>(json, JsonFiles.Options)
            ?? throw new InvalidDataException("实测证据 JSON 为空。");
        snapshot.Validate(cancellationToken);
        var store = new MpsMeasurementEvidenceStore();
        var timer = Stopwatch.StartNew();
        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("实测证据恢复超过时限。");
            store.Upsert(entry, cancellationToken);
        }
        return store;
    }

    private static MpsMeasurementEvidence Clone(MpsMeasurementEvidence value) => new()
    {
        SchemaVersion = value.SchemaVersion,
        EvidenceId = value.EvidenceId,
        ProviderId = value.ProviderId,
        AccountAlias = value.AccountAlias,
        ModelId = value.ModelId,
        Capability = value.Capability,
        Level = value.Level,
        Outcome = value.Outcome,
        Source = value.Source,
        SourceVersion = value.SourceVersion,
        ObservedUtc = value.ObservedUtc,
        ExpiresUtc = value.ExpiresUtc,
        Cost = value.Cost is null ? MpsPriceMetadata.Unknown("not-observed") : new MpsPriceMetadata
        {
            Status = value.Cost.Status, Amount = value.Cost.Amount, Currency = value.Cost.Currency,
            Unit = value.Cost.Unit, Basis = value.Cost.Basis, Source = value.Cost.Source,
            ObservedUtc = value.Cost.ObservedUtc
        },
        RequestCount = value.RequestCount,
        InputFingerprint = value.InputFingerprint,
        TaskId = value.TaskId,
        Limitations = value.Limitations,
        InvalidationConditions = value.InvalidationConditions
    };
}

/// <summary>实测证据文件快照；保存数量受限且证据标识唯一。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsMeasurementEvidenceSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset SavedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<MpsMeasurementEvidence> Entries { get; set; } = [];

    /// <summary>验证快照，确保不会静默覆盖证据。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || SavedUtc == default || Entries is null || Entries.Count > MpsMeasurementEvidenceStore.MaxEntries)
            throw new InvalidDataException("实测证据快照无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        foreach (var entry in Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("实测证据校验超过时限。");
            if (entry is null || !ids.Add(entry.EvidenceId)) throw new InvalidDataException("实测证据标识重复。");
            entry.Validate(cancellationToken);
        }
    }
}
