using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoProduction;

/// <summary>技能合同校验和文档读取的次数、大小及墙钟上限。</summary>
public sealed record MpsSkillExecutionValidationLimits
{
    public int MaxItemsPerCollection { get; init; } = 32;
    public int MaxDocumentBytes { get; init; } = 65_536;
    public TimeSpan MaxWallTime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>拒绝不可证明终止的配置。</summary>
    public void Validate()
    {
        if (MaxItemsPerCollection is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(MaxItemsPerCollection));
        if (MaxDocumentBytes is < 256 or > 262_144) throw new ArgumentOutOfRangeException(nameof(MaxDocumentBytes));
        if (MaxWallTime <= TimeSpan.Zero || MaxWallTime > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(MaxWallTime));
    }
}

/// <summary>结构、固定注册表、阶段回退与文档一致性的离线校验。</summary>
public static class MpsSkillExecutionContractValidator
{
    public const string SharedDocumentPath = "skills/video-production-series/references/skill-execution-contract.md";
    private const int MaxJsonCharacters = 1_048_576;
    private const int MaxDiagnostics = 512;

    /// <summary>工具只列当前共享 C# 核心，禁止脚本或未知工具进入许可集合。</summary>
    public static IReadOnlySet<string> KnownCSharpTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(MpsSkillRegistry), nameof(MpsProductionPlan), nameof(ProjectDirectory), nameof(ManifestValidator),
        nameof(AssetIndexer), nameof(ModelCatalogCache), nameof(ProviderModelRouter), nameof(MpsBudgetLedger),
        nameof(MediaTools), nameof(ProviderAsyncTaskCoordinator), nameof(IProviderAdapter), nameof(MediaWorkflows),
        nameof(MpsTimelineValidation), nameof(MpsProjectDiagnostics), nameof(CostAccounting), nameof(ClipTimeMapping)
    };

    /// <summary>按固定最大条目数校验所有合同，不读取网络、凭据或供应商。</summary>
    public static IReadOnlyList<MpsSkillDiagnostic> Validate(MpsSkillExecutionContractSet set, MpsSkillRegistry? registry = null,
        MpsSkillExecutionValidationLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        registry ??= MpsSkillRegistry.CreateDefault();
        var scope = new ValidationScope(limits, cancellationToken);
        var result = new List<MpsSkillDiagnostic>();
        result.AddRange(registry.Validate(cancellationToken));
        if (set.SchemaVersionValue != MpsSkillExecutionContractSet.SchemaVersion) Add(result, "execution.schema", "schema_version", "不支持的执行合同版本。");
        if (!IsSha256(set.SharedDocumentSha256)) Add(result, "execution.shared_hash", "shared_document_sha256", "共享文档必须有 SHA-256。");
        if (set.Contracts is null) { Add(result, "execution.contracts_missing", "contracts", "合同集合缺失。"); return result; }
        if (set.Contracts.Count != MpsSkillRegistry.MaxSkills) Add(result, "execution.count", "contracts", "必须恰好包含 11 个技能执行合同。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Math.Min(set.Contracts.Count, MpsSkillRegistry.MaxSkills * 2); index++)
        {
            scope.Check();
            var contract = set.Contracts[index];
            var path = $"contracts[{index}]";
            if (contract is null) { Add(result, "execution.null", path, "执行合同为空。"); continue; }
            if (string.IsNullOrWhiteSpace(contract.SkillId) || !registry.TryGet(contract.SkillId, out var definition))
            { Add(result, "execution.skill_unknown", path + ".skill_id", "合同必须引用已注册技能。"); continue; }
            if (!seen.Add(contract.SkillId)) Add(result, "execution.duplicate", path + ".skill_id", "技能执行合同重复。");
            if (index < MpsSkillRegistry.MaxSkills && contract.SkillId != MpsSkillRegistry.RequiredSkillIds[index]) Add(result, "execution.order", path + ".skill_id", "合同顺序必须与固定注册表一致。");
            if (contract.Stage != definition.Stage) Add(result, "execution.stage", path + ".stage", "阶段必须与技能注册表一致。");
            if (!Version.TryParse(contract.SkillVersion, out var version) || version.Build < 0 || version.Revision != -1) Add(result, "execution.version", path + ".skill_version", "技能版本必须是 major.minor.patch。");
            if (!IsSha256(contract.DocumentSha256)) Add(result, "execution.document_hash", path + ".document_sha256", "技能文档必须有 SHA-256。");
            ValidateCollection(contract.Inputs, path + ".inputs", scope, result, item => item.Name, item => item.Description);
            ValidateCollection(contract.PrerequisiteEvidence, path + ".prerequisite_evidence", scope, result, item => item.Id, item => item.Description);
            ValidateCollection(contract.AllowedCSharpTools, path + ".allowed_csharp_tools", scope, result, item => item.Name, item => item.Purpose);
            ValidateCollection(contract.Outputs, path + ".outputs", scope, result, item => item.Name, item => item.Description);
            ValidateCollection(contract.QualityGates, path + ".quality_gates", scope, result, item => item.Id, item => item.Description);
            ValidateKinds(contract.Inputs?.Select(item => item?.Kind), path + ".inputs.kind", scope, result);
            ValidateKinds(contract.PrerequisiteEvidence?.Select(item => item?.Kind), path + ".prerequisite_evidence.kind", scope, result);
            if (contract.AllowedCSharpTools is not null)
                foreach (var tool in contract.AllowedCSharpTools.Take(scope.Limits.MaxItemsPerCollection))
                { scope.Check(); if (tool is not null && !KnownCSharpTools.Contains(tool.Name)) Add(result, "execution.tool_unknown", path + ".allowed_csharp_tools", "只允许当前 C# 核心中的已知工具。"); }
            ValidateOutputs(contract, definition, path, scope, result);
            if (contract.QualityGates is not null && !contract.QualityGates.Any(item => item is { Blocking: true })) Add(result, "execution.blocking_gate_missing", path + ".quality_gates", "至少声明一个阻断质量门。");
            ValidateFallback(contract, definition, registry, path, result);
            ValidateStrings(contract.DocumentMarkers, path + ".document_markers", scope, result);
        }
        foreach (var id in MpsSkillRegistry.RequiredSkillIds)
        { scope.Check(); if (!seen.Contains(id)) Add(result, "execution.missing", "contracts", $"缺少技能合同：{id}。"); }
        return result;
    }

    /// <summary>严格加载 JSON，拒绝未知字段和必需字段缺失。</summary>
    public static MpsSkillContractLoadResult<MpsSkillExecutionContractSet> TryLoadJson(string json, MpsSkillRegistry? registry = null,
        MpsSkillExecutionValidationLimits? limits = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(null, [new("json.empty", "$", "JSON 不能为空。")]);
        if (json.Length > MaxJsonCharacters) return new(null, [new("json.too_large", "$", "执行合同 JSON 超过 1MiB。")]);
        var scope = new ValidationScope(limits, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            var shape = InspectShape(document.RootElement, scope);
            var set = JsonSerializer.Deserialize<MpsSkillExecutionContractSet>(json, MpsSkillExecutionContractJson.Options);
            if (set is null) return new(null, [new("json.empty", "$", "JSON 为空对象。")]);
            var diagnostics = shape.Concat(Validate(set, registry, limits, cancellationToken)).Take(MaxDiagnostics).ToArray();
            scope.Check();
            return diagnostics.Length == 0 ? new(set, diagnostics) : new(null, diagnostics);
        }
        catch (JsonException error) { return new(null, [new("json.invalid", error.Path ?? "$", "执行合同 JSON 无效或含未知字段。")]); }
        catch (NotSupportedException) { return new(null, [new("json.unsupported", "$", "执行合同 JSON 含不支持的类型。")]); }
    }

    /// <summary>只读取固定 11 份技能及一份共享文档；不递归扫描目录。</summary>
    public static IReadOnlyList<MpsSkillDiagnostic> ValidateDocuments(MpsSkillExecutionContractSet set, string repositoryRoot,
        MpsSkillExecutionValidationLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var scope = new ValidationScope(limits, cancellationToken);
        var result = Validate(set, limits: limits, cancellationToken: cancellationToken).ToList();
        if (result.Count > 0) return result;
        var root = Path.GetFullPath(repositoryRoot);
        for (var index = 0; index < MpsSkillRegistry.MaxSkills; index++)
        {
            scope.Check();
            var contract = set.Contracts[index];
            var relative = $"skills/{contract.SkillId}/SKILL.md";
            var text = ReadDocument(root, relative, scope, result);
            if (text is not null) ValidateDocumentText(contract, text, relative, result, limits, cancellationToken);
        }
        var sharedText = ReadDocument(root, SharedDocumentPath, scope, result);
        if (sharedText is not null && HashDocument(sharedText) != set.SharedDocumentSha256)
            Add(result, "document.shared_changed", SharedDocumentPath, "共享说明发生变化，需复核并更新执行合同摘要。");
        return result;
    }

    /// <summary>用内存文档验证相同规则，便于完全离线测试变更和失配。</summary>
    public static IReadOnlyList<MpsSkillDiagnostic> ValidateDocumentText(MpsSkillExecutionContract contract, string text,
        string path = "SKILL.md", ICollection<MpsSkillDiagnostic>? diagnostics = null,
        MpsSkillExecutionValidationLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(text);
        var scope = new ValidationScope(limits, cancellationToken);
        var local = new List<MpsSkillDiagnostic>();
        if (text.Length > 262_144) Add(local, "document.too_large", path, "技能文档超过硬上限。");
        else
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\uFEFF');
            if (!text.StartsWith("---\n", StringComparison.Ordinal)) Add(local, "document.frontmatter", path, "技能文档必须保留 YAML 头。");
            if (!text.Contains($"\nname: {contract.SkillId}\n", StringComparison.Ordinal)) Add(local, "document.skill_id", path, "技能文档 name 与合同 ID 不一致。");
            if (!text.Contains($"\nversion: {contract.SkillVersion}\n", StringComparison.Ordinal)) Add(local, "document.version", path, "技能文档 version 与合同版本不一致。");
            if (!text.Contains("## 阶段执行契约", StringComparison.Ordinal)) Add(local, "document.execution_missing", path, "技能文档缺少执行合同说明。");
            if (contract.DocumentMarkers is not null)
                foreach (var marker in contract.DocumentMarkers.Take(32))
                {
                    scope.Check();
                    if (!text.Contains(marker, StringComparison.Ordinal)) Add(local, "document.marker_missing", path, $"缺少说明：{marker}。");
                }
            if (HashDocument(text) != contract.DocumentSha256) Add(local, "document.changed", path, "技能文档发生变化，需复核并更新执行合同摘要。");
        }
        if (diagnostics is not null) foreach (var item in local.Take(32)) { scope.Check(); diagnostics.Add(item); }
        scope.Check();
        return local;
    }

    /// <summary>统一换行后计算摘要，避免 Windows/Unix 换行单独触发失配。</summary>
    public static string HashDocument(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 262_144) throw new ArgumentOutOfRangeException(nameof(text), "文档摘要超过 256KiB 字符上限。");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)))).ToLowerInvariant();
    }

    /// <summary>开始阶段前校验必需输入与证据，返回可展示阻断原因。</summary>
    public static IReadOnlyList<MpsSkillDiagnostic> ValidateReadiness(MpsSkillExecutionContract contract,
        IReadOnlySet<string> availableInputs, IReadOnlySet<string> availableEvidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(availableInputs);
        ArgumentNullException.ThrowIfNull(availableEvidence);
        var result = new List<MpsSkillDiagnostic>();
        var scope = new ValidationScope(null, cancellationToken);
        foreach (var input in contract.Inputs.Take(32))
        { scope.Check(); if (input.Required && !availableInputs.Contains(input.Name)) Add(result, "execution.input_missing", "inputs." + input.Name, "阶段必需输入尚未提供。"); }
        foreach (var evidence in contract.PrerequisiteEvidence.Take(32))
        { scope.Check(); if (evidence.Required && !availableEvidence.Contains(evidence.Id)) Add(result, "execution.evidence_missing", "prerequisite_evidence." + evidence.Id, "阶段前置证据尚未通过。"); }
        return result;
    }

    /// <summary>阻断门缺失或未通过时，拒绝把阶段标记为已通过。</summary>
    public static IReadOnlyList<MpsSkillDiagnostic> ValidateQualityGateResults(MpsSkillExecutionContract contract,
        IReadOnlyDictionary<string, bool> gateResults, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(gateResults);
        var result = new List<MpsSkillDiagnostic>();
        var scope = new ValidationScope(null, cancellationToken);
        foreach (var gate in contract.QualityGates.Take(32))
        { scope.Check(); if (gate.Blocking && (!gateResults.TryGetValue(gate.Id, out var passed) || !passed)) Add(result, "execution.gate_failed", "quality_gates." + gate.Id, "阻断质量门尚未通过。"); }
        return result;
    }

    /// <summary>检查 JSON 产物的顶层结构，媒体解码仍由对应质量门提供证据。</summary>
    public static IReadOnlyList<MpsSkillDiagnostic> ValidateJsonOutput(MpsSkillOutputRequirement output, string json,
        CancellationToken cancellationToken = default)
    {
        var result = new List<MpsSkillDiagnostic>();
        if (output.Format != "JSON") { Add(result, "output.format", output.Name, "此产物不是 JSON。"); return result; }
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonCharacters) { Add(result, "output.size", output.Name, "JSON 产物为空或过大。"); return result; }
        var scope = new ValidationScope(null, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) Add(result, "output.shape", output.Name, "JSON 产物根必须是对象。");
            else foreach (var field in output.RequiredFields.Take(32))
            { scope.Check(); if (!document.RootElement.TryGetProperty(field, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) Add(result, "output.field_missing", output.Name + "." + field, "JSON 产物缺少必需字段。"); }
        }
        catch (JsonException) { Add(result, "output.json_invalid", output.Name, "JSON 产物格式无效。"); }
        return result;
    }

    /// <summary>回退只指向更早的具体阶段，避免循环重算。</summary>
    private static void ValidateFallback(MpsSkillExecutionContract contract, MpsSkillDefinition definition, MpsSkillRegistry registry, string path, ICollection<MpsSkillDiagnostic> result)
    {
        if (definition.Order <= 1)
        { if (contract.FallbackSkillId is not null) Add(result, "execution.fallback", path + ".fallback_skill_id", "总入口和第一阶段没有更早回退阶段。"); return; }
        if (string.IsNullOrWhiteSpace(contract.FallbackSkillId) || !registry.TryGet(contract.FallbackSkillId, out var fallback) || fallback.Order <= 0 || fallback.Order >= definition.Order)
            Add(result, "execution.fallback", path + ".fallback_skill_id", "回退必须指向更早的具体技能阶段。");
    }

    /// <summary>产物结构与注册表最低产物保持一致。</summary>
    private static void ValidateOutputs(MpsSkillExecutionContract contract, MpsSkillDefinition definition, string path, ValidationScope scope, ICollection<MpsSkillDiagnostic> result)
    {
        if (contract.Outputs is null) return;
        foreach (var output in contract.Outputs.Take(scope.Limits.MaxItemsPerCollection))
        {
            scope.Check();
            if (output is null) continue;
            if (string.IsNullOrWhiteSpace(output.Kind)) Add(result, "output.kind_missing", path + ".outputs", "产物类型不能为空。");
            if (output.Format is not ("JSON" or "Markdown" or "WebM-alpha" or "WAV" or "MP4" or "SRT" or "PNG" or "directory")) Add(result, "output.format", path + ".outputs", "未知产物格式。");
            if (Path.IsPathRooted(output.Name) || output.Name.Contains(':') || output.Name.Split('/', '\\').Contains("..", StringComparer.Ordinal)) Add(result, "output.path", path + ".outputs", "产物名称必须是项目相对路径。");
            if (output.Format == "JSON") ValidateStrings(output.RequiredFields, path + ".outputs." + output.Name + ".required_fields", scope, result);
            else if (output.RequiredFields is null || output.RequiredFields.Count != 0) Add(result, "output.fields", path + ".outputs", "媒体与目录产物不能声明 JSON 字段。");
        }
        foreach (var expected in definition.ExpectedArtifacts.Take(32))
        { scope.Check(); if (!contract.Outputs.Any(item => item is { Required: true } && item.Name == expected)) Add(result, "execution.expected_output", path + ".outputs", $"缺少注册表规定的必需产物：{expected}。"); }
    }

    /// <summary>输入和证据类别不能省略。</summary>
    private static void ValidateKinds(IEnumerable<string?>? kinds, string path, ValidationScope scope, ICollection<MpsSkillDiagnostic> result)
    {
        if (kinds is null) return;
        foreach (var kind in kinds.Take(scope.Limits.MaxItemsPerCollection))
        { scope.Check(); if (string.IsNullOrWhiteSpace(kind)) Add(result, "execution.kind_missing", path, "输入或证据类型不能为空。"); }
    }

    /// <summary>集合条目有数量、取消和墙钟边界，名称不可重复。</summary>
    private static void ValidateCollection<T>(IReadOnlyList<T>? items, string path, ValidationScope scope, ICollection<MpsSkillDiagnostic> result,
        Func<T, string> id, Func<T, string> description) where T : class
    {
        if (items is null || items.Count == 0) { Add(result, "execution.collection_missing", path, "执行集合必须明确且非空。"); return; }
        if (items.Count > scope.Limits.MaxItemsPerCollection) Add(result, "execution.collection_limit", path, "执行集合超过条目上限。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Math.Min(items.Count, scope.Limits.MaxItemsPerCollection); index++)
        {
            scope.Check();
            var item = items[index];
            if (item is null) { Add(result, "execution.item_null", $"{path}[{index}]", "执行条目为空。"); continue; }
            var key = id(item);
            if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl)) Add(result, "execution.item_id", $"{path}[{index}]", "条目名称无效。");
            else if (!seen.Add(key)) Add(result, "execution.item_duplicate", $"{path}[{index}]", "条目名称重复。");
            var text = description(item);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2_000 || text.Any(char.IsControl)) Add(result, "execution.item_description", $"{path}[{index}]", "条目说明缺失或无效。");
        }
    }

    /// <summary>检查必需字段或文档标记的唯一性和长度。</summary>
    private static void ValidateStrings(IReadOnlyList<string>? items, string path, ValidationScope scope, ICollection<MpsSkillDiagnostic> result)
    {
        if (items is null || items.Count == 0) { Add(result, "execution.strings_missing", path, "字段或文档标记不能为空。"); return; }
        if (items.Count > scope.Limits.MaxItemsPerCollection) Add(result, "execution.collection_limit", path, "条目超过上限。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.Take(scope.Limits.MaxItemsPerCollection))
        { scope.Check(); if (string.IsNullOrWhiteSpace(item) || item.Length > 128 || item.Any(char.IsControl) || !seen.Add(item)) Add(result, "execution.string_invalid", path, "字段或标记为空、重复或过长。"); }
    }

    /// <summary>在默认值填充前检查 JSON 必需字段。</summary>
    private static IReadOnlyList<MpsSkillDiagnostic> InspectShape(JsonElement root, ValidationScope scope)
    {
        var result = new List<MpsSkillDiagnostic>();
        RequireFields(root, ["schema_version", "shared_document_sha256", "contracts"], "$", result);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("contracts", out var contracts) || contracts.ValueKind != JsonValueKind.Array) return result;
        var index = 0;
        foreach (var contract in contracts.EnumerateArray())
        {
            scope.Check();
            if (index >= MpsSkillRegistry.MaxSkills * 2) break;
            var path = $"contracts[{index++}]";
            RequireFields(contract, ["skill_id", "skill_version", "stage", "inputs", "prerequisite_evidence", "allowed_csharp_tools", "outputs", "quality_gates", "fallback_skill_id", "document_markers", "document_sha256"], path, result);
            if (contract.ValueKind != JsonValueKind.Object) continue;
            InspectItems(contract, "inputs", ["name", "kind", "required", "description"], path, scope, result);
            InspectItems(contract, "prerequisite_evidence", ["id", "kind", "required", "description"], path, scope, result);
            InspectItems(contract, "allowed_csharp_tools", ["name", "purpose"], path, scope, result);
            InspectItems(contract, "outputs", ["name", "kind", "format", "required", "description", "required_fields"], path, scope, result);
            InspectItems(contract, "quality_gates", ["id", "description", "blocking"], path, scope, result);
        }
        return result;
    }

    /// <summary>检查固定上限内的嵌套条目字段。</summary>
    private static void InspectItems(JsonElement contract, string property, string[] fields, string path, ValidationScope scope, ICollection<MpsSkillDiagnostic> result)
    {
        if (!contract.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        foreach (var item in items.EnumerateArray())
        { scope.Check(); if (index >= scope.Limits.MaxItemsPerCollection) break; RequireFields(item, fields, $"{path}.{property}[{index++}]", result); }
    }

    /// <summary>保留缺失字段路径，避免默认为空或 false。</summary>
    private static void RequireFields(JsonElement element, string[] fields, string path, ICollection<MpsSkillDiagnostic> result)
    {
        if (element.ValueKind != JsonValueKind.Object) { Add(result, "execution.json_shape", path, "合同条目必须是对象。"); return; }
        foreach (var field in fields)
            if (!element.TryGetProperty(field, out _)) Add(result, "execution.field_missing", path + "." + field, "缺少必需合同字段。");
    }

    /// <summary>读取唯一确定的仓库内 UTF-8 文档并拒绝越界链接。</summary>
    private static string? ReadDocument(string root, string relative, ValidationScope scope, ICollection<MpsSkillDiagnostic> result)
    {
        scope.Check();
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { Add(result, "document.path", relative, "文档路径越出仓库。"); return null; }
        try
        {
            // 固定路径且上限 12 个文件；拒绝重解析点以免读取仓库外内容。
            FileSystemInfo current = new FileInfo(path);
            for (var depth = 0; depth < 8; depth++)
            {
                scope.Check();
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) { Add(result, "document.reparse", relative, "技能文档不能通过重解析点读取。"); return null; }
                var parent = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
                if (parent is null || parent.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > scope.Limits.MaxDocumentBytes) { Add(result, "document.too_large", relative, "技能文档超过读取上限。"); return null; }
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            scope.Check();
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (IOException) { Add(result, "document.read_failed", relative, "无法读取技能文档。"); return null; }
        catch (UnauthorizedAccessException) { Add(result, "document.read_failed", relative, "无权读取技能文档。"); return null; }
        catch (DecoderFallbackException) { Add(result, "document.encoding", relative, "技能文档必须是 UTF-8。"); return null; }
    }

    /// <summary>摘要只允许固定 64 位十六进制字符。</summary>
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));

    /// <summary>诊断数量有固定上限，避免恶意输入膨胀。</summary>
    private static void Add(ICollection<MpsSkillDiagnostic> result, string code, string path, string message)
    { if (result.Count < MaxDiagnostics) result.Add(new(code, path, message)); }

    /// <summary>每一步都检查取消和墙钟；集合仍各自保留确定的条目上限。</summary>
    private sealed class ValidationScope
    {
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private readonly CancellationToken cancellationToken;
        public MpsSkillExecutionValidationLimits Limits { get; }
        /// <summary>启动受墙钟和取消限制的一次校验。</summary>
        public ValidationScope(MpsSkillExecutionValidationLimits? limits, CancellationToken cancellationToken)
        {
            Limits = limits ?? new MpsSkillExecutionValidationLimits();
            Limits.Validate();
            this.cancellationToken = cancellationToken;
            Check();
        }
        /// <summary>每个有界处理步骤前检查中止条件。</summary>
        public void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > Limits.MaxWallTime) throw new TimeoutException("技能执行合同校验已达到墙钟上限。");
        }
    }
}
