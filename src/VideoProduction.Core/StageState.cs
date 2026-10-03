using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>第一版制作链的十个有序阶段。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MpsProductionStage
{
    FeatureAudit,
    IntroductionOrder,
    ScriptStoryboard,
    ModelSelection,
    Presenter,
    Narration,
    LipSync,
    Compositing,
    QualityReview,
    CostDelivery
}

/// <summary>阶段生命周期；Invalidated 表示输入变化导致旧产物不可继续使用。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MpsStageStatus
{
    NotStarted,
    InProgress,
    PendingReview,
    Passed,
    Failed,
    Invalidated
}

/// <summary>单个阶段的状态、版本和可审计原因。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class MpsStageState
{
    public MpsProductionStage Stage { get; init; }
    [JsonInclude]
    public MpsStageStatus Status { get; internal set; } = MpsStageStatus.NotStarted;
    [JsonInclude]
    public string? Reason { get; internal set; }
    [JsonInclude]
    public long Revision { get; internal set; }
    [JsonInclude]
    public DateTimeOffset UpdatedUtc { get; internal set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsTerminal => Status is MpsStageStatus.Passed or MpsStageStatus.Failed;
}

/// <summary>十阶段状态机，按有向依赖传播上游变更，避免无关阶段被失效。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class MpsStageStateMachine
{
    public MpsStageStateMachine() => States = CreateInitialStates();

    /// <summary>按固定顺序读取全部十阶段状态。</summary>
    [JsonInclude]
    public List<MpsStageState> States { get; private set; }

    public MpsStageState this[MpsProductionStage stage] => Get(stage);

    public MpsStageState Get(MpsProductionStage stage)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        var state = States.FirstOrDefault(item => item.Stage == stage);
        return state ?? throw new InvalidDataException($"阶段状态缺失：{stage}。");
    }

    /// <summary>推进某阶段状态；失败和失效必须给出原因。</summary>
    public MpsStageState SetStatus(MpsProductionStage stage, MpsStageStatus status, string? reason = null)
    {
        var state = Get(stage);
        ValidateReason(status, reason);
        state.Status = status;
        state.Reason = NormalizeReason(reason);
        state.Revision++;
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        return state;
    }

    /// <summary>上游内容改变时，仅将依赖该阶段的下游阶段标为失效。</summary>
    public IReadOnlyList<MpsStageState> InvalidateDownstream(MpsProductionStage upstream, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("失效原因不能为空。", nameof(reason));
        var affected = new List<MpsStageState>();
        var fullReason = $"上游阶段 {upstream} 变更：{NormalizeReason(reason)}";
        foreach (var stage in Enum.GetValues<MpsProductionStage>())
        {
            if (stage <= upstream || !DependsOn(stage, upstream)) continue;
            var state = Get(stage);
            if (state.Status == MpsStageStatus.Invalidated && string.Equals(state.Reason, fullReason, StringComparison.Ordinal)) continue;
            state.Status = MpsStageStatus.Invalidated;
            state.Reason = fullReason;
            state.Revision++;
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            affected.Add(state);
        }
        return affected;
    }

    /// <summary>设置阶段为进行中并使其下游旧产物失效。</summary>
    public IReadOnlyList<MpsStageState> Begin(MpsProductionStage stage, string? reason = null)
    {
        var state = SetStatus(stage, MpsStageStatus.InProgress, reason);
        return InvalidateDownstream(stage, $"阶段开始重新计算（版本 {state.Revision}）");
    }

    /// <summary>将阶段标记通过；通过不自动改变其他阶段状态。</summary>
    public MpsStageState Pass(MpsProductionStage stage, string? reason = null) => SetStatus(stage, MpsStageStatus.Passed, reason);

    public MpsStageState Fail(MpsProductionStage stage, string reason) => SetStatus(stage, MpsStageStatus.Failed, reason);
    public MpsStageState RequestReview(MpsProductionStage stage, string? reason = null) => SetStatus(stage, MpsStageStatus.PendingReview, reason);

    /// <summary>判断后置阶段是否直接或间接依赖指定上游阶段。</summary>
    public static bool DependsOn(MpsProductionStage stage, MpsProductionStage upstream) =>
        stage > upstream;

    private static List<MpsStageState> CreateInitialStates() =>
        Enum.GetValues<MpsProductionStage>()
            .Select(stage => new MpsStageState { Stage = stage })
            .ToList();

    private static void ValidateReason(MpsStageStatus status, string? reason)
    {
        if (status is MpsStageStatus.Failed or MpsStageStatus.Invalidated && string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("失败或失效状态必须记录原因。", nameof(reason));
        if (reason is not null && (reason.Length > 4_000 || reason.Any(char.IsControl)))
            throw new ArgumentException("阶段原因过长或包含控制字符。", nameof(reason));
    }

    private static string? NormalizeReason(string? reason) => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
}

/// <summary>简短别名，便于领域层直接使用 StageState。</summary>
public sealed class StageState : MpsStageState { }

/// <summary>简短别名，便于领域层直接使用 StageStateMachine。</summary>
public sealed class StageStateMachine : MpsStageStateMachine { }

/// <summary>阶段名称的短别名。</summary>
public enum ProductionStage
{
    FeatureAudit = MpsProductionStage.FeatureAudit,
    IntroductionOrder = MpsProductionStage.IntroductionOrder,
    ScriptStoryboard = MpsProductionStage.ScriptStoryboard,
    ModelSelection = MpsProductionStage.ModelSelection,
    Presenter = MpsProductionStage.Presenter,
    Narration = MpsProductionStage.Narration,
    LipSync = MpsProductionStage.LipSync,
    Compositing = MpsProductionStage.Compositing,
    QualityReview = MpsProductionStage.QualityReview,
    CostDelivery = MpsProductionStage.CostDelivery
}

/// <summary>阶段状态的短别名。</summary>
public enum StageStatus
{
    NotStarted = MpsStageStatus.NotStarted,
    InProgress = MpsStageStatus.InProgress,
    PendingReview = MpsStageStatus.PendingReview,
    Passed = MpsStageStatus.Passed,
    Failed = MpsStageStatus.Failed,
    Invalidated = MpsStageStatus.Invalidated
}
