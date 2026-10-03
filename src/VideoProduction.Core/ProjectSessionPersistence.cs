using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>项目会话持久化记录；共享项目状态只出现一次，会话只保存自己的消息。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProjectSessionPersistenceRecord
{
    public string Format { get; init; } = "mps.project.sessions";
    public int SchemaVersion { get; init; } = 1;
    public MpsSharedProjectSnapshot Shared { get; init; } = new();
    public List<MpsSessionRecord> Sessions { get; init; } = [];
    public string? ActiveSessionId { get; init; }
}

/// <summary>会话共用的项目快照；素材、轨道、阶段产物和预算在此只保存一份。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSharedProjectSnapshot
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "未命名项目";
    public long Revision { get; init; }
    public List<MpsAssetReference> Assets { get; init; } = [];
    public List<MpsTrack> Tracks { get; init; } = [];
    public List<MpsStageArtifact> StageArtifacts { get; init; } = [];
    public MpsProjectBudgetSnapshot Budget { get; init; } = new();
    public MpsStageStateSnapshot Stages { get; init; } = new();
}

/// <summary>单条会话记录；不内嵌共享项目集合，避免切换会话产生重复副本。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSessionRecord
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "新会话";
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset LastActivityUtc { get; init; }
    public List<MpsConversationMessage> Messages { get; init; } = [];
}

/// <summary>预算持久化值；恢复时通过预算领域方法重建，保持预算约束。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProjectBudgetSnapshot
{
    public string Currency { get; init; } = "CNY";
    public decimal Limit { get; init; }
    public decimal Reserved { get; init; }
    public decimal Confirmed { get; init; }
    public decimal Unknown { get; init; }
}

/// <summary>十阶段状态的可移植快照，固定包含每个有序阶段一次。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsStageStateSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public List<MpsStageStateRecord> States { get; init; } = [];
}

/// <summary>阶段快照中的单个状态项。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsStageStateRecord
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MpsProductionStage Stage { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MpsStageStatus Status { get; init; }
    public string? Reason { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>会话和十阶段状态的导出、JSON 往返及恢复入口。</summary>
public static class MpsProjectSessionPersistence
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxSessions = 256;
    private const int MaxMessagesPerSession = 16_384;
    private const int MaxMessageLength = 1_000_000;

    /// <summary>导出项目会话；共享状态只复制到 Shared 节点，不挂到每条会话记录。</summary>
    public static MpsProjectSessionPersistenceRecord Export(MpsProjectSessionManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var shared = manager.Shared;
        var sharedSnapshot = new MpsSharedProjectSnapshot
        {
            ProjectId = shared.ProjectId,
            Title = shared.Title,
            Revision = shared.Revision,
            Assets = shared.Assets.Select(CloneAsset).ToList(),
            Tracks = shared.Tracks.Select(CloneTrack).ToList(),
            StageArtifacts = shared.StageArtifacts.Select(CloneStageArtifact).ToList(),
            Budget = CaptureBudget(shared.Budget),
            Stages = ExportSnapshot(shared.Stages)
        };

        var sessions = manager.Sessions.Select(session => new MpsSessionRecord
        {
            Id = session.Id,
            Title = session.Title,
            CreatedUtc = session.CreatedUtc,
            LastActivityUtc = session.LastActivityUtc,
            Messages = session.Messages.Select(CloneMessage).ToList()
        }).ToList();

        var record = new MpsProjectSessionPersistenceRecord
        {
            Shared = sharedSnapshot,
            Sessions = sessions,
            ActiveSessionId = manager.ActiveSessionId
        };
        ValidateRecord(record);
        return record;
    }

    /// <summary>使用稳定、可读的 JSON 保存会话记录。</summary>
    public static string ExportJson(MpsProjectSessionManager manager, JsonSerializerOptions? options = null)
    {
        var record = Export(manager);
        return JsonSerializer.Serialize(record, options ?? new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>从内存记录恢复项目；所有恢复会话引用同一个 Shared 实例。</summary>
    public static MpsProjectSessionManager Restore(MpsProjectSessionPersistenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record);
        var shared = new MpsProjectSharedState
        {
            ProjectId = record.Shared.ProjectId,
            Title = record.Shared.Title
        };
        foreach (var asset in record.Shared.Assets)
            shared.Assets.Add(CloneAsset(asset));
        foreach (var track in record.Shared.Tracks)
            shared.Tracks.Add(CloneTrack(track));
        foreach (var artifact in record.Shared.StageArtifacts)
            shared.StageArtifacts.Add(CloneStageArtifact(artifact));
        RestoreBudget(shared.Budget, record.Shared.Budget);
        RestoreSnapshot(shared.Stages, record.Shared.Stages);
        SetPrivateProperty(shared, nameof(MpsProjectSharedState.Revision), record.Shared.Revision);

        var manager = new MpsProjectSessionManager(shared);
        var sessions = manager.Sessions;
        foreach (var sessionRecord in record.Sessions)
        {
            var session = new MpsProjectSession
            {
                Shared = shared
            };
            SetInitProperty(session, nameof(MpsProjectSession.Id), sessionRecord.Id);
            SetInitProperty(session, nameof(MpsProjectSession.CreatedUtc), sessionRecord.CreatedUtc);
            SetPrivateProperty(session, nameof(MpsProjectSession.LastActivityUtc), sessionRecord.LastActivityUtc);
            session.Title = sessionRecord.Title;
            foreach (var message in sessionRecord.Messages)
                session.Messages.Add(CloneMessage(message));
            sessions.Add(session);
        }

        if (record.ActiveSessionId is not null)
            _ = manager.SwitchSession(record.ActiveSessionId);
        return manager;
    }

    /// <summary>从 JSON 恢复会话记录，并拒绝未知字段或不完整阶段快照。</summary>
    public static MpsProjectSessionManager RestoreJson(string json, JsonSerializerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("会话 JSON 不能为空。", nameof(json));
        var record = JsonSerializer.Deserialize<MpsProjectSessionPersistenceRecord>(json, options)
            ?? throw new JsonException("会话 JSON 为空。");
        return Restore(record);
    }

    /// <summary>导出十阶段状态快照；每个阶段只保留状态、原因和版本信息。</summary>
    public static MpsStageStateSnapshot ExportSnapshot(MpsStageStateMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var values = machine.States.Select(state => new MpsStageStateRecord
        {
            Stage = state.Stage,
            Status = state.Status,
            Reason = state.Reason,
            Revision = state.Revision,
            UpdatedUtc = state.UpdatedUtc
        }).ToList();
        var snapshot = new MpsStageStateSnapshot { States = values };
        ValidateSnapshot(snapshot);
        return snapshot;
    }

    /// <summary>将十阶段快照恢复到已有状态机；不替换状态机对象或其状态集合。</summary>
    public static void RestoreSnapshot(MpsStageStateMachine machine, MpsStageStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        foreach (var item in snapshot.States)
        {
            var state = machine.Get(item.Stage);
            state.Status = item.Status;
            state.Reason = item.Reason;
            state.Revision = item.Revision;
            state.UpdatedUtc = item.UpdatedUtc;
        }
    }

    /// <summary>将状态机快照恢复为新的状态机实例。</summary>
    public static MpsStageStateMachine RestoreSnapshot(MpsStageStateSnapshot snapshot)
    {
        var machine = new MpsStageStateMachine();
        RestoreSnapshot(machine, snapshot);
        return machine;
    }

    private static MpsProjectBudgetSnapshot CaptureBudget(MpsProjectBudget budget) => new()
    {
        Currency = budget.Currency,
        Limit = budget.Limit,
        Reserved = budget.Reserved,
        Confirmed = budget.Confirmed,
        Unknown = budget.Unknown
    };

    private static void RestoreBudget(MpsProjectBudget budget, MpsProjectBudgetSnapshot snapshot)
    {
        if (snapshot.Limit < 0 || snapshot.Reserved < 0 || snapshot.Confirmed < 0 || snapshot.Unknown < 0 ||
            snapshot.Reserved + snapshot.Confirmed + snapshot.Unknown > snapshot.Limit)
            throw new InvalidDataException("预算快照金额不满足项目预算约束。");
        if (string.IsNullOrWhiteSpace(snapshot.Currency) || snapshot.Currency.Length > 16 || snapshot.Currency.Any(char.IsControl))
            throw new InvalidDataException("预算币种无效。");
        budget.Currency = snapshot.Currency;
        budget.Limit = snapshot.Limit;
        if (snapshot.Reserved + snapshot.Confirmed > 0)
            budget.Reserve(snapshot.Reserved + snapshot.Confirmed);
        if (snapshot.Confirmed > 0)
            budget.Confirm(snapshot.Confirmed);
        if (snapshot.Unknown > 0)
            budget.RecordUnknown(snapshot.Unknown);
    }

    private static void ValidateRecord(MpsProjectSessionPersistenceRecord record)
    {
        if (record.Format != "mps.project.sessions" || record.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("会话记录格式或版本不受支持。");
        if (record.Shared is null || string.IsNullOrWhiteSpace(record.Shared.ProjectId) ||
            record.Shared.ProjectId.Length > 128 || record.Shared.ProjectId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(record.Shared.Title) || record.Shared.Title.Length > 200 ||
            record.Shared.Title.Any(char.IsControl) || record.Shared.Revision < 0)
            throw new InvalidDataException("共享项目快照无效。");
        if (record.Sessions is null || record.Sessions.Count > MaxSessions)
            throw new InvalidDataException("项目会话数量超过上限。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in record.Sessions)
        {
            if (session is null || string.IsNullOrWhiteSpace(session.Id) || session.Id.Length > 128 ||
                session.Id.Any(char.IsControl) || !ids.Add(session.Id) || string.IsNullOrWhiteSpace(session.Title) ||
                session.Title.Length > 200 || session.Title.Any(char.IsControl) || session.CreatedUtc == default ||
                session.LastActivityUtc == default || session.Messages is null || session.Messages.Count > MaxMessagesPerSession)
                throw new InvalidDataException("会话记录无效或重复。");
            foreach (var message in session.Messages)
            {
                if (message is null || string.IsNullOrWhiteSpace(message.Id) || message.Id.Length > 128 ||
                    message.Id.Any(char.IsControl) || string.IsNullOrWhiteSpace(message.Content) ||
                    message.Content.Length > MaxMessageLength || message.Content.Any(char.IsControl) || message.CreatedUtc == default ||
                    !Enum.IsDefined(message.Role))
                    throw new InvalidDataException("会话消息无效。");
            }
        }
        if (record.ActiveSessionId is not null && !ids.Contains(record.ActiveSessionId))
            throw new InvalidDataException("当前会话引用不存在。");
        if (record.Shared.Assets is null || record.Shared.Tracks is null || record.Shared.StageArtifacts is null ||
            record.Shared.Budget is null || record.Shared.Stages is null)
            throw new InvalidDataException("共享项目集合不能为空。");
        ValidateSnapshot(record.Shared.Stages);
    }

    private static void ValidateSnapshot(MpsStageStateSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != CurrentSchemaVersion || snapshot.States is null ||
            snapshot.States.Count != Enum.GetValues<MpsProductionStage>().Length)
            throw new InvalidDataException("十阶段快照版本或数量无效。");
        var stages = new HashSet<MpsProductionStage>();
        foreach (var state in snapshot.States)
        {
            if (state is null || !Enum.IsDefined(state.Stage) || !stages.Add(state.Stage) ||
                !Enum.IsDefined(state.Status) || state.Revision < 0 || state.UpdatedUtc == default ||
                (state.Status is MpsStageStatus.Failed or MpsStageStatus.Invalidated) && string.IsNullOrWhiteSpace(state.Reason) ||
                state.Reason is not null && (state.Reason.Length > 4_000 || state.Reason.Any(char.IsControl)))
                throw new InvalidDataException("阶段状态快照无效或重复。");
        }
    }

    private static void SetInitProperty<T>(object instance, string propertyName, T value) => SetProperty(instance, propertyName, value, includeNonPublic: false);

    private static void SetPrivateProperty<T>(object instance, string propertyName, T value) => SetProperty(instance, propertyName, value, includeNonPublic: true);

    private static void SetProperty<T>(object instance, string propertyName, T value, bool includeNonPublic)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public;
        if (includeNonPublic) flags |= BindingFlags.NonPublic;
        var property = instance.GetType().GetProperty(propertyName, flags)
            ?? throw new InvalidOperationException($"恢复属性不存在：{propertyName}。");
        property.SetValue(instance, value);
    }

    private static MpsAssetReference CloneAsset(MpsAssetReference source) => new()
    {
        Id = source.Id,
        Path = source.Path,
        Sha256 = source.Sha256,
        SizeBytes = source.SizeBytes,
        Media = source.Media,
        Source = source.Source,
        IndexedUtc = source.IndexedUtc,
        IsReachable = source.IsReachable
    };

    private static MpsStageArtifact CloneStageArtifact(MpsStageArtifact source) => new()
    {
        Id = source.Id,
        Stage = source.Stage,
        Path = source.Path,
        ContentHash = source.ContentHash,
        CreatedUtc = source.CreatedUtc
    };

    private static MpsConversationMessage CloneMessage(MpsConversationMessage source) => new()
    {
        Id = source.Id,
        Role = source.Role,
        Content = source.Content,
        CreatedUtc = source.CreatedUtc
    };

    private static MpsTrack CloneTrack(MpsTrack source)
    {
        var clone = new MpsTrack
        {
            Id = source.Id,
            Kind = source.Kind,
            Order = source.Order,
            ParentTrackId = source.ParentTrackId,
            Muted = source.Muted,
            Hidden = source.Hidden
        };
        foreach (var clip in source.Clips)
            clone.Clips.Add(CloneClip(clip));
        return clone;
    }

    private static MpsClip CloneClip(MpsClip source) => new()
    {
        Id = source.Id,
        TrackId = source.TrackId,
        AssetId = source.AssetId,
        TimelineStart = CloneTime(source.TimelineStart),
        SourceIn = CloneTime(source.SourceIn),
        SourceOut = CloneTime(source.SourceOut),
        SpeedNum = source.SpeedNum,
        SpeedDen = source.SpeedDen,
        Properties = CloneProperties(source.Properties)
    };

    private static MpsTime CloneTime(MpsTime source) => new() { Num = source.Num, Den = source.Den };

    private static MpsClipProperties CloneProperties(MpsClipProperties source) => new()
    {
        GainDb = source.GainDb,
        Opacity = source.Opacity,
        LipSynced = source.LipSynced,
        Transform = source.Transform is null ? null : new MpsTransform
        {
            X = source.Transform.X,
            Y = source.Transform.Y,
            Width = source.Transform.Width,
            Height = source.Transform.Height,
            Rotation = source.Transform.Rotation
        },
        Subtitle = source.Subtitle is null ? null : new MpsSubtitleProperties
        {
            Text = source.Subtitle.Text,
            FontFamily = source.Subtitle.FontFamily,
            FontSize = source.Subtitle.FontSize,
            Color = source.Subtitle.Color,
            Alignment = source.Subtitle.Alignment
        },
        PresenterOverlay = source.PresenterOverlay is null ? null : new MpsPresenterOverlay
        {
            KeyColor = source.PresenterOverlay.KeyColor,
            Similarity = source.PresenterOverlay.Similarity,
            Blend = source.PresenterOverlay.Blend
        }
    };
}

/// <summary>从领域对象调用持久化操作的扩展入口。</summary>
public static class MpsProjectSessionPersistenceExtensions
{
    public static MpsProjectSessionPersistenceRecord ExportPersistenceRecord(this MpsProjectSessionManager manager) =>
        MpsProjectSessionPersistence.Export(manager);

    public static string ExportPersistenceJson(this MpsProjectSessionManager manager, JsonSerializerOptions? options = null) =>
        MpsProjectSessionPersistence.ExportJson(manager, options);

    public static MpsStageStateSnapshot ExportPersistenceSnapshot(this MpsStageStateMachine machine) =>
        MpsProjectSessionPersistence.ExportSnapshot(machine);

    public static void RestorePersistenceSnapshot(this MpsStageStateMachine machine, MpsStageStateSnapshot snapshot) =>
        MpsProjectSessionPersistence.RestoreSnapshot(machine, snapshot);
}
