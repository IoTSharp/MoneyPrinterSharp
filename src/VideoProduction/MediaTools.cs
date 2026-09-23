using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace VideoProduction;

/// <summary>媒体探测的可序列化结果，保留后续时长和编解码验证所需字段。</summary>
public sealed record MediaInfo(string Path, double Duration, string Format, IReadOnlyList<MediaStreamInfo> Streams)
{
    public MediaStreamInfo? Video => Streams.FirstOrDefault(item => item.Type == "video");
    public MediaStreamInfo? Audio => Streams.FirstOrDefault(item => item.Type == "audio");
}

/// <summary>单个媒体流的实际参数；像素格式不单独作为透明有效的证据。</summary>
public sealed record MediaStreamInfo(int Index, string Type, string Codec, string PixelFormat, int Width, int Height, int Channels, int SampleRate, double Duration);

/// <summary>FFmpeg工具发现、探测及有界执行，不递归查找软件或依赖。</summary>
internal sealed class MediaTools
{
    private const string KnownBin = @"C:\Users\mysti\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg.Shared_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.1-full_build-shared\bin";
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TimeSpan totalTimeout;
    public string Ffmpeg { get; }
    public string Ffprobe { get; }
    public CancellationToken Cancellation { get; }

    /// <summary>按显式路径、PATH、已知固定路径依次发现工具，并建立总时限。</summary>
    public MediaTools(Arguments args, CancellationToken ct)
    {
        var seconds = args.Int("timeout", 900);
        if (seconds is < 5 or > 7200) throw new ArgumentException("--timeout必须在5到7200秒之间。");
        totalTimeout = TimeSpan.FromSeconds(seconds);
        Cancellation = ct;
        Ffmpeg = FindExecutable(args.Optional("ffmpeg"), "ffmpeg");
        Ffprobe = FindExecutable(args.Optional("ffprobe"), "ffprobe");
    }

    /// <summary>检查取消状态及总墙钟上限，所有媒体循环均调用该方法。</summary>
    public TimeSpan Remaining()
    {
        Cancellation.ThrowIfCancellationRequested();
        var remaining = totalTimeout - clock.Elapsed;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("媒体命令达到总墙钟时限。");
        return remaining;
    }

    /// <summary>在最多256个PATH项中定位可执行文件，不扫描任何子目录。</summary>
    private static string FindExecutable(string? configured, string name)
    {
        if (configured is not null)
        {
            var exact = Path.GetFullPath(configured);
            return File.Exists(exact) ? exact : throw new FileNotFoundException("明确指定的工具不存在。", exact);
        }
        var deadline = Stopwatch.StartNew();
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var folder in paths.Take(256))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("PATH工具发现超过3秒。");
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(folder.Trim('"'), file));
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        var known = Path.Combine(KnownBin, file);
        return File.Exists(known) ? known : throw new FileNotFoundException($"找不到{name}，请通过--{name}指定路径。");
    }

    /// <summary>通过统一进程运行器执行一次FFmpeg，失败时不自动重试。</summary>
    public async Task<ProcessResult> FfmpegAsync(IEnumerable<string> arguments, string? directory = null)
    {
        var prefix = new[] { "-hide_banner", "-nostdin", "-v", "warning", "-filter_threads", "2", "-filter_complex_threads", "2" };
        var result = await ProcessRunner.RunAsync(Ffmpeg, prefix.Concat(arguments), Remaining(), Cancellation, directory);
        if (result.ExitCode != 0) throw new InvalidOperationException($"FFmpeg退出{result.ExitCode}：{Tail(result.Stderr, 5000)}");
        return result;
    }

    /// <summary>取得JSON格式的真实媒体参数并限制输出大小和流数量。</summary>
    public async Task<MediaInfo> ProbeAsync(string path)
    {
        path = ExistingFile(path);
        var timeout = Remaining();
        if (timeout > TimeSpan.FromSeconds(30)) timeout = TimeSpan.FromSeconds(30);
        var result = await ProcessRunner.RunAsync(Ffprobe, new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path }, timeout, Cancellation);
        if (result.ExitCode != 0) throw new InvalidDataException("ffprobe无法读取媒体：" + Tail(result.Stderr, 3000));
        if (result.Stdout.Length > 2 * 1024 * 1024) throw new InvalidDataException("媒体探测响应超过2MiB。");
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var format = root.GetProperty("format");
        var streams = new List<MediaStreamInfo>();
        foreach (var stream in root.GetProperty("streams").EnumerateArray().Take(32))
        {
            Remaining();
            streams.Add(new MediaStreamInfo(Integer(stream, "index"), Text(stream, "codec_type"), Text(stream, "codec_name"), Text(stream, "pix_fmt"), Integer(stream, "width"), Integer(stream, "height"), Integer(stream, "channels"), Integer(stream, "sample_rate"), Number(stream, "duration")));
        }
        return new MediaInfo(path, Number(format, "duration"), Text(format, "format_name"), streams);
    }

    /// <summary>只接受已存在的本地文件，拒绝以URL代替本地媒体。</summary>
    public static string ExistingFile(string path)
    {
        if (path.Contains("://", StringComparison.Ordinal)) throw new ArgumentException("媒体输入仅允许本地文件。");
        path = Path.GetFullPath(path);
        return File.Exists(path) ? path : throw new FileNotFoundException("素材不存在。", path);
    }

    /// <summary>拒绝覆盖已有交付物，并创建其父目录。</summary>
    public static string NewOutput(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("输出已存在，拒绝覆盖：" + path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>读取文本字段，缺失字段返回空字符串。</summary>
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.ToString() : "";

    /// <summary>读取整数参数，未提供或无效时返回零。</summary>
    private static int Integer(JsonElement item, string key) => int.TryParse(Text(item, key), out var value) ? value : 0;

    /// <summary>读取浮点参数，排除NaN及无穷值。</summary>
    private static double Number(JsonElement item, string key) => double.TryParse(Text(item, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;

    /// <summary>使用固定区域输出FFmpeg秒数和滤镜数值。</summary>
    public static string N(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    /// <summary>限制错误内容长度，保留末尾诊断信息。</summary>
    private static string Tail(string text, int length) => text.Length <= length ? text : text[^length..];
}

/// <summary>独占临时目录，只回收本对象创建且路径身份一致的文件树。</summary>
internal sealed class MediaScratch : IDisposable
{
    public string DirectoryPath { get; }
    private readonly string identity = Guid.NewGuid().ToString("N");
    private readonly string parent;

    /// <summary>在输出目录中创建带随机身份的工作目录。</summary>
    public MediaScratch(string outputParent)
    {
        parent = Path.GetFullPath(outputParent);
        Directory.CreateDirectory(parent);
        DirectoryPath = Path.Combine(parent, ".media-work-" + identity);
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, ".owned"), identity);
    }

    /// <summary>核对精确路径和标记后清理本次中间文件，禁止跟随重解析目录。</summary>
    public void Dispose()
    {
        var expected = Path.Combine(parent, ".media-work-" + identity);
        var marker = Path.Combine(DirectoryPath, ".owned");
        if (!Path.GetFullPath(DirectoryPath).Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(marker) || File.ReadAllText(marker) != identity) return;
        var deadline = Stopwatch.StartNew();
        var pending = new Stack<string>();
        var visited = new List<string>();
        pending.Push(DirectoryPath);
        var count = 0;
        while (pending.Count > 0 && count < 4096 && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            var directory = pending.Pop();
            visited.Add(directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory).Take(4096 - count))
            {
                if (++count >= 4096 || deadline.Elapsed >= TimeSpan.FromSeconds(10)) return;
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child); else File.Delete(child);
            }
        }
        if (pending.Count != 0) return;
        foreach (var directory in visited.AsEnumerable().Reverse()) Directory.Delete(directory, false);
    }
}
