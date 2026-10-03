using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>项目范围的共享状态；会话只保存对话，不复制这些集合。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProjectSharedState
{
    /// <summary>共享项目的稳定标识。</summary>
    public string ProjectId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>项目标题。</summary>
    public string Title { get; set; } = "未命名项目";

    /// <summary>可选的现有项目根文档；绑定后资产和轨道直接使用其集合。</summary>
    [JsonIgnore]
    public MpsProjectDocument? SourceDocument { get; }

    /// <summary>所有会话共用的素材引用。</summary>
    [JsonInclude]
    public List<MpsAssetReference> Assets { get; private set; } = [];

    /// <summary>所有会话共用的时间线轨道。</summary>
    [JsonInclude]
    public List<MpsTrack> Tracks { get; private set; } = [];

    /// <summary>所有会话共用的阶段产物索引。</summary>
    [JsonInclude]
    public List<MpsStageArtifact> StageArtifacts { get; private set; } = [];

    /// <summary>所有会话共用的预算账本。</summary>
    [JsonInclude]
    public MpsProjectBudget Budget { get; private set; } = new();

    /// <summary>所有会话共用的十阶段状态。</summary>
    [JsonInclude]
    public MpsStageStateMachine Stages { get; private set; } = new();

    /// <summary>共享状态发生有效编辑时递增，便于会话检测变化。</summary>
    public long Revision { get; private set; }

    public MpsProjectSharedState() { }

    /// <summary>从已有项目根文档建立共享视图，不复制媒体或时间线集合。</summary>
    public MpsProjectSharedState(MpsProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ProjectId = document.Id;
        Title = document.Title;
        SourceDocument = document;
        Assets = document.Assets;
        Tracks = document.Tracks;
    }

    /// <summary>标记共享项目发生编辑；不会创建素材或时间线副本。</summary>
    public long Touch() => ++Revision;

    /// <summary>登记一个阶段产物并更新共享版本。</summary>
    public void AddStageArtifact(MpsStageArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(artifact.Id)) throw new ArgumentException("阶段产物标识不能为空。", nameof(artifact));
        if (StageArtifacts.Any(item => string.Equals(item.Id, artifact.Id, StringComparison.Ordinal)))
            throw new InvalidOperationException("阶段产物标识重复。");
        StageArtifacts.Add(artifact);
        Touch();
    }
}

/// <summary>阶段产物只保存项目内相对引用或摘要，不保存凭据和签名地址。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsStageArtifact
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public MpsProductionStage Stage { get; init; }
    public string Path { get; set; } = "";
    public string? ContentHash { get; set; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>项目预算按预留、已确认和未知金额分开记录，避免未知费用被当作零。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProjectBudget
{
    public string Currency { get; set; } = "CNY";
    public decimal Limit { get; set; }
    [JsonInclude]
    public decimal Reserved { get; private set; }
    [JsonInclude]
    public decimal Confirmed { get; private set; }
    [JsonInclude]
    public decimal Unknown { get; private set; }

    /// <summary>预算剩余值；未知费用不被误算成可用余额。</summary>
    [JsonIgnore]
    public decimal Remaining => Limit - Reserved - Confirmed - Unknown;

    /// <summary>预留预算；超出上限时拒绝。</summary>
    public void Reserve(decimal amount)
    {
        ValidateAmount(amount);
        if (amount > Remaining) throw new InvalidOperationException("项目预算不足。");
        Reserved += amount;
    }

    /// <summary>将预留转为已确认费用。</summary>
    public void Confirm(decimal amount)
    {
        ValidateAmount(amount);
        if (amount > Reserved) throw new InvalidOperationException("确认费用不能超过预留金额。");
        Reserved -= amount;
        Confirmed += amount;
    }

    /// <summary>释放尚未使用的预留金额。</summary>
    public void Release(decimal amount)
    {
        ValidateAmount(amount);
        if (amount > Reserved) throw new InvalidOperationException("释放金额不能超过预留金额。");
        Reserved -= amount;
    }

    /// <summary>记录尚未确认的费用；未知费用仍占用预算。</summary>
    public void RecordUnknown(decimal amount)
    {
        ValidateAmount(amount);
        if (amount > Remaining) throw new InvalidOperationException("项目预算不足。");
        Unknown += amount;
    }

    private static void ValidateAmount(decimal amount)
    {
        if (amount <= 0 || amount > 1_000_000_000m) throw new ArgumentOutOfRangeException(nameof(amount));
    }
}

/// <summary>一次独立的项目对话会话；会话通过 Shared 引用同一份项目状态。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProjectSession
{
    [JsonIgnore]
    public MpsProjectSharedState Shared { get; internal set; } = null!;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新会话";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;
    [JsonInclude]
    public List<MpsConversationMessage> Messages { get; private set; } = [];

    /// <summary>追加消息；消息只属于当前会话，不会复制项目素材。</summary>
    public MpsConversationMessage Append(MpsConversationRole role, string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 1_000_000 || content.Any(char.IsControl))
            throw new ArgumentException("会话消息为空、过长或包含控制字符。", nameof(content));
        var message = new MpsConversationMessage { Role = role, Content = content };
        Messages.Add(message);
        LastActivityUtc = message.CreatedUtc;
        return message;
    }

    public MpsConversationMessage AddUserMessage(string content) => Append(MpsConversationRole.User, content);
    public MpsConversationMessage AddAssistantMessage(string content) => Append(MpsConversationRole.Assistant, content);
}

public enum MpsConversationRole
{
    User,
    Assistant,
    System,
    Tool
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsConversationMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public MpsConversationRole Role { get; init; }
    public string Content { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>项目会话容器；切换会话只切换引用，不复制媒体、轨道或预算。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class MpsProjectSessionManager : IJsonOnDeserialized
{
    private const int MaxSessions = 256;
    // 持久化辅助层按此稳定字段读取会话集合；属性 setter 仅用于 JSON 重开。
    private List<MpsProjectSession> _sessions = [];

    [JsonInclude]
    public MpsProjectSharedState Shared { get; private set; }
    [JsonInclude]
    public List<MpsProjectSession> Sessions
    {
        get => _sessions;
        private set => _sessions = value ?? [];
    }
    [JsonInclude]
    public string? ActiveSessionId { get; private set; }
    public MpsProjectSession? ActiveSession => ActiveSessionId is null ? null : GetSession(ActiveSessionId);

    public MpsProjectSessionManager(string title = "未命名项目")
        : this(new MpsProjectSharedState { Title = title }) { }

    /// <summary>供项目 JSON 重开使用的空项目构造器。</summary>
    public MpsProjectSessionManager() : this(new MpsProjectSharedState()) { }

    public MpsProjectSessionManager(MpsProjectSharedState shared)
    {
        Shared = shared ?? throw new ArgumentNullException(nameof(shared));
        if (string.IsNullOrWhiteSpace(shared.ProjectId)) throw new ArgumentException("项目标识不能为空。", nameof(shared));
    }

    /// <summary>绑定已有项目根文档；会话仍只持有共享视图，不复制媒体或时间线。</summary>
    public MpsProjectSessionManager(MpsProjectDocument document)
        : this(new MpsProjectSharedState(document)) { }

    /// <summary>创建独立对话会话并绑定同一共享项目状态。</summary>
    public MpsProjectSession CreateSession(string title = "新会话")
    {
        if (Sessions.Count >= MaxSessions) throw new InvalidOperationException("项目会话数量超过上限。");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl))
            throw new ArgumentException("会话标题无效。", nameof(title));
        var session = new MpsProjectSession { Title = title, Shared = Shared };
        Sessions.Add(session);
        ActiveSessionId ??= session.Id;
        return session;
    }

    /// <summary>切换到已有会话；不会修改共享时间线。</summary>
    public MpsProjectSession SwitchSession(string sessionId)
    {
        var session = GetSession(sessionId) ?? throw new KeyNotFoundException("会话不存在。");
        ActiveSessionId = session.Id;
        return session;
    }

    public MpsProjectSession? GetSession(string sessionId) =>
        Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));

    /// <summary>删除会话；项目共享状态和其他会话保持不变。</summary>
    public bool RemoveSession(string sessionId)
    {
        var index = Sessions.FindIndex(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
        if (index < 0) return false;
        Sessions.RemoveAt(index);
        if (string.Equals(ActiveSessionId, sessionId, StringComparison.Ordinal))
            ActiveSessionId = Sessions.Count == 0 ? null : Sessions[Math.Min(index, Sessions.Count - 1)].Id;
        return true;
    }

    /// <summary>JSON 重开后重新绑定会话的共享状态引用。</summary>
    public void OnDeserialized()
    {
        foreach (var session in Sessions)
            session.Shared = Shared;
    }
}

/// <summary>项目级别的便捷门面；同一对象同时提供共享状态与会话管理。</summary>
public sealed class MpsProject : MpsProjectSessionManager
{
    public MpsProject() : base() { }
    public MpsProject(string title = "未命名项目") : base(title) { }
    public MpsProject(MpsProjectSharedState shared) : base(shared) { }
    public MpsProject(MpsProjectDocument document) : base(document) { }
}

/// <summary>短名称兼容门面，供桌面层以 ProjectSessionStore 表达会话存储。</summary>
public sealed class ProjectSessionStore : MpsProjectSessionManager
{
    public ProjectSessionStore() : base() { }
    public ProjectSessionStore(string title = "未命名项目") : base(title) { }
    public ProjectSessionStore(MpsProjectSharedState shared) : base(shared) { }
    public ProjectSessionStore(MpsProjectDocument document) : base(document) { }
}
