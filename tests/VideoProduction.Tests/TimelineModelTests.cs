using System.Text.Json;
using System.Text.Json.Nodes;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>验证多轨项目模型可序列化、重建并拒绝破坏性结构。</summary>
public static class TimelineModelTests
{
    private const string ScreenAssetId = "11111111111111111111111111111111";
    private const string PresenterAssetId = "22222222222222222222222222222222";
    private const string NarrationAssetId = "33333333333333333333333333333333";
    private const string MusicAssetId = "44444444444444444444444444444444";

    /// <summary>执行有限的时间线模型断言，不访问网络或媒体服务。</summary>
    public static void Run()
    {
        var project = CreateProject();
        RoundTripsSingleSourceOfTruth(project);
        PersistsThroughProjectDirectory(project);
        RejectsInvalidTimeline(project);
        RejectsUnknownNestedFields(project);
    }

    /// <summary>五类轨道共享素材索引，JSON 往返后精确保留时间、层级状态和专属属性。</summary>
    private static void RoundTripsSingleSourceOfTruth(MpsProjectDocument project)
    {
        MpsTimelineValidation.Validate(project);
        var json = JsonSerializer.Serialize(project, JsonFiles.Options);
        Assert(json.Contains("\"kind\": \"screen\"", StringComparison.Ordinal), "轨道类型应以受控字符串写入");
        Assert(json.Contains("\"timeline_start\":", StringComparison.Ordinal) && json.Contains("\"num\": 1", StringComparison.Ordinal), "时间应以有理数对象写入");
        var reopened = JsonSerializer.Deserialize<MpsProjectDocument>(json, JsonFiles.Options) ?? throw new InvalidOperationException("项目 JSON 为空");
        MpsTimelineValidation.Validate(reopened);
        Assert(reopened.Tracks.Count == 5 && reopened.Tracks[0].Clips.Count == 1, "轨道或片段往返数量变化");
        Assert(reopened.Tracks[0].Clips[0].TimelineStart.ToRational() == new RationalTime(1, 3), "时间线起点精度丢失");
        Assert(reopened.Tracks[1].Clips[0].Properties.PresenterOverlay is not null, "主持人叠加属性丢失");
        Assert(reopened.Tracks[4].Clips[0].Properties.Subtitle?.Text == "欢迎使用 MPS", "字幕属性丢失");
        Assert(reopened.Tracks[0].Muted && reopened.Tracks[1].Hidden, "静音或隐藏状态丢失");
    }

    /// <summary>轨道和片段外键、坐标、速度及属性越界时必须拒绝。</summary>
    private static void RejectsInvalidTimeline(MpsProjectDocument source)
    {
        var missingAsset = Clone(source);
        missingAsset.Tracks[0].Clips[0].AssetId = "missing";
        AssertRejects(missingAsset, "缺失素材引用");

        var duplicateTrack = Clone(source);
        duplicateTrack.Tracks[1].Id = duplicateTrack.Tracks[0].Id;
        AssertRejects(duplicateTrack, "重复轨道 ID");

        var badSpeed = Clone(source);
        badSpeed.Tracks[0].Clips[0].SpeedNum = 0;
        AssertRejects(badSpeed, "无效变速");

        var badRange = Clone(source);
        badRange.Tracks[0].Clips[0].SourceOut = new MpsTime { Num = 0, Den = 1 };
        AssertRejects(badRange, "无效源范围");

        var badSubtitle = Clone(source);
        badSubtitle.Tracks[4].Clips[0].Properties.Subtitle!.Text = "";
        AssertRejects(badSubtitle, "空字幕");

        var badProfile = Clone(source);
        badProfile.Profiles[0].FpsNum = 0;
        AssertRejects(badProfile, "无效帧率");

        var cycle = Clone(source);
        cycle.Tracks[0].ParentTrackId = "presenter";
        AssertRejects(cycle, "轨道父级循环");

        var overlap = Clone(source);
        overlap.Tracks[0].Clips.Add(new MpsClip
        {
            Id = "screen-overlap",
            TrackId = "screen",
            AssetId = ScreenAssetId,
            TimelineStart = new MpsTime { Num = 1, Den = 1 },
            SourceIn = new MpsTime(),
            SourceOut = new MpsTime { Num = 2, Den = 1 }
        });
        MpsTimelineValidation.Validate(overlap);
    }

    /// <summary>通过项目目录保存并重开，确认根文件仍是预览和渲染的唯一时间线来源。</summary>
    private static void PersistsThroughProjectDirectory(MpsProjectDocument project)
    {
        var root = Path.Combine(Path.GetTempPath(), "mps-timeline-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = ProjectDirectory.Create(root, "时间线持久化测试");
            ProjectDirectory.Save(root, project);
            var reopened = ProjectDirectory.Open(root);
            Assert(reopened.ActiveProfileId == "landscape" && reopened.Tracks.Count == 5, "项目目录重开丢失时间线");
            Assert(reopened.Tracks[1].ParentTrackId == "screen", "轨道父级没有持久化");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>根及嵌套模型均拒绝未知字段，避免保存时静默丢失。</summary>
    private static void RejectsUnknownNestedFields(MpsProjectDocument project)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(project, JsonFiles.Options))!.AsObject();
        node["tracks"]![0]!["unexpected"] = true;
        try
        {
            _ = JsonSerializer.Deserialize<MpsProjectDocument>(node.ToJsonString(), JsonFiles.Options);
            throw new InvalidOperationException("嵌套未知字段未被拒绝");
        }
        catch (JsonException) { }
    }

    /// <summary>构造包含屏幕、主持人、旁白、音乐和字幕的最小可重建项目。</summary>
    private static MpsProjectDocument CreateProject()
    {
        return new MpsProjectDocument
        {
            Id = "0123456789abcdef0123456789abcdef",
            Profiles = [new MpsProjectProfile { Id = "landscape", Width = 1920, Height = 1080 }],
            ActiveProfileId = "landscape",
            Assets =
            [
                new MpsAssetReference { Id = ScreenAssetId, Path = "assets/source/screen.mp4" },
                new MpsAssetReference { Id = PresenterAssetId, Path = "assets/generated/presenter.webm" },
                new MpsAssetReference { Id = NarrationAssetId, Path = "assets/generated/narration.m4a" },
                new MpsAssetReference { Id = MusicAssetId, Path = "assets/source/music.m4a" }
            ],
            Tracks =
            [
                new MpsTrack
                {
                    Id = "screen", Kind = MpsTrackKind.Screen, Order = 0, Muted = true,
                    Clips = [new MpsClip { Id = "screen-clip", TrackId = "screen", AssetId = ScreenAssetId, TimelineStart = new MpsTime { Num = 1, Den = 3 }, SourceOut = new MpsTime { Num = 3 } }]
                },
                new MpsTrack
                {
                    Id = "presenter", Kind = MpsTrackKind.Presenter, Order = 1, ParentTrackId = "screen", Hidden = true,
                    Clips = [new MpsClip { Id = "presenter-clip", TrackId = "presenter", AssetId = PresenterAssetId, SourceOut = new MpsTime { Num = 3 }, Properties = new MpsClipProperties { PresenterOverlay = new MpsPresenterOverlay(), LipSynced = false } }]
                },
                new MpsTrack
                {
                    Id = "narration", Kind = MpsTrackKind.Narration, Order = 2,
                    Clips = [new MpsClip { Id = "narration-clip", TrackId = "narration", AssetId = NarrationAssetId, SourceOut = new MpsTime { Num = 3 }, Properties = new MpsClipProperties { GainDb = -3 } }]
                },
                new MpsTrack
                {
                    Id = "music", Kind = MpsTrackKind.Music, Order = 3,
                    Clips = [new MpsClip { Id = "music-clip", TrackId = "music", AssetId = MusicAssetId, SourceOut = new MpsTime { Num = 3 }, SpeedNum = 2 }]
                },
                new MpsTrack
                {
                    Id = "subtitles", Kind = MpsTrackKind.Subtitle, Order = 4,
                    Clips = [new MpsClip { Id = "subtitle-clip", TrackId = "subtitles", SourceOut = new MpsTime { Num = 3 }, Properties = new MpsClipProperties { Subtitle = new MpsSubtitleProperties { Text = "欢迎使用 MPS" } } }]
                }
            ]
        };
    }

    /// <summary>JSON 深复制测试数据，避免单个拒绝场景污染后续断言。</summary>
    private static MpsProjectDocument Clone(MpsProjectDocument project) =>
        JsonSerializer.Deserialize<MpsProjectDocument>(JsonSerializer.Serialize(project, JsonFiles.Options), JsonFiles.Options) ?? throw new InvalidOperationException("项目复制失败");

    /// <summary>断言时间线校验按预期拒绝输入。</summary>
    private static void AssertRejects(MpsProjectDocument project, string caseName)
    {
        try
        {
            MpsTimelineValidation.Validate(project);
            throw new InvalidOperationException($"应拒绝{caseName}");
        }
        catch (InvalidDataException) { }
    }

    /// <summary>统一测试断言消息。</summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
