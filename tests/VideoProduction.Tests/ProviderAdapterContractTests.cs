using System.Text;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>统一提供商适配器契约的离线合同测试。</summary>
public static class ProviderAdapterContractTests
{
    /// <summary>验证分层接口、分页边界、未知状态和敏感字段拒绝。</summary>
    public static void Run()
    {
        InterfacesRemainSplit();
        ValidationRejectsSecretsAndUnboundedInputs();
        ModelsCarryUnknownEvidenceWithoutGuessing();
    }

    private static void InterfacesRemainSplit()
    {
        Assert(typeof(IProviderCatalogAdapter).GetMethod(nameof(IProviderCatalogAdapter.ListModelsAsync)) is not null, "目录接口缺失");
        Assert(typeof(IProviderAccountAdapter).GetMethod(nameof(IProviderAccountAdapter.GetAccountStateAsync)) is not null, "账号接口缺失");
        Assert(typeof(IProviderCapabilityAdapter).GetMethod(nameof(IProviderCapabilityAdapter.ProbeCapabilityAsync)) is not null, "能力探测接口缺失");
        Assert(typeof(IProviderTaskSubmissionAdapter).GetMethod(nameof(IProviderTaskSubmissionAdapter.SubmitAsync)) is not null, "提交接口缺失");
        Assert(typeof(IProviderTaskStatusAdapter).GetMethod(nameof(IProviderTaskStatusAdapter.GetStatusAsync)) is not null, "状态接口缺失");
        Assert(typeof(IProviderArtifactDownloadAdapter).GetMethod(nameof(IProviderArtifactDownloadAdapter.DownloadAsync)) is not null, "下载接口缺失");
        Assert(typeof(IProviderUsageAdapter).GetMethod(nameof(IProviderUsageAdapter.GetUsageAsync)) is not null, "用量接口缺失");
        Assert(typeof(IProviderErrorMapperAdapter).GetMethod(nameof(IProviderErrorMapperAdapter.MapError)) is not null, "错误映射接口缺失");
    }

    private static void ValidationRejectsSecretsAndUnboundedInputs()
    {
        ProviderAdapterValidation.Validate(new ProviderAdapterCatalogQuery(PageSize: 256));
        ProviderAdapterValidation.Validate(new ProviderAdapterTaskQuery("demo", "task-1"));
        try { ProviderAdapterValidation.Validate(new ProviderAdapterSubmitRequest("demo", "model", MpsCapabilityKind.TextPlanning, "https://secret")); throw new InvalidOperationException("提交不应接受 URL 原文"); }
        catch (InvalidDataException) { }
        try { ProviderAdapterValidation.Validate(new ProviderAdapterCatalogQuery(PageSize: 257)); throw new InvalidOperationException("目录页大小无上限"); }
        catch (InvalidDataException) { }
        try { ProviderAdapterValidation.ValidateTaskId("../escape"); throw new InvalidOperationException("任务号不应接受路径"); }
        catch (InvalidDataException) { }
        using var readOnly = new MemoryStream(Encoding.UTF8.GetBytes("fixture"), writable: false);
        try { ProviderAdapterValidation.Validate(new ProviderAdapterDownloadRequest("demo", "task-1", "out"), readOnly); throw new InvalidOperationException("下载不应写入只读流"); }
        catch (InvalidDataException) { }
    }

    private static void ModelsCarryUnknownEvidenceWithoutGuessing()
    {
        var descriptor = MpsModelDescriptor.Unknown("gateway-model");
        ProviderAdapterValidation.Validate(descriptor);
        Assert(descriptor.Capabilities.SequenceEqual([MpsCapabilityKind.Unknown]), "未知模型能力不应由名称推断");
        var page = new ProviderAdapterPage<MpsModelDescriptor>([descriptor], null, DateTimeOffset.UtcNow, "offline", null);
        ProviderAdapterValidation.Validate(page);
    }

    /// <summary>统一断言消息，避免引入额外测试框架和网络依赖。</summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
