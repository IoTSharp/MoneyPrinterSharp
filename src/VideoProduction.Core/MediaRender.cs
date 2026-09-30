using System.Globalization;
using System.Text;

namespace VideoProduction;

/// <summary>确定性逐帧合成，使用源时间统一计算音频、主持人和字幕的播放速度。</summary>
public static partial class MediaWorkflows
{
    /// <summary>逐章完成截图、任务栏、主持人、字幕和配音合成，再无损拼接章节。</summary>
    public static async Task<string> RenderAsync(MediaTools tools, string manifestFile, string destination, bool allowUnsynced = false, string? font = null, Action<string>? progress = null)
    {
        var manifestPath = MediaTools.ExistingFile(manifestFile);
        var manifest = JsonFiles.Read<VideoManifest>(manifestPath);
        ValidateManifest(tools, manifest);
        var baseDirectory = Path.GetDirectoryName(manifestPath)!;
        var output = MediaTools.NewOutput(destination);
        if (!Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("render输出必须是.mp4。");
        var reportPath = output + ".render.json";
        if (File.Exists(reportPath)) throw new IOException("渲染报告已存在，拒绝覆盖。");
        using var scratch = new MediaScratch(Path.GetDirectoryName(output)!);
        if (font is null && OperatingSystem.IsWindows() && File.Exists(@"C:\Windows\Fonts\msyh.ttc")) font = @"C:\Windows\Fonts\msyh.ttc";
        if (font is not null)
        {
            var fonts = Path.Combine(scratch.DirectoryPath, "fonts");
            Directory.CreateDirectory(fonts);
            File.Copy(MediaTools.ExistingFile(font), Path.Combine(fonts, Path.GetFileName(font)), false);
        }
        var chapters = new List<RenderedChapter>();
        var totalSeconds = 0d;
        var unsynced = false;
        for (var sceneIndex = 0; sceneIndex < manifest.Scenes.Count; sceneIndex++)
        {
            tools.Remaining();
            var scene = manifest.Scenes[sceneIndex];
            var rate = scene.AudioRate ?? manifest.AudioRate;
            ValidateRate(rate);
            var screen = ResolveMedia(scene.Screen, baseDirectory);
            var audio = await tools.ProbeAsync(ResolveMedia(scene.Audio, baseDirectory));
            if (audio.Audio is null || audio.Duration <= 0) throw new InvalidDataException($"{scene.Id}配音无有效音轨或时长。");
            var duration = audio.Duration / rate;
            totalSeconds += duration;
            if (duration > 1200 || totalSeconds > 7200) throw new InvalidDataException("单章输出最多1200秒，整片最多7200秒。");
            var clips = new List<PreparedClip>();
            var sourceCursor = 0d;
            foreach (var item in scene.Clips)
            {
                tools.Remaining();
                if (!item.LipSynced && !allowUnsynced) throw new InvalidDataException($"{scene.Id}存在未同步口型素材；明确允许未同步口型后才可导出标注草稿。");
                unsynced |= !item.LipSynced;
                if (sourceCursor >= audio.Duration - .005) break;
                var video = await tools.ProbeAsync(ResolveMedia(item.Video, baseDirectory));
                if (video.Video is null || video.Duration <= 0) throw new InvalidDataException($"{scene.Id}主持人素材没有有效视频。");
                if (!double.IsFinite(item.Offset) || item.Offset < 0 || item.Offset >= video.Duration) throw new InvalidDataException($"{scene.Id}片段偏移无效。");
                var requested = item.Duration ?? video.Duration - item.Offset;
                if (!double.IsFinite(requested) || requested <= 0 || requested > video.Duration - item.Offset + .08) throw new InvalidDataException($"{scene.Id}源片段时长超出素材。");
                var take = Math.Min(requested, audio.Duration - sourceCursor);
                clips.Add(new PreparedClip(video, item.Offset, take, sourceCursor, sourceCursor / rate, take / rate, item.LipSynced));
                sourceCursor += take;
            }
            if (clips.Count == 0 || sourceCursor < audio.Duration - .08) throw new InvalidDataException($"{scene.Id}动作素材不足：源片段{MediaTools.N(sourceCursor)}秒，配音{MediaTools.N(audio.Duration)}秒；不会循环旧口型补足。");
            var assName = $"chapter-{sceneIndex:D3}.ass";
            var assPath = Path.Combine(scratch.DirectoryPath, assName);
            var captionsEstimated = scene.Captions.Count == 0;
            WriteAss(tools, assPath, manifest, scene, audio.Duration, rate, allowUnsynced && clips.Any(item => !item.LipSynced));
            var chapterName = $"chapter-{sceneIndex:D3}.mp4";
            var chapterPath = Path.Combine(scratch.DirectoryPath, chapterName);
            var inputArgs = new List<string> { "-loop", "1", "-framerate", manifest.Fps.ToString(CultureInfo.InvariantCulture), "-i", screen, "-i", audio.Path };
            foreach (var clip in clips) { tools.Remaining(); inputArgs.AddRange(["-i", clip.Info.Path]); }
            var graphName = $"chapter-{sceneIndex:D3}.filter";
            var graph = BuildSceneGraph(tools, manifest, clips, audio.Duration, rate, assName, font is not null);
            File.WriteAllText(Path.Combine(scratch.DirectoryPath, graphName), graph, new UTF8Encoding(false));
            inputArgs.AddRange(["-filter_complex_script", graphName, "-map", "[video]", "-map", "[audio]", "-t", MediaTools.N(duration), "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-r", manifest.Fps.ToString(CultureInfo.InvariantCulture), "-fps_mode", "cfr", "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", "-threads", "2", "-movflags", "+faststart", "-n", chapterPath]);
            progress?.Invoke($"合成 {sceneIndex + 1}/{manifest.Scenes.Count}：{scene.Title}，{MediaTools.N(duration)} 秒，速度 {MediaTools.N(rate)}");
            await tools.FfmpegAsync(inputArgs, scratch.DirectoryPath);
            var rendered = await tools.ProbeAsync(chapterPath);
            if (rendered.Video?.Codec != "h264" || rendered.Audio?.Codec != "aac" || Math.Abs(rendered.Duration - duration) > .16) throw new InvalidDataException($"{scene.Id}章节输出编码或时长不符。");
            chapters.Add(new RenderedChapter(scene.Id, chapterName, audio.Duration, rate, duration, rendered.Duration, captionsEstimated, clips));
        }
        var concatFile = Path.Combine(scratch.DirectoryPath, "chapters.concat");
        File.WriteAllLines(concatFile, chapters.Select(chapter => $"file '{chapter.File}'"), new UTF8Encoding(false));
        var assembled = Path.Combine(scratch.DirectoryPath, "assembled.mp4");
        await tools.FfmpegAsync(["-f", "concat", "-safe", "1", "-i", "chapters.concat", "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-movflags", "+faststart", "-n", assembled], scratch.DirectoryPath);
        var final = await tools.ProbeAsync(assembled);
        var durationError = Math.Abs(final.Duration - totalSeconds);
        if (durationError > .25 + manifest.Scenes.Count / (double)manifest.Fps) throw new InvalidDataException("拼接成片时长偏离音频时间轴。");
        File.Move(assembled, output);
        JsonFiles.Write(reportPath, new { manifest = manifestPath, output, output_status = unsynced ? "draft-unsynced" : "rendered", duration = final.Duration, expected_duration = totalSeconds, duration_error_seconds = durationError, target_seconds = manifest.TargetSeconds, target_difference_seconds = final.Duration - manifest.TargetSeconds, width = manifest.Width, height = manifest.Height, fps = manifest.Fps, ffmpeg = tools.Ffmpeg, source_time_contract = "clips.offset/duration和captions.start/end均为源秒数；所有呈现时间统一除以audio_rate。", chapters, quality_note = "口型同步标记来自清单，须用真实视频审核；自动合成不证明语义口型、脸部及细教鞭完好。" });
        return output;
    }

    /// <summary>校验明确数量上限及画布参数，防止滤镜链或资源规模失控。</summary>
    private static void ValidateManifest(MediaTools tools, VideoManifest manifest)
    {
        if (manifest.Width is < 320 or > 3840 || manifest.Height is < 240 or > 2160 || manifest.Width % 2 != 0 || manifest.Height % 2 != 0) throw new InvalidDataException("画布应为320×240到3840×2160之间的偶数尺寸。");
        if (manifest.Fps is < 1 or > 60 || manifest.TaskbarHeight < 0 || manifest.TaskbarHeight > manifest.Height / 3) throw new InvalidDataException("帧率或任务栏高度无效。");
        if (manifest.Scenes.Count is < 1 or > 40) throw new InvalidDataException("清单必须含1到40章。");
        ValidateRate(manifest.AudioRate);
        ValidateKey(manifest.Presenter.KeyColor, manifest.Presenter.Similarity, manifest.Presenter.Blend);
        if (manifest.Presenter.Height < 32 || manifest.Presenter.Height > manifest.Height || !double.IsFinite(manifest.Presenter.FeetY) || manifest.Presenter.FeetY is < .1 or > 1 || manifest.Presenter.CenterX < 0 || manifest.Presenter.CenterX > manifest.Width) throw new InvalidDataException("主持人位置、身高或脚底比例无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scene in manifest.Scenes.Take(40))
        {
            tools.Remaining();
            if (string.IsNullOrWhiteSpace(scene.Id) || !ids.Add(scene.Id)) throw new InvalidDataException("章节id缺失或重复。");
            if (scene.Clips.Count is < 1 or > 160 || scene.Captions.Count > 400 || scene.Narration.Length > 20000 || scene.Title.Length > 200) throw new InvalidDataException("章节素材、字幕或文本数量超过限制。");
        }
    }

    /// <summary>约束单次atempo范围，音画使用同一个速率。</summary>
    private static void ValidateRate(double rate)
    {
        if (!double.IsFinite(rate) || rate is < .5 or > 2) throw new InvalidDataException("audio_rate必须在0.5到2之间。");
    }

    /// <summary>素材相对路径以清单目录为基准，禁止读取网络地址。</summary>
    private static string ResolveMedia(string value, string directory) => MediaTools.ExistingFile(Path.IsPathRooted(value) ? value : Path.Combine(directory, value));

    /// <summary>建立逐片段源裁剪、统一变速、抠像及叠加滤镜，避免源秒数与输出秒数混用。</summary>
    private static string BuildSceneGraph(MediaTools tools, VideoManifest manifest, IReadOnlyList<PreparedClip> clips, double sourceDuration, double rate, string assName, bool hasFont)
    {
        var p = manifest.Presenter;
        var presenterWidth = clips.Max(clip => (int)Math.Ceiling(clip.Info.Video!.Width * p.Height / (double)clip.Info.Video.Height / 2) * 2);
        if (presenterWidth > manifest.Width * 3) throw new InvalidDataException("主持人片段宽高比异常。");
        var graph = new StringBuilder();
        for (var index = 0; index < clips.Count; index++)
        {
            tools.Remaining();
            var clip = clips[index];
            graph.Append($"[{index + 2}:v]trim=start={MediaTools.N(clip.Offset)}:duration={MediaTools.N(clip.SourceDuration)},setpts=(PTS-STARTPTS)/{MediaTools.N(rate)},format=rgba,colorkey={p.KeyColor}:{MediaTools.N(p.Similarity)}:{MediaTools.N(p.Blend)},scale=-2:{p.Height},pad={presenterWidth}:{p.Height}:(ow-iw)/2:0:color=black@0,setsar=1,fps={manifest.Fps},format=yuva420p[clip{index}];\n");
        }
        graph.Append(string.Concat(Enumerable.Range(0, clips.Count).Select(index => $"[clip{index}]")));
        graph.Append($"concat=n={clips.Count}:v=1:a=0[presenter];\n");
        var top = manifest.Height - manifest.TaskbarHeight;
        graph.Append($"[0:v]scale={manifest.Width}:{top}:force_original_aspect_ratio=decrease,pad={manifest.Width}:{manifest.Height}:(ow-iw)/2:0:color=0x142333,setsar=1,fps={manifest.Fps},drawbox=x=0:y={top}:w=iw:h={manifest.TaskbarHeight}:color=0xEAF0F7:t=fill[background];\n");
        var left = p.CenterX - presenterWidth / 2;
        var y = top - p.Height * p.FeetY;
        // FeetY表示人物脚底在原主持人画面中的相对位置，因此按该锚点对齐任务栏顶线。
        graph.Append($"[background][presenter]overlay=x={left}:y={MediaTools.N(y)}:shortest=1:eof_action=endall:format=auto,subtitles=filename={assName}{(hasFont ? ":fontsdir=fonts" : "")},format=yuv420p[video];\n");
        var duration = sourceDuration / rate;
        graph.Append($"[1:a]atrim=duration={MediaTools.N(sourceDuration)},asetpts=PTS-STARTPTS,atempo={MediaTools.N(rate)},apad,atrim=duration={MediaTools.N(duration)}[audio]\n");
        return graph.ToString();
    }

    /// <summary>生成本地ASS字幕，时间始终从源配音秒数除以统一播放速率。</summary>
    private static void WriteAss(MediaTools tools, string path, VideoManifest manifest, VideoScene scene, double sourceDuration, double rate, bool unsynced)
    {
        var width = manifest.Width;
        var height = manifest.Height;
        var fontSize = Math.Max(16, (int)Math.Round(height / 36d));
        var top = height - manifest.TaskbarHeight;
        var text = new StringBuilder($"[Script Info]\nScriptType: v4.00+\nPlayResX: {width}\nPlayResY: {height}\nWrapStyle: 0\nScaledBorderAndShadow: yes\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Caption,Microsoft YaHei,{fontSize},&H00FFFFFF,&H00FFFFFF,&H00132230,&HA0132230,0,0,0,0,100,100,0,0,3,8,0,2,28,{Math.Min(width / 3, 350)},{manifest.TaskbarHeight + 28},1\nStyle: Title,Microsoft YaHei,{fontSize + 4},&H00FFFFFF,&H00FFFFFF,&H00152232,&H00152232,-1,0,0,0,100,100,0,0,3,9,0,7,28,28,25,1\nStyle: Taskbar,Microsoft YaHei,{Math.Max(12, fontSize - 5)},&H004F3A28,&H004F3A28,&H00EAF0F7,&H00EAF0F7,0,0,0,0,100,100,0,0,1,0,0,7,24,24,{top + Math.Max(5, manifest.TaskbarHeight / 3)},1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        var end = sourceDuration / rate;
        AddAss(text, 0, end, "Title", scene.Title);
        if (manifest.TaskbarHeight > 0) AddAss(text, 0, end, "Taskbar", "MoneyPrinter#  ·  " + manifest.Title + "  ·  AI 生成主持人与配音" + (unsynced ? "  ·  未同步口型草稿" : ""));
        var cues = scene.Captions.Count > 0 ? scene.Captions : EstimateCaptions(tools, scene.Narration, sourceDuration);
        foreach (var cue in cues.Take(400))
        {
            tools.Remaining();
            if (!double.IsFinite(cue.Start) || !double.IsFinite(cue.End) || cue.Start < 0 || cue.End <= cue.Start || cue.End > sourceDuration + .1 || cue.Text.Length > 2000) throw new InvalidDataException(scene.Id + "字幕时间或长度无效。");
            AddAss(text, cue.Start / rate, Math.Min(sourceDuration, cue.End) / rate, "Caption", cue.Text);
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>没有人工字幕时间时按字数估计，报告会明确标记此局限。</summary>
    private static List<CaptionCue> EstimateCaptions(MediaTools tools, string narration, double duration)
    {
        if (string.IsNullOrWhiteSpace(narration)) return [];
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var character in narration.Take(20000))
        {
            tools.Remaining();
            line.Append(character);
            if (line.Length >= 30 || "。！？；".Contains(character)) { lines.Add(line.ToString()); line.Clear(); }
            if (lines.Count >= 399) break;
        }
        if (line.Length > 0) lines.Add(line.ToString());
        var total = lines.Sum(item => item.Length);
        var cursor = 0d;
        return lines.Select(item => { var start = cursor; cursor += duration * item.Length / total; return new CaptionCue { Start = start, End = cursor, Text = item }; }).ToList();
    }

    /// <summary>转义ASS控制字符，防止字幕正文被作为样式指令执行。</summary>
    private static void AddAss(StringBuilder builder, double start, double end, string style, string text)
    {
        var escaped = text.Replace("\\", "＼", StringComparison.Ordinal).Replace("{", "｛", StringComparison.Ordinal).Replace("}", "｝", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\\N", StringComparison.Ordinal);
        builder.AppendLine($"Dialogue: 0,{AssTime(start)},{AssTime(end)},{style},,0,0,0,,{escaped}");
    }

    /// <summary>将秒数转为ASS百分之一秒时间码。</summary>
    private static string AssTime(double seconds)
    {
        var ticks = (long)Math.Round(seconds * 100);
        return $"{ticks / 360000}:{ticks / 6000 % 60:D2}:{ticks / 100 % 60:D2}.{ticks % 100:D2}";
    }

    /// <summary>已解析片段同时记录源与呈现时间，便于复核音画同步。</summary>
    private sealed record PreparedClip(MediaInfo Info, double Offset, double SourceDuration, double SourceStart, double OutputStart, double OutputDuration, bool LipSynced);

    /// <summary>每章实际编码结果及来源，用于最终制作报告。</summary>
    private sealed record RenderedChapter(string Id, string File, double SourceAudioSeconds, double AudioRate, double ExpectedSeconds, double ActualSeconds, bool CaptionsEstimated, IReadOnlyList<PreparedClip> Clips);
}
