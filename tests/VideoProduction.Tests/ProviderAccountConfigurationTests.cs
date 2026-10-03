using System.Text.Json;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>提供商账号配置、多账号切换和脱敏快照的离线合同测试。</summary>
public static class ProviderAccountConfigurationTests
{
    /// <summary>只使用内存对象，验证凭据目标引用和集合边界。</summary>
    public static void Run()
    {
        MultipleAccountsCanBeSelectedWithoutSecrets();
        ValidationRejectsDuplicatesAndUnsafeFields();
        SnapshotIsSerializableAndRejectsUnknownFields();
        CancellationIsHonoredAtBoundaries();
    }

    private static void MultipleAccountsCanBeSelectedWithoutSecrets()
    {
        var set = new ProviderAccountConfigurationSet();
        set.Add(Account("moark", "office", "MPS:Moark:Office"));
        set.Add(Account("moark", "lab", "MPS:Moark:Lab"));
        set.Add(Account("sonnet.vip", "default", "MPS:Sonnet:Default"));

        var selected = set.Select("MOARK", "LAB");
        Assert(selected.AccountAlias == "lab", "账号选择应按提供商和别名切换");
        Assert(set.CurrentSelection?.ProviderId == "moark" && set.CurrentSelection.AccountAlias == "lab", "当前账号选择未保存");
        Assert(set.Find("sonnet.vip", "default")?.CredentialTarget?.TargetName == "MPS:Sonnet:Default", "账号目标引用丢失");
        Assert(set.ToRedactedSnapshot().Accounts.Count == 3, "脱敏快照缺少账号");
    }

    private static void ValidationRejectsDuplicatesAndUnsafeFields()
    {
        var duplicateAlias = new ProviderAccountConfigurationSet();
        duplicateAlias.Add(Account("moark", "same", "MPS:Moark:One"));
        ExpectInvalid(() => duplicateAlias.Add(Account("MOARK", "SAME", "MPS:Moark:Two")), "同一提供商的重复别名应拒绝");

        var duplicateTarget = new ProviderAccountConfigurationSet();
        duplicateTarget.Add(Account("moark", "first", "MPS:Shared"));
        ExpectInvalid(() => duplicateTarget.Add(Account("sonnet.vip", "second", "mps:shared")), "重复凭据目标应拒绝");

        ExpectInvalid(() => Account("https://evil.example", "ok", "MPS:Target").Validate(), "提供商标识不应接受 URL");
        ExpectInvalid(() => Account("moark", "bad\nname", "MPS:Target").Validate(), "账号别名不应接受控制字符");
        ExpectInvalid(() => Account("moark", "ok", "MPS:Target\0").Validate(), "凭据目标不应接受控制字符");

        var missingSelection = new ProviderAccountConfigurationSet
        {
            Accounts = [Account("moark", "ok", "MPS:Target")],
            CurrentSelection = new ProviderAccountSelection { ProviderId = "moark", AccountAlias = "missing" }
        };
        ExpectInvalid(() => missingSelection.Validate(), "当前选择必须引用已配置账号");

        var disabled = new ProviderAccountConfigurationSet();
        disabled.Add(new ProviderAccountConfiguration
        {
            ProviderId = "moark", AccountAlias = "disabled", Enabled = false,
            CredentialTarget = new CredentialTarget("MPS:Disabled")
        });
        ExpectInvalid(() => disabled.Select("moark", "disabled"), "停用账号不应被选中");
    }

    private static void SnapshotIsSerializableAndRejectsUnknownFields()
    {
        var set = new ProviderAccountConfigurationSet();
        set.Add(Account("moark", "office", "MPS:Moark:Office"));
        set.Select("moark", "office");
        var snapshot = set.ToRedactedSnapshot();
        var json = JsonSerializer.Serialize(snapshot, JsonFiles.Options);

        Assert(json.Contains("windows_credential_manager", StringComparison.Ordinal), "快照应写出凭据存储类型");
        Assert(json.Contains("MPS:Moark:Office", StringComparison.Ordinal), "快照应保留目标引用以便显示");
        Assert(!json.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
            !json.Contains("authorization", StringComparison.OrdinalIgnoreCase), "快照不得包含秘密或请求头");

        var reopened = JsonSerializer.Deserialize<ProviderAccountConfigurationSnapshot>(json, JsonFiles.Options)!;
        Assert(reopened.Accounts.Count == 1 && reopened.CurrentSelection?.AccountAlias == "office", "脱敏快照 JSON 往返失败");
        reopened.Validate();

        var unknown = json.TrimEnd('}', '\n', '\r', ' ', '\t') + ",\"unknown\":true}";
        ExpectJson(() => JsonSerializer.Deserialize<ProviderAccountConfigurationSnapshot>(unknown, JsonFiles.Options), "快照未知字段应拒绝");
    }

    private static void CancellationIsHonoredAtBoundaries()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var set = new ProviderAccountConfigurationSet();
        ExpectCancelled(() => set.Validate(cancelled.Token), "集合校验应响应取消");
        ExpectCancelled(() => new CredentialTarget("MPS:Target").Validate(cancelled.Token), "凭据目标校验应响应取消");
    }

    private static ProviderAccountConfiguration Account(string provider, string alias, string target) => new()
    {
        ProviderId = provider,
        AccountAlias = alias,
        CredentialTarget = new CredentialTarget(target)
    };

    private static void ExpectInvalid(Action action, string message)
    {
        try { action(); throw new InvalidOperationException(message); }
        catch (InvalidDataException) { }
    }

    private static void ExpectJson(Action action, string message)
    {
        try { action(); throw new InvalidOperationException(message); }
        catch (JsonException) { }
    }

    private static void ExpectCancelled(Action action, string message)
    {
        try { action(); throw new InvalidOperationException(message); }
        catch (OperationCanceledException) { }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
