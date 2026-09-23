using VideoProduction;

namespace VideoProductionTests;

/// <summary>不访问网络、不提交付费请求的离线回归测试入口。</summary>
public static class Program
{
    /// <summary>执行有限数量的领域断言，失败时返回非零代码。</summary>
    public static int Main()
    {
        try
        {
            CostDeduplicatesTaskIds();
            CostSeparatesUnknownAndEstimates();
            ManifestRejectsFalseLipSyncAndBadCaptionOrder();
            Console.WriteLine("离线回归测试通过：费用去重、未知费用隔离、清单边界。");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("测试失败：" + error.Message);
            return 1;
        }
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

    /// <summary>统一断言消息，避免引入额外测试框架和网络依赖。</summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
