using System.Diagnostics;
using System.Text.Json.Nodes;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>11 个技能执行合同与技能说明的一致性离线回归。</summary>
public static class SkillExecutionContractTests
{
    /// <summary>执行固定数量内存断言和 12 份本地文档校验，不调用网络或付费服务。</summary>
    public static void Run()
    {
        DefaultContractsAreCompleteAndIndependent();
        JsonRoundTripIsStrict();
        UnknownToolsAndFutureFallbackAreRejected();
        RequiredInputsAndEvidenceBlockStart();
        BlockingGatesAndOutputFieldsMustPass();
        DocumentsDetectVersionAndContentChanges();
        LimitsAndCancellationAreEnforced();
        RepositoryDocumentsMatchContracts();
    }

    /// <summary>默认合同保留固定入口并避免跨项目共享可变集合。</summary>
    private static void DefaultContractsAreCompleteAndIndependent()
    {
        var original = MpsSkillExecutionContractSet.CreateDefault();
        Assert(original.Contracts.Count == 11, "执行合同必须恰好覆盖 11 个技能");
        Assert(original.Contracts.Select(item => item.SkillId).SequenceEqual(MpsSkillRegistry.RequiredSkillIds), "执行合同改变了既有技能 ID 或顺序");
        var diagnostics = MpsSkillExecutionContractValidator.Validate(original);
        Assert(diagnostics.Count == 0, "默认执行合同无效：" + string.Join("; ", diagnostics.Select(item => item.Code + ": " + item.Path)));
        var next = MpsSkillExecutionContractSet.CreateDefault();
        next.Get("video-03-screenplay").Outputs[0].RequiredFields.Clear();
        next.Get("video-production-series").Inputs.Clear();
        Assert(original.Get("video-03-screenplay").Outputs[0].RequiredFields.Count > 0, "默认产物字段跨项目共享");
        Assert(original.Get("video-production-series").Inputs.Count > 0, "默认输入集合跨项目共享");
        Assert(original.Get("video-production-series").Stage is null, "总入口不能绑定具体阶段");
        Assert(original.Get("video-06-narration").FallbackSkillId == "video-03-screenplay", "旁白文字错误应先回退冻结讲稿");
    }

    /// <summary>严格 JSON 拒绝遗漏必填布尔、未知字段、重复和未知技能。</summary>
    private static void JsonRoundTripIsStrict()
    {
        var set = MpsSkillExecutionContractSet.CreateDefault();
        var loaded = MpsSkillExecutionContractValidator.TryLoadJson(set.ToJson());
        Assert(loaded.Success && loaded.Value!.ToJson() == set.ToJson(), "执行合同 JSON 往返不稳定");

        var missing = JsonNode.Parse(set.ToJson())!.AsObject();
        missing["contracts"]![0]!["inputs"]![0]!.AsObject().Remove("required");
        var missingResult = MpsSkillExecutionContractValidator.TryLoadJson(missing.ToJsonString());
        Assert(!missingResult.Success && missingResult.Diagnostics.Any(item => item.Code == "execution.field_missing" && item.Path.EndsWith(".required", StringComparison.Ordinal)), "缺失 required 字段不应静默变为可选");

        var unknown = JsonNode.Parse(set.ToJson())!.AsObject();
        unknown["contracts"]![0]!["unknown_field"] = "sample";
        Assert(!MpsSkillExecutionContractValidator.TryLoadJson(unknown.ToJsonString()).Success, "未知执行字段应被拒绝");

        var duplicate = JsonNode.Parse(set.ToJson())!.AsObject();
        duplicate["contracts"]![1]!["skill_id"] = "video-production-series";
        Assert(MpsSkillExecutionContractValidator.TryLoadJson(duplicate.ToJsonString()).Diagnostics.Any(item => item.Code == "execution.duplicate"), "重复技能执行合同应有稳定诊断");

        var invalid = JsonNode.Parse(set.ToJson())!.AsObject();
        invalid["contracts"]![1]!["skill_id"] = "video-unknown";
        Assert(MpsSkillExecutionContractValidator.TryLoadJson(invalid.ToJsonString()).Diagnostics.Any(item => item.Code == "execution.skill_unknown"), "未知技能执行合同应有稳定诊断");
    }

    /// <summary>工具许可只能使用已有 C# 核心，回退不能向未来阶段。</summary>
    private static void UnknownToolsAndFutureFallbackAreRejected()
    {
        var invalid = JsonNode.Parse(MpsSkillExecutionContractSet.CreateDefault().ToJson())!.AsObject();
        invalid["contracts"]![2]!["allowed_csharp_tools"]![0]!["name"] = "python";
        invalid["contracts"]![2]!["fallback_skill_id"] = "video-10-cost-delivery";
        var result = MpsSkillExecutionContractValidator.TryLoadJson(invalid.ToJsonString());
        Assert(result.Diagnostics.Any(item => item.Code == "execution.tool_unknown"), "Python 或未知工具不能加入阶段许可");
        Assert(result.Diagnostics.Any(item => item.Code == "execution.fallback"), "回退不能指向未来阶段");

        var missingOutput = JsonNode.Parse(MpsSkillExecutionContractSet.CreateDefault().ToJson())!.AsObject();
        missingOutput["contracts"]![3]!["outputs"]![0]!["required"] = false;
        Assert(MpsSkillExecutionContractValidator.TryLoadJson(missingOutput.ToJsonString()).Diagnostics.Any(item => item.Code == "execution.expected_output"), "注册表规定的产物必须保持必需");
    }

    /// <summary>缺失最终旁白或音频证据时阻断口型阶段。</summary>
    private static void RequiredInputsAndEvidenceBlockStart()
    {
        var contract = MpsSkillExecutionContractSet.CreateDefault().Get("video-07-lip-sync");
        var missing = MpsSkillExecutionContractValidator.ValidateReadiness(contract,
            new HashSet<string>(["presenter"], StringComparer.Ordinal),
            new HashSet<string>(["task.recovery"], StringComparer.Ordinal));
        Assert(missing.Any(item => item.Code == "execution.input_missing" && item.Path.EndsWith("final-narration", StringComparison.Ordinal)), "没有最终旁白不能开始口型");
        Assert(missing.Any(item => item.Code == "execution.evidence_missing" && item.Path.EndsWith("audio.final", StringComparison.Ordinal)), "没有音频指纹不能开始口型");
        var passed = MpsSkillExecutionContractValidator.ValidateReadiness(contract,
            new HashSet<string>(contract.Inputs.Select(item => item.Name), StringComparer.Ordinal),
            new HashSet<string>(contract.PrerequisiteEvidence.Select(item => item.Id), StringComparer.Ordinal));
        Assert(passed.Count == 0, "完整输入和证据应能进入阶段预检");
    }

    /// <summary>产物字段和实际质量门结果都必需，文件名称不代表完成。</summary>
    private static void BlockingGatesAndOutputFieldsMustPass()
    {
        var contract = MpsSkillExecutionContractSet.CreateDefault().Get("video-10-cost-delivery");
        var empty = MpsSkillExecutionContractValidator.ValidateQualityGateResults(contract, new Dictionary<string, bool>());
        Assert(empty.Count == contract.QualityGates.Count, "缺失阻断门结果不能自动通过");
        var failed = contract.QualityGates.ToDictionary(item => item.Id, item => item.Id != "currency-separated", StringComparer.Ordinal);
        Assert(MpsSkillExecutionContractValidator.ValidateQualityGateResults(contract, failed).Count == 1, "多币种门失败应阻断交付");
        var passed = contract.QualityGates.ToDictionary(item => item.Id, _ => true, StringComparer.Ordinal);
        Assert(MpsSkillExecutionContractValidator.ValidateQualityGateResults(contract, passed).Count == 0, "全部门通过后应允许阶段完成预检");

        var output = contract.Outputs[0];
        Assert(MpsSkillExecutionContractValidator.ValidateJsonOutput(output, "{}").Count == output.RequiredFields.Count, "空 JSON 不能充当费用报告");
        var body = new JsonObject();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        foreach (var field in output.RequiredFields.Take(32)) { timeout.Token.ThrowIfCancellationRequested(); body[field] = new JsonArray(); }
        Assert(MpsSkillExecutionContractValidator.ValidateJsonOutput(output, body.ToJsonString()).Count == 0, "完整字段费用报告结构应通过");
        body["currencies"] = null;
        Assert(MpsSkillExecutionContractValidator.ValidateJsonOutput(output, body.ToJsonString()).Any(item => item.Path.EndsWith("currencies", StringComparison.Ordinal)), "空币种字段不能通过结构校验");
    }

    /// <summary>全文变化或版本不一致会使合同失配，换行转换不会。</summary>
    private static void DocumentsDetectVersionAndContentChanges()
    {
        var source = MpsSkillExecutionContractSet.CreateDefault().Get("video-01-feature-audit");
        const string text = "---\nname: video-01-feature-audit\nversion: 1.0.0\n---\n\n## 工作方式\n\n## 交付\n\n## 阻断条件\n\n## 阶段执行契约\n";
        var contract = new MpsSkillExecutionContract
        {
            SkillId = source.SkillId, SkillVersion = source.SkillVersion,
            DocumentMarkers = source.DocumentMarkers.ToList(), DocumentSha256 = MpsSkillExecutionContractValidator.HashDocument(text)
        };
        Assert(MpsSkillExecutionContractValidator.ValidateDocumentText(contract, text).Count == 0, "合法内存技能文档应通过");
        Assert(MpsSkillExecutionContractValidator.ValidateDocumentText(contract, text.Replace("\n", "\r\n", StringComparison.Ordinal)).Count == 0, "换行风格不应单独导致合同失配");
        Assert(MpsSkillExecutionContractValidator.ValidateDocumentText(contract, text + "未经同步的变更\n").Any(item => item.Code == "document.changed"), "文档正文变更必须更新合同摘要");
        var version = MpsSkillExecutionContractValidator.ValidateDocumentText(contract, text.Replace("version: 1.0.0", "version: 2.0.0", StringComparison.Ordinal));
        Assert(version.Any(item => item.Code == "document.version"), "技能文档和合同版本失配应有明确诊断");
    }

    /// <summary>取消和大小/条目上限不能由合同加载绕过。</summary>
    private static void LimitsAndCancellationAreEnforced()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Throws<OperationCanceledException>(() => MpsSkillExecutionContractValidator.Validate(MpsSkillExecutionContractSet.CreateDefault(), cancellationToken: canceled.Token), "校验必须支持取消");
        Throws<ArgumentOutOfRangeException>(() => MpsSkillExecutionContractValidator.Validate(MpsSkillExecutionContractSet.CreateDefault(), limits: new() { MaxWallTime = Timeout.InfiniteTimeSpan }), "不能配置无界墙钟");
        var set = MpsSkillExecutionContractSet.CreateDefault();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 33; index++) { timeout.Token.ThrowIfCancellationRequested(); set.Contracts[0].DocumentMarkers.Add("marker-" + index); }
        Assert(MpsSkillExecutionContractValidator.Validate(set).Any(item => item.Code == "execution.collection_limit"), "文档标记不能超过条目上限");
        Assert(!MpsSkillExecutionContractValidator.TryLoadJson(new string(' ', 1_048_577)).Success, "过大的合同 JSON 必须拒绝");
    }

    /// <summary>对仓库固定文档进行全文摘要、版本与说明段落校验。</summary>
    private static void RepositoryDocumentsMatchContracts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var root = FindRepositoryRoot(timeout.Token);
        var result = MpsSkillExecutionContractValidator.ValidateDocuments(MpsSkillExecutionContractSet.CreateDefault(), root, cancellationToken: timeout.Token);
        Assert(result.Count == 0, "技能文档与执行合同失配：" + string.Join("; ", result.Select(item => item.Code + ": " + item.Path)));
    }

    /// <summary>只从测试输出目录向上最多八层寻找确定根标记，不扫描目录。</summary>
    private static string FindRepositoryRoot(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("测试仓库根定位超时。");
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && File.Exists(Path.Combine(directory.FullName, "src", "VideoProduction.Core", "SkillRegistry.cs"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("固定父路径中未找到测试仓库。");
    }

    /// <summary>断言受控异常，避免测试静默继续。</summary>
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    /// <summary>用简单异常报告离线断言失败。</summary>
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
