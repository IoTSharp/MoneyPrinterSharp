using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>价格事实的可信度；未知和估算不能被当作已确认实付。</summary>
public enum MpsPriceStatus
{
    Unknown,
    Estimated,
    Confirmed
}

/// <summary>统一的价格元数据。金额、币种、单位和估算依据必须一起保存。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsPriceMetadata
{
    public MpsPriceStatus Status { get; init; }
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }
    public string? Unit { get; init; }
    public string? Basis { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset? ObservedUtc { get; init; }

    /// <summary>只有完整的已确认事实才计入已确认费用。</summary>
    [JsonIgnore]
    public bool IsConfirmed => Status == MpsPriceStatus.Confirmed && IsComplete;

    /// <summary>估算金额可用于预算预留，但仍保持估算标记。</summary>
    [JsonIgnore]
    public bool IsEstimate => Status == MpsPriceStatus.Estimated && IsComplete;

    /// <summary>兼容模型目录的已知价格判断；估算仍不等于已确认实付。</summary>
    [JsonIgnore]
    public bool IsKnown => IsConfirmed || IsEstimate;

    /// <summary>未知价格不伪装成免费；缺失字段会使价格保持未知。</summary>
    [JsonIgnore]
    public bool IsComplete => Amount is >= 0 && !string.IsNullOrWhiteSpace(Currency) &&
        !string.IsNullOrWhiteSpace(Unit) && !string.IsNullOrWhiteSpace(Basis);

    /// <summary>构造没有价格事实的元数据。</summary>
    public static MpsPriceMetadata Unknown(string? basis = null) => new()
    {
        Status = MpsPriceStatus.Unknown,
        Basis = basis
    };

    /// <summary>构造离线或目录提供的预算估算。</summary>
    public static MpsPriceMetadata Estimate(decimal amount, string currency, string unit, string basis,
        string? source = null, DateTimeOffset? observedUtc = null) => new()
    {
        Status = MpsPriceStatus.Estimated, Amount = amount, Currency = currency,
        Unit = unit, Basis = basis, Source = source, ObservedUtc = observedUtc
    };

    /// <summary>构造供应商账单或用量返回的已确认价格。</summary>
    public static MpsPriceMetadata Confirmed(decimal amount, string currency, string unit, string basis,
        string? source = null, DateTimeOffset? observedUtc = null) => new()
    {
        Status = MpsPriceStatus.Confirmed, Amount = amount, Currency = currency,
        Unit = unit, Basis = basis, Source = source, ObservedUtc = observedUtc
    };

    /// <summary>校验长度、金额和状态边界，不访问供应商。</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Status)) throw new InvalidDataException("价格状态无效。");
        if (Amount is < 0 or > 1_000_000_000m) throw new InvalidDataException("价格金额超出范围。");
        if (Currency is not null && !ValidText(Currency, 16)) throw new InvalidDataException("价格币种无效。");
        if (Unit is not null && !ValidText(Unit, 64)) throw new InvalidDataException("价格单位无效。");
        if (Basis is not null && (!ValidText(Basis, 512) || SensitiveText(Basis))) throw new InvalidDataException("价格依据无效或包含敏感字段。");
        if (Source is not null && (!ValidText(Source, 2048) || SensitiveText(Source))) throw new InvalidDataException("价格来源无效或包含敏感字段。");
        if (Status == MpsPriceStatus.Unknown && Amount is not null)
            throw new InvalidDataException("未知价格不能携带看似已知的金额。");
        if (Status is MpsPriceStatus.Estimated or MpsPriceStatus.Confirmed && !IsComplete)
            throw new InvalidDataException("估算或确认价格必须同时包含金额、币种、单位和依据。");
        if (ObservedUtc is { } value && (value < DateTimeOffset.UnixEpoch || value > DateTimeOffset.UtcNow.AddMinutes(5)))
            throw new InvalidDataException("价格观察时间无效。");
    }

    private static bool ValidText(string value, int max) => value.Length <= max && !value.Any(char.IsControl);

    /// <summary>来源允许公开文档地址，拒绝认证字段及签名/查询地址进入预算快照。</summary>
    private static bool SensitiveText(string value) => value.Contains("bearer ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("authorization", StringComparison.OrdinalIgnoreCase) || value.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token=", StringComparison.OrdinalIgnoreCase) || value.Contains("secret=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("signature=", StringComparison.OrdinalIgnoreCase) || value.Contains("sig=", StringComparison.OrdinalIgnoreCase) ||
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Query.Length > 0 || uri.UserInfo.Length > 0);
}

/// <summary>模型或能力的统一限制元数据；null 表示供应商尚未给出事实。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsLimitMetadata
{
    public int? MaxInputTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public double? MaxDurationSeconds { get; init; }
    public long? MaxBytes { get; init; }
    public int? MaxConcurrentRequests { get; init; }
    public int? RequestsPerMinute { get; init; }
    public List<string>? Formats { get; init; }
    public List<string>? AspectRatios { get; init; }
    public List<string>? Resolutions { get; init; }

    /// <summary>至少有一项限制事实时返回 true；空集合仍代表未知限制。</summary>
    [JsonIgnore]
    public bool HasKnownLimit => MaxInputTokens is not null || MaxOutputTokens is not null ||
        MaxDurationSeconds is not null || MaxBytes is not null || MaxConcurrentRequests is not null ||
        RequestsPerMinute is not null || Formats is { Count: > 0 } || AspectRatios is { Count: > 0 } ||
        Resolutions is { Count: > 0 };

    /// <summary>限制目录的兼容别名；未知字段不会被解释为无限制。</summary>
    [JsonIgnore]
    public bool IsKnown => HasKnownLimit;

    /// <summary>限制值只允许正数，列表数量和文本长度均有上限。</summary>
    public void Validate()
    {
        if (MaxInputTokens is <= 0 || MaxOutputTokens is <= 0 || MaxDurationSeconds is <= 0 ||
            MaxDurationSeconds is double.NaN or double.PositiveInfinity or double.NegativeInfinity ||
            MaxBytes is <= 0 || MaxConcurrentRequests is <= 0 || RequestsPerMinute is <= 0 ||
            !ValidList(Formats, 64) || !ValidList(AspectRatios, 64) || !ValidList(Resolutions, 64))
            throw new InvalidDataException("限制元数据无效。");
    }

    private static bool ValidList(List<string>? values, int maxCount) =>
        values is null || values.Count <= maxCount && values.All(value => !string.IsNullOrWhiteSpace(value) &&
            value.Length <= 64 && !value.Any(char.IsControl));
}

/// <summary>提供商、模型能力和价格/限制事实的统一快照。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsPricingLimitMetadata
{
    public MpsPriceMetadata Price { get; init; } = MpsPriceMetadata.Unknown();
    public MpsLimitMetadata Limits { get; init; } = new();

    /// <summary>验证嵌套价格与限制，避免未知字段静默丢失。</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Price);
        ArgumentNullException.ThrowIfNull(Limits);
        Price.Validate();
        Limits.Validate();
    }
}

/// <summary>预算账本中的一条稳定任务行。</summary>
public enum MpsBudgetLineState
{
    Reserved,
    Confirmed,
    Unknown,
    Released
}

/// <summary>账本行快照；ProviderId+TaskId 是跨重试唯一键。</summary>
public sealed record MpsBudgetLedgerEntry(
    string ProviderId,
    string TaskId,
    string Currency,
    decimal Amount,
    MpsBudgetLineState State,
    MpsPriceMetadata Price,
    string? AccountAlias = null,
    string? ModelId = null,
    DateTimeOffset? UpdatedUtc = null)
{
    /// <summary>稳定的去重键，不含账号、模型等可能变化的观察字段。</summary>
    public string StableKey => MpsBudgetLedger.StableKey(ProviderId, TaskId);

    /// <summary>价格元数据的兼容别名。</summary>
    [JsonIgnore]
    public MpsPriceMetadata PriceMetadata => Price;
}

/// <summary>项目范围的线程安全费用预算账本，拒绝硬预算越界并按供应商任务去重。</summary>
public sealed class MpsBudgetLedger
{
    private const int MaxEntries = 20_000;
    private readonly object gate = new();
    private readonly Dictionary<string, MpsBudgetLedgerEntry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> taskBindings = new(StringComparer.Ordinal);
    private decimal limit;

    public MpsBudgetLedger(decimal limit, string currency = "CNY")
    {
        if (limit < 0 || limit > 1_000_000_000m) throw new ArgumentOutOfRangeException(nameof(limit));
        Currency = NormalizeCurrency(currency);
        this.limit = limit;
    }

    public string Currency { get; }

    /// <summary>硬预算上限；降低上限时不能越过当前已占用金额。</summary>
    public decimal Limit
    {
        get { lock (gate) return limit; }
        set
        {
            if (value < 0 || value > 1_000_000_000m) throw new ArgumentOutOfRangeException(nameof(value));
            lock (gate)
            {
                if (value < Reserved + Confirmed + Unknown) throw new MpsBudgetExceededException(value, Reserved + Confirmed + Unknown, 0);
                limit = value;
            }
        }
    }

    public decimal Reserved { get { lock (gate) return Sum(MpsBudgetLineState.Reserved); } }
    public decimal Confirmed { get { lock (gate) return Sum(MpsBudgetLineState.Confirmed); } }
    public decimal Unknown { get { lock (gate) return Sum(MpsBudgetLineState.Unknown); } }
    public decimal Remaining { get { lock (gate) return limit - SumAll(); } }
    public int EntryCount { get { lock (gate) return entries.Count; } }

    /// <summary>预留估算金额；同一 provider+task 重复预留只返回原行。</summary>
    public MpsBudgetLedgerEntry Reserve(string providerId, string taskId, decimal amount,
        MpsPriceMetadata? price = null, string? accountAlias = null, string? modelId = null,
        DateTimeOffset? nowUtc = null)
    {
        ValidateKey(providerId, taskId);
        ValidateAmount(amount);
        ValidatePrice(price);
        lock (gate)
        {
            EnsureCurrency(price);
            var key = ResolveKey(providerId, taskId);
            if (entries.TryGetValue(key, out var existing))
            {
                if (existing.State == MpsBudgetLineState.Reserved && existing.Amount == amount) return existing;
                throw new InvalidOperationException("同一提供商任务已经记账，不能使用不同金额重复预留。");
            }
            EnsureCapacity();
            EnsureAvailable(amount);
            var line = new MpsBudgetLedgerEntry(providerId.Trim(), taskId.Trim(), Currency, amount,
                MpsBudgetLineState.Reserved, price ?? MpsPriceMetadata.Estimate(amount, Currency, "request", "budget-reservation"),
                accountAlias, modelId, nowUtc ?? DateTimeOffset.UtcNow);
            entries.Add(key, line);
            return line;
        }
    }

    /// <summary>将预留转为供应商确认费用，实际金额增加时仍执行硬预算检查。</summary>
    public MpsBudgetLedgerEntry Confirm(string providerId, string taskId, decimal amount,
        MpsPriceMetadata? price = null, DateTimeOffset? nowUtc = null)
    {
        ValidateKey(providerId, taskId);
        ValidateAmount(amount);
        ValidatePrice(price);
        ValidatePriceState(price, MpsPriceStatus.Confirmed);
        lock (gate)
        {
            var key = ResolveKey(providerId, taskId);
            if (!entries.TryGetValue(key, out var existing)) throw new KeyNotFoundException("待确认任务不存在。");
            EnsureCurrency(price);
            if (existing.State == MpsBudgetLineState.Confirmed && existing.Amount == amount) return existing;
            if (existing.State != MpsBudgetLineState.Reserved)
                throw new InvalidOperationException("只有预留中的任务才能确认或对账。");
            var occupiedWithoutCurrent = SumAll() - existing.Amount;
            if (occupiedWithoutCurrent + amount > limit)
                throw new MpsBudgetExceededException(limit, limit - occupiedWithoutCurrent, amount);
            var confirmed = existing with
            {
                Amount = amount, State = MpsBudgetLineState.Confirmed,
                Price = price ?? MpsPriceMetadata.Confirmed(amount, Currency, existing.Price.Unit ?? "request", "provider-usage"),
                UpdatedUtc = nowUtc ?? DateTimeOffset.UtcNow
            };
            entries[key] = confirmed;
            return confirmed;
        }
    }

    /// <summary>把预留金额消费为供应商确认费用的兼容名称。</summary>
    public MpsBudgetLedgerEntry Consume(string providerId, string taskId, decimal amount,
        MpsPriceMetadata? price = null, DateTimeOffset? nowUtc = null) =>
        Confirm(providerId, taskId, amount, price, nowUtc);

    /// <summary>供应商未知价格时把预留转为未知占用；未知金额不会被算作零。</summary>
    public MpsBudgetLedgerEntry MarkUnknown(string providerId, string taskId, decimal? estimate = null,
        MpsPriceMetadata? price = null, DateTimeOffset? nowUtc = null)
    {
        ValidateKey(providerId, taskId);
        ValidatePrice(price);
        ValidatePriceState(price, MpsPriceStatus.Unknown);
        lock (gate)
        {
            var key = ResolveKey(providerId, taskId);
            if (!entries.TryGetValue(key, out var existing)) throw new KeyNotFoundException("待标记任务不存在。");
            if (existing.State == MpsBudgetLineState.Unknown && (estimate is null || existing.Amount == estimate)) return existing;
            if (existing.State is not (MpsBudgetLineState.Reserved or MpsBudgetLineState.Unknown))
                throw new InvalidOperationException("只有预留或未知任务才能标记未知费用。");
            var amount = estimate ?? existing.Amount;
            ValidateAmount(amount);
            if (existing.State == MpsBudgetLineState.Unknown && amount < existing.Amount)
                throw new InvalidOperationException("未知费用占用不能未经确认而减少。");
            EnsureCurrency(price);
            var unknown = existing with
            {
                Amount = amount, State = MpsBudgetLineState.Unknown,
                Price = price ?? MpsPriceMetadata.Unknown("provider-usage-unknown"),
                UpdatedUtc = nowUtc ?? DateTimeOffset.UtcNow
            };
            var occupiedWithoutCurrent = SumAll() - existing.Amount;
            if (occupiedWithoutCurrent + amount > limit)
                throw new MpsBudgetExceededException(limit, limit - occupiedWithoutCurrent, amount);
            entries[key] = unknown;
            return unknown;
        }
    }

    /// <summary>记录未知费用并继续占用预算的兼容名称。</summary>
    public MpsBudgetLedgerEntry RecordUnknown(string providerId, string taskId, decimal? estimate = null,
        MpsPriceMetadata? price = null, DateTimeOffset? nowUtc = null) =>
        MarkUnknown(providerId, taskId, estimate, price, nowUtc);

    /// <summary>取消尚未提交的预留并保留释放记录，避免重试再次重复扣账。</summary>
    public MpsBudgetLedgerEntry Release(string providerId, string taskId, DateTimeOffset? nowUtc = null)
    {
        ValidateKey(providerId, taskId);
        lock (gate)
        {
            var key = ResolveKey(providerId, taskId);
            if (!entries.TryGetValue(key, out var existing)) throw new KeyNotFoundException("待释放任务不存在。");
            if (existing.State == MpsBudgetLineState.Released) return existing;
            if (existing.State != MpsBudgetLineState.Reserved)
                throw new InvalidOperationException("只有预留中的任务才能释放。");
            var released = existing with { Amount = 0, State = MpsBudgetLineState.Released, UpdatedUtc = nowUtc ?? DateTimeOffset.UtcNow };
            entries[key] = released;
            return released;
        }
    }

    /// <summary>导入供应商用量并按 provider+task 对账；重复观察不增加费用。</summary>
    public MpsBudgetLedgerEntry Reconcile(string providerId, string taskId, decimal amount,
        MpsPriceMetadata? price = null, DateTimeOffset? nowUtc = null)
    {
        ValidateKey(providerId, taskId);
        ValidateAmount(amount);
        ValidatePrice(price);
        ValidatePriceState(price, MpsPriceStatus.Confirmed);
        lock (gate)
        {
            EnsureCurrency(price);
            var key = ResolveKey(providerId, taskId);
            if (!entries.TryGetValue(key, out var existing))
            {
                EnsureCapacity();
                EnsureAvailable(amount);
                var line = new MpsBudgetLedgerEntry(providerId.Trim(), taskId.Trim(), Currency, amount,
                    MpsBudgetLineState.Confirmed,
                    price ?? MpsPriceMetadata.Confirmed(amount, Currency, "request", "provider-usage"),
                    UpdatedUtc: nowUtc ?? DateTimeOffset.UtcNow);
                entries.Add(key, line);
                return line;
            }
            if (existing.State == MpsBudgetLineState.Confirmed && existing.Amount == amount) return existing;
            if (existing.State == MpsBudgetLineState.Reserved) return Confirm(providerId, taskId, amount, price, nowUtc);
            if (existing.State == MpsBudgetLineState.Unknown)
            {
                var occupiedWithoutCurrent = SumAll() - existing.Amount;
                if (occupiedWithoutCurrent + amount > limit)
                    throw new MpsBudgetExceededException(limit, limit - occupiedWithoutCurrent, amount);
                var confirmed = existing with { Amount = amount, State = MpsBudgetLineState.Confirmed,
                    Price = price ?? MpsPriceMetadata.Confirmed(amount, Currency, existing.Price.Unit ?? "request", "provider-usage"),
                    UpdatedUtc = nowUtc ?? DateTimeOffset.UtcNow };
                entries[key] = confirmed;
                return confirmed;
            }
            throw new InvalidOperationException("已释放任务不能再次对账。");
        }
    }

    /// <summary>返回稳定排序的副本，调用方不能修改账本内部状态。</summary>
    public IReadOnlyList<MpsBudgetLedgerEntry> Snapshot()
    {
        lock (gate) return entries.Values.OrderBy(value => value.StableKey, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>收到稳定任务号后原子绑定调用前的预留意图，避免释放/重建之间被并发占用。</summary>
    public MpsBudgetLedgerEntry BindTaskId(string providerId, string provisionalTaskId, string providerTaskId)
    {
        ValidateKey(providerId, provisionalTaskId);
        ValidateKey(providerId, providerTaskId);
        lock (gate)
        {
            var originalKey = StableKey(providerId, provisionalTaskId);
            var targetKey = StableKey(providerId, providerTaskId);
            if (taskBindings.TryGetValue(originalKey, out var boundKey))
            {
                if (boundKey != targetKey) throw new InvalidOperationException("预留意图已绑定其他供应商任务号。");
                return entries[boundKey];
            }
            if (!entries.TryGetValue(originalKey, out var existing)) throw new KeyNotFoundException("待绑定预留意图不存在。");
            if (originalKey == targetKey) return existing;
            if (taskBindings.ContainsValue(originalKey)) throw new InvalidOperationException("已绑定的供应商任务号不能再次替换。");
            if (existing.State is not (MpsBudgetLineState.Reserved or MpsBudgetLineState.Unknown))
                throw new InvalidOperationException("只有预留或未知费用意图可以绑定任务号。");
            if (entries.ContainsKey(targetKey) || taskBindings.ContainsKey(targetKey))
                throw new InvalidOperationException("供应商任务号已关联其他预算行，需核对而不能重复记账。");
            if (taskBindings.Count >= MaxEntries) throw new InvalidOperationException("预算任务绑定超过上限。");
            var bound = existing with { TaskId = providerTaskId.Trim() };
            entries.Remove(originalKey);
            entries.Add(targetKey, bound);
            taskBindings.Add(originalKey, targetKey);
            return bound;
        }
    }

    /// <summary>旧意图引用仍指向同一预算行；不形成无界别名链。</summary>
    private string ResolveKey(string providerId, string taskId)
    {
        var key = StableKey(providerId, taskId);
        return taskBindings.TryGetValue(key, out var bound) ? bound : key;
    }

    /// <summary>构造大小受限的稳定去重键。</summary>
    public static string StableKey(string providerId, string taskId)
    {
        ValidateKey(providerId, taskId);
        return providerId.Trim().ToLowerInvariant() + ":" + taskId.Trim().ToLowerInvariant();
    }

    private decimal Sum(MpsBudgetLineState state) => entries.Values.Where(value => value.State == state).Sum(value => value.Amount);
    private decimal SumAll() => entries.Values.Where(value => value.State is MpsBudgetLineState.Reserved or MpsBudgetLineState.Confirmed or MpsBudgetLineState.Unknown).Sum(value => value.Amount);
    private void EnsureCapacity() { if (entries.Count >= MaxEntries) throw new InvalidOperationException("预算账本条目超过上限。"); }
    private void EnsureAvailable(decimal amount)
    {
        var available = limit - SumAll();
        if (amount > available) throw new MpsBudgetExceededException(limit, available, amount);
    }

    private void EnsureCurrency(MpsPriceMetadata? price)
    {
        if (price?.Currency is { } currency && !string.Equals(NormalizeCurrency(currency), Currency, StringComparison.Ordinal))
            throw new InvalidOperationException("价格币种与预算账本不一致。");
    }

    private static string NormalizeCurrency(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16 || value.Any(char.IsControl)) throw new ArgumentException("币种无效。", nameof(value));
        return value.Trim().ToUpperInvariant();
    }

    private static void ValidateKey(string providerId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 128 || providerId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(taskId) || taskId.Length > 256 || taskId.Any(char.IsControl))
            throw new ArgumentException("提供商或任务标识无效。");
        ProviderAccountConfiguration.ValidateProviderId(providerId.Trim());
        ProviderAdapterValidation.ValidateTaskId(taskId.Trim());
    }

    private static void ValidateAmount(decimal amount)
    {
        if (amount < 0 || amount > 1_000_000_000m) throw new ArgumentOutOfRangeException(nameof(amount));
    }

    private static void ValidatePrice(MpsPriceMetadata? price) => price?.Validate();

    /// <summary>结算状态与价格事实必须一致，防止估算被误记为供应商确认费用。</summary>
    private static void ValidatePriceState(MpsPriceMetadata? price, MpsPriceStatus required)
    {
        if (price is not null && price.Status != required)
            throw new InvalidDataException(required == MpsPriceStatus.Confirmed
                ? "确认或对账必须使用已确认价格。"
                : "未知费用必须使用未知价格元数据。");
    }
}

/// <summary>硬预算拒绝的结构化异常；上层可以在界面显示剩余额度。</summary>
public sealed class MpsBudgetExceededException : InvalidOperationException
{
    public MpsBudgetExceededException(decimal limit, decimal available, decimal requested)
        : base("请求金额超过项目硬预算。")
    {
        Limit = limit;
        Available = available;
        Requested = requested;
    }

    public decimal Limit { get; }
    public decimal Available { get; }
    public decimal Requested { get; }
}
