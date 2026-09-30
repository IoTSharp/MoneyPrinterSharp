using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MediaBaseline;

/// <summary>只使用本地合成媒体，测量单机离线性能；不代表桌面预览或录屏性能。</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>解析固定参数，提供取消与八分钟总时限，并始终删除本次专属样本目录。</summary>
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args is ["--help"])
        {
            Console.WriteLine("MPS 本地媒体基线：--output NEW_REPORT.json [--ffmpeg PATH] [--ffprobe PATH]\n" +
                "--preflight-only 仅测 160×90 极小样本；常规模式增加横竖 1080p、30fps、15秒样本。\n" +
                "最多32个串行子进程，每阶段90秒，总计8分钟；按 Ctrl+C 取消。\n" +
                "报告不得覆盖已有文件；临时媒体自动清理；精确进程日志在 .runs/media-baseline/。\n" +
                "此测量不证明真实 GUI 预览、录屏、Windows 10 或双机验收通过。");
            return 0;
        }

        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(TimeSpan.FromMinutes(8));
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        string? temporary = null;
        try
        {
            var options = Options.Parse(args);
            var runId = Guid.NewGuid().ToString("N");
            temporary = Path.Combine(Path.GetTempPath(), "mps-media-baseline-" + runId);
            Directory.CreateDirectory(temporary);
            var runner = new MeasuredRunner(Path.Combine(Environment.CurrentDirectory, ".runs", "media-baseline", runId), cancel.Token);
            var report = new Report(DateTimeOffset.UtcNow, MachineInfo.Read(), options.PreflightOnly);
            var ffmpeg = await runner.RunAsync("ffmpeg-version", options.Ffmpeg, ["-version"]);
            var ffprobe = await runner.RunAsync("ffprobe-version", options.Ffprobe, ["-version"]);
            report.Ffmpeg = ffmpeg.Output.Split('\n', 2)[0].Trim();
            report.Ffprobe = ffprobe.Output.Split('\n', 2)[0].Trim();

            // 先验证极小输入与进程生命周期，避免直接启动大样本批次。
            var tiny = Path.Combine(temporary, "preflight.mp4");
            await runner.RunAsync("preflight-generate", options.Ffmpeg,
                ["-hide_banner", "-v", "error", "-nostdin", "-filter_threads", "1", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10", "-frames:v", "2", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", tiny]);
            var preflight = await runner.RunAsync("preflight-decode", options.Ffmpeg, DecodeArguments(tiny));
            RequireFrames(preflight.Output, 2);
            report.PreflightPassed = true;

            if (!options.PreflightOnly)
            {
                var shapes = new[] { (Name: "landscape", Width: 1920, Height: 1080), (Name: "portrait", Width: 1080, Height: 1920) };
                foreach (var shape in shapes)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    var source = Path.Combine(temporary, shape.Name + "-source.mp4");
                    var encoded = Path.Combine(temporary, shape.Name + "-encoded.mp4");
                    var generate = await runner.RunAsync(shape.Name + "-generate", options.Ffmpeg,
                        ["-hide_banner", "-v", "error", "-nostdin", "-filter_threads", "1", "-f", "lavfi", "-i", $"testsrc2=size={shape.Width}x{shape.Height}:rate=30",
                        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "15", "-c:v", "libx264", "-threads", "2", "-preset", "veryfast", "-crf", "23", "-g", "60", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", source]);
                    report.Measurements.Add(Measurement.From(shape.Name, "generate", 1, generate));
                    for (var repeat = 1; repeat <= 3; repeat++)
                    {
                        var probe = await runner.RunAsync($"{shape.Name}-probe-{repeat}", options.Ffprobe, ProbeArguments(source));
                        ValidateProbe(probe.Output, shape.Width, shape.Height);
                        report.Measurements.Add(Measurement.From(shape.Name, "probe", repeat, probe));
                    }
                    // 三个固定定位点；包含进程启动与一帧解码，不包括任何图形界面显示。
                    foreach (var position in new[] { 2, 7, 12 })
                    {
                        var seek = await runner.RunAsync($"{shape.Name}-seek-{position}", options.Ffmpeg,
                            ["-hide_banner", "-v", "error", "-nostdin", "-threads", "2", "-ss", position.ToString(CultureInfo.InvariantCulture), "-i", source,
                            "-map", "0:v:0", "-frames:v", "1", "-an", "-threads", "2", "-progress", "pipe:1", "-f", "null", "-"]);
                        RequireFrames(seek.Output, 1);
                        report.Measurements.Add(Measurement.From(shape.Name, "seek-one-frame", position, seek));
                    }
                    var render = await runner.RunAsync(shape.Name + "-encode", options.Ffmpeg,
                        ["-hide_banner", "-v", "error", "-nostdin", "-filter_threads", "1", "-threads", "2", "-i", source,
                        "-map", "0:v:0", "-map", "0:a:0", "-c:v", "libx264", "-threads", "2", "-preset", "veryfast", "-crf", "23", "-g", "60", "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", encoded]);
                    report.Measurements.Add(Measurement.From(shape.Name, "encode-15s", 1, render));
                    var outputProbe = await runner.RunAsync(shape.Name + "-output-probe", options.Ffprobe, ProbeArguments(encoded));
                    ValidateProbe(outputProbe.Output, shape.Width, shape.Height);
                    var decode = await runner.RunAsync(shape.Name + "-full-decode", options.Ffmpeg, DecodeArguments(encoded));
                    RequireFrames(decode.Output, 450);
                    report.Measurements.Add(Measurement.From(shape.Name, "full-decode", 1, decode));
                    report.Samples.Add(new Sample(shape.Name, shape.Width, shape.Height, 30, 15, new FileInfo(source).Length, new FileInfo(encoded).Length, 450, true));
                }
            }

            report.FinishedAtUtc = DateTimeOffset.UtcNow;
            report.ChildProcessCount = runner.ProcessCount;
            report.AllChildProcessesExited = runner.AllExited;
            CleanupTemporary(temporary);
            temporary = null;
            report.TemporaryMediaRemoved = true;
            using (var output = new FileStream(options.Output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(output, report, JsonOptions, cancel.Token);
            Console.WriteLine($"完成：{report.ChildProcessCount} 个子进程均退出，临时媒体已删除，报告已保存。");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("已取消或达到八分钟总时限；本次进程树和媒体进入清理路径。");
            return 2;
        }
        catch (Exception exception)
        {
            // 私有路径只保留在本地进程日志；控制台不转储外部程序原始响应。
            Console.Error.WriteLine($"测量失败（{exception.GetType().Name}）；请核对参数、工具和本地 .runs 记录。");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            if (temporary is not null) CleanupTemporary(temporary);
        }
    }

    /// <summary>生成固定范围的探测参数，避免文件路径作为 shell 文本解释。</summary>
    private static string[] ProbeArguments(string file) => ["-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height,avg_frame_rate:format=duration", "-of", "json", file];

    /// <summary>完整解码音视频并输出实际帧数；解码错误使 FFmpeg 失败。</summary>
    private static string[] DecodeArguments(string file) => ["-hide_banner", "-v", "error", "-xerror", "-nostdin", "-threads", "2", "-i", file, "-map", "0:v:0", "-map", "0:a?", "-threads", "2", "-progress", "pipe:1", "-f", "null", "-"];

    /// <summary>验证编码、尺寸、帧率、音频存在及十五秒时长，避免只把退出码作为验收。</summary>
    private static void ValidateProbe(string output, int width, int height)
    {
        using var json = JsonDocument.Parse(output);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().Take(8).ToArray();
        var video = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
        var audio = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "audio");
        var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (video.GetProperty("width").GetInt32() != width || video.GetProperty("height").GetInt32() != height ||
            video.GetProperty("codec_name").GetString() != "h264" || audio.GetProperty("codec_name").GetString() != "aac" ||
            video.GetProperty("avg_frame_rate").GetString() != "30/1" || Math.Abs(duration - 15) > 0.1)
            throw new InvalidDataException("合成媒体参数不符合基线。");
    }

    /// <summary>读取有界 FFmpeg 进度，要求结尾帧数准确并到达 end。</summary>
    private static void RequireFrames(string output, int expected)
    {
        var lines = output.Split('\n');
        var final = lines.Take(10000).LastOrDefault(line => line.StartsWith("frame=", StringComparison.Ordinal));
        if (final is null || !int.TryParse(final.AsSpan(6).Trim(), out var frames) || frames != expected || !output.Contains("progress=end", StringComparison.Ordinal))
            throw new InvalidDataException("实际解码帧数不符。");
    }

    /// <summary>只删除本工具创建的系统临时目录；拒绝非预期路径和重解析点。</summary>
    private static void CleanupTemporary(string directory)
    {
        var full = Path.GetFullPath(directory);
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var name = Path.GetFileName(full);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !name.StartsWith("mps-media-baseline-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name["mps-media-baseline-".Length..], "N", out _))
            throw new IOException("拒绝清理非本次临时路径。");
        if (Directory.Exists(full))
        {
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("拒绝清理重解析目录。");
            Directory.Delete(full, true);
        }
    }

    private sealed record Options(string Output, string Ffmpeg, string Ffprobe, bool PreflightOnly)
    {
        /// <summary>最多七个参数，只接受固定选项，且不覆盖历史测量报告。</summary>
        public static Options Parse(string[] args)
        {
            if (args.Length > 7) throw new ArgumentException("参数过多。");
            string? output = null;
            var ffmpeg = "ffmpeg";
            var ffprobe = "ffprobe";
            var preflightOnly = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--preflight-only") { preflightOnly = true; continue; }
                if (i + 1 >= args.Length) throw new ArgumentException("缺少参数值。");
                switch (args[i])
                {
                    case "--output": output = Path.GetFullPath(args[++i]); break;
                    case "--ffmpeg": ffmpeg = args[++i]; break;
                    case "--ffprobe": ffprobe = args[++i]; break;
                    default: throw new ArgumentException("未知参数。");
                }
            }
            if (output is null || File.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output))) throw new ArgumentException("需要位于已有目录的新报告路径。");
            return new Options(output, ffmpeg, ffprobe, preflightOnly);
        }
    }

    private sealed record MachineInfo(string Os, string Framework, string Architecture, string Cpu, int LogicalProcessors, long PhysicalMemoryBytes, string Gpu)
    {
        /// <summary>只读取固定 CPU 注册表和内存 API，不收集机器名、用户、序列号或私有路径。</summary>
        public static MachineInfo Read()
        {
            var cpu = "unknown";
            long memory = 0;
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                cpu = key?.GetValue("ProcessorNameString") as string ?? "unknown";
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref status)) memory = checked((long)status.TotalPhysical);
            }
            return new MachineInfo(RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(), cpu.Trim(), Environment.ProcessorCount, memory,
                "not queried; CPU libx264 only; GPU inventory belongs in the accompanying acceptance record");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }

    private sealed class Report(DateTimeOffset started, MachineInfo machine, bool preflightOnly)
    {
        public int SchemaVersion { get; } = 1;
        public DateTimeOffset StartedAtUtc { get; } = started;
        public DateTimeOffset FinishedAtUtc { get; set; }
        public MachineInfo Machine { get; } = machine;
        public bool PreflightOnly { get; } = preflightOnly;
        public string Scope { get; } = "Single-machine synthetic offline media only; not GUI preview, capture, Windows 10 or two-machine acceptance.";
        public string Encoding { get; } = "CPU libx264, veryfast, CRF 23, GOP 60, yuv420p; AAC 128kbps/48kHz; 2 codec threads, 1 filter thread";
        public string MemoryMethod { get; } = "Windows K32GetProcessMemoryInfo peak working set, sampled every 100ms and once after exit if supported; 0 means unavailable; per child process only, excludes UI, parent and simultaneous system load";
        public int StageTimeoutSeconds { get; } = 90;
        public int TotalTimeoutSeconds { get; } = 480;
        public int MaximumChildProcesses { get; } = 32;
        public string Ffmpeg { get; set; } = "";
        public string Ffprobe { get; set; } = "";
        public bool PreflightPassed { get; set; }
        public int ChildProcessCount { get; set; }
        public bool AllChildProcessesExited { get; set; }
        public bool TemporaryMediaRemoved { get; set; }
        public List<Sample> Samples { get; } = [];
        public List<Measurement> Measurements { get; } = [];
    }

    private sealed record Sample(string Shape, int Width, int Height, int Fps, int DurationSeconds, long SourceBytes, long EncodedBytes, int DecodedVideoFrames, bool OutputProbePassed);
    private sealed record Measurement(string Shape, string Operation, int RepeatOrSeekSeconds, double ElapsedMilliseconds, long PeakWorkingSetBytes)
    {
        /// <summary>将单进程结果转成可入库的无私有路径聚合指标。</summary>
        public static Measurement From(string shape, string operation, int repeat, RunResult result) => new(shape, operation, repeat, Math.Round(result.Elapsed.TotalMilliseconds, 2), result.PeakWorkingSet);
    }
}

internal sealed record RunResult(string Output, TimeSpan Elapsed, long PeakWorkingSet);

/// <summary>有界执行、定期进度和精确进程归属日志，失败及取消只回收本次进程树。</summary>
internal sealed class MeasuredRunner(string logDirectory, CancellationToken cancellation)
{
    public int ProcessCount { get; private set; }
    public bool AllExited { get; private set; } = true;

    /// <summary>每阶段九十秒、最多三十二进程，记录 PID、创建时间、父进程与完整参数。</summary>
    public async Task<RunResult> RunAsync(string stage, string executable, string[] arguments)
    {
        cancellation.ThrowIfCancellationRequested();
        if (++ProcessCount > 32 || arguments.Length > 64) throw new InvalidOperationException("达到进程或参数数量上限。");
        Directory.CreateDirectory(logDirectory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        var timer = Stopwatch.StartNew();
        DateTime started = default;
        var launched = false;
        long peak = 0;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        string? log = null;
        var state = "running";
        try
        {
            if (!process.Start()) throw new IOException("外部进程未启动。");
            launched = true;
            started = process.StartTime.ToUniversalTime();
            log = Path.Combine(logDirectory, $"{ProcessCount:00}-{stage}.json");
            WriteLog(false);
            Console.WriteLine($"[{ProcessCount}/32] {stage} 开始；PID {process.Id}，父 PID {Environment.ProcessId}。");
            stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            var exit = process.WaitForExitAsync(timeout.Token);
            // 900 次×100ms 为采样上限，独立九十秒令牌为墙钟上限；没有忙等待。
            for (var sample = 0; sample < 900 && !exit.IsCompleted; sample++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                peak = Math.Max(peak, ReadPeakWorkingSet(process));
                if (sample > 0 && sample % 100 == 0) Console.WriteLine($"  {stage} 已运行 {timer.Elapsed.TotalSeconds:0.0} 秒。");
                await Task.WhenAny(exit, Task.Delay(100, timeout.Token));
            }
            await exit;
            peak = Math.Max(peak, ReadPeakWorkingSet(process));
            var output = await stdout;
            var errors = await stderr;
            if (process.ExitCode != 0) throw new IOException($"阶段退出码 {process.ExitCode}；诊断长度 {errors.Length}。");
            state = "exited";
            timer.Stop();
            Console.WriteLine($"  {stage} 完成：{timer.Elapsed.TotalMilliseconds:0} ms，峰值 {peak / 1048576d:0.0} MiB。");
            return new RunResult(output, timer.Elapsed, peak);
        }
        finally
        {
            // 同一 Process 对象、PID、创建时间与本地日志确认归属；不按名字杀进程。
            if (launched)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        if (process.StartTime.ToUniversalTime() != started) throw new IOException("进程创建时间不符，拒绝清理。");
                        process.Kill(entireProcessTree: true);
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await process.WaitForExitAsync(cleanup.Token);
                    }
                }
                finally
                {
                    timeout.Cancel();
                    if (stdout is not null && stderr is not null)
                        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (Exception e) when (e is OperationCanceledException or IOException or TimeoutException) { }
                    AllExited &= process.HasExited;
                    WriteLog(true);
                }
            }
        }

        // 原始参数仅写入 Git 忽略目录，用于本机生命周期审计。
        void WriteLog(bool finished)
        {
            if (log is null) return;
            File.WriteAllText(log, JsonSerializer.Serialize(new { pid = process.Id, parent_pid = Environment.ProcessId, started_at_utc = started,
                executable, arguments, state = finished && state == "running" ? "cancelled-or-failed" : state,
                finished_at_utc = finished ? DateTimeOffset.UtcNow : (DateTimeOffset?)null, exited = finished && process.HasExited }));
        }
    }

    /// <summary>Windows 使用已持有的进程句柄读取峰值，避免已退出进程的托管属性抛错。</summary>
    private static long ReadPeakWorkingSet(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            var memory = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            return K32GetProcessMemoryInfo(process.SafeHandle, ref memory, memory.Size) ? checked((long)memory.PeakWorkingSet) : 0;
        }
        if (process.HasExited) return 0;
        try { process.Refresh(); return process.PeakWorkingSet64; }
        catch (InvalidOperationException) { return 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemoryCounters counters, uint size);

    /// <summary>最多读取四 MiB 字符，管道读取受阶段及总取消令牌控制。</summary>
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        for (var chunk = 0; chunk < 1024; chunk++)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) return output.ToString();
            output.Append(buffer, 0, read);
        }
        throw new IOException("输出超过四 MiB 字符。");
    }
}
