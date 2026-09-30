using System.Text.RegularExpressions;

namespace VideoProduction;

/// <summary>校验旧版制作清单及其本地素材引用，供命令行和桌面项目共同使用。</summary>
public static class ManifestValidator
{
    /// <summary>检查清单结构、源时间、字幕和本地素材路径。</summary>
    public static List<string> Validate(VideoManifest manifest, string manifestPath, bool draft = false)
    {
        var errors = new List<string>();
        if (manifest.Version != 1) errors.Add("不支持的清单版本。");
        if (manifest.Width is < 320 or > 3840 || manifest.Height is < 240 or > 2160 || manifest.Width % 2 != 0 || manifest.Height % 2 != 0) errors.Add("分辨率须为偶数，范围320×240到3840×2160。");
        if (manifest.Fps is < 1 or > 60) errors.Add("fps必须在1到60之间。");
        if (manifest.TaskbarHeight < 0 || manifest.TaskbarHeight > manifest.Height / 3) errors.Add("任务栏高度不合法。");
        if (!ValidRate(manifest.AudioRate)) errors.Add("audio_rate必须在0.5到2之间。");
        if (!double.IsFinite(manifest.TargetSeconds) || manifest.TargetSeconds is < 1 or > 1800) errors.Add("target_seconds必须在1至1800秒之间。");
        if (manifest.Scenes.Count > 40 || !draft && manifest.Scenes.Count == 0) errors.Add("成片必须有1至40个章节。");
        if (manifest.Presenter.Height < 20 || manifest.Presenter.Height > manifest.Height || !double.IsFinite(manifest.Presenter.FeetY) || manifest.Presenter.FeetY is <= 0 or > 1) errors.Add("主持人高度或feet_y无效。");
        if (!Regex.IsMatch(manifest.Presenter.KeyColor, "^0x[0-9a-fA-F]{6}$", RegexOptions.None, TimeSpan.FromMilliseconds(50))) errors.Add("key_color必须为0xRRGGBB。");
        if (!double.IsFinite(manifest.Presenter.Similarity) || manifest.Presenter.Similarity is < 0.01 or > 1 || !double.IsFinite(manifest.Presenter.Blend) || manifest.Presenter.Blend is < 0 or > 1) errors.Add("抠像参数超出范围。");
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scene in manifest.Scenes.Take(40))
        {
            if (!Regex.IsMatch(scene.Id, "^[a-zA-Z0-9_-]{1,64}$", RegexOptions.None, TimeSpan.FromMilliseconds(50)) || !identifiers.Add(scene.Id)) errors.Add($"章节编号无效或重复：{scene.Id}");
            if (scene.AudioRate.HasValue && !ValidRate(scene.AudioRate.Value)) errors.Add($"{scene.Id}：音频速度无效。");
            if (!draft && (string.IsNullOrWhiteSpace(scene.Narration) || string.IsNullOrWhiteSpace(scene.Evidence))) errors.Add($"{scene.Id}：需要旁白和功能依据。");
            if (scene.Clips.Count > 160 || !draft && scene.Clips.Count == 0) errors.Add($"{scene.Id}：动作片段应为1至160个。");
            if (scene.Captions.Count > 400) errors.Add($"{scene.Id}：字幕不能超过400条。");
            double previousEnd = 0;
            foreach (var cue in scene.Captions.Take(400))
            {
                if (!double.IsFinite(cue.Start) || !double.IsFinite(cue.End) || cue.Start < previousEnd || cue.End <= cue.Start || string.IsNullOrWhiteSpace(cue.Text)) errors.Add($"{scene.Id}：字幕时间重叠或无效。");
                previousEnd = cue.End;
            }
            foreach (var clip in scene.Clips.Take(160))
            {
                if (!double.IsFinite(clip.Offset) || clip.Offset < 0 || clip.Duration is { } duration && (!double.IsFinite(duration) || duration <= 0)) errors.Add($"{scene.Id}：片段源时间无效。");
            }
            if (!draft)
                foreach (var asset in new[] { scene.Screen, scene.Audio }.Concat(scene.Clips.Take(160).Select(c => c.Video)))
                    try { _ = ResolveAsset(asset, manifestPath); } catch (Exception e) when (e is IOException or ArgumentException) { errors.Add($"{scene.Id}：{e.Message}"); }
        }
        return errors;
    }

    /// <summary>只接受存在的本地资产，拒绝 URL 和目录素材。</summary>
    public static string ResolveAsset(string asset, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(asset) || asset.Contains("://", StringComparison.Ordinal)) throw new ArgumentException("素材须为非空本地路径。");
        var path = Path.GetFullPath(asset, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        if (!File.Exists(path)) throw new FileNotFoundException($"素材不存在：{asset}");
        return path;
    }

    /// <summary>排除无穷大和非数值语速。</summary>
    private static bool ValidRate(double rate) => double.IsFinite(rate) && rate is >= 0.5 and <= 2;
}
