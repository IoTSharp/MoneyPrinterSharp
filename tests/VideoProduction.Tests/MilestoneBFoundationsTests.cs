using System.Text.Json;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>里程碑 B 并发、证据、文本和迁移契约的离线回归。</summary>
public static class MilestoneBFoundationsTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await ProjectLockIsExclusive(cancellationToken);
        EvidenceRejectsSignedUrls();
        TextLayoutIsUnicodeStable();
        MigrationRetainsSourcesAndReportsUnmigrated(cancellationToken);
        EditHistoryAndAtomicStoreRoundTrip(cancellationToken);
        ProjectDiagnosticsLocateFixablePaths();
        SessionAndStageStateShareProjectState();
    }

    private static async Task ProjectLockIsExclusive(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "mps-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProjectDirectory.Create(root, "lock test");
            using var first = MpsProjectConcurrency.Acquire(root, TimeSpan.FromSeconds(1), cancellationToken);
            var second = Task.Run(() =>
            {
                try { using var lease = MpsProjectConcurrency.Acquire(root, TimeSpan.FromMilliseconds(150), cancellationToken); return false; }
                catch (TimeoutException) { return true; }
            }, cancellationToken);
            Assert(await second.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken), "第二个窗口必须进入只读/等待而不能取得写入锁");
            var owner = MpsProjectConcurrency.ReadOwner(root);
            Assert(owner?.ProcessId == Environment.ProcessId, "锁文件应保留当前拥有者诊断");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void EvidenceRejectsSignedUrls()
    {
        var project = new MpsProjectDocument { Evidence = [new MpsEvidenceRecord { Source = "https://example.test/file?sig=secret", SubjectId = "claim" }] };
        try { MpsEvidenceValidation.Validate(project); throw new InvalidOperationException("签名地址不应通过证据校验"); }
        catch (InvalidDataException) { }
    }

    private static void TextLayoutIsUnicodeStable()
    {
        var normalized = MpsTextLayout.Normalize(" e\r\ń\u0001中 ");
        Assert(normalized == " e\ń中 ", "文本规范化应保留 Unicode 与换行并清除控制字符");
        var lines = MpsTextLayout.Wrap("中文 mixed words", 6);
        Assert(lines.Count >= 2 && lines.All(line => line.Length > 0), "双语文本应按有限宽度换行");
        Assert(MpsTextLayout.ResolveFallbackFonts(["", "Segoe UI", "Segoe UI"]).Count == 1, "字体回退应去重并跳过空值");
    }

    private static void MigrationRetainsSourcesAndReportsUnmigrated(CancellationToken cancellationToken)
    {
        var manifest = new VideoManifest
        {
            Width = 1920,
            Height = 1080,
            Fps = 30,
            Scenes =
            [
                new VideoScene
                {
                    Id = "intro",
                    Screen = "assets/source/screen.png",
                    Audio = "assets/source/intro.wav",
                    Evidence = "公开演示页面",
                    Clips = [new PresenterClip { Video = "assets/source/presenter.webm", Offset = 0, Duration = 1, LipSynced = false }],
                    Captions = [new CaptionCue { Start = 0, End = 1, Text = "你好 hello" }]
                }
            ]
        };
        var result = MpsProjectMigration.FromManifest(manifest, "迁移项目", cancellationToken);
        Assert(result.Project.Assets.Count == 3, "迁移应保留屏幕、旁白和主持人素材");
        Assert(result.Project.Evidence.Count == 1 && result.Project.Evidence[0].Source == "公开演示页面", "迁移应保留证据来源");
        Assert(result.Project.Tracks.SelectMany(track => track.Clips).Any(clip => clip.Properties.LipSynced == false), "迁移应保留口型未同步声明");
        var json = JsonSerializer.Serialize(result.Project);
        Assert(!json.Contains("presenter.webm?", StringComparison.Ordinal), "迁移项目不得生成带查询参数的来源路径");
    }

    private static void EditHistoryAndAtomicStoreRoundTrip(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "mps-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = ProjectDirectory.Create(root, "编辑测试");
            var asset = new MpsAssetReference { Path = "assets/source/screen.mp4" };
            project.Assets.Add(asset);
            var screen = new MpsTrack { Id = "screen", Kind = MpsTrackKind.Screen, Order = 0 };
            var subtitle = new MpsTrack { Id = "subtitle", Kind = MpsTrackKind.Subtitle, Order = 1 };
            var clip = new MpsClip
            {
                Id = "clip", TrackId = screen.Id, AssetId = asset.Id,
                SourceOut = MpsTime.From(new RationalTime(2, 1)),
                Properties = new MpsClipProperties()
            };
            screen.Clips.Add(clip);
            subtitle.Clips.Add(new MpsClip
            {
                Id = "caption", TrackId = subtitle.Id, SourceOut = MpsTime.From(new RationalTime(1, 1)),
                Properties = new MpsClipProperties { Subtitle = new MpsSubtitleProperties { Text = "旧" } }
            });
            project.Tracks.Add(screen); project.Tracks.Add(subtitle);
            var history = new MpsEditHistory();
            history.Execute(project, new SetGainCommand("clip", -6));
            history.Execute(project, new UpdateSubtitleCommand("caption", "新字幕"));
            Assert(project.Tracks[0].Clips[0].Properties.GainDb == -6, "音量命令未应用");
            Assert(history.Undo(project) && project.Tracks[1].Clips[0].Properties.Subtitle!.Text == "旧", "字幕撤销失败");
            Assert(history.Redo(project) && project.Tracks[1].Clips[0].Properties.Subtitle!.Text == "新字幕", "字幕重做失败");
            var store = new MpsAtomicProjectStore(maxSnapshots: 2, maxSnapshotBytes: 2 * 1024 * 1024);
            store.Save(root, project, "manual", cancellationToken);
            var recovered = store.RecoverLatest(root, cancellationToken);
            Assert(recovered.Tracks[1].Clips[0].Properties.Subtitle!.Text == "新字幕", "原子版本恢复失败");
            Assert(Directory.EnumerateFiles(Path.Combine(root, "versions"), "*.mps.json").Take(3).Count() <= 2, "版本数量未受限");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void ProjectDiagnosticsLocateFixablePaths()
    {
        var project = new MpsProjectDocument { Title = "", Assets = [new MpsAssetReference { Path = "../outside.mp4" }] };
        var diagnostics = MpsProjectDiagnostics.Validate(project, Path.GetTempPath());
        Assert(diagnostics.Any(item => item.Path == "title"), "详细校验应定位项目标题");
        Assert(diagnostics.Any(item => item.Path == "assets[0].path"), "详细校验应定位素材路径");
        try { MpsProjectDiagnostics.ThrowIfInvalid(project); throw new InvalidOperationException("无效项目不应通过阻断校验"); }
        catch (InvalidDataException) { }
    }

    private static void SessionAndStageStateShareProjectState()
    {
        var document = new MpsProjectDocument { Id = "0123456789abcdef0123456789abcdef", Title = "会话测试" };
        var manager = new MpsProject(document);
        var first = manager.CreateSession("脚本");
        var second = manager.CreateSession("复核");
        first.AddUserMessage("保留上下文");
        Assert(ReferenceEquals(first.Shared.Assets, second.Shared.Assets), "会话不得复制素材集合");
        Assert(ReferenceEquals(first.Shared.Tracks, document.Tracks), "会话应绑定项目时间线");
        manager.Shared.Stages.Pass(MpsProductionStage.FeatureAudit, "证据已确认");
        manager.Shared.Stages.Pass(MpsProductionStage.IntroductionOrder, "顺序已确认");
        var affected = manager.Shared.Stages.InvalidateDownstream(MpsProductionStage.FeatureAudit, "功能主张更新");
        Assert(affected.Any(state => state.Stage == MpsProductionStage.IntroductionOrder && state.Status == MpsStageStatus.Invalidated), "上游变化应使下游失效");
        Assert(manager.Shared.Stages[MpsProductionStage.FeatureAudit].Status == MpsStageStatus.Passed, "上游阶段不应被自身失效传播覆盖");
        manager.SwitchSession(second.Id);
        Assert(manager.ActiveSession?.Id == second.Id && first.Messages.Count == 1, "会话切换不应丢失对话");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
