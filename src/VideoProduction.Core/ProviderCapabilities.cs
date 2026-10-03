using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>提供商能力的稳定分类；未知值不能被模型名称隐式推断。</summary>
[JsonConverter(typeof(MpsCapabilityKindJsonConverter))]
public enum MpsCapabilityKind
{
    Unknown,
    TextPlanning,
    VisualUnderstanding,
    ImageGeneration,
    SpeechTranscription,
    SpeechSynthesis,
    VideoGeneration,
    LipSync
}

/// <summary>能力证据层级；公开列出、账号可用和实测通过是相互独立的事实。</summary>
[JsonConverter(typeof(MpsCapabilityEvidenceStatusJsonConverter))]
public enum MpsCapabilityEvidenceStatus
{
    Unknown,
    PubliclyListed,
    AccountCallable,
    Measured
}

/// <summary>以小写蛇形字符串序列化能力枚举，并拒绝数字枚举值。</summary>
public sealed class MpsCapabilityKindJsonConverter : JsonStringEnumConverter
{
    /// <summary>固定文件中的能力标识，未知值写为 unknown。</summary>
    public MpsCapabilityKindJsonConverter() : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) { }
}

/// <summary>以小写蛇形字符串序列化能力证据状态。</summary>
public sealed class MpsCapabilityEvidenceStatusJsonConverter : JsonStringEnumConverter
{
    /// <summary>固定文件中的证据状态标识，未知状态不冒充可用。</summary>
    public MpsCapabilityEvidenceStatusJsonConverter() : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) { }
}

/// <summary>能力输入或输出的一项数据契约；媒体类型未知时保留 null。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsCapabilityDataSpec
{
    public string Name { get; set; } = "unknown";
    public string? MediaType { get; set; }
    public bool Required { get; set; }
    public string? Description { get; set; }
}

/// <summary>供应商能力描述，明确区分输入、输出和用途，不包含密钥或原始响应。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsCapabilityDescriptor
{
    public int SchemaVersion { get; set; } = 1;
    public MpsCapabilityKind Kind { get; set; }
    public string DisplayName { get; set; } = "unknown";
    public MpsCapabilityEvidenceStatus EvidenceStatus { get; set; }
    public List<MpsCapabilityDataSpec> Inputs { get; set; } = [];
    public List<MpsCapabilityDataSpec> Outputs { get; set; } = [];
    public List<string> Uses { get; set; } = [];
    public string? Source { get; set; }
    public DateTimeOffset? ObservedUtc { get; set; }

    /// <summary>校验能力描述的规模和必要标识，避免把空描述当作已知能力。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || Kind == MpsCapabilityKind.Unknown ||
            string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 256 || DisplayName.Any(char.IsControl) ||
            Inputs is null || Outputs is null || Uses is null || Inputs.Count > 64 || Outputs.Count > 64 || Uses.Count > 64)
            throw new InvalidDataException("能力描述无效或能力仍未知。");
        ValidateSpecs(Inputs, cancellationToken);
        ValidateSpecs(Outputs, cancellationToken);
        foreach (var use in Uses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(use) || use.Length > 128 || use.Any(char.IsControl))
                throw new InvalidDataException("能力用途无效。");
        }
        if (Source is not null && (Source.Length > 2048 || Source.Any(char.IsControl)))
            throw new InvalidDataException("能力来源无效。");
    }

    /// <summary>返回七类能力的脱敏模板；调用方必须另行填入提供商和实测证据。</summary>
    public static IReadOnlyList<MpsCapabilityDescriptor> CreateCatalog()
    {
        return
        [
            Create(MpsCapabilityKind.TextPlanning, "文本策划/脚本", [("brief", "text/plain", true, "用户目标、受众和约束")], [("plan", "application/json", true, "章节、讲稿或分镜结构")], ["planning", "script"]),
            Create(MpsCapabilityKind.VisualUnderstanding, "视觉理解", [("image_or_video", "unknown", true, "截图、录屏或视频帧")], [("observations", "application/json", true, "可追溯的视觉观察和证据")], ["evidence", "review"]),
            Create(MpsCapabilityKind.ImageGeneration, "图片生成", [("prompt", "text/plain", true, "静态画面描述"), ("reference_image", "image/*", false, "可选参考图")], [("image", "image/*", true, "静态图片")], ["illustration", "overlay"]),
            Create(MpsCapabilityKind.SpeechTranscription, "语音转写", [("audio", "audio/*", true, "录音或旁白音频")], [("transcript", "text/plain", true, "文字和时间戳")], ["transcription", "captioning"]),
            Create(MpsCapabilityKind.SpeechSynthesis, "语音合成", [("text", "text/plain", true, "冻结后的旁白文本")], [("audio", "audio/*", true, "配音音频"), ("timing", "application/json", false, "可选音素或时间信息")], ["narration", "voiceover"]),
            Create(MpsCapabilityKind.VideoGeneration, "视频生成", [("prompt", "text/plain", true, "动作或镜头描述"), ("reference_image", "image/*", false, "可选首帧或参考图")], [("video", "video/*", true, "连续视频")], ["motion", "b_roll"]),
            Create(MpsCapabilityKind.LipSync, "口型同步", [("video", "video/*", true, "人物视频"), ("audio", "audio/*", true, "与视频绑定的最终音频")], [("video", "video/*", true, "绑定同一音频的口型视频")], ["lip_sync", "presenter"])
        ];
    }

    /// <summary>按分类创建一份独立模板，避免调用方修改共享对象。</summary>
    public static MpsCapabilityDescriptor Create(MpsCapabilityKind kind)
    {
        var template = CreateCatalog().FirstOrDefault(item => item.Kind == kind);
        if (template is null) throw new ArgumentOutOfRangeException(nameof(kind), "未知能力没有模板。");
        return template;
    }

    private static MpsCapabilityDescriptor Create(
        MpsCapabilityKind kind,
        string displayName,
        (string Name, string MediaType, bool Required, string Description)[] inputs,
        (string Name, string MediaType, bool Required, string Description)[] outputs,
        string[] uses)
    {
        return new MpsCapabilityDescriptor
        {
            Kind = kind,
            DisplayName = displayName,
            EvidenceStatus = MpsCapabilityEvidenceStatus.Unknown,
            Inputs = inputs.Select(item => new MpsCapabilityDataSpec
            {
                Name = item.Name,
                MediaType = item.MediaType == "unknown" ? null : item.MediaType,
                Required = item.Required,
                Description = item.Description
            }).ToList(),
            Outputs = outputs.Select(item => new MpsCapabilityDataSpec
            {
                Name = item.Name,
                MediaType = item.MediaType == "unknown" ? null : item.MediaType,
                Required = item.Required,
                Description = item.Description
            }).ToList(),
            Uses = [.. uses]
        };
    }

    private static void ValidateSpecs(IEnumerable<MpsCapabilityDataSpec> specs, CancellationToken cancellationToken)
    {
        foreach (var spec in specs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (spec is null || string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 128 || spec.Name.Any(char.IsControl) ||
                spec.MediaType is not null && (spec.MediaType.Length > 128 || spec.MediaType.Any(char.IsControl)) ||
                spec.Description is not null && (spec.Description.Length > 2048 || spec.Description.Any(char.IsControl)))
                throw new InvalidDataException("能力输入输出字段无效。");
        }
    }
}
