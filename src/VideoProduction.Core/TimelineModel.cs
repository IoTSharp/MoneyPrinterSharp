using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>项目时间线支持的轨道类型；枚举值以小写字符串写入项目文件。</summary>
[JsonConverter(typeof(MpsTrackKindJsonConverter))]
public enum MpsTrackKind
{
    Screen,
    Presenter,
    Narration,
    Music,
    Subtitle,
    Overlay
}

/// <summary>仅为时间线轨道提供小写字符串枚举，避免改变其他旧 JSON 契约。</summary>
public sealed class MpsTrackKindJsonConverter : JsonStringEnumConverter
{
    /// <summary>只接受小写字符串，拒绝数字枚举，保持项目文件可读且稳定。</summary>
    public MpsTrackKindJsonConverter() : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) { }
}

/// <summary>项目画布与时间基配置；预览和渲染共用同一份配置。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProjectProfile
{
    public string Id { get; set; } = "main";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int FpsNum { get; set; } = 30;
    public int FpsDen { get; set; } = 1;
    public int AudioSampleRate { get; set; } = 48_000;
}

/// <summary>时间线轨道；order 越小越先处理，轨道内片段引用同一项目资产索引。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsTrack
{
    public string Id { get; set; } = "";
    public MpsTrackKind Kind { get; set; }
    public int Order { get; set; }
    public string? ParentTrackId { get; set; }
    public bool Muted { get; set; }
    public bool Hidden { get; set; }
    public List<MpsClip> Clips { get; set; } = [];
}

/// <summary>项目时间线片段；源范围和时间线位置均为精确有理数边界。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsClip
{
    public string Id { get; set; } = "";
    public string TrackId { get; set; } = "";
    public string? AssetId { get; set; }
    public MpsTime TimelineStart { get; set; } = new();
    public MpsTime SourceIn { get; set; } = new();
    public MpsTime SourceOut { get; set; } = new() { Num = 1 };
    public long SpeedNum { get; set; } = 1;
    public long SpeedDen { get; set; } = 1;
    public MpsClipProperties Properties { get; set; } = new();
}

/// <summary>JSON 中的精确非负秒数，固定为 { num, den }，不使用浮点累计时间。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsTime
{
    public long Num { get; set; }
    public long Den { get; set; } = 1;

    /// <summary>转换为核心时间坐标并执行非负、分母及十二小时边界校验。</summary>
    public RationalTime ToRational() => new(Num, Den);

    /// <summary>从核心时间坐标生成稳定的约分 JSON 值。</summary>
    public static MpsTime From(RationalTime value) => new() { Num = value.Numerator, Den = value.Denominator };
}

/// <summary>片段的通用音量、透明度、布局及轨道专属显示属性。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsClipProperties
{
    public double GainDb { get; set; }
    public double Opacity { get; set; } = 1;
    public MpsTransform? Transform { get; set; }
    public MpsSubtitleProperties? Subtitle { get; set; }
    public MpsPresenterOverlay? PresenterOverlay { get; set; }
    public bool? LipSynced { get; set; }
}

/// <summary>视觉片段的归一化位置及旋转；同一值供预览和导出使用。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsTransform
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
    public double Rotation { get; set; }
}

/// <summary>字幕文字及样式；字幕片段可不引用媒体资产。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSubtitleProperties
{
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 42;
    public string Color { get; set; } = "#FFFFFF";
    public string Alignment { get; set; } = "center";
}

/// <summary>主持人叠加与抠像参数；口型声明位于片段属性的 LipSynced。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsPresenterOverlay
{
    public string KeyColor { get; set; } = "0x50B470";
    public double Similarity { get; set; } = 0.2;
    public double Blend { get; set; } = 0.08;
}

/// <summary>项目时间线模型的集中校验器；只校验结构，不检查媒体文件内容。</summary>
public static class MpsTimelineValidation
{
    private const int MaxProfiles = 8;
    private const int MaxTracks = 64;
    private const int MaxClipsPerTrack = 4096;

    /// <summary>校验画布、轨道、片段引用、坐标、速度及专属属性。</summary>
    public static void Validate(MpsProjectDocument project, CancellationToken cancellationToken = default)
    {
        if (project is null || project.Assets is null || project.Profiles is null || project.Tracks is null)
            throw new InvalidDataException("项目时间线集合不能为空。");
        if (project.Assets.Count > 4096 || project.Profiles.Count > MaxProfiles || project.Tracks.Count > MaxTracks)
            throw new InvalidDataException("项目素材、画布或轨道数量超过上限。");

        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in project.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (profile is null || !ValidId(profile.Id) || !profileIds.Add(profile.Id) ||
                profile.Width is < 320 or > 3840 || profile.Height is < 240 or > 2160 ||
                profile.Width % 2 != 0 || profile.Height % 2 != 0 ||
                profile.AudioSampleRate is < 1 or > 384_000)
                throw new InvalidDataException("项目画布配置无效或重复。");
            try { _ = new FrameRate(profile.FpsNum, profile.FpsDen); }
            catch (ArgumentOutOfRangeException) { throw new InvalidDataException("项目画布帧率无效。"); }
        }
        if (project.Profiles.Count > 0 && (string.IsNullOrWhiteSpace(project.ActiveProfileId) ||
            !profileIds.Contains(project.ActiveProfileId)))
            throw new InvalidDataException("项目当前画布必须引用已定义的配置。");

        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in project.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset is null || !ValidId(asset.Id) || !assetIds.Add(asset.Id))
                throw new InvalidDataException("项目素材索引无效或重复。");
        }
        var trackIds = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        var clipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in project.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (track is null || !Enum.IsDefined(track.Kind) || !ValidId(track.Id) || !trackIds.Add(track.Id) || track.Order < 0 || track.Order > 4095 || !orders.Add(track.Order) ||
                track.Clips is null || track.Clips.Count > MaxClipsPerTrack)
                throw new InvalidDataException("时间线轨道无效、重复或超出规模。");
            foreach (var clip in track.Clips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clip is null || !ValidId(clip.Id) || !clipIds.Add(clip.Id) || clip.TrackId != track.Id ||
                    clip.TimelineStart is null || clip.SourceIn is null || clip.SourceOut is null || clip.Properties is null)
                    throw new InvalidDataException("时间线片段标识或属性无效、重复。");
                if (track.Kind != MpsTrackKind.Subtitle && (string.IsNullOrWhiteSpace(clip.AssetId) || !assetIds.Contains(clip.AssetId)))
                    throw new InvalidDataException("媒体片段必须引用项目素材。");
                if (track.Kind == MpsTrackKind.Subtitle && clip.AssetId is not null && !assetIds.Contains(clip.AssetId))
                    throw new InvalidDataException("字幕片段引用了不存在的素材。");

                RationalTime timelineStart;
                RationalTime sourceIn;
                RationalTime sourceOut;
                try
                {
                    timelineStart = clip.TimelineStart.ToRational();
                    sourceIn = clip.SourceIn.ToRational();
                    sourceOut = clip.SourceOut.ToRational();
                    _ = new ClipTimeMapping(new SourceTime(sourceIn), new SourceTime(sourceOut),
                        new ProjectTime(timelineStart), clip.SpeedNum, clip.SpeedDen);
                }
                catch (ArgumentException) { throw new InvalidDataException("时间线片段坐标或速度无效。"); }
                catch (OverflowException) { throw new InvalidDataException("时间线片段坐标超出范围。"); }

                ValidateProperties(track.Kind, clip.Properties);
            }
        }
        foreach (var track in project.Tracks)
            EnsureParentDepth(track, trackIds, project.Tracks);
    }

    /// <summary>校验片段属性的有限数值和轨道专属字段。</summary>
    private static void ValidateProperties(MpsTrackKind kind, MpsClipProperties properties)
    {
        if (!double.IsFinite(properties.GainDb) || properties.GainDb is < -120 or > 24 ||
            !double.IsFinite(properties.Opacity) || properties.Opacity is < 0 or > 1)
            throw new InvalidDataException("片段增益或透明度无效。");
        if (properties.Transform is { } transform &&
            (!double.IsFinite(transform.X) || !double.IsFinite(transform.Y) ||
             !double.IsFinite(transform.Width) || !double.IsFinite(transform.Height) || !double.IsFinite(transform.Rotation) ||
             transform.Width <= 0 || transform.Height <= 0 || transform.Width > 4 || transform.Height > 4 ||
             transform.X is < -4 or > 4 || transform.Y is < -4 or > 4 || transform.Rotation is < -360 or > 360))
            throw new InvalidDataException("片段布局属性无效。");
        if (kind == MpsTrackKind.Subtitle && properties.Subtitle is null)
            throw new InvalidDataException("字幕轨道片段必须包含字幕属性。");
        if (properties.Subtitle is { } subtitle &&
            (kind != MpsTrackKind.Subtitle || string.IsNullOrWhiteSpace(subtitle.Text) || subtitle.Text.Length > 10_000 ||
             string.IsNullOrWhiteSpace(subtitle.FontFamily) || subtitle.FontFamily.Length > 128 ||
             !double.IsFinite(subtitle.FontSize) || subtitle.FontSize is <= 0 or > 512 ||
             string.IsNullOrWhiteSpace(subtitle.Color) || string.IsNullOrWhiteSpace(subtitle.Alignment)))
            throw new InvalidDataException("字幕属性无效或出现在非字幕轨道。");
        if (kind == MpsTrackKind.Presenter && properties.PresenterOverlay is null)
            throw new InvalidDataException("主持人轨道片段必须包含叠加属性。");
        if (properties.PresenterOverlay is { } presenter &&
            (kind != MpsTrackKind.Presenter || !System.Text.RegularExpressions.Regex.IsMatch(presenter.KeyColor, "^0x[0-9a-fA-F]{6}$") ||
             !double.IsFinite(presenter.Similarity) || presenter.Similarity is < 0.01 or > 1 ||
             !double.IsFinite(presenter.Blend) || presenter.Blend is < 0 or > 1))
            throw new InvalidDataException("主持人叠加属性无效或出现在非主持人轨道。");
    }

    /// <summary>沿父轨道向上遍历，限制层级深度并拒绝循环。</summary>
    private static void EnsureParentDepth(MpsTrack track, HashSet<string> trackIds, IReadOnlyList<MpsTrack> tracks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { track.Id };
        var parentId = track.ParentTrackId;
        for (var depth = 0; parentId is not null; depth++)
        {
            if (depth >= 32 || !trackIds.Contains(parentId) || !seen.Add(parentId))
                throw new InvalidDataException("轨道父级层级无效或存在循环。");
            parentId = tracks.Single(parent => parent.Id == parentId).ParentTrackId;
        }
    }

    /// <summary>稳定 ID 只允许短 ASCII 标识，避免路径、控制字符和日志换行进入项目文件。</summary>
    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
