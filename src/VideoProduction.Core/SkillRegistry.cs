using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>技能注册表或阶段计划校验时产生的可诊断问题。</summary>
public sealed record MpsSkillDiagnostic(string Code, string Path, string Message, bool IsBlocking = true);

/// <summary>严格读取技能注册表或计划失败时携带完整诊断的异常。</summary>
public sealed class MpsSkillContractException : Exception
{
    public IReadOnlyList<MpsSkillDiagnostic> Diagnostics { get; }

    public MpsSkillContractException(IEnumerable<MpsSkillDiagnostic> diagnostics, Exception? innerException = null)
        : base(CreateMessage(diagnostics), innerException)
    {
        Diagnostics = diagnostics.ToArray();
    }

    private static string CreateMessage(IEnumerable<MpsSkillDiagnostic> diagnostics)
    {
        var items = diagnostics.Take(8).Select(item => $"{item.Path}: {item.Message}");
        return "技能契约校验失败：" + string.Join("；", items);
    }
}

/// <summary>JSON 读取的非异常结果，供 CLI 或桌面层展示所有问题。</summary>
public sealed record MpsSkillContractLoadResult<T>(T? Value, IReadOnlyList<MpsSkillDiagnostic> Diagnostics)
{
    public bool Success => Value is not null && Diagnostics.Count == 0;
}

/// <summary>一个可发现的技能入口及其阶段依赖。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSkillDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>总入口为 null；其余技能映射到十阶段中的一个阶段。</summary>
    [JsonPropertyName("stage")]
    public MpsProductionStage? Stage { get; init; }

    [JsonPropertyName("order")]
    public int Order { get; init; }

    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; init; } = [];

    /// <summary>阶段完成时最低应登记的项目内产物名称。</summary>
    [JsonPropertyName("expected_artifacts")]
    public List<string> ExpectedArtifacts { get; init; } = [];

    /// <summary>技能文件的仓库相对路径，固定为 skills/&lt;id&gt;/SKILL.md。</summary>
    [JsonPropertyName("skill_path")]
    public string SkillPath { get; init; } = "";
}

/// <summary>11 个技能入口的固定注册表。注册表不根据目录扫描结果动态扩展。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class MpsSkillRegistry
{
    public const string SchemaVersion = "mps.skill-registry.v1";
    public const int MaxSkills = 11;

    /// <summary>固定的总入口和十个阶段入口，顺序与生产链一致。</summary>
    public static IReadOnlyList<string> RequiredSkillIds { get; } =
    [
        "video-production-series",
        "video-01-feature-audit",
        "video-02-narrative-plan",
        "video-03-screenplay",
        "video-04-model-selection",
        "video-05-presenter",
        "video-06-narration",
        "video-07-lip-sync",
        "video-08-composition",
        "video-09-quality-review",
        "video-10-cost-delivery"
    ];

    [JsonPropertyName("schema_version")]
    public string? SchemaVersionValue { get; init; } = SchemaVersion;

    [JsonPropertyName("skills")]
    public List<MpsSkillDefinition> Skills { get; init; } = [];

    /// <summary>建立与仓库 skills/ 目录一致的离线默认注册表。</summary>
    public static MpsSkillRegistry CreateDefault() => new()
    {
        Skills =
        [
            Definition("video-production-series", "视频制作系列", "编排从功能审计到成本交付的十阶段制作链。", null, 0, [], ["production-plan.json"]),
            Definition("video-01-feature-audit", "功能审计", "建立有证据和边界的功能主张底稿。", MpsProductionStage.FeatureAudit, 1, [], ["feature-audit.json"]),
            Definition("video-02-narrative-plan", "叙事规划", "把审计结果组织为面向受众的讲解主线。", MpsProductionStage.IntroductionOrder, 2, ["video-01-feature-audit"], ["narrative-plan.json"]),
            Definition("video-03-screenplay", "讲稿与分镜", "冻结朗读文本、镜头、字幕和主持人动作。", MpsProductionStage.ScriptStoryboard, 3, ["video-02-narrative-plan"], ["screenplay.json", "shot-list.json"]),
            Definition("video-04-model-selection", "模型选型", "比较已验证的能力、限制、成本和风险。", MpsProductionStage.ModelSelection, 4, ["video-03-screenplay"], ["model-matrix.json"]),
            Definition("video-05-presenter", "主持人素材", "准备稳定、可合成的主持人母版和动作片段。", MpsProductionStage.Presenter, 5, ["video-04-model-selection"], ["presenter-manifest.json"]),
            Definition("video-06-narration", "旁白", "依据冻结讲稿交付分章音频和实测时长。", MpsProductionStage.Narration, 6, ["video-05-presenter"], ["narration-manifest.json", "timings.json"]),
            Definition("video-07-lip-sync", "口型同步", "用同一最终旁白逐段生成并记录口型任务。", MpsProductionStage.LipSync, 7, ["video-06-narration"], ["lip-sync-map.json"]),
            Definition("video-08-composition", "透明合成", "合成真实界面、主持人、字幕和最终音频。", MpsProductionStage.Compositing, 8, ["video-07-lip-sync"], ["composition-manifest.json"]),
            Definition("video-09-quality-review", "质量复核", "独立检查媒体、证据、音画和披露要求。", MpsProductionStage.QualityReview, 9, ["video-08-composition"], ["quality-review.json"]),
            Definition("video-10-cost-delivery", "成本与交付", "按任务号对账并建立可复现的交付包。", MpsProductionStage.CostDelivery, 10, ["video-09-quality-review"], ["cost-report.json", "delivery-manifest.json"])
        ]
    };

    /// <summary>短别名，保持产品入口和领域层命名兼容。</summary>
    public static MpsSkillRegistry Default => CreateDefault();

    public MpsSkillDefinition Get(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) throw new ArgumentException("技能 ID 不能为空。", nameof(skillId));
        return Skills.FirstOrDefault(item => string.Equals(item.Id, skillId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"技能不存在：{skillId}。");
    }

    public bool TryGet(string skillId, out MpsSkillDefinition definition)
    {
        definition = Skills.FirstOrDefault(item => string.Equals(item.Id, skillId, StringComparison.Ordinal))!;
        return definition is not null;
    }

    /// <summary>校验数量、字段、固定 ID、顺序、依赖和产物契约。</summary>
    public IReadOnlyList<MpsSkillDiagnostic> Validate(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<MpsSkillDiagnostic>();
        if (!string.Equals(SchemaVersionValue, SchemaVersion, StringComparison.Ordinal))
            Add(diagnostics, "registry.schema_version", "schema_version", $"只支持 {SchemaVersion}。", 1);
        if (Skills is null)
        {
            Add(diagnostics, "registry.skills_missing", "skills", "技能集合缺失。", 1);
            return diagnostics;
        }
        if (Skills.Count != MaxSkills)
            Add(diagnostics, "registry.count", "skills", $"必须恰好包含 {MaxSkills} 个技能入口，当前为 {Skills.Count}。", 1);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Skills.Count && index < MaxSkills * 2; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var skill = Skills[index];
            var path = $"skills[{index}]";
            if (skill is null)
            {
                Add(diagnostics, "skill.null", path, "技能定义为空。", index);
                continue;
            }
            if (string.IsNullOrWhiteSpace(skill.Id)) Add(diagnostics, "skill.id_missing", path + ".id", "技能 ID 不能为空。", index);
            else if (!seen.Add(skill.Id)) Add(diagnostics, "skill.duplicate", path + ".id", $"技能 ID 重复：{skill.Id}。", index);
            if (!string.IsNullOrWhiteSpace(skill.Id) && !RequiredSkillIds.Contains(skill.Id, StringComparer.Ordinal))
                Add(diagnostics, "skill.unknown", path + ".id", $"未知技能 ID：{skill.Id}。", index);
            if (string.IsNullOrWhiteSpace(skill.DisplayName)) Add(diagnostics, "skill.display_name_missing", path + ".display_name", "显示名称不能为空。", index);
            if (string.IsNullOrWhiteSpace(skill.Description)) Add(diagnostics, "skill.description_missing", path + ".description", "技能描述不能为空。", index);
            var expectedPath = string.IsNullOrWhiteSpace(skill.Id) ? "" : $"skills/{skill.Id}/SKILL.md";
            if (!string.Equals(skill.SkillPath, expectedPath, StringComparison.Ordinal))
                Add(diagnostics, "skill.path", path + ".skill_path", $"技能路径必须为 {expectedPath}。", index);
            var expectedOrder = !string.IsNullOrWhiteSpace(skill.Id) ? RequiredSkillIds.Select((id, value) => (id, value)).Where(item => string.Equals(item.id, skill.Id, StringComparison.Ordinal)).Select(item => item.value).DefaultIfEmpty(-1).First() : -1;
            if (expectedOrder >= 0 && skill.Order != expectedOrder)
                Add(diagnostics, "skill.order", path + ".order", $"技能顺序应为 {expectedOrder}。", index);
            if (expectedOrder == 0 && skill.Stage is not null)
                Add(diagnostics, "skill.stage", path + ".stage", "总入口不能绑定具体阶段。", index);
            if (expectedOrder > 0 && skill.Stage != (MpsProductionStage)(expectedOrder - 1))
                Add(diagnostics, "skill.stage", path + ".stage", "技能阶段与固定顺序不一致。", index);
            ValidateDependencies(skill, expectedOrder, path, diagnostics, index);
            if (skill.ExpectedArtifacts is null || skill.ExpectedArtifacts.Count == 0)
                Add(diagnostics, "skill.artifacts_missing", path + ".expected_artifacts", "至少声明一个阶段产物。", index);
            else
            {
                var artifactSet = new HashSet<string>(StringComparer.Ordinal);
                for (var artifactIndex = 0; artifactIndex < skill.ExpectedArtifacts.Count && artifactIndex < 32; artifactIndex++)
                {
                    var artifact = skill.ExpectedArtifacts[artifactIndex];
                    if (string.IsNullOrWhiteSpace(artifact)) Add(diagnostics, "artifact.name_missing", $"{path}.expected_artifacts[{artifactIndex}]", "产物名称不能为空。", index);
                    else if (!artifactSet.Add(artifact)) Add(diagnostics, "artifact.duplicate", $"{path}.expected_artifacts[{artifactIndex}]", $"产物名称重复：{artifact}。", index);
                }
            }
        }
        foreach (var expected in RequiredSkillIds)
            if (!seen.Contains(expected)) Add(diagnostics, "skill.missing", "skills", $"缺少固定技能入口：{expected}。", 0);
        return diagnostics;
    }

    public void ValidateOrThrow(CancellationToken cancellationToken = default)
    {
        var diagnostics = Validate(cancellationToken);
        if (diagnostics.Count > 0) throw new MpsSkillContractException(diagnostics);
    }

    public string ToJson() => JsonSerializer.Serialize(this, MpsSkillContractJson.Options);

    public static MpsSkillRegistry LoadJson(string json, CancellationToken cancellationToken = default)
    {
        var result = TryLoadJson(json, cancellationToken);
        if (!result.Success) throw new MpsSkillContractException(result.Diagnostics);
        return result.Value!;
    }

    public static MpsSkillRegistry FromJson(string json, CancellationToken cancellationToken = default) => LoadJson(json, cancellationToken);

    public static MpsSkillContractLoadResult<MpsSkillRegistry> TryLoadJson(string json, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new(null, [new("json.empty", "$", "JSON 不能为空。")] );
        if (json.Length > 1_048_576)
            return new(null, [new("json.too_large", "$", "技能注册表 JSON 超过 1MiB。")] );
        try
        {
            var shapeDiagnostics = InspectRegistryJsonShape(json);
            var registry = JsonSerializer.Deserialize<MpsSkillRegistry>(json, MpsSkillContractJson.Options);
            if (registry is null) return new(null, [new("json.empty", "$", "JSON 为空对象。")] );
            var diagnostics = shapeDiagnostics.Concat(registry.Validate(cancellationToken)).ToArray();
            return diagnostics.Length == 0 ? new(registry, diagnostics) : new(null, diagnostics);
        }
        catch (JsonException error)
        {
            return new(null, [new("json.invalid", error.Path ?? "$", error.Message)]);
        }
        catch (NotSupportedException error)
        {
            return new(null, [new("json.unsupported", "$", error.Message)]);
        }
    }

    private static MpsSkillDefinition Definition(string id, string displayName, string description, MpsProductionStage? stage, int order, IEnumerable<string> dependencies, IEnumerable<string> artifacts) => new()
    {
        Id = id,
        DisplayName = displayName,
        Description = description,
        Stage = stage,
        Order = order,
        Dependencies = dependencies.ToList(),
        ExpectedArtifacts = artifacts.ToList(),
        SkillPath = $"skills/{id}/SKILL.md"
    };

    private static void ValidateDependencies(MpsSkillDefinition skill, int expectedOrder, string path, ICollection<MpsSkillDiagnostic> diagnostics, int rank)
    {
        if (skill.Dependencies is null)
        {
            Add(diagnostics, "dependency_missing", path + ".dependencies", "依赖字段缺失。", rank);
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < skill.Dependencies.Count && index < 32; index++)
        {
            var dependency = skill.Dependencies[index];
            var dependencyPath = $"{path}.dependencies[{index}]";
            if (string.IsNullOrWhiteSpace(dependency)) Add(diagnostics, "dependency.id_missing", dependencyPath, "依赖 ID 不能为空。", rank);
            else if (!seen.Add(dependency)) Add(diagnostics, "dependency.duplicate", dependencyPath, $"依赖 ID 重复：{dependency}。", rank);
            else if (!RequiredSkillIds.Contains(dependency, StringComparer.Ordinal)) Add(diagnostics, "dependency.unknown", dependencyPath, $"未知依赖：{dependency}。", rank);
        }
        // 第一个阶段直接开始，后续阶段依赖前一个阶段；总入口只负责编排。
        var expectedDependencies = expectedOrder <= 1 ? new List<string>() : [RequiredSkillIds[expectedOrder - 1]];
        if (!skill.Dependencies.SequenceEqual(expectedDependencies, StringComparer.Ordinal))
            Add(diagnostics, "dependency.order", path + ".dependencies", "阶段依赖必须严格指向前一个固定阶段。", rank);
    }

    private static void Add(ICollection<MpsSkillDiagnostic> diagnostics, string code, string path, string message, int rank)
    {
        if (diagnostics.Count < 512) diagnostics.Add(new(code, path, message));
    }

    /// <summary>在默认集合填充前检查注册表必需字段，保留缺失字段路径。</summary>
    private static IReadOnlyList<MpsSkillDiagnostic> InspectRegistryJsonShape(string json)
    {
        using var document = JsonDocument.Parse(json);
        var diagnostics = new List<MpsSkillDiagnostic>();
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new("json.shape", "$", "技能注册表 JSON 根必须是对象。"));
            return diagnostics;
        }
        if (!root.TryGetProperty("schema_version", out _)) diagnostics.Add(new("registry.schema_version_missing", "schema_version", "缺少 schema_version 字段。"));
        if (!root.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new("registry.skills_missing", "skills", "缺少 skills 数组。"));
            return diagnostics;
        }
        var index = 0;
        foreach (var skill in skills.EnumerateArray())
        {
            if (skill.ValueKind == JsonValueKind.Object)
            {
                var path = $"skills[{index}]";
                foreach (var property in new[] { "id", "display_name", "description", "stage", "order", "dependencies", "expected_artifacts", "skill_path" })
                    if (!skill.TryGetProperty(property, out _)) diagnostics.Add(new("skill.field_missing", path + "." + property, $"缺少 {property} 字段。"));
            }
            index++;
            if (index >= MaxSkills * 2) break;
        }
        return diagnostics;
    }
}

/// <summary>阶段计划中的一个技能状态、产物引用和阻断项。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSkillPlanEntry
{
    [JsonPropertyName("skill_id")]
    public string SkillId { get; init; } = "";

    [JsonPropertyName("status")]
    public MpsStageStatus Status { get; internal set; } = MpsStageStatus.NotStarted;

    [JsonPropertyName("artifact_refs")]
    public List<string> ArtifactRefs { get; set; } = [];

    [JsonPropertyName("blockers")]
    public List<MpsPlanBlocker> Blockers { get; set; } = [];

    [JsonPropertyName("invalidated_by")]
    public string? InvalidatedBy { get; internal set; }

    [JsonPropertyName("revision")]
    public long Revision { get; internal set; }

    [JsonPropertyName("updated_utc")]
    public DateTimeOffset UpdatedUtc { get; internal set; } = DateTimeOffset.UtcNow;
}

/// <summary>技能阶段产物的可审计项目内引用。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsPlanArtifact
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("skill_id")]
    public string SkillId { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; init; }

    [JsonPropertyName("created_utc")]
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>阻断阶段推进的原因；消息中不得放凭据、原始响应或签名地址。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsPlanBlocker
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("blocking")]
    public bool Blocking { get; init; } = true;

    [JsonPropertyName("created_utc")]
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>11 个技能的可恢复阶段计划；计划状态与旧十阶段状态机可互相迁移。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class MpsProductionPlan
{
    public const string SchemaVersion = "mps.production-plan.v1";

    [JsonPropertyName("schema_version")]
    public string? SchemaVersionValue { get; init; } = SchemaVersion;

    [JsonPropertyName("registry_schema_version")]
    public string? RegistrySchemaVersion { get; init; } = MpsSkillRegistry.SchemaVersion;

    [JsonPropertyName("entries")]
    public List<MpsSkillPlanEntry> Entries { get; init; } = [];

    /// <summary>阶段产物的可审计详情；条目的 artifact_refs 只保存稳定 ID。</summary>
    [JsonPropertyName("artifacts")]
    public List<MpsPlanArtifact> Artifacts { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<MpsSkillPlanEntry> Skills => Entries;

    public static MpsProductionPlan CreateDefault(MpsSkillRegistry? registry = null)
    {
        registry ??= MpsSkillRegistry.CreateDefault();
        registry.ValidateOrThrow();
        return new()
        {
            Entries = registry.Skills.Select(skill => new MpsSkillPlanEntry
            {
                SkillId = skill.Id,
                ArtifactRefs = [],
                Blockers = []
            }).ToList(),
            Artifacts = []
        };
    }

    public MpsSkillPlanEntry Get(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) throw new ArgumentException("技能 ID 不能为空。", nameof(skillId));
        return Entries.FirstOrDefault(item => string.Equals(item.SkillId, skillId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"计划中不存在技能：{skillId}。");
    }

    public bool TryGet(string skillId, out MpsSkillPlanEntry entry)
    {
        entry = Entries.FirstOrDefault(item => string.Equals(item.SkillId, skillId, StringComparison.Ordinal))!;
        return entry is not null;
    }

    /// <summary>设置状态并检查阶段顺序；从已通过状态重做会使下游失效。</summary>
    public MpsSkillPlanEntry SetStatus(string skillId, MpsStageStatus status, string? reason = null, MpsSkillRegistry? registry = null)
    {
        registry ??= MpsSkillRegistry.CreateDefault();
        registry.ValidateOrThrow();
        var entry = Get(skillId);
        if (reason is not null && (reason.Length > 4_000 || reason.Any(char.IsControl))) throw new ArgumentException("状态原因过长或包含控制字符。", nameof(reason));
        if (status is MpsStageStatus.Failed or MpsStageStatus.Invalidated && string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("失败或失效状态必须记录原因。", nameof(reason));
        if (status is MpsStageStatus.InProgress or MpsStageStatus.PendingReview or MpsStageStatus.Passed)
        {
            var definition = registry.Get(skillId);
            foreach (var dependency in definition.Dependencies)
                if (Get(dependency).Status != MpsStageStatus.Passed)
                    throw new InvalidOperationException($"技能 {skillId} 依赖 {dependency} 尚未通过。请按固定顺序推进。");
        }
        if (!IsAllowedTransition(entry.Status, status))
            throw new InvalidOperationException($"技能 {skillId} 不能从 {entry.Status} 迁移到 {status}。");
        entry.Status = status;
        entry.InvalidatedBy = status == MpsStageStatus.Invalidated ? reason!.Trim() : null;
        entry.Revision++;
        entry.UpdatedUtc = DateTimeOffset.UtcNow;
        if (status is MpsStageStatus.InProgress or MpsStageStatus.Invalidated)
            InvalidateDownstream(skillId, reason ?? "上游阶段变更", registry);
        return entry;
    }

    /// <summary>为技能登记不重复的项目内产物引用。</summary>
    public void AddArtifact(MpsPlanArtifact artifact, MpsSkillRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        registry ??= MpsSkillRegistry.CreateDefault();
        registry.Get(artifact.SkillId);
        var entry = Get(artifact.SkillId);
        if (string.IsNullOrWhiteSpace(artifact.Id) || string.IsNullOrWhiteSpace(artifact.Path) || string.IsNullOrWhiteSpace(artifact.Kind))
            throw new ArgumentException("产物必须包含 ID、类型和项目内路径。", nameof(artifact));
        if (artifact.Path.StartsWith('/') || Path.IsPathRooted(artifact.Path) || artifact.Path.Split('/', '\\').Any(part => part == ".."))
            throw new ArgumentException("产物路径必须是项目内相对路径。", nameof(artifact));
        var artifactRefs = entry.ArtifactRefs;
        if (artifactRefs.Contains(artifact.Id, StringComparer.Ordinal)) throw new InvalidOperationException("阶段产物引用重复。");
        var artifacts = Artifacts;
        if (artifacts.Any(item => string.Equals(item.Id, artifact.Id, StringComparison.Ordinal))) throw new InvalidOperationException("阶段产物 ID 重复。");
        artifacts.Add(artifact);
        artifactRefs.Add(artifact.Id);
        entry.Revision++;
        entry.UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public void AddBlocker(string skillId, MpsPlanBlocker blocker)
    {
        ArgumentNullException.ThrowIfNull(blocker);
        var entry = Get(skillId);
        if (string.IsNullOrWhiteSpace(blocker.Code) || string.IsNullOrWhiteSpace(blocker.Message) || blocker.Message.Length > 4_000 || blocker.Message.Any(char.IsControl))
            throw new ArgumentException("阻断原因必须包含简短代码和不含控制字符的消息。", nameof(blocker));
        var blockers = entry.Blockers;
        if (blockers.Any(item => string.Equals(item.Code, blocker.Code, StringComparison.Ordinal))) return;
        blockers.Add(blocker);
        entry.Revision++;
        entry.UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public bool RemoveBlocker(string skillId, string code)
    {
        var entry = Get(skillId);
        var blockers = entry.Blockers;
        var removed = blockers.RemoveAll(item => string.Equals(item.Code, code, StringComparison.Ordinal)) > 0;
        if (removed) { entry.Revision++; entry.UpdatedUtc = DateTimeOffset.UtcNow; }
        return removed;
    }

    public IReadOnlyList<MpsSkillDiagnostic> Validate(MpsSkillRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        registry ??= MpsSkillRegistry.CreateDefault();
        var diagnostics = new List<MpsSkillDiagnostic>();
        if (!string.Equals(SchemaVersionValue, SchemaVersion, StringComparison.Ordinal)) Add(diagnostics, "plan.schema_version", "schema_version", $"只支持 {SchemaVersion}。");
        if (!string.Equals(RegistrySchemaVersion, MpsSkillRegistry.SchemaVersion, StringComparison.Ordinal)) Add(diagnostics, "plan.registry_schema_version", "registry_schema_version", "注册表版本不兼容。");
        if (Entries is null) { Add(diagnostics, "plan.entries_missing", "entries", "阶段计划条目缺失。"); return diagnostics; }
        if (Artifacts is null) Add(diagnostics, "plan.artifacts_missing", "artifacts", "阶段产物集合字段缺失。");
        if (Entries.Count != MpsSkillRegistry.MaxSkills) Add(diagnostics, "plan.count", "entries", $"阶段计划必须包含 {MpsSkillRegistry.MaxSkills} 项。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Entries.Count && index < MpsSkillRegistry.MaxSkills * 2; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Entries[index];
            var path = $"entries[{index}]";
            if (entry is null) { Add(diagnostics, "plan.entry_null", path, "计划条目为空。"); continue; }
            if (string.IsNullOrWhiteSpace(entry.SkillId)) Add(diagnostics, "plan.skill_id_missing", path + ".skill_id", "技能 ID 不能为空。");
            else if (!seen.Add(entry.SkillId)) Add(diagnostics, "plan.duplicate", path + ".skill_id", $"计划技能重复：{entry.SkillId}。");
            else if (!registry.TryGet(entry.SkillId, out _)) Add(diagnostics, "plan.skill_unknown", path + ".skill_id", $"计划包含未知技能：{entry.SkillId}。");
            if (entry.ArtifactRefs is null) Add(diagnostics, "plan.artifacts_missing", path + ".artifact_refs", "产物引用字段缺失。");
            if (entry.Blockers is null) Add(diagnostics, "plan.blockers_missing", path + ".blockers", "阻断原因字段缺失。");
            if (entry.Revision < 0) Add(diagnostics, "plan.revision", path + ".revision", "版本号不能为负数。");
            ValidateBlockers(entry, path, diagnostics);
        }
        foreach (var definition in registry.Skills.Take(MpsSkillRegistry.MaxSkills))
            if (!seen.Contains(definition.Id)) Add(diagnostics, "plan.skill_missing", "entries", $"计划缺少技能：{definition.Id}。");
        ValidateArtifacts(diagnostics, registry);
        return diagnostics;
    }

    public void ValidateOrThrow(MpsSkillRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = Validate(registry, cancellationToken);
        if (diagnostics.Count > 0) throw new MpsSkillContractException(diagnostics);
    }

    public string ToJson() => JsonSerializer.Serialize(this, MpsSkillContractJson.Options);

    public static MpsProductionPlan LoadJson(string json, MpsSkillRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        var result = TryLoadJson(json, registry, cancellationToken);
        if (!result.Success) throw new MpsSkillContractException(result.Diagnostics);
        return result.Value!;
    }

    public static MpsSkillContractLoadResult<MpsProductionPlan> TryLoadJson(string json, MpsSkillRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(null, [new("json.empty", "$", "JSON 不能为空。")]);
        if (json.Length > 1_048_576) return new(null, [new("json.too_large", "$", "阶段计划 JSON 超过 1MiB。")]);
        try
        {
            var shapeDiagnostics = InspectPlanJsonShape(json);
            var plan = JsonSerializer.Deserialize<MpsProductionPlan>(json, MpsSkillContractJson.Options);
            if (plan is null) return new(null, [new("json.empty", "$", "JSON 为空对象。")]);
            var diagnostics = shapeDiagnostics.Concat(plan.Validate(registry, cancellationToken)).ToArray();
            return diagnostics.Length == 0 ? new(plan, diagnostics) : new(null, diagnostics);
        }
        catch (JsonException error) { return new(null, [new("json.invalid", error.Path ?? "$", error.Message)]); }
        catch (NotSupportedException error) { return new(null, [new("json.unsupported", "$", error.Message)]); }
    }

    /// <summary>把旧十阶段状态机迁移为 11 项计划，并保留状态、原因、版本和更新时间。</summary>
    public static MpsProductionPlan FromStageStateMachine(MpsStageStateMachine machine, MpsSkillRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        registry ??= MpsSkillRegistry.CreateDefault();
        var plan = CreateDefault(registry);
        var root = plan.Get("video-production-series");
        root.Status = machine.States.All(state => state.Status == MpsStageStatus.Passed) ? MpsStageStatus.Passed : MpsStageStatus.InProgress;
        root.Revision = machine.States.Sum(state => state.Revision);
        foreach (var definition in registry.Skills.Take(MpsSkillRegistry.MaxSkills).Where(item => item.Stage is not null))
        {
            var state = machine.Get(definition.Stage!.Value);
            var entry = plan.Get(definition.Id);
            entry.Status = state.Status;
            entry.InvalidatedBy = state.Reason;
            entry.Revision = state.Revision;
            entry.UpdatedUtc = state.UpdatedUtc;
        }
        return plan;
    }

    private void InvalidateDownstream(string skillId, string reason, MpsSkillRegistry registry, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visited.Add(skillId) || visited.Count > MpsSkillRegistry.MaxSkills) return;
        var descendants = registry.Skills.Take(MpsSkillRegistry.MaxSkills).Where(skill => skill.Dependencies.Contains(skillId, StringComparer.Ordinal)).Select(skill => skill.Id).ToArray();
        foreach (var descendant in descendants)
        {
            var child = Get(descendant);
            child.Status = MpsStageStatus.Invalidated;
            child.InvalidatedBy = $"上游技能 {skillId} 变更：{reason.Trim()}";
            child.Revision++;
            child.UpdatedUtc = DateTimeOffset.UtcNow;
            InvalidateDownstream(descendant, reason, registry, visited);
        }
    }

    private void ValidateArtifacts(ICollection<MpsSkillDiagnostic> diagnostics, MpsSkillRegistry registry)
    {
        if (Artifacts is null) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Artifacts.Count && index < 512; index++)
        {
            var artifact = Artifacts[index];
            var path = $"artifacts[{index}]";
            if (artifact is null) { Add(diagnostics, "plan.artifact_null", path, "阶段产物为空。"); continue; }
            if (string.IsNullOrWhiteSpace(artifact.Id)) Add(diagnostics, "plan.artifact_id_missing", path + ".id", "阶段产物 ID 不能为空。");
            else if (!seen.Add(artifact.Id)) Add(diagnostics, "plan.artifact_duplicate", path + ".id", $"阶段产物 ID 重复：{artifact.Id}。");
            if (string.IsNullOrWhiteSpace(artifact.SkillId) || !registry.TryGet(artifact.SkillId, out _)) Add(diagnostics, "plan.artifact_skill_unknown", path + ".skill_id", "阶段产物必须引用已注册技能。");
            if (string.IsNullOrWhiteSpace(artifact.Kind)) Add(diagnostics, "plan.artifact_kind_missing", path + ".kind", "阶段产物类型不能为空。");
            if (string.IsNullOrWhiteSpace(artifact.Path) || artifact.Path.StartsWith('/') || Path.IsPathRooted(artifact.Path) || artifact.Path.Split('/', '\\').Any(part => part == ".."))
                Add(diagnostics, "plan.artifact_path_invalid", path + ".path", "阶段产物路径必须是项目内相对路径。");
        }
        foreach (var entry in Entries.Take(MpsSkillRegistry.MaxSkills))
        {
            if (entry?.ArtifactRefs is null) continue;
            foreach (var reference in entry.ArtifactRefs.Take(512))
            {
                var artifact = Artifacts.FirstOrDefault(item => string.Equals(item.Id, reference, StringComparison.Ordinal));
                if (artifact is null) Add(diagnostics, "plan.artifact_ref_unknown", $"entries[{entry.SkillId}].artifact_refs", $"未找到产物引用：{reference}。");
                else if (!string.Equals(artifact.SkillId, entry.SkillId, StringComparison.Ordinal)) Add(diagnostics, "plan.artifact_ref_skill", $"entries[{entry.SkillId}].artifact_refs", "产物引用的技能与阶段条目不一致。");
            }
        }
    }

    /// <summary>在默认值填充前检查必需 JSON 字段，区分“缺失”与空集合。</summary>
    private static IReadOnlyList<MpsSkillDiagnostic> InspectPlanJsonShape(string json)
    {
        using var document = JsonDocument.Parse(json);
        var diagnostics = new List<MpsSkillDiagnostic>();
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new("json.shape", "$", "阶段计划 JSON 根必须是对象。"));
            return diagnostics;
        }
        if (!root.TryGetProperty("schema_version", out _)) diagnostics.Add(new("plan.schema_version_missing", "schema_version", "缺少 schema_version 字段。"));
        if (!root.TryGetProperty("registry_schema_version", out _)) diagnostics.Add(new("plan.registry_schema_version_missing", "registry_schema_version", "缺少 registry_schema_version 字段。"));
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new("plan.entries_missing", "entries", "缺少 entries 数组。"));
            return diagnostics;
        }
        if (!root.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
            diagnostics.Add(new("plan.artifacts_missing", "artifacts", "缺少 artifacts 数组。"));
        var index = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object)
            {
                var path = $"entries[{index}]";
                if (!entry.TryGetProperty("skill_id", out _)) diagnostics.Add(new("plan.skill_id_missing", path + ".skill_id", "缺少 skill_id 字段。"));
                if (!entry.TryGetProperty("status", out _)) diagnostics.Add(new("plan.status_missing", path + ".status", "缺少 status 字段。"));
                if (!entry.TryGetProperty("artifact_refs", out _)) diagnostics.Add(new("plan.artifacts_missing", path + ".artifact_refs", "缺少 artifact_refs 字段。"));
                if (!entry.TryGetProperty("blockers", out _)) diagnostics.Add(new("plan.blockers_missing", path + ".blockers", "缺少 blockers 字段。"));
            }
            index++;
            if (index >= MpsSkillRegistry.MaxSkills * 2) break;
        }
        return diagnostics;
    }

    private static bool IsAllowedTransition(MpsStageStatus from, MpsStageStatus to) => from == to || (from, to) switch
    {
        (MpsStageStatus.NotStarted, MpsStageStatus.InProgress or MpsStageStatus.Invalidated) => true,
        (MpsStageStatus.InProgress, MpsStageStatus.PendingReview or MpsStageStatus.Failed or MpsStageStatus.Invalidated) => true,
        (MpsStageStatus.PendingReview, MpsStageStatus.Passed or MpsStageStatus.Failed or MpsStageStatus.InProgress or MpsStageStatus.Invalidated) => true,
        (MpsStageStatus.Passed, MpsStageStatus.InProgress or MpsStageStatus.Invalidated) => true,
        (MpsStageStatus.Failed, MpsStageStatus.InProgress or MpsStageStatus.Invalidated) => true,
        (MpsStageStatus.Invalidated, MpsStageStatus.InProgress or MpsStageStatus.NotStarted) => true,
        _ => false
    };

    private static void ValidateBlockers(MpsSkillPlanEntry entry, string path, ICollection<MpsSkillDiagnostic> diagnostics)
    {
        if (entry.Blockers is null) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entry.Blockers.Count && index < 256; index++)
        {
            var blocker = entry.Blockers[index];
            var blockerPath = $"{path}.blockers[{index}]";
            if (blocker is null) { Add(diagnostics, "plan.blocker_null", blockerPath, "阻断原因为空。"); continue; }
            if (string.IsNullOrWhiteSpace(blocker.Code)) Add(diagnostics, "plan.blocker_code_missing", blockerPath + ".code", "阻断代码不能为空。");
            else if (!seen.Add(blocker.Code)) Add(diagnostics, "plan.blocker_duplicate", blockerPath + ".code", $"阻断代码重复：{blocker.Code}。");
            if (string.IsNullOrWhiteSpace(blocker.Message)) Add(diagnostics, "plan.blocker_message_missing", blockerPath + ".message", "阻断消息不能为空。");
            if (blocker.Message.Length > 4_000 || blocker.Message.Any(char.IsControl)) Add(diagnostics, "plan.blocker_message_invalid", blockerPath + ".message", "阻断消息过长或包含控制字符。");
        }
    }

    private static void Add(ICollection<MpsSkillDiagnostic> diagnostics, string code, string path, string message)
    {
        if (diagnostics.Count < 512) diagnostics.Add(new(code, path, message));
    }
}

/// <summary>兼容简短产品命名的注册表门面。</summary>
public sealed class SkillRegistry : MpsSkillRegistry
{
    public SkillRegistry() { }
}

/// <summary>兼容简短产品命名的阶段计划门面。</summary>
public sealed class ProductionPlan : MpsProductionPlan
{
    public ProductionPlan() { }
}

internal static class MpsSkillContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonFiles.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
