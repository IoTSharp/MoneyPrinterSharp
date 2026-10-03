using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>模型可声明的模态；缺失信息使用 unknown，不按模型名称猜测。</summary>
[JsonConverter(typeof(MpsModelModalityJsonConverter))]
public enum MpsModelModality
{
    Unknown,
    Text,
    Image,
    Audio,
    Video
}

/// <summary>模型执行方式；无法确认时保持 unknown。</summary>
[JsonConverter(typeof(MpsModelExecutionModeJsonConverter))]
public enum MpsModelExecutionMode
{
    Unknown,
    Synchronous,
    Asynchronous
}

/// <summary>模型目录事实层级；目录存在不等于当前账号可调用或能力已实测。</summary>
[JsonConverter(typeof(MpsModelEvidenceStatusJsonConverter))]
public enum MpsModelEvidenceStatus
{
    Unknown,
    PubliclyListed,
    AccountCallable,
    Measured
}

/// <summary>模型枚举共用的小写蛇形 JSON 转换器基类。</summary>
public abstract class MpsModelEnumJsonConverter : JsonStringEnumConverter
{
    /// <summary>禁止数字枚举并固定小写蛇形命名。</summary>
    protected MpsModelEnumJsonConverter() : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) { }
}

/// <summary>模态枚举的 JSON 转换器。</summary>
public sealed class MpsModelModalityJsonConverter : MpsModelEnumJsonConverter { }

/// <summary>执行方式枚举的 JSON 转换器。</summary>
public sealed class MpsModelExecutionModeJsonConverter : MpsModelEnumJsonConverter { }

/// <summary>模型证据枚举的 JSON 转换器。</summary>
public sealed class MpsModelEvidenceStatusJsonConverter : MpsModelEnumJsonConverter { }

/// <summary>模型输入限制和输出限制；未知限制保留 null，不能当作无限制。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsModelLimits
{
    public int? ContextTokens { get; set; }
    public double? MaxDurationSeconds { get; set; }
    public List<string>? AspectRatios { get; set; }
    public List<string>? Resolutions { get; set; }
    public List<string>? Formats { get; set; }
}

/// <summary>已知价格的计量方式；金额或币种缺失时整体仍是未知价格。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsModelPrice
{
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Unit { get; set; }
    public string? Basis { get; set; }

    /// <summary>只有金额、币种、单位和计价依据都存在时才可视为已知价格。</summary>
    [JsonIgnore]
    public bool IsKnown => Amount is not null && Currency is not null && Unit is not null && Basis is not null;
}

/// <summary>可序列化的模型描述符；能力列表必须由目录或实测显式提供。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsModelDescriptor
{
    public int SchemaVersion { get; set; } = 1;
    public string ModelId { get; set; } = "unknown";
    public string DisplayName { get; set; } = "unknown";
    public string? ModelVersion { get; set; }
    public List<MpsModelModality> Modalities { get; set; } = [MpsModelModality.Unknown];
    public List<MpsCapabilityKind> Capabilities { get; set; } = [MpsCapabilityKind.Unknown];
    public MpsModelLimits Limits { get; set; } = new();
    public MpsModelExecutionMode ExecutionMode { get; set; }
    public MpsModelEvidenceStatus EvidenceStatus { get; set; }
    public MpsModelPrice? Price { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? ObservedUtc { get; set; }

    /// <summary>校验描述符边界；不会根据 ID、展示名或版本推导模态和能力。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || !ValidText(ModelId, 256) || !ValidText(DisplayName, 256) ||
            Modalities is null || Modalities.Count == 0 || Modalities.Count > 16 || Modalities.Contains(MpsModelModality.Unknown) && Modalities.Count > 1 ||
            Capabilities is null || Capabilities.Count == 0 || Capabilities.Count > 32 || Capabilities.Contains(MpsCapabilityKind.Unknown) && Capabilities.Count > 1 ||
            Limits is null)
            throw new InvalidDataException("模型描述符无效或包含未决未知值。");
        if (ModelVersion is not null && !ValidText(ModelVersion, 128)) throw new InvalidDataException("模型版本无效。");
        if (Source is not null && !ValidText(Source, 2048)) throw new InvalidDataException("模型来源无效。");
        ValidateLimits(Limits);
        if (Price is not null) ValidatePrice(Price);
    }

    /// <summary>清除任何猜测并构造未知模型描述符，适用于无法读取目录的场景。</summary>
    public static MpsModelDescriptor Unknown(string? modelId = null)
    {
        return new MpsModelDescriptor
        {
            ModelId = string.IsNullOrWhiteSpace(modelId) ? "unknown" : modelId,
            DisplayName = "unknown",
            Modalities = [MpsModelModality.Unknown],
            Capabilities = [MpsCapabilityKind.Unknown],
            Limits = new(),
            ExecutionMode = MpsModelExecutionMode.Unknown,
            EvidenceStatus = MpsModelEvidenceStatus.Unknown,
            Price = null
        };
    }

    private static bool ValidText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    private static void ValidateLimits(MpsModelLimits limits)
    {
        if (limits.ContextTokens is <= 0 || limits.MaxDurationSeconds is <= 0 || limits.MaxDurationSeconds is double.NaN or double.PositiveInfinity or double.NegativeInfinity ||
            !ValidList(limits.AspectRatios, 64, 64) || !ValidList(limits.Resolutions, 64, 64) || !ValidList(limits.Formats, 64, 64))
            throw new InvalidDataException("模型限制无效。");
    }

    private static void ValidatePrice(MpsModelPrice price)
    {
        if (price.Amount is < 0 || price.Currency is not null && !ValidText(price.Currency, 16) ||
            price.Unit is not null && !ValidText(price.Unit, 64) || price.Basis is not null && !ValidText(price.Basis, 512))
            throw new InvalidDataException("模型价格无效。");
    }

    private static bool ValidList(List<string>? values, int maxCount, int maxLength) =>
        values is null || values.Count <= maxCount && values.All(value => ValidText(value, maxLength));
}
