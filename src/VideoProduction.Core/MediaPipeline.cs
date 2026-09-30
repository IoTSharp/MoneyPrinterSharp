using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoProduction;

/// <summary>共享的 FFmpeg 媒体流程；调用方提供显式参数并控制取消与进度显示。</summary>
public static partial class MediaWorkflows
{
    /// <summary>确认所需编码器和滤镜存在，并报告准确工具路径及版本。</summary>
    public static async Task<MediaDoctorReport> DoctorAsync(MediaTools tools)
    {
        var version = await tools.FfmpegAsync(["-version"]);
        var filters = await tools.FfmpegAsync(["-filters"]);
        var encoders = await tools.FfmpegAsync(["-encoders"]);
        var capabilities = new Dictionary<string, bool>();
        foreach (var name in new[] { "colorkey", "overlay", "subtitles", "atempo", "alphaextract", "signalstats" })
        {
            tools.Remaining();
            capabilities[name] = filters.Stdout.Contains(name, StringComparison.Ordinal);
        }
        foreach (var name in new[] { "libx264", "libvpx-vp9", "aac" }) { tools.Remaining(); capabilities[name] = encoders.Stdout.Contains(name, StringComparison.Ordinal); }
        var probe = await ProcessRunner.RunAsync(tools.Ffprobe, ["-version"], TimeSpan.FromSeconds(Math.Min(15, tools.Remaining().TotalSeconds)), tools.Cancellation);
        if (probe.ExitCode != 0) throw new InvalidOperationException("ffprobe启动失败。");
        var report = new MediaDoctorReport(tools.Ffmpeg, tools.Ffprobe, version.Stdout.Split('\n')[0], probe.Stdout.Split('\n')[0], capabilities, capabilities.Values.All(value => value));
        return report;
    }

    /// <summary>按真实配音时长切成不超过平台限制的WAV片段，并记录源起止时刻。</summary>
    public static async Task<string> SplitAsync(MediaTools tools, string input, string outputDirectory, double maximum = 40, Action<string>? progress = null)
    {
        var source = await tools.ProbeAsync(input);
        if (source.Audio is null || source.Duration <= 0) throw new InvalidDataException("split输入必须包含有时长的音轨。");
        if (!double.IsFinite(maximum) || maximum < 1 || maximum > 60) throw new ArgumentException("分段长度必须在1到60秒之间。");
        var count = (int)Math.Ceiling(source.Duration / maximum);
        if (count > 256) throw new ArgumentException("最多生成256段，请缩短原始音频。");
        var directory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("分段交付目录已存在，拒绝混入旧文件。");
        Directory.CreateDirectory(directory);
        using var scratch = new MediaScratch(Path.GetDirectoryName(directory)!);
        var records = new List<object>();
        for (var index = 0; index < count; index++)
        {
            tools.Remaining();
            var start = index * maximum;
            var duration = Math.Min(maximum, source.Duration - start);
            var name = $"segment-{index + 1:D3}.wav";
            var temporary = Path.Combine(scratch.DirectoryPath, name);
            await tools.FfmpegAsync(["-i", source.Path, "-map", "0:a:0", "-af", $"atrim=start={MediaTools.N(start)}:duration={MediaTools.N(duration)},asetpts=PTS-STARTPTS", "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", "-threads", "2", "-n", temporary]);
            var actual = await tools.ProbeAsync(temporary);
            if (Math.Abs(actual.Duration - duration) > .04 || actual.Duration > 60.04) throw new InvalidDataException("分段实际时长与清单不符。");
            File.Move(temporary, Path.Combine(directory, name));
            records.Add(new { index = index + 1, file = name, start, end = start + duration, duration = actual.Duration });
            JsonFiles.Write(Path.Combine(directory, "segments.json"), new { source = source.Path, source_duration = source.Duration, max_seconds = maximum, complete = index + 1 == count, segments = records });
            progress?.Invoke($"分段 {index + 1}/{count}：{MediaTools.N(actual.Duration)} 秒");
        }
        return directory;
    }

    /// <summary>将绿幕转为带真实alpha平面的VP9 WebM，透明验证失败时不交付。</summary>
    public static async Task<string> KeyAsync(MediaTools tools, string input, string destination, string color = "0x50B470", double similarity = .20, double blend = .08)
    {
        var source = await tools.ProbeAsync(input);
        if (source.Video is null || source.Duration <= 0) throw new InvalidDataException("key输入必须为有效视频。");
        var output = MediaTools.NewOutput(destination);
        if (!Path.GetExtension(output).Equals(".webm", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("透明视频输出扩展名必须为.webm。");
        ValidateKey(color, similarity, blend);
        using var scratch = new MediaScratch(Path.GetDirectoryName(output)!);
        var temporary = Path.Combine(scratch.DirectoryPath, "keyed.webm");
        await tools.FfmpegAsync(["-i", source.Path, "-map", "0:v:0", "-an", "-vf", $"format=rgba,colorkey={color}:{MediaTools.N(similarity)}:{MediaTools.N(blend)},format=yuva420p", "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-auto-alt-ref", "0", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-threads", "2", "-crf", "24", "-b:v", "0", "-metadata:s:v:0", "alpha_mode=1", "-n", temporary]);
        var info = await tools.ProbeAsync(temporary);
        var alpha = await CheckAlphaAsync(tools, info, scratch.DirectoryPath);
        if (!alpha.HasTransparentAndOpaquePixels) throw new InvalidDataException("未验证到同时存在的透明与不透明像素，拒绝把普通WebM当透明视频交付。");
        File.Move(temporary, output);
        JsonFiles.Write(output + ".key.json", new { source = source.Path, output, color, similarity, blend, alpha, warning = "alpha通过只证明透明平面有效；细教鞭、头发和肤色保留仍需检查黑白底代表帧。" });
        return output;
    }

    /// <summary>探测成片、完整解码视频和音频，并提取代表帧以便人工检查。</summary>
    public static async Task<string> VerifyAsync(MediaTools tools, string input, string? output = null, string? framesDirectory = null, bool requireAlpha = false, double? expectedSeconds = null)
    {
        var info = await tools.ProbeAsync(input);
        var reportPath = MediaTools.NewOutput(output ?? info.Path + ".verify.json");
        if (info.Video is null || info.Duration <= 0 || !double.IsFinite(info.Duration)) throw new InvalidDataException("成片没有可用视频或有限时长。");
        var expected = expectedSeconds ?? info.Duration;
        if (!double.IsFinite(expected) || expected <= 0) throw new ArgumentException("预期时长必须为正有限数。");
        var frames = framesDirectory is { } configured ? Path.GetFullPath(configured) : Path.ChangeExtension(reportPath, null) + "-frames";
        if (Directory.Exists(frames) || File.Exists(frames)) throw new IOException("代表帧目录已存在。");
        Directory.CreateDirectory(frames);
        using var scratch = new MediaScratch(Path.GetDirectoryName(reportPath)!);
        var decodeArgs = new List<string> { "-xerror", "-i", info.Path, "-map", "0:v:0", "-map", "0:a?", "-threads", "2", "-f", "null", "-" };
        await tools.FfmpegAsync(decodeArgs);
        var frameRecords = new List<object>();
        var times = new[] { Math.Min(.25, info.Duration / 4), info.Duration / 2, Math.Max(0, info.Duration - .25) };
        for (var index = 0; index < times.Length; index++)
        {
            tools.Remaining();
            var frame = Path.Combine(frames, $"frame-{index + 1}.png");
            await tools.FfmpegAsync(["-ss", MediaTools.N(times[index]), "-i", info.Path, "-map", "0:v:0", "-frames:v", "1", "-update", "1", "-threads", "2", "-n", frame]);
            frameRecords.Add(new { seconds = times[index], path = frame, bytes = new FileInfo(frame).Length });
            if (requireAlpha)
            {
                // 在黑白底上实际合成，便于发现绿边、教鞭断裂和人物被过度抠除。
                foreach (var background in new[] { "black", "white" })
                {
                    tools.Remaining();
                    var composite = Path.Combine(frames, $"frame-{index + 1}-{background}.png");
                    await tools.FfmpegAsync(["-f", "lavfi", "-i", $"color=c={background}:s={info.Video.Width}x{info.Video.Height}:r=1", "-c:v", "libvpx-vp9", "-ss", MediaTools.N(times[index]), "-i", info.Path, "-filter_complex", "[0:v][1:v]overlay=shortest=1:format=auto,format=rgb24", "-frames:v", "1", "-update", "1", "-an", "-threads", "2", "-n", composite]);
                    frameRecords.Add(new { seconds = times[index], path = composite, bytes = new FileInfo(composite).Length });
                }
            }
        }
        double? maxVolume = null;
        if (info.Audio is not null)
        {
            var audio = await tools.FfmpegAsync(["-v", "info", "-i", info.Path, "-map", "0:a:0", "-vn", "-af", "volumedetect", "-threads", "2", "-f", "null", "-"]);
            var match = Regex.Match(audio.Stderr, @"max_volume:\s*(-?(?:\d+(?:\.\d+)?|inf))\s*dB", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (match.Success) maxVolume = match.Groups[1].Value == "-inf" ? -999 : double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        var alpha = requireAlpha ? await CheckAlphaAsync(tools, info, scratch.DirectoryPath) : null;
        var durationOkay = Math.Abs(info.Duration - expected) <= .25;
        var audioOkay = requireAlpha || info.Audio is not null && maxVolume.HasValue && maxVolume > -80;
        var passed = durationOkay && audioOkay && (alpha is null || alpha.HasTransparentAndOpaquePixels);
        JsonFiles.Write(reportPath, new { passed, info, expected_seconds = expected, duration_okay = durationOkay, full_decode_passed = true, audio_present = info.Audio is not null, max_volume_db = maxVolume, audio_non_silent = audioOkay, alpha, representative_frames = frameRecords, review_needed = "人工检查代表帧的面部、教鞭、脚底、抠像边缘及实际口型；自动检查不声称语义或口型已通过。" });
        if (!passed) throw new InvalidDataException("媒体验证未通过，详见已保存报告。");
        return reportPath;
    }

    /// <summary>强制使用支持alpha的VP9解码器，抽取alpha平面测量最小和最大值。</summary>
    private static async Task<AlphaReport> CheckAlphaAsync(MediaTools tools, MediaInfo info, string workingDirectory)
    {
        if (info.Video?.Codec != "vp9") throw new InvalidDataException("真实alpha检查当前仅支持VP9 WebM。");
        var samples = new List<AlphaSample>();
        var times = new[] { Math.Min(.1, info.Duration / 4), info.Duration / 2, Math.Max(0, info.Duration - .1) };
        for (var index = 0; index < times.Length; index++)
        {
            tools.Remaining();
            var name = $"alpha-{Guid.NewGuid():N}.txt";
            await tools.FfmpegAsync(["-c:v", "libvpx-vp9", "-ss", MediaTools.N(times[index]), "-i", info.Path, "-an", "-vf", $"alphaextract,signalstats,metadata=mode=print:file={name}", "-frames:v", "1", "-threads", "2", "-f", "null", "-"], workingDirectory);
            var text = File.ReadAllText(Path.Combine(workingDirectory, name));
            var minimum = Statistic(text, "YMIN");
            var maximum = Statistic(text, "YMAX");
            samples.Add(new AlphaSample(times[index], minimum, maximum));
        }
        return new AlphaReport(samples.All(item => item.Minimum < 250 && item.Maximum > 250), samples);
    }

    /// <summary>读取signalstats实际alpha值；缺少字段时立即失败。</summary>
    private static double Statistic(string text, string field)
    {
        var match = Regex.Match(text, @"lavfi\.signalstats\." + field + @"=(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : throw new InvalidDataException("alpha统计缺少" + field);
    }

    /// <summary>限制抠像参数，避免滤镜字符串注入或非法阈值。</summary>
    private static void ValidateKey(string color, double similarity, double blend)
    {
        if (!Regex.IsMatch(color, @"^(?:0x|#)[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) throw new ArgumentException("抠像颜色必须为0xRRGGBB或#RRGGBB。");
        if (!double.IsFinite(similarity) || similarity < .01 || similarity > 1 || !double.IsFinite(blend) || blend < 0 || blend > 1) throw new ArgumentException("similarity需在0.01到1之间，blend需在0到1之间。");
    }

    /// <summary>实际alpha抽帧结果，不能通过像素格式标签代替测量。</summary>
    private sealed record AlphaReport(bool HasTransparentAndOpaquePixels, IReadOnlyList<AlphaSample> Samples);

    /// <summary>一个代表时刻的alpha平面极值。</summary>
    private sealed record AlphaSample(double Seconds, double Minimum, double Maximum);
}

/// <summary>已探测的本机媒体能力，供界面展示真实版本和可用滤镜。</summary>
public sealed record MediaDoctorReport(string Ffmpeg, string Ffprobe, string FfmpegVersion, string FfprobeVersion, IReadOnlyDictionary<string, bool> Capabilities, bool Ready);
