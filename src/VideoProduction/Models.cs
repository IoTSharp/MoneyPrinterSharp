namespace VideoProduction;

/// <summary>一次制作的可移植清单；所有时间以原始素材秒数记录。</summary>
public sealed class VideoManifest
{
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "软件功能讲解";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public int TaskbarHeight { get; set; } = 56;
    public double AudioRate { get; set; } = 1;
    public double TargetSeconds { get; set; } = 300;
    public PresenterSettings Presenter { get; set; } = new();
    public List<VideoScene> Scenes { get; set; } = [];
}

/// <summary>主持人定位及抠像参数，需通过实际白底、黑底抽帧校准。</summary>
public sealed class PresenterSettings
{
    public int CenterX { get; set; } = 1120;
    public int Height { get; set; } = 370;
    public double FeetY { get; set; } = 0.96;
    public string KeyColor { get; set; } = "0x50B470";
    public double Similarity { get; set; } = 0.20;
    public double Blend { get; set; } = 0.08;
}

/// <summary>章节使用真实截图、完整配音、顺序视频片段及对应旁白。</summary>
public sealed class VideoScene
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Screen { get; set; } = "";
    public string Audio { get; set; } = "";
    public string Narration { get; set; } = "";
    public string Evidence { get; set; } = "";
    public double? AudioRate { get; set; }
    public List<PresenterClip> Clips { get; set; } = [];
    public List<CaptionCue> Captions { get; set; } = [];
}

/// <summary>片段时长与偏移为源时间；不同旁白的旧口型素材必须标记未同步。</summary>
public sealed class PresenterClip
{
    public string Video { get; set; } = "";
    public double Offset { get; set; }
    public double? Duration { get; set; }
    public bool LipSynced { get; set; } = true;
}

/// <summary>字幕时间为原始配音时间；渲染时与配音统一除以速度。</summary>
public sealed class CaptionCue
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>外部命令的执行结果，不含秘密参数。</summary>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
