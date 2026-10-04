using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>授权探测的执行状态；Unknown 表示必须先查询旧任务，禁止自动重提。</summary>
[JsonConverter(typeof(MpsProbeExecutionStateJsonConverter))]
public enum MpsProbeExecutionState
{
    IntentRegistered,
    Submitted,
    Succeeded,
    Failed,
    Unknown
}

/// <summary>授权探测预检结论，界面可据此显示需要用户采取的动作。</summary>
[JsonConverter(typeof(MpsProbePreflightDecisionJsonConverter))]
public enum MpsProbePreflightDecision
{
    Allowed,
    NotAuthorized,
    Expired,
    InvalidRequest,
    RequestLimitExceeded,
    SingleLimitExceeded,
    CumulativeLimitExceeded,
    UnknownCost,
    RecoveryRequired
}

/// <summary>以小写蛇形序列化探测状态。</summary>
public sealed class MpsProbeExecutionStateJsonConverter : JsonStringEnumConverter
{
    /// <summary>禁止数字枚举值进入项目文件。</summary>
    public MpsProbeExecutionStateJsonConverter() : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) { }
}

/// <summary>以小写蛇形序列化预检结论。</summary>
public sealed class MpsProbePreflightDecisionJsonConverter : JsonStringEnumConverter
{
    /// <summary>固定预检文案对应的稳定值。</summary>
    public MpsProbePreflightDecisionJsonConverter() : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) { }
}

/// <summary>一次小额探测的授权范围；凭据只通过账号别名引用。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProbeAuthorization
{
    public int SchemaVersion { get; set; } = 1;
    public string AuthorizationId { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string AccountAlias { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public MpsCapabilityKind Capability { get; set; }
    public MpsMeasurementLevel Level { get; set; }
    public string RequestSummary { get; set; } = string.Empty;
    public string Currency { get; set; } = "CNY";
    public decimal SingleLimitAmount { get; set; }
    public decimal CumulativeLimitAmount { get; set; }
    public int MaxRequests { get; set; } = 1;
    public bool Granted { get; set; }
    public DateTimeOffset AuthorizedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }

    /// <summary>创建已明确授权的计划范围；调用方仍需展示摘要后再执行。</summary>
    public static MpsProbeAuthorization GrantFor(string projectId, string providerId, string accountAlias,
        string modelId, MpsCapabilityKind capability, MpsMeasurementLevel level, string requestSummary,
        decimal singleLimitAmount, decimal cumulativeLimitAmount, int maxRequests,
        DateTimeOffset authorizedUtc, DateTimeOffset expiresUtc, string currency = "CNY") => new()
    {
        ProjectId = projectId,
        ProviderId = providerId,
        AccountAlias = accountAlias,
        ModelId = modelId,
        Capability = capability,
        Level = level,
        RequestSummary = requestSummary,
        Currency = currency,
        SingleLimitAmount = singleLimitAmount,
        CumulativeLimitAmount = cumulativeLimitAmount,
        MaxRequests = maxRequests,
        Granted = true,
        AuthorizedUtc = authorizedUtc,
        ExpiresUtc = expiresUtc
    };

    /// <summary>校验授权金额、次数、期限和脱敏字段。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || !Enum.IsDefined(Capability) || Capability == MpsCapabilityKind.Unknown ||
            !Enum.IsDefined(Level) || AuthorizedUtc == default || ExpiresUtc <= AuthorizedUtc ||
            SingleLimitAmount < 0 || CumulativeLimitAmount < 0 || CumulativeLimitAmount < SingleLimitAmount ||
            MaxRequests is < 1 or > 128)
            throw new InvalidDataException("探测授权范围无效或尚未授权。");
        ValidateId(AuthorizationId, 128, "授权标识");
        ValidateId(ProjectId, 256, "项目标识");
        ValidateId(ProviderId, 128, "提供商");
        ValidateId(AccountAlias, 128, "账号别名");
        ValidateId(ModelId, 256, "模型标识");
        ValidateText(RequestSummary, 2048, "请求摘要");
        if (string.IsNullOrWhiteSpace(Currency) || Currency.Length is < 3 or > 16 || Currency != Currency.Trim() || Currency.Any(char.IsControl))
            throw new InvalidDataException("授权币种无效。");
        if (SingleLimitAmount > 1_000_000_000m || CumulativeLimitAmount > 1_000_000_000m)
            throw new InvalidDataException("授权金额超出范围。");
    }

    private static void ValidateId(string value, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value != value.Trim() || value.Any(char.IsControl) ||
            value.Contains("://", StringComparison.Ordinal) || ContainsSensitiveValue(value))
            throw new InvalidDataException($"{name}无效。");
    }

    private static void ValidateText(string value, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl) || ContainsSensitiveValue(value))
            throw new InvalidDataException($"{name}无效。");
    }

    private static bool ContainsSensitiveValue(string value) => value.Contains("sig=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("signature=", StringComparison.OrdinalIgnoreCase) || value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("bearer ", StringComparison.OrdinalIgnoreCase) || value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("authorization:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一次探测请求的脱敏输入和预估价格；正文必须留在调用方受控素材区。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProbeRequest
{
    public string InputFingerprint { get; set; } = string.Empty;
    public string RequestSummary { get; set; } = string.Empty;
    public MpsPriceMetadata Cost { get; set; } = MpsPriceMetadata.Unknown("未确认");

    /// <summary>校验脱敏输入，未知费用不会被当作零。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(InputFingerprint) || InputFingerprint.Length > 512 || InputFingerprint.Any(char.IsControl) ||
            InputFingerprint.Contains("http", StringComparison.OrdinalIgnoreCase) || InputFingerprint.Contains("://", StringComparison.Ordinal) ||
            InputFingerprint.Contains("secret", StringComparison.OrdinalIgnoreCase) || InputFingerprint.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            InputFingerprint.Contains("authorization", StringComparison.OrdinalIgnoreCase) || InputFingerprint.Contains("bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("探测输入必须是脱敏指纹。");
        if (string.IsNullOrWhiteSpace(RequestSummary) || RequestSummary.Length > 2048 || RequestSummary.Any(char.IsControl) ||
            RequestSummary.Contains("sig=", StringComparison.OrdinalIgnoreCase) || RequestSummary.Contains("signature=", StringComparison.OrdinalIgnoreCase) ||
            RequestSummary.Contains("token=", StringComparison.OrdinalIgnoreCase) || RequestSummary.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            RequestSummary.Contains("bearer ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("探测请求摘要无效。");
        ArgumentNullException.ThrowIfNull(Cost);
        Cost.Validate();
    }
}

/// <summary>预检返回的稳定结果；Allowed=false 时绝不登记新的调用意图。</summary>
public sealed record MpsProbePreflightResult(
    MpsProbePreflightDecision Decision,
    string Reason,
    decimal? Amount,
    int RequestsUsed,
    decimal CumulativeUsed,
    string? RecoveryAttemptId = null)
{
    /// <summary>是否允许登记一次新探测。</summary>
    public bool Allowed => Decision == MpsProbePreflightDecision.Allowed;
}

/// <summary>探测登记结果；包含稳定幂等键，提交超时后沿用同一键查询。</summary>
public sealed record MpsProbeAdmission(MpsProbePreflightResult Preflight, MpsProbeRecoveryEntry? Attempt)
{
    /// <summary>是否已经登记提交意图。</summary>
    public bool Allowed => Preflight.Allowed && Attempt is not null;
}

/// <summary>探测恢复条目；只保存指纹、任务号、状态和费用摘要。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProbeRecoveryEntry
{
    public int SchemaVersion { get; set; } = 1;
    public string PlanId { get; set; } = string.Empty;
    public string AttemptId { get; set; } = Guid.NewGuid().ToString("N");
    public string ProviderId { get; set; } = string.Empty;
    public string AccountAlias { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public MpsCapabilityKind Capability { get; set; }
    public MpsMeasurementLevel Level { get; set; }
    public string InputFingerprint { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? TaskId { get; set; }
    public MpsProbeExecutionState State { get; set; } = MpsProbeExecutionState.IntentRegistered;
    public MpsPriceMetadata Cost { get; set; } = MpsPriceMetadata.Unknown("未确认");
    public decimal ReservedAmount { get; set; }
    public string? ErrorMessage { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>校验恢复条目并拒绝秘密、地址和原始错误响应。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || !Enum.IsDefined(Capability) || Capability == MpsCapabilityKind.Unknown ||
            !Enum.IsDefined(Level) || !Enum.IsDefined(State) || ReservedAmount < 0 || ReservedAmount > 1_000_000_000m ||
            Sequence is < 1 or > 128 || CreatedUtc == default || UpdatedUtc < CreatedUtc)
            throw new InvalidDataException("探测恢复条目无效。");
        ValidateText(PlanId, 128, "计划标识");
        ValidateText(AttemptId, 128, "尝试标识");
        ValidateText(ProviderId, 128, "提供商");
        ValidateText(AccountAlias, 128, "账号");
        ValidateText(ModelId, 256, "模型");
        ValidateFingerprint(InputFingerprint, "输入指纹");
        ValidateFingerprint(IdempotencyKey, "幂等键");
        if (TaskId is not null) ValidateTaskId(TaskId);
        if (ErrorMessage is not null && (ErrorMessage.Length > 512 || ErrorMessage.Any(char.IsControl) ||
            ErrorMessage.Contains("http", StringComparison.OrdinalIgnoreCase) ||
            ErrorMessage.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            ErrorMessage.Contains("token", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("探测错误消息包含敏感内容。");
        if (Cost is null) throw new InvalidDataException("探测恢复成本不能为空。");
        Cost.Validate();
        if (State is MpsProbeExecutionState.Succeeded or MpsProbeExecutionState.Failed && (!Cost.IsConfirmed || TaskId is null))
            throw new InvalidDataException("探测终态必须绑定稳定任务号和确认费用。");
    }

    private static void ValidateText(string value, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value != value.Trim() || value.Any(char.IsControl) ||
            value.Contains("sig=", StringComparison.OrdinalIgnoreCase) || value.Contains("signature=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token=", StringComparison.OrdinalIgnoreCase) || value.Contains("bearer ", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{name}无效。");
    }

    private static void ValidateFingerprint(string value, string name)
    {
        ValidateText(value, 512, name);
        if (value.Contains("http", StringComparison.OrdinalIgnoreCase) || value.Contains("://", StringComparison.Ordinal) ||
            value.Contains("secret", StringComparison.OrdinalIgnoreCase) || value.Contains("token", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{name}不能包含地址或秘密。");
    }

    private static void ValidateTaskId(string value)
    {
        ValidateText(value, 256, "任务号");
        if (value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidDataException("任务号无效。");
    }
}

/// <summary>探测恢复文件快照；快照内容可安全落盘并在重开时先查旧任务。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProbeRecoverySnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset SavedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<MpsProbeRecoveryEntry> Entries { get; set; } = [];

    /// <summary>检查数量、幂等键和任务号唯一性。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || SavedUtc == default || Entries is null || Entries.Count > MpsProbeRecoveryStore.MaxEntries)
            throw new InvalidDataException("探测恢复快照无效。");
        var attempts = new HashSet<string>(StringComparer.Ordinal);
        var idempotency = new HashSet<string>(StringComparer.Ordinal);
        var tasks = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<string>(StringComparer.Ordinal);
        var planCounts = Entries.Where(entry => entry is not null).GroupBy(entry => entry.PlanId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        foreach (var entry in Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("探测快照校验超过时限。");
            if (entry is null) throw new InvalidDataException("探测恢复条目不能为空。");
            entry.Validate(cancellationToken);
            // 每组 n 个互异正序号都不得超过 n，因此序号恰好连续覆盖 1..n。
            if (entry.Sequence > planCounts[entry.PlanId] || !attempts.Add(entry.AttemptId) || !idempotency.Add(entry.IdempotencyKey) ||
                !sequences.Add(entry.PlanId + ":" + entry.Sequence) ||
                entry.TaskId is not null && !tasks.Add(MpsBudgetLedger.StableKey(entry.ProviderId, entry.TaskId)))
                throw new InvalidDataException("探测恢复快照包含重复标识。");
        }
    }
}

/// <summary>线程安全的探测恢复仓库；不保存请求正文、凭据或签名地址。</summary>
public sealed class MpsProbeRecoveryStore
{
    public const int MaxEntries = 4096;
    private readonly object gate = new();
    private readonly Dictionary<string, MpsProbeRecoveryEntry> entries = new(StringComparer.Ordinal);

    /// <summary>添加或更新一条恢复状态。</summary>
    public void Upsert(MpsProbeRecoveryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Validate(cancellationToken);
        lock (gate)
        {
            if (!entries.ContainsKey(entry.AttemptId) && entries.Count >= MaxEntries)
                throw new InvalidDataException("探测恢复条目超过上限。");
            if (entry.TaskId is not null && entries.Values.Any(item => item.AttemptId != entry.AttemptId && item.TaskId is not null &&
                MpsBudgetLedger.StableKey(item.ProviderId, item.TaskId) == MpsBudgetLedger.StableKey(entry.ProviderId, entry.TaskId)))
                throw new InvalidDataException("任务号已经关联其他探测尝试。");
            if (entries.TryGetValue(entry.AttemptId, out var existing) &&
                (existing.PlanId != entry.PlanId || existing.ProviderId != entry.ProviderId || existing.AccountAlias != entry.AccountAlias ||
                 existing.ModelId != entry.ModelId || existing.Capability != entry.Capability || existing.Level != entry.Level ||
                 existing.InputFingerprint != entry.InputFingerprint || existing.IdempotencyKey != entry.IdempotencyKey || existing.Sequence != entry.Sequence))
                throw new InvalidDataException("恢复条目的稳定授权范围不能被改写。");
            if (existing is not null && (existing.ReservedAmount != entry.ReservedAmount || existing.CreatedUtc != entry.CreatedUtc ||
                existing.TaskId is not null && existing.TaskId != entry.TaskId))
                throw new InvalidDataException("已登记预算或已绑定供应商任务号不能被抹掉或替换。");
            entries[entry.AttemptId] = Clone(entry);
        }
    }

    /// <summary>在共享仓库锁内完成预检与登记，避免多个计划实例同时越过预算次数边界。</summary>
    internal T ExecuteExclusive<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (gate) return action();
    }

    /// <summary>移除尚未登记预算的失败意图；已执行任务不会走此路径。</summary>
    internal bool Remove(string attemptId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return entries.Remove(attemptId);
    }

    /// <summary>返回恢复条目的脱敏副本。</summary>
    public MpsProbeRecoveryEntry? Find(string attemptId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return entries.TryGetValue(attemptId, out var entry) ? Clone(entry) : null;
    }

    /// <summary>返回同一计划的有限排序快照。</summary>
    public IReadOnlyList<MpsProbeRecoveryEntry> FindPlan(string planId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return entries.Values.Where(item => item.PlanId == planId).OrderBy(item => item.Sequence).Select(Clone).ToArray();
    }

    /// <summary>导出恢复 JSON，供项目关闭时保存。</summary>
    public string ExportJson(CancellationToken cancellationToken = default)
    {
        var snapshot = new MpsProbeRecoverySnapshot { Entries = FindAll(cancellationToken).ToList() };
        snapshot.Validate(cancellationToken);
        return JsonSerializer.Serialize(snapshot, JsonFiles.Options);
    }

    /// <summary>返回可组合保存的恢复快照。</summary>
    public MpsProbeRecoverySnapshot ToSnapshot(CancellationToken cancellationToken = default)
    {
        var snapshot = new MpsProbeRecoverySnapshot { Entries = FindAll(cancellationToken).ToList() };
        snapshot.Validate(cancellationToken);
        return snapshot;
    }

    /// <summary>从 JSON 重开恢复仓库，不访问供应商或系统凭据。</summary>
    public static MpsProbeRecoveryStore ReopenJson(string json, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("探测恢复 JSON 为空或超过大小上限。");
        var snapshot = JsonSerializer.Deserialize<MpsProbeRecoverySnapshot>(json, JsonFiles.Options)
            ?? throw new InvalidDataException("探测恢复 JSON 为空。");
        snapshot.Validate(cancellationToken);
        var store = new MpsProbeRecoveryStore();
        var timer = Stopwatch.StartNew();
        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("探测恢复超过时限。");
            store.Upsert(entry, cancellationToken);
        }
        return store;
    }

    private IReadOnlyList<MpsProbeRecoveryEntry> FindAll(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return entries.Values.OrderBy(item => item.AttemptId, StringComparer.Ordinal).Select(Clone).ToArray();
    }

    private static MpsProbeRecoveryEntry Clone(MpsProbeRecoveryEntry value) => new()
    {
        SchemaVersion = value.SchemaVersion,
        PlanId = value.PlanId,
        AttemptId = value.AttemptId,
        ProviderId = value.ProviderId,
        AccountAlias = value.AccountAlias,
        ModelId = value.ModelId,
        Capability = value.Capability,
        Level = value.Level,
        InputFingerprint = value.InputFingerprint,
        IdempotencyKey = value.IdempotencyKey,
        TaskId = value.TaskId,
        State = value.State,
        Cost = Clone(value.Cost),
        ReservedAmount = value.ReservedAmount,
        ErrorMessage = value.ErrorMessage,
        Sequence = value.Sequence,
        CreatedUtc = value.CreatedUtc,
        UpdatedUtc = value.UpdatedUtc
    };

    private static MpsPriceMetadata Clone(MpsPriceMetadata value) => new()
    {
        Status = value.Status, Amount = value.Amount, Currency = value.Currency, Unit = value.Unit,
        Basis = value.Basis, Source = value.Source, ObservedUtc = value.ObservedUtc
    };
}

/// <summary>授权小额探测计划；所有外部调用前必须经过 Preflight/Begin。</summary>
public sealed class MpsAuthorizedProbePlan
{
    private readonly MpsProbeAuthorization authorization;
    private readonly MpsBudgetLedger ledger;
    private readonly MpsProbeRecoveryStore recovery;

    /// <summary>创建计划时立即验证授权和预算币种。</summary>
    public MpsAuthorizedProbePlan(MpsProbeAuthorization authorization, MpsBudgetLedger ledger,
        MpsProbeRecoveryStore? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        this.authorization = JsonSerializer.Deserialize<MpsProbeAuthorization>(JsonSerializer.Serialize(authorization, JsonFiles.Options), JsonFiles.Options)!;
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.recovery = recovery ?? new MpsProbeRecoveryStore();
        authorization.Validate();
        if (!string.Equals(ledger.Currency, authorization.Currency.Trim().ToUpperInvariant(), StringComparison.Ordinal))
            throw new InvalidDataException("探测授权币种与预算账本不一致。");
        PlanId = authorization.AuthorizationId;
        RestoreBudgetReservations();
    }

    /// <summary>计划使用的授权标识。</summary>
    public string PlanId { get; }

    /// <summary>只读授权副本。</summary>
    public MpsProbeAuthorization Authorization => JsonSerializer.Deserialize<MpsProbeAuthorization>(
        JsonSerializer.Serialize(authorization, JsonFiles.Options), JsonFiles.Options)!;

    /// <summary>恢复仓库，供关闭与重开流程使用。</summary>
    public MpsProbeRecoveryStore Recovery => recovery;

    /// <summary>执行外部调用前检查授权、价格、次数、单次和累计金额。</summary>
    public MpsProbePreflightResult Preflight(MpsProbeRequest request, DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) return new MpsProbePreflightResult(MpsProbePreflightDecision.InvalidRequest, "探测请求不能为空。", null, 0, 0);
        try { request.Validate(cancellationToken); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            return new MpsProbePreflightResult(MpsProbePreflightDecision.InvalidRequest, error.Message, null, 0, 0);
        }
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        return recovery.ExecuteExclusive(() =>
        {
            var attempts = recovery.FindPlan(PlanId, cancellationToken);
            var latest = attempts.OrderByDescending(item => item.Sequence).FirstOrDefault();
            if (!authorization.Granted) return Denied(MpsProbePreflightDecision.NotAuthorized, "尚未保存用户授权。", attempts);
            if (now < authorization.AuthorizedUtc || now >= authorization.ExpiresUtc)
                return Denied(MpsProbePreflightDecision.Expired, "探测授权尚未生效或已过期。", attempts);
            if (!string.Equals(request.RequestSummary, authorization.RequestSummary, StringComparison.Ordinal))
                return Denied(MpsProbePreflightDecision.InvalidRequest, "请求摘要与已授权计划不一致。", attempts);
            if (request.Cost.Currency is not null && !string.Equals(request.Cost.Currency.Trim().ToUpperInvariant(), ledger.Currency, StringComparison.Ordinal))
                return Denied(MpsProbePreflightDecision.InvalidRequest, "探测费用币种与授权不一致。", attempts);
            if (authorization.Level == MpsMeasurementLevel.ReadOnlyMetadata)
                return Denied(MpsProbePreflightDecision.InvalidRequest, "只读元数据应使用只读适配器，不进入调用计划。", attempts);
            if (authorization.Level == MpsMeasurementLevel.FreeMinimalCall && (!request.Cost.IsConfirmed || request.Cost.Amount != 0m))
                return Denied(MpsProbePreflightDecision.UnknownCost, "免费调用必须有确认的零成本事实。", attempts);
            var matching = attempts.FirstOrDefault(item => item.InputFingerprint == request.InputFingerprint);
            if (matching is not null)
                return new MpsProbePreflightResult(MpsProbePreflightDecision.RecoveryRequired,
                    "相同探测已经登记，必须读取或查询已有结果。", null, attempts.Count, Sum(attempts), matching.AttemptId);
            if (latest is { State: MpsProbeExecutionState.IntentRegistered or MpsProbeExecutionState.Submitted or MpsProbeExecutionState.Unknown })
                return new MpsProbePreflightResult(MpsProbePreflightDecision.RecoveryRequired,
                    "已有探测可能已提交，必须先查询旧任务。", null, attempts.Count, Sum(attempts), latest.AttemptId);
            if (attempts.Count >= authorization.MaxRequests)
                return Denied(MpsProbePreflightDecision.RequestLimitExceeded, "探测次数已达到授权上限。", attempts);

            var amountResult = ResolveAmount(request.Cost);
            if (!amountResult.Allowed)
                return new MpsProbePreflightResult(MpsProbePreflightDecision.UnknownCost, amountResult.Reason,
                    amountResult.Amount, attempts.Count, Sum(attempts));
            var amount = amountResult.Amount!.Value;
            if (amount > authorization.SingleLimitAmount)
                return new MpsProbePreflightResult(MpsProbePreflightDecision.SingleLimitExceeded, "单次金额超过授权上限。",
                    amount, attempts.Count, Sum(attempts));
            var cumulative = Sum(attempts);
            if (cumulative + amount > authorization.CumulativeLimitAmount)
                return new MpsProbePreflightResult(MpsProbePreflightDecision.CumulativeLimitExceeded, "累计金额超过授权上限。",
                    amount, attempts.Count, cumulative);
            if (amount > ledger.Remaining)
                return new MpsProbePreflightResult(MpsProbePreflightDecision.CumulativeLimitExceeded, "项目硬预算不足。",
                    amount, attempts.Count, cumulative);
            return new MpsProbePreflightResult(MpsProbePreflightDecision.Allowed, "允许登记探测提交意图。",
                amount, attempts.Count, cumulative);
        });
    }

    /// <summary>登记提交意图并预留预算；返回拒绝时不会产生新的恢复条目。</summary>
    public MpsProbeAdmission Begin(MpsProbeRequest request, DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        return recovery.ExecuteExclusive(() =>
        {
            // 共享恢复仓库锁覆盖预检、登记与预算预留，保证多个计划实例的次数边界一致。
            var preflight = Preflight(request, now, cancellationToken);
            if (!preflight.Allowed) return new MpsProbeAdmission(preflight, null);
            var attempts = recovery.FindPlan(PlanId, cancellationToken);
            var sequence = attempts.Count + 1;
            var attemptId = Guid.NewGuid().ToString("N");
            var key = BuildIdempotencyKey(authorization, request.InputFingerprint, sequence);
            var amount = preflight.Amount ?? 0m;
            var entry = new MpsProbeRecoveryEntry
            {
                PlanId = PlanId,
                AttemptId = attemptId,
                ProviderId = authorization.ProviderId,
                AccountAlias = authorization.AccountAlias,
                ModelId = authorization.ModelId,
                Capability = authorization.Capability,
                Level = authorization.Level,
                InputFingerprint = request.InputFingerprint,
                IdempotencyKey = key,
                State = MpsProbeExecutionState.IntentRegistered,
                Cost = Clone(request.Cost),
                ReservedAmount = amount,
                Sequence = sequence,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            recovery.Upsert(entry, cancellationToken);
            try
            {
                ledger.Reserve(authorization.ProviderId, attemptId, amount, request.Cost,
                    authorization.AccountAlias, authorization.ModelId, now);
            }
            catch
            {
                recovery.Remove(attemptId, cancellationToken);
                throw;
            }
            return new MpsProbeAdmission(preflight, entry);
        });
    }

    /// <summary>兼容名称：登记一次受授权的探测。</summary>
    public MpsProbeAdmission TryBegin(MpsProbeRequest request, DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default) => Begin(request, nowUtc, cancellationToken);

    /// <summary>保存授权和恢复状态的同一快照，供用户复核与重开。</summary>
    public MpsProbePlanSnapshot ToSnapshot(CancellationToken cancellationToken = default)
    {
        var snapshot = new MpsProbePlanSnapshot
        {
            Authorization = Authorization,
            Recovery = new MpsProbeRecoverySnapshot { Entries = recovery.FindPlan(PlanId, cancellationToken).ToList() }
        };
        snapshot.Validate(cancellationToken);
        return snapshot;
    }

    /// <summary>导出脱敏的授权计划 JSON。</summary>
    public string ExportJson(CancellationToken cancellationToken = default) => JsonSerializer.Serialize(ToSnapshot(cancellationToken), JsonFiles.Options);

    /// <summary>从本地 JSON 恢复授权与预算占用，不触发供应商调用。</summary>
    public static MpsAuthorizedProbePlan ReopenJson(string json, MpsBudgetLedger ledger, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("探测计划 JSON 为空或超过大小上限。");
        var snapshot = JsonSerializer.Deserialize<MpsProbePlanSnapshot>(json, JsonFiles.Options)
            ?? throw new InvalidDataException("探测计划 JSON 为空。");
        snapshot.Validate(cancellationToken);
        var recovery = MpsProbeRecoveryStore.ReopenJson(JsonSerializer.Serialize(snapshot.Recovery, JsonFiles.Options), cancellationToken);
        return new MpsAuthorizedProbePlan(snapshot.Authorization, ledger, recovery);
    }

    /// <summary>提交成功后绑定供应商任务号；该调用不重新登记预算。</summary>
    public MpsProbeRecoveryEntry MarkSubmitted(string attemptId, string taskId, DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(attemptId, cancellationToken);
        ValidateTaskId(taskId);
        if (entry.State != MpsProbeExecutionState.IntentRegistered)
            throw new InvalidOperationException("只有已登记意图的探测才能绑定任务号。");
        entry.TaskId = taskId;
        ledger.BindTaskId(entry.ProviderId, entry.AttemptId, taskId);
        entry.State = MpsProbeExecutionState.Submitted;
        entry.UpdatedUtc = nowUtc ?? DateTimeOffset.UtcNow;
        recovery.Upsert(entry, cancellationToken);
        return entry;
    }

    /// <summary>人工或异步恢复查询找回旧任务号后绑定预算，绝不创建新提交意图。</summary>
    public MpsProbeRecoveryEntry AttachRecoveredTaskId(string attemptId, string taskId,
        DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(attemptId, cancellationToken);
        ValidateTaskId(taskId);
        if (entry.State != MpsProbeExecutionState.Unknown)
            throw new InvalidOperationException("只有未知探测可找回旧任务号。");
        if (entry.TaskId is not null && entry.TaskId != taskId)
            throw new InvalidDataException("恢复任务号不能替换已有任务。");
        ledger.BindTaskId(entry.ProviderId, BudgetTaskId(entry), taskId);
        entry.TaskId = taskId;
        entry.UpdatedUtc = nowUtc ?? DateTimeOffset.UtcNow;
        recovery.Upsert(entry, cancellationToken);
        return entry;
    }

    /// <summary>记录已知最终费用和成功状态；账本按任务标识确认一次。</summary>
    public MpsProbeRecoveryEntry MarkSucceeded(string attemptId, MpsPriceMetadata finalCost,
        DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finalCost);
        finalCost.Validate();
        if (!finalCost.IsConfirmed) throw new InvalidDataException("成功探测必须使用已确认费用。");
        var entry = GetEntry(attemptId, cancellationToken);
        if (entry.State == MpsProbeExecutionState.Succeeded && SameCost(entry.Cost, finalCost)) return entry;
        if (entry.State is not (MpsProbeExecutionState.IntentRegistered or MpsProbeExecutionState.Submitted or MpsProbeExecutionState.Unknown))
            throw new InvalidOperationException("探测已经结束或需要恢复查询。");
        var updated = nowUtc ?? DateTimeOffset.UtcNow;
        if (!FinalCostWithinAuthorization(entry, finalCost))
            return RecordCostOverrun(entry, finalCost, updated);
        ledger.Reconcile(entry.ProviderId, BudgetTaskId(entry), finalCost.Amount!.Value, finalCost, updated);
        entry.Cost = Clone(finalCost);
        entry.State = MpsProbeExecutionState.Succeeded;
        entry.ErrorMessage = null;
        entry.UpdatedUtc = updated;
        recovery.Upsert(entry, cancellationToken);
        return entry;
    }

    /// <summary>记录明确失败；费用未知时保持 Unknown，避免自动重提。</summary>
    public MpsProbeRecoveryEntry MarkFailed(string attemptId, MpsPriceMetadata? finalCost = null,
        string? reason = null, DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(attemptId, cancellationToken);
        if (entry.State == MpsProbeExecutionState.Failed && finalCost is not null && SameCost(entry.Cost, finalCost)) return entry;
        if (entry.State is MpsProbeExecutionState.Succeeded or MpsProbeExecutionState.Failed)
            throw new InvalidOperationException("终态探测不能被后续失败观察覆盖。");
        var updated = nowUtc ?? DateTimeOffset.UtcNow;
        if (finalCost?.IsConfirmed == true)
        {
            if (!FinalCostWithinAuthorization(entry, finalCost))
                return RecordCostOverrun(entry, finalCost, updated);
            ledger.Reconcile(entry.ProviderId, BudgetTaskId(entry), finalCost.Amount!.Value, finalCost, updated);
            entry.Cost = Clone(finalCost);
            entry.State = MpsProbeExecutionState.Failed;
        }
        else
        {
            ledger.MarkUnknown(entry.ProviderId, BudgetTaskId(entry), estimate: null, MpsPriceMetadata.Unknown("失败费用未确认"), updated);
            entry.State = MpsProbeExecutionState.Unknown;
            entry.Cost = MpsPriceMetadata.Unknown("失败费用未确认");
        }
        entry.ErrorMessage = SafeReason(reason ?? "探测失败，费用或任务状态需要人工确认。");
        entry.UpdatedUtc = updated;
        recovery.Upsert(entry, cancellationToken);
        return entry;
    }

    /// <summary>提交超时或用户中止时保留未知状态和预算占用，禁止盲目重发。</summary>
    public MpsProbeRecoveryEntry MarkUnknown(string attemptId, string reason,
        DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(attemptId, cancellationToken);
        if (entry.State is MpsProbeExecutionState.Succeeded or MpsProbeExecutionState.Failed)
            throw new InvalidOperationException("已确认终态探测不能回退为未知。");
        var updated = nowUtc ?? DateTimeOffset.UtcNow;
        var occupied = ledger.Snapshot().First(item => item.StableKey == MpsBudgetLedger.StableKey(entry.ProviderId, BudgetTaskId(entry))).Amount;
        ledger.MarkUnknown(entry.ProviderId, BudgetTaskId(entry), occupied,
            MpsPriceMetadata.Unknown("探测超时或用户中止"), updated);
        entry.State = MpsProbeExecutionState.Unknown;
        if (!entry.Cost.IsConfirmed) entry.Cost = MpsPriceMetadata.Unknown("探测状态或费用未确认");
        entry.ErrorMessage = SafeReason(reason);
        entry.UpdatedUtc = updated;
        recovery.Upsert(entry, cancellationToken);
        return entry;
    }

    /// <summary>恢复查询获得明确终态后显式关闭未知记录；该方法不会发起新提交。</summary>
    public MpsProbeRecoveryEntry ResolveRecovered(string attemptId, MpsProbeExecutionState terminalState,
        MpsPriceMetadata? finalCost = null, string? taskId = null, DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (terminalState is not (MpsProbeExecutionState.Succeeded or MpsProbeExecutionState.Failed))
            throw new ArgumentOutOfRangeException(nameof(terminalState), "恢复只能写入成功或失败终态。");
        var entry = GetEntry(attemptId, cancellationToken);
        if (entry.State != MpsProbeExecutionState.Unknown)
            throw new InvalidOperationException("只有未知状态的探测需要恢复查询。");
        if (taskId is not null) ValidateTaskId(taskId);
        if (taskId is not null && entry.TaskId is null)
        {
            ledger.BindTaskId(entry.ProviderId, entry.AttemptId, taskId);
            entry.TaskId = taskId;
        }
        else if (taskId is not null && entry.TaskId != taskId)
            throw new InvalidDataException("恢复查询不能替换已有供应商任务号。");
        recovery.Upsert(entry, cancellationToken);
        if (terminalState == MpsProbeExecutionState.Succeeded)
            return MarkSucceeded(attemptId, finalCost ?? throw new InvalidDataException("恢复成功必须提供确认费用。"), nowUtc, cancellationToken);
        return MarkFailed(attemptId, finalCost, "恢复查询确认任务失败。", nowUtc, cancellationToken);
    }

    private MpsProbeRecoveryEntry GetEntry(string attemptId, CancellationToken cancellationToken)
    {
        var entry = recovery.Find(attemptId, cancellationToken) ?? throw new KeyNotFoundException("探测恢复条目不存在。");
        if (entry.PlanId != PlanId) throw new InvalidOperationException("探测条目不属于当前授权计划。");
        return entry;
    }

    /// <summary>重开计划时恢复预算占用，未知条目仍占用原上限。</summary>
    private void RestoreBudgetReservations()
    {
        var existing = ledger.Snapshot().ToDictionary(item => item.StableKey, StringComparer.Ordinal);
        var attempts = recovery.FindPlan(PlanId);
        var timer = Stopwatch.StartNew();
        foreach (var entry in attempts)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("探测预算恢复超过时限。");
            if (entry.ProviderId != authorization.ProviderId || entry.AccountAlias != authorization.AccountAlias ||
                entry.ModelId != authorization.ModelId || entry.Capability != authorization.Capability || entry.Level != authorization.Level)
                throw new InvalidDataException("恢复探测范围与授权计划不一致。");
            var taskId = BudgetTaskId(entry);
            if (existing.TryGetValue(MpsBudgetLedger.StableKey(entry.ProviderId, taskId), out var line))
            {
                ValidateExistingBudgetLine(entry, line);
                continue;
            }
            var amount = entry.State == MpsProbeExecutionState.Unknown && entry.Cost.IsConfirmed &&
                string.Equals(entry.Cost.Currency, ledger.Currency, StringComparison.OrdinalIgnoreCase)
                ? Math.Max(entry.ReservedAmount, entry.Cost.Amount!.Value) : entry.ReservedAmount;
            if (amount > ledger.Remaining) amount = entry.ReservedAmount;
            ledger.Reserve(entry.ProviderId, taskId, amount,
                MpsPriceMetadata.Estimate(amount, ledger.Currency, "request", "恢复探测预算"),
                entry.AccountAlias, entry.ModelId, entry.CreatedUtc);
            if (entry.State == MpsProbeExecutionState.Unknown)
                ledger.MarkUnknown(entry.ProviderId, taskId, amount, nowUtc: entry.UpdatedUtc);
            else if (entry.State is MpsProbeExecutionState.Succeeded or MpsProbeExecutionState.Failed && entry.Cost.IsConfirmed)
                ledger.Reconcile(entry.ProviderId, taskId, entry.Cost.Amount!.Value, entry.Cost, entry.UpdatedUtc);
        }
    }

    /// <summary>绑定任务号后始终按供应商稳定任务号对账。</summary>
    private static string BudgetTaskId(MpsProbeRecoveryEntry entry) => entry.TaskId ?? entry.AttemptId;

    /// <summary>恢复时只接受同账号/模型、金额和状态均兼容的既有预算行。</summary>
    private void ValidateExistingBudgetLine(MpsProbeRecoveryEntry entry, MpsBudgetLedgerEntry line)
    {
        if (line.AccountAlias != entry.AccountAlias || line.ModelId != entry.ModelId ||
            !string.Equals(line.Currency, ledger.Currency, StringComparison.Ordinal) || line.State == MpsBudgetLineState.Released)
            throw new InvalidDataException("既有预算行的账号、模型或状态与探测恢复条目不一致。");
        if (entry.State is MpsProbeExecutionState.Succeeded or MpsProbeExecutionState.Failed)
        {
            if (line.State != MpsBudgetLineState.Confirmed || !SameCost(entry.Cost, line.Price) || line.Amount != entry.Cost.Amount)
                throw new InvalidDataException("探测终态与已确认预算金额不一致。");
            return;
        }
        if (entry.State == MpsProbeExecutionState.Unknown)
        {
            if (line.State != MpsBudgetLineState.Unknown || line.Amount < entry.ReservedAmount)
                throw new InvalidDataException("未知探测必须保留兼容的未知预算占用。");
            return;
        }
        if (line.State != MpsBudgetLineState.Reserved || line.Amount != entry.ReservedAmount)
            throw new InvalidDataException("待提交或已提交探测的预算预留金额不一致。");
    }

    /// <summary>实际费用必须再次满足授权；超限观察不能被标为授权内通过。</summary>
    private bool FinalCostWithinAuthorization(MpsProbeRecoveryEntry entry, MpsPriceMetadata cost)
    {
        if (!string.Equals(cost.Currency?.Trim().ToUpperInvariant(), ledger.Currency, StringComparison.Ordinal) ||
            cost.Amount > authorization.SingleLimitAmount) return false;
        var other = recovery.FindPlan(PlanId).Where(item => item.AttemptId != entry.AttemptId).ToArray();
        return Sum(other) + cost.Amount!.Value <= authorization.CumulativeLimitAmount;
    }

    /// <summary>记录超授权实付观察；预算不能容纳时保留原占用并明确需要人工对账。</summary>
    private MpsProbeRecoveryEntry RecordCostOverrun(MpsProbeRecoveryEntry entry, MpsPriceMetadata cost, DateTimeOffset updated)
    {
        var sameCurrency = string.Equals(cost.Currency?.Trim().ToUpperInvariant(), ledger.Currency, StringComparison.Ordinal);
        var currentAmount = ledger.Snapshot().First(item => item.StableKey == MpsBudgetLedger.StableKey(entry.ProviderId, BudgetTaskId(entry))).Amount;
        var occupied = sameCurrency ? Math.Max(currentAmount, Math.Max(entry.ReservedAmount, cost.Amount!.Value)) : currentAmount;
        try { ledger.MarkUnknown(entry.ProviderId, BudgetTaskId(entry), occupied, nowUtc: updated); }
        catch (MpsBudgetExceededException) { ledger.MarkUnknown(entry.ProviderId, BudgetTaskId(entry), estimate: null, nowUtc: updated); }
        entry.Cost = Clone(cost);
        entry.State = MpsProbeExecutionState.Unknown;
        entry.ErrorMessage = "实际费用超出授权或币种不一致，需要人工对账。";
        entry.UpdatedUtc = updated;
        recovery.Upsert(entry);
        return entry;
    }

    /// <summary>重复终态观察只接受相同金额和币种，不允许静默改写已确认费用。</summary>
    private static bool SameCost(MpsPriceMetadata one, MpsPriceMetadata two) => one.IsConfirmed && two.IsConfirmed &&
        one.Amount == two.Amount && string.Equals(one.Currency, two.Currency, StringComparison.OrdinalIgnoreCase);

    private MpsProbePreflightResult Denied(MpsProbePreflightDecision decision, string reason,
        IReadOnlyList<MpsProbeRecoveryEntry> attempts) =>
        new(decision, reason, null, attempts.Count, Sum(attempts));

    private static (bool Allowed, decimal? Amount, string Reason) ResolveAmount(MpsPriceMetadata cost)
    {
        if (cost.Status == MpsPriceStatus.Unknown || !cost.IsComplete)
            return (false, null, "费用未知，需用户确认价格后再探测。");
        if (cost.Amount is null) return (false, null, "费用金额未知，不能触发探测。");
        return (true, cost.Amount.Value, "费用字段完整。");
    }

    private static decimal Sum(IReadOnlyList<MpsProbeRecoveryEntry> attempts) => attempts.Sum(item =>
        item.Cost.IsConfirmed ? item.Cost.Amount!.Value : item.ReservedAmount);

    private static string BuildIdempotencyKey(MpsProbeAuthorization auth, string fingerprint, int sequence)
    {
        var canonical = string.Join("\n", auth.AuthorizationId, auth.ProviderId, auth.AccountAlias, auth.ModelId, auth.Capability,
            auth.Level, fingerprint, sequence);
        return "mps-probe-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..40];
    }

    private static MpsPriceMetadata Clone(MpsPriceMetadata value) => new()
    {
        Status = value.Status, Amount = value.Amount, Currency = value.Currency, Unit = value.Unit,
        Basis = value.Basis, Source = value.Source, ObservedUtc = value.ObservedUtc
    };

    private static string SafeReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl) ||
            value.Contains("http", StringComparison.OrdinalIgnoreCase) || value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token", StringComparison.OrdinalIgnoreCase))
            return "探测未完成，需恢复查询。";
        return value;
    }

    private static void ValidateTaskId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidDataException("探测任务号无效。");
    }
}

/// <summary>探测授权与恢复状态的组合快照；不包含素材正文。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProbePlanSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public MpsProbeAuthorization Authorization { get; set; } = new();
    public MpsProbeRecoverySnapshot Recovery { get; set; } = new();

    /// <summary>校验授权与全部恢复条目属于同一计划。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        if (SchemaVersion is < 1 or > 100 || Authorization is null || Recovery is null)
            throw new InvalidDataException("授权探测计划快照无效。");
        Authorization.Validate(cancellationToken);
        Recovery.Validate(cancellationToken);
        if (Recovery.Entries.Count > Authorization.MaxRequests || Recovery.Entries.Any(item => item.PlanId != Authorization.AuthorizationId || item.ProviderId != Authorization.ProviderId ||
            item.AccountAlias != Authorization.AccountAlias || item.ModelId != Authorization.ModelId ||
            item.Capability != Authorization.Capability || item.Level != Authorization.Level || item.Sequence > Authorization.MaxRequests))
            throw new InvalidDataException("探测快照不能包含其他授权计划的条目。");
    }
}
