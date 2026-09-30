using System.Text.Json;

namespace VideoProduction;

/// <summary>媒体 CLI 适配层；参数解析和终端输出委托共享媒体流程。</summary>
public static class MediaPipeline
{
    /// <summary>校验命令选项并映射为共享媒体服务的显式参数。</summary>
    public static async Task<int> RunAsync(string command, Arguments args, CancellationToken ct)
    {
        switch (command)
        {
            case "doctor": args.Allow("ffmpeg", "ffprobe", "timeout", "output"); break;
            case "probe": args.Allow("input", "output", "ffmpeg", "ffprobe", "timeout"); break;
            case "split": args.Allow("input", "output-dir", "max-seconds", "ffmpeg", "ffprobe", "timeout"); break;
            case "key": args.Allow("input", "output", "color", "similarity", "blend", "ffmpeg", "ffprobe", "timeout"); break;
            case "render": args.Allow("manifest", "output", "allow-unsynced", "font", "ffmpeg", "ffprobe", "timeout"); break;
            case "verify": args.Allow("input", "output", "frames-dir", "require-alpha", "expected-seconds", "ffmpeg", "ffprobe", "timeout"); break;
            default: throw new ArgumentException("未知媒体命令：" + command);
        }
        var timeout = args.Int("timeout", 900);
        if (timeout is < 5 or > 7200) throw new ArgumentException("--timeout必须在5到7200秒之间。");
        var tools = new MediaTools(args.Optional("ffmpeg"), args.Optional("ffprobe"), TimeSpan.FromSeconds(timeout), ct);
        switch (command)
        {
            case "doctor":
                var doctor = await MediaWorkflows.DoctorAsync(tools);
                SaveOrPrint(args.Optional("output"), doctor);
                if (!doctor.Ready) throw new InvalidOperationException("缺少必需编码器或滤镜，详见doctor报告。");
                break;
            case "probe":
                SaveOrPrint(args.Optional("output"), await tools.ProbeAsync(args.Required("input")));
                break;
            case "split":
                await MediaWorkflows.SplitAsync(tools, args.Required("input"), args.Required("output-dir"), args.Number("max-seconds", 40), Console.WriteLine);
                break;
            case "key":
                Console.WriteLine(await MediaWorkflows.KeyAsync(tools, args.Required("input"), args.Required("output"), args.Optional("color") ?? "0x50B470", args.Number("similarity", .20), args.Number("blend", .08)));
                break;
            case "render":
                Console.WriteLine(await MediaWorkflows.RenderAsync(tools, args.Required("manifest"), args.Required("output"), args.Has("allow-unsynced"), args.Optional("font"), Console.WriteLine));
                break;
            case "verify":
                Console.WriteLine(await MediaWorkflows.VerifyAsync(tools, args.Required("input"), args.Optional("output"), args.Optional("frames-dir"), args.Has("require-alpha"), args.Optional("expected-seconds") is null ? null : args.Number("expected-seconds", 0)));
                break;
        }
        return 0;
    }

    /// <summary>探测结果可写入新报告文件，未指定路径时输出 JSON。</summary>
    private static void SaveOrPrint<T>(string? path, T value)
    {
        if (path is null) Console.WriteLine(JsonSerializer.Serialize(value, JsonFiles.Options));
        else JsonFiles.Write(MediaTools.NewOutput(path), value);
    }
}
