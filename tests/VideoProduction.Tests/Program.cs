using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>不访问网络、不提交付费请求的离线回归测试入口。</summary>
public static class Program
{
    /// <summary>执行有限数量的领域断言，失败时返回非零代码。</summary>
    public static async Task<int> Main()
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            CostDeduplicatesTaskIds();
            CostSeparatesUnknownAndEstimates();
            ManifestRejectsFalseLipSyncAndBadCaptionOrder();
            ProjectDirectorySurvivesCopyAndMove();
            await ProjectDirectoryCreateDoesNotOverwrite();
            await ProjectOutboundAuthorizationIsExact();
            TimelineCoordinatesTests.Run();
            TimelineModelTests.Run();
            await AssetIndexTests.RunAsync(deadline.Token);
            await MilestoneBFoundationsTests.RunAsync(deadline.Token);
            ProjectSessionPersistenceTests.Run();
            await SecurityContractTests.RunAsync(deadline.Token);
            Console.WriteLine("离线回归测试通过：费用与脱敏、清单边界、项目复制移动、素材索引、会话/阶段快照、证据迁移、编辑撤销/原子版本、时间坐标、多轨模型与安全契约。");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("测试失败：" + error.Message);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    /// <summary>同一供应商任务在嵌套记录中出现两次也只能计算一次。</summary>
    private static void CostDeduplicatesTaskIds()
    {
        var rows = new[]
        {
            new CostObservation("moark", "same", null, "motion", "ViduQ2-Turbo", "success", 0.4375m, "CNY", null),
            new CostObservation("moark", "same", null, "motion", "ViduQ2-Turbo", "success", 0.4375m, "CNY", null),
            new CostObservation("moark", "other", null, "voice", "Qwen3-TTS", "success", 0.1092m, "CNY", null)
        };
        var report = CostReport.Summarize(rows);
        Assert(report.Tasks.Count == 2, "任务应按 provider+task_id 去重");
        Assert(report.ConfirmedCny == 0.5467m, "已确认费用合计错误");
    }

    /// <summary>空价格不能当免费；估算只进入未知预算，不进入已确认合计。</summary>
    private static void CostSeparatesUnknownAndEstimates()
    {
        var rows = new[] { new CostObservation("moark", "pending", null, "lipsync", "Duix-Avatar", "unknown", null, null, 8.51m) };
        var report = CostReport.Summarize(rows);
        Assert(report.ConfirmedCny == 0 && report.UnknownCount == 1 && report.UnknownEstimateCny == 8.51m, "未知费用隔离错误");
    }

    /// <summary>章节字幕必须递增，且默认不接受未同步口型片段。</summary>
    private static void ManifestRejectsFalseLipSyncAndBadCaptionOrder()
    {
        var manifest = new VideoManifest
        {
            Scenes = [new VideoScene
            {
                Id = "intro", Narration = "介绍", Evidence = "功能页面",
                Clips = [new PresenterClip { Video = "presenter.webm", LipSynced = false }],
                Captions = [new CaptionCue { Start = 1, End = 2, Text = "二" }, new CaptionCue { Start = .5, End = 1.5, Text = "一" }]
            }]
        };
        var errors = ProjectCommands.Validate(manifest, Path.Combine(Path.GetTempPath(), "manifest.json"), draft: true);
        Assert(errors.Any(error => error.Contains("字幕", StringComparison.Ordinal)), "应拒绝倒序字幕");
        Assert(manifest.Scenes[0].Clips[0].LipSynced == false, "测试素材应保留同步声明");
    }

    /// <summary>同一素材引用在项目复制与移动后仍从新根解析，越界路径被拒绝。</summary>
    private static void ProjectDirectorySurvivesCopyAndMove()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "mps-directory-test-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(temporary, "original");
        var copied = Path.Combine(temporary, "copied");
        var moved = Path.Combine(temporary, "moved");
        try
        {
            var project = ProjectDirectory.Create(original, "本地测试项目");
            var asset = new MpsAssetReference { Path = "assets/source/sample.txt" };
            File.WriteAllText(ProjectDirectory.ResolvePath(original, asset.Path), "local fixture");
            project.Assets.Add(asset);
            ProjectDirectory.Save(original, project);

            Directory.CreateDirectory(Path.Combine(copied, "assets", "source"));
            File.Copy(Path.Combine(original, ProjectDirectory.ProjectFileName), Path.Combine(copied, ProjectDirectory.ProjectFileName));
            File.Copy(ProjectDirectory.ResolvePath(original, asset.Path), ProjectDirectory.ResolvePath(copied, asset.Path));
            Directory.Move(copied, moved);

            var reopened = ProjectDirectory.Open(moved);
            Assert(reopened.Id == project.Id && reopened.Assets.Single().Path == asset.Path, "复制移动后项目引用变化");
            Assert(File.ReadAllText(ProjectDirectory.ResolvePath(moved, reopened.Assets.Single().Path)) == "local fixture", "复制移动后素材未从新根解析");
            File.Delete(ProjectDirectory.ResolvePath(moved, asset.Path));
            Assert(ProjectDirectory.Open(moved).Assets.Count == 1, "素材失联时不应删除项目引用");
            Assert(RejectsInvalidPath(moved, "../outside.txt"), "项目路径不应允许上行");
            Assert(RejectsInvalidPath(moved, "C:/outside.txt"), "项目路径不应允许绝对盘符");
            var linkedRoot = Path.Combine(temporary, "linked-root");
            var linkCreated = false;
            try
            {
                Directory.CreateSymbolicLink(linkedRoot, original);
                linkCreated = true;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // 未开放符号链接权限的机器跳过此项；其余路径测试仍执行。
            }
            if (linkCreated)
            {
                try
                {
                    Assert(RejectsInvalidPath(linkedRoot, asset.Path), "项目根重解析点应被拒绝");
                    try { _ = ProjectDirectory.Open(linkedRoot); throw new InvalidOperationException("空项目根重解析点被读取"); }
                    catch (InvalidDataException) { }
                    try { ProjectDirectory.Save(linkedRoot, project); throw new InvalidOperationException("项目根重解析点被写入"); }
                    catch (InvalidDataException) { }
                }
                finally { Directory.Delete(linkedRoot); }
            }
            var file = Path.Combine(moved, ProjectDirectory.ProjectFileName);
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            json["credential"] = "fixture-only";
            File.WriteAllText(file, json.ToJsonString());
            try { _ = ProjectDirectory.Open(moved); throw new InvalidOperationException("未知凭据字段被接受"); }
            catch (JsonException) { }
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    /// <summary>仅将预期的非法路径异常视为拒绝。</summary>
    private static bool RejectsInvalidPath(string root, string relativePath)
    {
        try { _ = ProjectDirectory.ResolvePath(root, relativePath); return false; }
        catch (InvalidDataException) { return true; }
    }

    /// <summary>两个并发创建者最多一人成功，不覆盖先写入的根文件。</summary>
    private static async Task ProjectDirectoryCreateDoesNotOverwrite()
    {
        var parent = Path.Combine(Path.GetTempPath(), "mps-create-test-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "project");
        try
        {
            var attempts = new[] { "first", "second" }.Select(title => Task.Run(() =>
            {
                try { return ProjectDirectory.Create(root, title).Title; }
                catch (IOException) { return null; }
            })).ToArray();
            var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(20));
            Assert(results.Count(value => value is not null) == 1, "并发创建不能同时成功");
            Assert(ProjectDirectory.Open(root).Title == results.Single(value => value is not null), "并发创建覆盖了已写入的项目");
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    /// <summary>外发范围必须精确匹配账号、用途和文件内容，默认拒绝及过期均不能放行。</summary>
    private static async Task ProjectOutboundAuthorizationIsExact()
    {
        var root = Path.Combine(Path.GetTempPath(), "mps-authorization-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = ProjectDirectory.Create(root, "外发测试");
            var asset = new MpsAssetReference { Path = "assets/source/fixture.txt" };
            var path = ProjectDirectory.ResolvePath(root, asset.Path);
            var content = Encoding.UTF8.GetBytes("approved local fixture");
            await File.WriteAllBytesAsync(path, content);
            asset.Sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
            project.Assets.Add(asset);
            Assert(!await ProjectOutboundPolicy.IsAllowedAsync(project, root, "moark", "test", "vision", "review", asset.Id), "无授权应默认拒绝");
            var grant = new MpsOutboundAuthorization
            {
                Provider = "moark", AccountAlias = "test", Capability = "vision", Purpose = "review",
                AssetId = asset.Id, Sha256 = asset.Sha256, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            project.Authorizations.Add(grant);
            ProjectDirectory.Save(root, project);
            var reopened = ProjectDirectory.Open(root);
            Assert(await ProjectOutboundPolicy.IsAllowedAsync(reopened, root, "moark", "test", "vision", "review", asset.Id), "精确授权应放行");
            Assert(!await ProjectOutboundPolicy.IsAllowedAsync(reopened, root, "sonnet.vip", "test", "vision", "review", asset.Id), "跨提供商应拒绝");
            Assert(!await ProjectOutboundPolicy.IsAllowedAsync(reopened, root, "moark", "other", "vision", "review", asset.Id), "跨账号应拒绝");
            Assert(!await ProjectOutboundPolicy.IsAllowedAsync(reopened, root, "moark", "test", "vision", "publish", asset.Id), "用途改变应拒绝");
            await File.WriteAllTextAsync(path, "changed fixture");
            Assert(!await ProjectOutboundPolicy.IsAllowedAsync(reopened, root, "moark", "test", "vision", "review", asset.Id), "内容哈希变化应拒绝");
            await File.WriteAllBytesAsync(path, content);
            reopened.Authorizations[0].ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            Assert(!await ProjectOutboundPolicy.IsAllowedAsync(reopened, root, "moark", "test", "vision", "review", asset.Id), "过期授权应拒绝");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>统一断言消息，避免引入额外测试框架和网络依赖。</summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
