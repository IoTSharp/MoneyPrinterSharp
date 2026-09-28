using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoProduction;

namespace DemoAssets;

/// <summary>从代码和 FFmpeg 合成不含第三方媒体的短小双语演示工程。</summary>
public static class Program
{
    private static readonly string[] AssetNames = ["screen-zh.png", "screen-en.png", "screen-recording.mp4", "presenter-placeholder.mp4", "tone.wav", "captions.srt"];

    /// <summary>仅接受明确输出目录，并给全部外部进程设置次数、墙钟和取消边界。</summary>
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output") { Console.Error.WriteLine("用法：DemoAssets --output DIR"); return 2; }
        var root = Path.GetFullPath(args[1]);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Take(1).Any()) { Console.Error.WriteLine("输出目录非空，拒绝覆盖。"); return 2; }
        var ffmpeg = FindOnPath("ffmpeg.exe");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        var created = new List<string>();
        try
        {
            var assets = Path.Combine(root, "assets");
            Directory.CreateDirectory(assets);
            foreach (var name in AssetNames.Concat(["manifest.json", "README.md"]))
                if (File.Exists(Path.Combine(name is "manifest.json" or "README.md" ? root : assets, name))) throw new IOException("演示输出已有同名文件。");
            SaveScreen(Path.Combine(assets, AssetNames[0]), "示例软件", "项目列表", "录制桌面", "生成报告", created);
            SaveScreen(Path.Combine(assets, AssetNames[1]), "Sample App", "Projects", "Record Screen", "Export Report", created);
            created.Add(Path.Combine(assets, "tone.wav"));
            await RunFfmpegAsync(ffmpeg, ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4", "-c:a", "pcm_s16le", "-t", "4", "-n", Path.Combine(assets, "tone.wav")], cancel.Token);
            created.Add(Path.Combine(assets, "screen-recording.mp4"));
            await RunFfmpegAsync(ffmpeg, ["-loop", "1", "-framerate", "30", "-i", Path.Combine(assets, "screen-zh.png"), "-i", Path.Combine(assets, "tone.wav"), "-t", "4", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-n", Path.Combine(assets, "screen-recording.mp4")], cancel.Token);
            created.Add(Path.Combine(assets, "presenter-placeholder.mp4"));
            await RunFfmpegAsync(ffmpeg, ["-f", "lavfi", "-i", "color=c=0x50B470:s=640x720:r=30:d=4", "-vf", "drawbox=x=220:y=150:w=200:h=450:color=white:t=fill,drawbox=x=270:y=70:w=100:h=100:color=white:t=fill", "-t", "4", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-n", Path.Combine(assets, "presenter-placeholder.mp4")], cancel.Token);
            WriteNew(Path.Combine(assets, "captions.srt"), "1\n00:00:00,000 --> 00:00:02,000\n示例软件界面\n\n2\n00:00:02,000 --> 00:00:04,000\nSample App interface\n", created);
            var manifest = new VideoManifest { Title = "MPS 无敏感媒体回归样本", Width = 1280, Height = 720, TargetSeconds = 4, Scenes = [new VideoScene { Id = "demo", Title = "合成界面", Screen = "assets/screen-zh.png", Audio = "assets/tone.wav", Narration = "占位音调，不是实际旁白。", Evidence = "本仓库 DemoAssets 合成界面；非真实软件功能证据。", Clips = [new PresenterClip { Video = "assets/presenter-placeholder.mp4", Offset = 0, Duration = 4, LipSynced = false }], Captions = [new CaptionCue { Start = 0, End = 2, Text = "示例软件界面" }, new CaptionCue { Start = 2, End = 4, Text = "Sample App interface" }] }] };
            var manifestPath = Path.Combine(root, "manifest.json");
            WriteNew(manifestPath, JsonSerializer.Serialize(manifest, JsonFiles.Options), created);
            WriteNew(Path.Combine(root, "README.md"), "# MPS 合成演示工程\n\n全部界面、录屏、音调和主持人替身由本仓库 `tools/DemoAssets` 与 FFmpeg 在本地生成，按仓库 MIT 许可使用。画面显著标为 DEMO ONLY，不代表真实软件，也不含用户业务数据。\n\n`tone.wav` 是音调，不是中文或英文配音；`captions.srt` 仅用于字幕/时码回归，不满足成片旁白验收。`presenter-placeholder.mp4` 是几何形状，`lip_synced=false`，不能宣称口型同步。四秒素材只用于极短媒体回归，不在 V1 15 秒交付范围内。\n\n重新生成：`dotnet run --project tools/DemoAssets/DemoAssets.csproj -- --output NEW_EMPTY_DIR`。\n", created);
            Console.WriteLine($"演示工程已生成：{root}");
            return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine("生成失败：" + error.Message);
            foreach (var path in created) if (File.Exists(path)) File.Delete(path);
            return 1;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    /// <summary>只从 PATH 的前 64 个目录查找 FFmpeg。</summary>
    private static string FindOnPath(string name)
    {
        var entries = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var directory in entries.Take(64))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("PATH 中找不到 FFmpeg。");
    }

    /// <summary>使用矢量绘图合成明确标注的中英文假界面截图。</summary>
    private static void SaveScreen(string path, string heading, string first, string second, string third, List<string> created)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 249, 249)), null, new Rect(0, 0, 1280, 720));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 55, 58)), null, new Rect(0, 0, 1280, 72));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(226, 237, 235)), null, new Rect(0, 72, 230, 648));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(255, 255, 255)), new Pen(new SolidColorBrush(Color.FromRgb(204, 218, 216)), 1), new Rect(275, 128, 945, 495));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 124, 114)), null, new Rect(315, 223, 245, 74));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(79, 125, 159)), null, new Rect(582, 223, 245, 74));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(168, 111, 89)), null, new Rect(849, 223, 245, 74));
            DrawText(drawing, heading, 30, Brushes.White, 30, 16);
            DrawText(drawing, "DEMO ONLY / 合成界面", 25, new SolidColorBrush(Color.FromRgb(191, 214, 210)), 822, 19);
            DrawText(drawing, "MoneyPrinter#", 23, new SolidColorBrush(Color.FromRgb(24, 55, 58)), 26, 110);
            DrawText(drawing, first, 22, Brushes.White, 337, 240);
            DrawText(drawing, second, 22, Brushes.White, 604, 240);
            DrawText(drawing, third, 22, Brushes.White, 871, 240);
            DrawText(drawing, "01  Demo  /  02  Preview  /  03  Export", 25, new SolidColorBrush(Color.FromRgb(57, 76, 80)), 315, 390);
        }
        var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
        created.Add(path);
    }

    /// <summary>在固定字体和像素密度下绘制简短双语标签。</summary>
    private static void DrawText(DrawingContext drawing, string value, double size, Brush brush, double x, double y)
    {
        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush, 1.0);
        drawing.DrawText(text, new Point(x, y));
    }

    /// <summary>记录子进程身份并在 45 秒内执行一次本地 FFmpeg 命令。</summary>
    private static async Task RunFfmpegAsync(string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(45));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("-hide_banner"); info.ArgumentList.Add("-loglevel"); info.ArgumentList.Add("error");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg 未启动。");
        var createdUtc = process.StartTime.ToUniversalTime();
        Console.WriteLine($"FFmpeg PID={process.Id}, created={createdUtc:O}, parent={Environment.ProcessId}, command={executable} {string.Join(' ', info.ArgumentList.Select(Quote))}");
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(limit.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            var stderr = await stderrTask;
            _ = await stdoutTask;
            if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg 失败：" + stderr[..Math.Min(500, stderr.Length)]);
        }
        catch
        {
            if (!process.HasExited && process.StartTime.ToUniversalTime() == createdUtc) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>仅为进程身份报告显示参数，不执行任何 shell 拼接。</summary>
    private static string Quote(string value) => value.Contains(' ') ? '"' + value + '"' : value;

    /// <summary>创建新的 UTF-8 文本文件，避免覆盖已有素材。</summary>
    private static void WriteNew(string path, string content, List<string> created)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
        created.Add(path);
    }
}
