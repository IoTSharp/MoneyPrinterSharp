using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>凭据保存位置；核心只保存位置引用，不接触凭据值。</summary>
[JsonConverter(typeof(CredentialStoreKindJsonConverter))]
public enum CredentialStoreKind
{
    WindowsCredentialManager
}

/// <summary>将凭据抽象为 Windows Credential Manager 目标名称。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CredentialTarget
{
    /// <summary>默认构造函数仅用于 JSON 反序列化，空目标会在校验时拒绝。</summary>
    public CredentialTarget() { }

    /// <summary>创建当前用户凭据管理器目标引用。</summary>
    public CredentialTarget(string targetName) => TargetName = targetName;

    /// <summary>创建指定存储类型的目标引用。</summary>
    public CredentialTarget(CredentialStoreKind store, string targetName)
    {
        Store = store;
        TargetName = targetName;
    }

    public CredentialStoreKind Store { get; set; } = CredentialStoreKind.WindowsCredentialManager;

    /// <summary>Credential Manager 的目标名；这里绝不保存密钥文本。</summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>兼容调用方对“目标”字段的语义命名，不参与序列化。</summary>
    [JsonIgnore]
    public string Target => TargetName;

    /// <summary>创建一个独立的脱敏目标快照。</summary>
    public CredentialTargetSnapshot ToRedactedSnapshot(CancellationToken cancellationToken = default)
    {
        Validate(cancellationToken);
        return new CredentialTargetSnapshot { Store = Store, TargetName = TargetName };
    }

    /// <summary>校验目标类型和名称，不读取凭据保险库。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(Store) || Store != CredentialStoreKind.WindowsCredentialManager ||
            string.IsNullOrWhiteSpace(TargetName) || TargetName.Length > 256 || TargetName.Any(char.IsControl) ||
            TargetName != TargetName.Trim() || TargetName.Contains("://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Windows 凭据目标无效。");
    }
}

/// <summary>可序列化的脱敏凭据目标，只含存储位置和目标名。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CredentialTargetSnapshot
{
    public CredentialStoreKind Store { get; set; } = CredentialStoreKind.WindowsCredentialManager;
    public string TargetName { get; set; } = string.Empty;

    /// <summary>兼容调用方对“目标”字段的语义命名，不参与序列化。</summary>
    [JsonIgnore]
    public string Target => TargetName;

    /// <summary>校验快照中仍只有位置引用。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        new CredentialTarget(Store, TargetName).Validate(cancellationToken);
    }
}

/// <summary>一个提供商账号的非敏感配置；凭据值永不进入此对象。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderAccountConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string ProviderId { get; set; } = string.Empty;
    public string AccountAlias { get; set; } = string.Empty;
    public CredentialTarget? CredentialTarget { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>校验提供商、别名和凭据目标的边界。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100)
            throw new InvalidDataException("账号配置版本无效。");

        ValidateProviderId(ProviderId);
        ValidateAlias(AccountAlias);
        if (CredentialTarget is null)
            throw new InvalidDataException("账号缺少 Windows 凭据目标。");
        CredentialTarget.Validate(cancellationToken);
    }

    /// <summary>校验提供商标识，避免它被当作 URL 或路径片段使用。</summary>
    internal static void ValidateProviderId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim() ||
            value.Any(char.IsControl) || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
            throw new InvalidDataException("提供商标识无效。");
    }

    /// <summary>校验账号别名并拒绝控制字符。</summary>
    internal static void ValidateAlias(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidDataException("账号别名无效。");
    }
}

/// <summary>当前使用的提供商和账号别名，不包含凭据。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderAccountSelection
{
    public string ProviderId { get; set; } = string.Empty;
    public string AccountAlias { get; set; } = string.Empty;

    /// <summary>校验选择键的格式。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProviderAccountConfiguration.ValidateProviderId(ProviderId);
        ProviderAccountConfiguration.ValidateAlias(AccountAlias);
    }
}

/// <summary>账号配置与多账号选择的最小契约；实现不得暴露凭据值。</summary>
public interface IProviderAccountConfigurationStore
{
    IReadOnlyList<ProviderAccountConfiguration> Accounts { get; }
    ProviderAccountSelection? CurrentSelection { get; }
    void Add(ProviderAccountConfiguration account, CancellationToken cancellationToken = default);
    bool Remove(string providerId, string accountAlias, CancellationToken cancellationToken = default);
    ProviderAccountConfiguration? Find(string providerId, string accountAlias, CancellationToken cancellationToken = default);
    ProviderAccountConfiguration Select(string providerId, string accountAlias, CancellationToken cancellationToken = default);
    void Validate(CancellationToken cancellationToken = default);
    ProviderAccountConfigurationSnapshot ToRedactedSnapshot(CancellationToken cancellationToken = default);
}

/// <summary>提供商账号集合，限制规模并维护一个可恢复的当前选择。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderAccountConfigurationSet : IProviderAccountConfigurationStore
{
    /// <summary>防止配置文件成为无界批处理输入。</summary>
    public const int MaxAccounts = 128;

    public int SchemaVersion { get; set; } = 1;
    public List<ProviderAccountConfiguration> Accounts { get; set; } = [];
    public ProviderAccountSelection? CurrentSelection { get; set; }

    IReadOnlyList<ProviderAccountConfiguration> IProviderAccountConfigurationStore.Accounts => Accounts;
    ProviderAccountSelection? IProviderAccountConfigurationStore.CurrentSelection => CurrentSelection;

    /// <summary>添加账号并拒绝重复的提供商/别名或凭据目标。</summary>
    public void Add(ProviderAccountConfiguration account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        cancellationToken.ThrowIfCancellationRequested();
        if (Accounts is null || Accounts.Count >= MaxAccounts)
            throw new InvalidDataException("账号数量超过上限。");
        account.Validate(cancellationToken);
        if (Find(account.ProviderId, account.AccountAlias, cancellationToken) is not null)
            throw new InvalidDataException("提供商账号别名重复。");
        for (var index = 0; index < Accounts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = Accounts[index];
            if (existing?.CredentialTarget is not null &&
                existing.CredentialTarget.Store == account.CredentialTarget!.Store &&
                string.Equals(existing.CredentialTarget.TargetName, account.CredentialTarget.TargetName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("凭据目标重复。");
        }
        Accounts.Add(account);
        Validate(cancellationToken);
    }

    /// <summary>移除账号；移除当前账号时同时清空选择。</summary>
    public bool Remove(string providerId, string accountAlias, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Accounts is null || Accounts.Count > MaxAccounts)
            throw new InvalidDataException("提供商账号集合无效。");
        ProviderAccountConfiguration.ValidateProviderId(providerId);
        ProviderAccountConfiguration.ValidateAlias(accountAlias);
        var index = -1;
        for (var candidateIndex = 0; candidateIndex < Accounts.Count; candidateIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = Accounts[candidateIndex];
            if (item is not null && string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.AccountAlias, accountAlias, StringComparison.OrdinalIgnoreCase))
            {
                index = candidateIndex;
                break;
            }
        }
        if (index < 0) return false;
        Accounts.RemoveAt(index);
        if (CurrentSelection is not null &&
            string.Equals(CurrentSelection.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(CurrentSelection.AccountAlias, accountAlias, StringComparison.OrdinalIgnoreCase))
            CurrentSelection = null;
        return true;
    }

    /// <summary>切换到已配置且启用的账号。</summary>
    public ProviderAccountConfiguration Select(string providerId, string accountAlias, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProviderAccountConfiguration.ValidateProviderId(providerId);
        ProviderAccountConfiguration.ValidateAlias(accountAlias);
        var account = Find(providerId, accountAlias, cancellationToken);
        if (account is null) throw new InvalidDataException("未找到提供商账号。");
        if (!account.Enabled) throw new InvalidDataException("提供商账号已停用。");
        CurrentSelection = new ProviderAccountSelection { ProviderId = account.ProviderId, AccountAlias = account.AccountAlias };
        return account;
    }

    /// <summary>按不区分大小写的提供商和别名查找账号。</summary>
    public ProviderAccountConfiguration? Find(string providerId, string accountAlias, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Accounts is null || Accounts.Count > MaxAccounts)
            throw new InvalidDataException("提供商账号集合无效。");
        ProviderAccountConfiguration.ValidateProviderId(providerId);
        ProviderAccountConfiguration.ValidateAlias(accountAlias);
        for (var index = 0; index < Accounts.Count && index < MaxAccounts; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = Accounts[index];
            if (account is not null && string.Equals(account.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(account.AccountAlias, accountAlias, StringComparison.OrdinalIgnoreCase)) return account;
        }
        return null;
    }

    /// <summary>校验完整集合和当前选择的存在性。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || Accounts is null || Accounts.Count > MaxAccounts)
            throw new InvalidDataException("提供商账号集合无效。");

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Accounts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = Accounts[index] ?? throw new InvalidDataException("账号配置为空。");
            account.Validate(cancellationToken);
            var aliasKey = account.ProviderId + "\u001f" + account.AccountAlias;
            if (!aliases.Add(aliasKey)) throw new InvalidDataException("提供商账号别名重复。");
            var target = account.CredentialTarget!;
            var targetKey = target.Store + "\u001f" + target.TargetName;
            if (!targets.Add(targetKey)) throw new InvalidDataException("凭据目标重复。");
        }

        if (CurrentSelection is null) return;
        CurrentSelection.Validate(cancellationToken);
        var selected = Find(CurrentSelection.ProviderId, CurrentSelection.AccountAlias, cancellationToken);
        if (selected is null || !selected.Enabled) throw new InvalidDataException("当前账号选择不存在或已停用。");
    }

    /// <summary>生成可写入项目或界面的脱敏副本，不读取任何凭据。</summary>
    public ProviderAccountConfigurationSnapshot ToRedactedSnapshot(CancellationToken cancellationToken = default)
    {
        Validate(cancellationToken);
        var snapshot = new ProviderAccountConfigurationSnapshot
        {
            SchemaVersion = SchemaVersion,
            CurrentSelection = CurrentSelection is null ? null : new ProviderAccountSelection
            {
                ProviderId = CurrentSelection.ProviderId,
                AccountAlias = CurrentSelection.AccountAlias
            }
        };
        for (var index = 0; index < Accounts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = Accounts[index];
            snapshot.Accounts.Add(new ProviderAccountSnapshot
            {
                SchemaVersion = account.SchemaVersion,
                ProviderId = account.ProviderId,
                AccountAlias = account.AccountAlias,
                Enabled = account.Enabled,
                CredentialTarget = account.CredentialTarget!.ToRedactedSnapshot(cancellationToken)
            });
        }
        return snapshot;
    }
}

/// <summary>账号配置的序列化脱敏快照，只有目标引用和选择状态。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderAccountConfigurationSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public List<ProviderAccountSnapshot> Accounts { get; set; } = [];
    public ProviderAccountSelection? CurrentSelection { get; set; }

    /// <summary>校验脱敏快照，确保界面不会显示越界或重复账号。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || Accounts is null || Accounts.Count > ProviderAccountConfigurationSet.MaxAccounts)
            throw new InvalidDataException("账号脱敏快照无效。");
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Accounts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = Accounts[index] ?? throw new InvalidDataException("账号脱敏快照为空。");
            if (account.SchemaVersion is < 1 or > 100)
                throw new InvalidDataException("账号脱敏快照版本无效。");
            ProviderAccountConfiguration.ValidateProviderId(account.ProviderId);
            ProviderAccountConfiguration.ValidateAlias(account.AccountAlias);
            account.CredentialTarget?.Validate(cancellationToken);
            if (account.CredentialTarget is null)
                throw new InvalidDataException("账号脱敏快照缺少凭据目标。");
            if (!aliases.Add(account.ProviderId + "\u001f" + account.AccountAlias) ||
                !targets.Add(account.CredentialTarget.Store + "\u001f" + account.CredentialTarget.TargetName))
                throw new InvalidDataException("账号脱敏快照包含重复项。");
        }
        if (CurrentSelection is null) return;
        CurrentSelection.Validate(cancellationToken);
        var found = false;
        var selectedEnabled = false;
        for (var index = 0; index < Accounts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = Accounts[index];
            if (string.Equals(account.ProviderId, CurrentSelection.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(account.AccountAlias, CurrentSelection.AccountAlias, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                selectedEnabled = account.Enabled;
                break;
            }
        }
        if (!found || !selectedEnabled) throw new InvalidDataException("账号脱敏快照的当前选择不存在或已停用。");
    }
}

/// <summary>单个账号的脱敏展示字段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderAccountSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string ProviderId { get; set; } = string.Empty;
    public string AccountAlias { get; set; } = string.Empty;
    public CredentialTargetSnapshot CredentialTarget { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

/// <summary>以小写蛇形字符串保存凭据存储类型，拒绝数字枚举。</summary>
public sealed class CredentialStoreKindJsonConverter : JsonStringEnumConverter
{
    public CredentialStoreKindJsonConverter() : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) { }
}
