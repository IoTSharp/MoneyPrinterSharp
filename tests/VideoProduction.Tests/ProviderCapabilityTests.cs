using System.Text.Json;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>里程碑 C 能力分类与模型描述符的离线回归，不访问供应商网络。</summary>
public static class ProviderCapabilityTests
{
    /// <summary>执行有限的分类、未知值和 JSON 契约断言。</summary>
    public static void Run()
    {
        CatalogSeparatesInputOutputAndUse();
        UnknownModelDoesNotInferCapability();
        DescriptorRoundTripsKnownPriceAndLimits();
        JsonRejectsNumericEnumsAndUnmappedFields();
    }

    private static void CatalogSeparatesInputOutputAndUse()
    {
        var catalog = MpsCapabilityDescriptor.CreateCatalog();
        Assert(catalog.Count == 7, "能力目录必须包含七类能力");
        Assert(catalog.Select(item => item.Kind).Distinct().Count() == 7, "能力分类不能重复");
        var lipSync = MpsCapabilityDescriptor.Create(MpsCapabilityKind.LipSync);
        var speech = MpsCapabilityDescriptor.Create(MpsCapabilityKind.SpeechSynthesis);
        Assert(lipSync.Inputs.Any(item => item.Name == "video") && lipSync.Inputs.Any(item => item.Name == "audio"), "口型同步必须要求视频和音频输入");
        Assert(speech.Inputs.Count == 1 && speech.Inputs[0].Name == "text", "语音合成输入应是文本");
        Assert(lipSync.Outputs.Single().Name == "video" && speech.Outputs.Any(item => item.Name == "audio"), "能力输出不能混用");
        Assert(lipSync.Uses.Contains("lip_sync") && speech.Uses.Contains("narration"), "能力用途应独立记录");
        foreach (var descriptor in catalog) descriptor.Validate();
    }

    private static void UnknownModelDoesNotInferCapability()
    {
        var descriptor = MpsModelDescriptor.Unknown("ViduQ2-Turbo");
        descriptor.Validate();
        Assert(descriptor.Capabilities.SequenceEqual([MpsCapabilityKind.Unknown]), "模型 ID 不能推断能力");
        Assert(descriptor.Modalities.SequenceEqual([MpsModelModality.Unknown]), "模型 ID 不能推断模态");
        var json = JsonSerializer.Serialize(descriptor, JsonFiles.Options);
        using var document = JsonDocument.Parse(json);
        var capabilities = document.RootElement.GetProperty("capabilities");
        Assert(capabilities.GetArrayLength() == 1 && capabilities[0].GetString() == "unknown", "未知能力必须显式写入 unknown");
        Assert(document.RootElement.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Null, "未知价格必须显式写入 null");
    }

    private static void DescriptorRoundTripsKnownPriceAndLimits()
    {
        var descriptor = new MpsModelDescriptor
        {
            ModelId = "example-tts",
            DisplayName = "Example TTS",
            ModelVersion = "2026-01",
            Modalities = [MpsModelModality.Text, MpsModelModality.Audio],
            Capabilities = [MpsCapabilityKind.SpeechSynthesis],
            Limits = new MpsModelLimits
            {
                ContextTokens = 4096,
                MaxDurationSeconds = 120,
                AspectRatios = null,
                Resolutions = null,
                Formats = ["audio/wav", "audio/mp3"]
            },
            ExecutionMode = MpsModelExecutionMode.Asynchronous,
            EvidenceStatus = MpsModelEvidenceStatus.Measured,
            Price = new MpsModelPrice { Amount = 0.12m, Currency = "CNY", Unit = "second", Basis = "measured" },
            Source = "offline-fixture",
            ObservedUtc = DateTimeOffset.UnixEpoch
        };
        descriptor.Validate();
        Assert(descriptor.Price!.IsKnown, "完整价格字段应标记为已知");
        var reopened = JsonSerializer.Deserialize<MpsModelDescriptor>(JsonSerializer.Serialize(descriptor, JsonFiles.Options), JsonFiles.Options)!;
        Assert(reopened.ExecutionMode == MpsModelExecutionMode.Asynchronous && reopened.Price!.Amount == 0.12m, "模型描述符 JSON 往返丢失字段");
        Assert(reopened.Limits.Formats!.SequenceEqual(["audio/wav", "audio/mp3"]) && reopened.Limits.AspectRatios is null, "未知限制应保留 null");
    }

    private static void JsonRejectsNumericEnumsAndUnmappedFields()
    {
        var descriptor = MpsModelDescriptor.Unknown("fixture");
        var numeric = JsonSerializer.SerializeToElement(new { model_id = "fixture", display_name = "fixture", modalities = new[] { 0 }, capabilities = new[] { "unknown" }, limits = new { }, execution_mode = "unknown", evidence_status = "unknown", price = (object?)null }, JsonFiles.Options);
        try { _ = JsonSerializer.Deserialize<MpsModelDescriptor>(numeric.GetRawText(), JsonFiles.Options); throw new InvalidOperationException("数字模态枚举不应通过"); }
        catch (JsonException) { }
        var unknownProperty = JsonSerializer.SerializeToElement(new { model_id = "fixture", display_name = "fixture", modalities = new[] { "unknown" }, capabilities = new[] { "unknown" }, limits = new { }, execution_mode = "unknown", evidence_status = "unknown", price = (object?)null, extra = true }, JsonFiles.Options);
        try { _ = JsonSerializer.Deserialize<MpsModelDescriptor>(unknownProperty.GetRawText(), JsonFiles.Options); throw new InvalidOperationException("未映射模型字段不应通过"); }
        catch (JsonException) { }
        descriptor.Validate();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
