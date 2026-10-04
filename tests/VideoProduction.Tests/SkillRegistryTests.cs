using System.Text.Json;
using System.Text.Json.Nodes;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>技能注册表、阶段依赖和计划状态的完全离线合同测试。</summary>
public static class SkillRegistryTests
{
    /// <summary>执行固定数量的快速内存断言，不读取技能目录或访问网络。</summary>
    public static void Run()
    {
        DefaultRegistryHasElevenEntries();
        RegistryJsonRoundTrip();
        RegistryDiagnosticsAreActionable();
        PlanEnforcesOrderAndTracksArtifacts();
        PlanRoundTripAndStateMigration();
    }

    private static void DefaultRegistryHasElevenEntries()
    {
        var registry = MpsSkillRegistry.CreateDefault();
        Assert(registry.Skills.Count == 11, "默认注册表必须固定包含 11 个技能入口");
        Assert(registry.Skills.Select(skill => skill.Id).SequenceEqual(MpsSkillRegistry.RequiredSkillIds), "技能入口顺序不稳定");
        Assert(registry.Validate().Count == 0, "默认注册表不应产生校验诊断");
        Assert(registry.Get("video-production-series").Stage is null, "VideoProduction 总入口不能绑定具体阶段");
        Assert(registry.Get("video-10-cost-delivery").Dependencies.SequenceEqual(["video-09-quality-review"]), "最后阶段依赖不正确");
    }

    private static void RegistryJsonRoundTrip()
    {
        var original = MpsSkillRegistry.CreateDefault();
        var json = original.ToJson();
        var restored = MpsSkillRegistry.LoadJson(json);
        Assert(restored.Skills.Count == 11, "注册表 JSON 往返丢失条目");
        Assert(restored.Skills[3].ExpectedArtifacts.Contains("shot-list.json"), "注册表 JSON 往返丢失产物契约");
        Assert(restored.ToJson() == json, "注册表 JSON 往返应保持确定性输出");
    }

    private static void RegistryDiagnosticsAreActionable()
    {
        var node = JsonNode.Parse(MpsSkillRegistry.CreateDefault().ToJson())!.AsObject();
        var skills = node["skills"]!.AsArray();
        skills.RemoveAt(1);
        skills[0]!["id"] = "unknown-skill";
        var missing = MpsSkillRegistry.TryLoadJson(node.ToJsonString());
        Assert(!missing.Success, "缺失、重复或未知技能不应通过加载");
        Assert(missing.Diagnostics.Any(item => item.Code == "skill.unknown"), "未知技能应有稳定诊断代码");
        Assert(missing.Diagnostics.Any(item => item.Code == "skill.missing"), "缺失技能应有稳定诊断代码");

        var unknown = JsonNode.Parse(MpsSkillRegistry.CreateDefault().ToJson())!.AsObject();
        unknown["unexpected"] = true;
        var unknownResult = MpsSkillRegistry.TryLoadJson(unknown.ToJsonString());
        Assert(!unknownResult.Success && unknownResult.Diagnostics.Any(item => item.Code == "json.invalid"), "未知 JSON 字段应被拒绝并可诊断");

        var duplicate = JsonNode.Parse(MpsSkillRegistry.CreateDefault().ToJson())!.AsObject();
        duplicate["skills"]!.AsArray()[1]!["id"] = "video-02-narrative-plan";
        var duplicateResult = MpsSkillRegistry.TryLoadJson(duplicate.ToJsonString());
        Assert(duplicateResult.Diagnostics.Any(item => item.Code == "skill.duplicate"), "重复技能应有稳定诊断代码");
    }

    private static void PlanEnforcesOrderAndTracksArtifacts()
    {
        var registry = MpsSkillRegistry.CreateDefault();
        var plan = MpsProductionPlan.CreateDefault(registry);
        Assert(plan.Validate(registry).Count == 0, "默认阶段计划不应产生诊断");
        Throws<InvalidOperationException>(() => plan.SetStatus("video-02-narrative-plan", MpsStageStatus.InProgress, registry: registry), "未通过前置阶段不能开始");

        plan.SetStatus("video-01-feature-audit", MpsStageStatus.InProgress, registry: registry);
        plan.AddArtifact(new MpsPlanArtifact
        {
            Id = "audit-1",
            SkillId = "video-01-feature-audit",
            Kind = "feature-audit",
            Path = "records/feature-audit.json"
        }, registry);
        plan.SetStatus("video-01-feature-audit", MpsStageStatus.PendingReview, registry: registry);
        plan.SetStatus("video-01-feature-audit", MpsStageStatus.Passed, registry: registry);
        plan.SetStatus("video-02-narrative-plan", MpsStageStatus.InProgress, registry: registry);
        Assert(plan.Get("video-02-narrative-plan").Status == MpsStageStatus.InProgress, "依赖通过后阶段应能开始");
        plan.AddBlocker("video-02-narrative-plan", new MpsPlanBlocker { Code = "evidence.pending", Message = "证据待复核" });
        Assert(plan.Get("video-02-narrative-plan").Blockers!.Count == 1, "阻断原因未登记");
        plan.SetStatus("video-02-narrative-plan", MpsStageStatus.PendingReview, "证据待复核", registry);
        Assert(plan.Validate(registry).Count == 0, "合法阶段计划不应产生诊断");
    }

    private static void PlanRoundTripAndStateMigration()
    {
        var registry = MpsSkillRegistry.CreateDefault();
        var plan = MpsProductionPlan.CreateDefault(registry);
        plan.SetStatus("video-01-feature-audit", MpsStageStatus.InProgress, registry: registry);
        plan.AddBlocker("video-01-feature-audit", new MpsPlanBlocker { Code = "offline", Message = "离线等待复核" });
        var restored = MpsProductionPlan.LoadJson(plan.ToJson(), registry);
        Assert(restored.Get("video-01-feature-audit").Blockers!.Single().Code == "offline", "阶段计划 JSON 往返丢失阻断原因");

        var missing = JsonNode.Parse(plan.ToJson())!.AsObject();
        missing["entries"]!.AsArray()[0]!.AsObject().Remove("artifact_refs");
        var missingResult = MpsProductionPlan.TryLoadJson(missing.ToJsonString(), registry);
        Assert(missingResult.Diagnostics.Any(item => item.Code == "plan.artifacts_missing"), "缺失产物引用字段应有稳定诊断代码");

        var machine = new MpsStageStateMachine();
        machine.Pass(MpsProductionStage.FeatureAudit, "审计完成");
        var migrated = MpsProductionPlan.FromStageStateMachine(machine, registry);
        Assert(migrated.Get("video-01-feature-audit").Status == MpsStageStatus.Passed, "旧十阶段状态迁移失败");
        Assert(migrated.Get("video-01-feature-audit").InvalidatedBy == "审计完成", "迁移应保留旧状态原因");
        Assert(migrated.Get("video-production-series").Status == MpsStageStatus.InProgress, "未完成十阶段时总入口应保持进行中");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); throw new InvalidOperationException(message); }
        catch (T) { }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
