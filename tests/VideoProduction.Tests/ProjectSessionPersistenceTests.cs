using System.Text.Json;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>会话持久化与十阶段快照的离线回归；不访问网络或付费接口。</summary>
public static class ProjectSessionPersistenceTests
{
    /// <summary>验证共享集合、阶段失效传播及 JSON 往返。</summary>
    public static void Run()
    {
        SessionSwitchKeepsSharedCollections();
        UpstreamInvalidationOnlyAffectsDownstream();
        JsonRoundTripRestoresStableIdsAndStages();
    }

    private static void SessionSwitchKeepsSharedCollections()
    {
        var manager = new MpsProjectSessionManager("持久化测试");
        manager.Shared.Assets.Add(new MpsAssetReference { Id = "asset-1", Path = "assets/source/a.mp4" });
        manager.Shared.Tracks.Add(new MpsTrack { Id = "screen", Kind = MpsTrackKind.Screen, Order = 0 });
        manager.Shared.Budget.Limit = 100;
        manager.Shared.Budget.Reserve(10);
        var first = manager.CreateSession("第一轮");
        var second = manager.CreateSession("第二轮");
        first.AddUserMessage("先检查素材");
        manager.SwitchSession(second.Id);
        Assert(ReferenceEquals(first.Shared, second.Shared), "会话切换必须引用同一共享状态");
        Assert(ReferenceEquals(first.Shared.Assets, second.Shared.Assets), "素材集合不能按会话复制");
        Assert(ReferenceEquals(first.Shared.Tracks, second.Shared.Tracks), "轨道集合不能按会话复制");
        Assert(ReferenceEquals(first.Shared.Budget, second.Shared.Budget), "预算不能按会话复制");
        Assert(second.Messages.Count == 0 && first.Messages.Count == 1, "消息必须只属于各自会话");
    }

    private static void UpstreamInvalidationOnlyAffectsDownstream()
    {
        var machine = new MpsStageStateMachine();
        machine.Pass(MpsProductionStage.FeatureAudit);
        machine.Pass(MpsProductionStage.IntroductionOrder);
        machine.Pass(MpsProductionStage.ScriptStoryboard);
        machine.Begin(MpsProductionStage.IntroductionOrder, "重新检查顺序");
        Assert(machine[MpsProductionStage.FeatureAudit].Status == MpsStageStatus.Passed, "上游之前的阶段不应失效");
        Assert(machine[MpsProductionStage.IntroductionOrder].Status == MpsStageStatus.InProgress, "变更阶段应进入进行中");
        Assert(machine[MpsProductionStage.ScriptStoryboard].Status == MpsStageStatus.Invalidated, "直接下游应失效");
        Assert(machine[MpsProductionStage.CostDelivery].Status == MpsStageStatus.Invalidated, "后置下游应失效");
    }

    private static void JsonRoundTripRestoresStableIdsAndStages()
    {
        var manager = new MpsProjectSessionManager("往返项目");
        manager.Shared.Assets.Add(new MpsAssetReference { Id = "asset-1", Path = "assets/source/a.mp4" });
        manager.Shared.Budget.Limit = 100;
        manager.Shared.Budget.Reserve(20);
        manager.Shared.Budget.Confirm(5);
        manager.Shared.Budget.RecordUnknown(3);
        manager.Shared.Stages.Pass(MpsProductionStage.FeatureAudit, "证据已核对");
        var session = manager.CreateSession("主会话");
        session.AddUserMessage("生成初版脚本");
        var json = MpsProjectSessionPersistence.ExportJson(manager);
        var restored = MpsProjectSessionPersistence.RestoreJson(json);
        Assert(restored.ActiveSessionId == session.Id, "JSON 往返必须保留当前会话 ID");
        Assert(restored.Sessions.Single().Id == session.Id, "JSON 往返必须保留会话稳定 ID");
        Assert(restored.Sessions.Single().Messages.Single().Content == "生成初版脚本", "JSON 往返必须保留会话消息");
        Assert(restored.Shared.Assets.Count == 1 && restored.Shared.Assets[0].Path == "assets/source/a.mp4", "JSON 往返必须保留共享素材");
        Assert(restored.Shared.Budget.Reserved == 15 && restored.Shared.Budget.Confirmed == 5 && restored.Shared.Budget.Unknown == 3, "JSON 往返必须保留预算分项");
        Assert(restored.Shared.Stages[MpsProductionStage.FeatureAudit].Status == MpsStageStatus.Passed, "JSON 往返必须保留阶段状态");
        Assert(ReferenceEquals(restored.Shared, restored.Sessions.Single().Shared), "恢复后的会话必须共享同一项目状态");
        var stageJson = JsonSerializer.Serialize(restored.Shared.Stages.ExportPersistenceSnapshot());
        var stageSnapshot = JsonSerializer.Deserialize<MpsStageStateSnapshot>(stageJson)!;
        var machine = MpsProjectSessionPersistence.RestoreSnapshot(stageSnapshot);
        Assert(machine[MpsProductionStage.FeatureAudit].Reason == "证据已核对", "阶段快照 JSON 往返必须保留原因");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
