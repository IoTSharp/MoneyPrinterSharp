namespace VideoProduction;

/// <summary>账号或模型事实的三态结果；无法查询时必须保持 Unknown。</summary>
public enum ProviderAccountFactState
{
    Unknown,
    Confirmed,
    Rejected
}

/// <summary>
/// 账号可用性记录。账号存在、模型存在、权限、余额、配额和区域分别记录，
/// 401/403/余额不足不会被压成“模型不存在”。
/// </summary>
public sealed record ProviderAccountAvailability(
    string ProviderId,
    string AccountAlias,
    string? ModelId,
    ProviderAccountFactState AccountExistence,
    ProviderAccountFactState ModelExistence,
    ProviderAccountFactState Permission,
    ProviderAccountFactState Balance,
    ProviderAccountFactState Quota,
    ProviderAccountFactState Region,
    ProviderAdapterErrorCategory ErrorCategory,
    DateTimeOffset ObservedUtc,
    string? RegionCode = null,
    decimal? BalanceRemaining = null,
    decimal? QuotaRemaining = null,
    string? Message = null)
{
    /// <summary>校验记录边界，禁止凭据、原始响应或无界数值进入状态。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(ProviderId, 128, "提供商标识");
        ValidateToken(AccountAlias, 128, "账号别名");
        if (ModelId is not null) ValidateToken(ModelId, 256, "模型标识");
        if (!Enum.IsDefined(AccountExistence) || !Enum.IsDefined(ModelExistence) ||
            !Enum.IsDefined(Permission) || !Enum.IsDefined(Balance) || !Enum.IsDefined(Quota) ||
            !Enum.IsDefined(Region) || !Enum.IsDefined(ErrorCategory) || ObservedUtc == default)
            throw new InvalidDataException("账号可用性状态无效。");
        if (RegionCode is not null) ValidateToken(RegionCode, 64, "区域");
        if (BalanceRemaining is < 0 or > 1_000_000_000_000m || QuotaRemaining is < 0 or > 1_000_000_000_000m)
            throw new InvalidDataException("余额或配额数值无效。");
        if (Message is not null) ValidateToken(Message, 512, "状态摘要");
    }

    /// <summary>账号状态是否已明确得到认证，和模型目录存在性相互独立。</summary>
    public bool IsAccountKnown => AccountExistence != ProviderAccountFactState.Unknown;

    /// <summary>模型存在性是否已明确；401/403/余额不足映射后通常仍为 Unknown。</summary>
    public bool IsModelExistenceKnown => ModelExistence != ProviderAccountFactState.Unknown;

    /// <summary>
    /// 将现有提供商账号契约映射为分离事实。适配器只提供账号级状态时，
    /// 未查询到的余额、配额、区域和模型存在性保持 Unknown。
    /// </summary>
    public static ProviderAccountAvailability FromAdapterState(
        string providerId,
        ProviderAdapterAccountState state,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var account = ProviderAccountFactState.Unknown;
        var model = ProviderAccountFactState.Unknown;
        var permission = ProviderAccountFactState.Unknown;
        var balance = ProviderAccountFactState.Unknown;
        var quota = ProviderAccountFactState.Unknown;

        switch (state.ErrorCategory)
        {
            case ProviderAdapterErrorCategory.Authentication:
                // 401只能说明当前凭据未被接受，不能断言账号或模型不存在。
                break;
            case ProviderAdapterErrorCategory.Permission:
                account = ProviderAccountFactState.Confirmed;
                permission = ProviderAccountFactState.Rejected;
                break;
            case ProviderAdapterErrorCategory.Billing:
                account = ProviderAccountFactState.Confirmed;
                balance = ProviderAccountFactState.Rejected;
                break;
            case ProviderAdapterErrorCategory.QuotaExceeded:
                account = ProviderAccountFactState.Confirmed;
                quota = ProviderAccountFactState.Rejected;
                break;
            default:
                if (state.Availability == ProviderAdapterAvailability.Available)
                {
                    account = ProviderAccountFactState.Confirmed;
                    permission = ProviderAccountFactState.Confirmed;
                }
                else if (state.Availability == ProviderAdapterAvailability.Unavailable)
                {
                    // 服务不可用时不把暂时性失败记作账号或模型不存在。
                }
                break;
        }

        var result = new ProviderAccountAvailability(
            providerId,
            state.AccountAlias,
            modelId,
            account,
            model,
            permission,
            balance,
            quota,
            ProviderAccountFactState.Unknown,
            state.ErrorCategory,
            state.ObservedUtc,
            Message: state.Message);
        result.Validate();
        return result;
    }

    /// <summary>只在适配器明确返回模型目录结果时更新模型存在性。</summary>
    public ProviderAccountAvailability WithModelExistence(ProviderAccountFactState existence)
    {
        if (!Enum.IsDefined(existence)) throw new InvalidDataException("模型存在性无效。");
        var result = this with { ModelExistence = existence };
        result.Validate();
        return result;
    }

    private static void ValidateToken(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new InvalidDataException($"{name}无效。");
    }
}

/// <summary>有界账号可用性记录表；按提供商、账号和可选模型隔离，避免与目录缓存混用。</summary>
public sealed class ProviderAccountAvailabilityStore
{
    private readonly object gate = new();
    private readonly int maxEntries;
    private readonly Dictionary<Key, ProviderAccountAvailability> entries = [];

    /// <summary>创建记录表；超过条目上限时只淘汰最早观察的旧记录。</summary>
    public ProviderAccountAvailabilityStore(int maxEntries = 1024)
    {
        if (maxEntries is < 1 or > 100_000) throw new InvalidDataException("账号状态记录上限无效。");
        this.maxEntries = maxEntries;
    }

    /// <summary>插入或更新一条状态；调用方取消时不修改记录。</summary>
    public void Record(ProviderAccountAvailability availability, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(availability);
        availability.Validate(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var key = new Key(availability.ProviderId, availability.AccountAlias, availability.ModelId);
        lock (gate)
        {
            if (!entries.ContainsKey(key) && entries.Count >= maxEntries)
            {
                var oldest = entries.OrderBy(pair => pair.Value.ObservedUtc).First().Key;
                entries.Remove(oldest);
            }
            entries[key] = availability;
        }
    }

    /// <summary>按提供商、账号和可选模型读取一条记录。</summary>
    public bool TryGet(string providerId, string accountAlias, string? modelId, out ProviderAccountAvailability availability)
    {
        var key = new Key(providerId, accountAlias, modelId);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var value))
            {
                availability = value;
                return true;
            }
        }

        availability = null!;
        return false;
    }

    /// <summary>返回有界、按观察时间排序的快照，不暴露内部字典。</summary>
    public IReadOnlyList<ProviderAccountAvailability> Snapshot(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Array.AsReadOnly(entries.Values.OrderBy(value => value.ObservedUtc).ToArray());
        }
    }

    private readonly record struct Key(string ProviderId, string AccountAlias, string? ModelId);
}
