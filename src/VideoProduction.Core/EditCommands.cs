namespace VideoProduction;

/// <summary>可撤销的项目编辑命令；命令只改变项目 JSON，不改写原始素材。</summary>
public abstract class MpsEditCommand
{
    public abstract string Description { get; }
    protected abstract void ApplyCore(MpsProjectDocument project);
    protected abstract void RevertCore(MpsProjectDocument project);

    internal void Apply(MpsProjectDocument project) => ApplyCore(project);
    internal void Revert(MpsProjectDocument project) => RevertCore(project);
}

/// <summary>统一管理编辑、撤销和重做栈，并在每次操作后校验项目。</summary>
public sealed class MpsEditHistory
{
    private readonly Stack<MpsEditCommand> undo = new();
    private readonly Stack<MpsEditCommand> redo = new();

    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;

    public void Execute(MpsProjectDocument project, MpsEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        command.Apply(project);
        try { MpsTimelineValidation.Validate(project); }
        catch { command.Revert(project); throw; }
        undo.Push(command);
        redo.Clear();
    }

    public bool Undo(MpsProjectDocument project)
    {
        if (undo.Count == 0) return false;
        var command = undo.Pop();
        command.Revert(project);
        try { MpsTimelineValidation.Validate(project); }
        catch { command.Apply(project); undo.Push(command); throw; }
        redo.Push(command);
        return true;
    }

    public bool Redo(MpsProjectDocument project)
    {
        if (redo.Count == 0) return false;
        var command = redo.Pop();
        command.Apply(project);
        try { MpsTimelineValidation.Validate(project); }
        catch { command.Revert(project); redo.Push(command); throw; }
        undo.Push(command);
        return true;
    }
}

/// <summary>移动片段并可恢复原轨道和时间坐标。</summary>
public sealed class MoveClipCommand : MpsEditCommand
{
    private readonly string clipId;
    private readonly string targetTrackId;
    private readonly MpsTime targetStart;
    private MpsTrack? oldTrack;
    private MpsTime? oldStart;

    public MoveClipCommand(string clipId, string targetTrackId, MpsTime targetStart)
    {
        this.clipId = clipId; this.targetTrackId = targetTrackId; this.targetStart = targetStart;
    }
    public override string Description => "移动片段";
    protected override void ApplyCore(MpsProjectDocument project)
    {
        var (sourceTrack, clip) = Find(project, clipId);
        var target = project.Tracks.SingleOrDefault(track => track.Id == targetTrackId) ?? throw new InvalidOperationException("目标轨道不存在。");
        oldTrack ??= sourceTrack;
        oldStart ??= clip.TimelineStart;
        if (!ReferenceEquals(sourceTrack, target)) { sourceTrack.Clips.Remove(clip); target.Clips.Add(clip); }
        clip.TrackId = target.Id; clip.TimelineStart = targetStart;
    }
    protected override void RevertCore(MpsProjectDocument project)
    {
        var (_, clip) = Find(project, clipId);
        var current = project.Tracks.Single(track => track.Id == clip.TrackId);
        var original = oldTrack ?? throw new InvalidOperationException("移动命令没有原始状态。");
        if (!ReferenceEquals(current, original)) { current.Clips.Remove(clip); original.Clips.Add(clip); }
        clip.TrackId = original.Id; clip.TimelineStart = oldStart!;
    }
    internal static (MpsTrack Track, MpsClip Clip) Find(MpsProjectDocument project, string id)
    {
        foreach (var track in project.Tracks)
            foreach (var clip in track.Clips)
                if (clip.Id == id) return (track, clip);
        throw new KeyNotFoundException("片段不存在。");
    }
}

/// <summary>更新片段源入出点。</summary>
public sealed class TrimClipCommand : MpsEditCommand
{
    private readonly string clipId; private readonly MpsTime sourceIn; private readonly MpsTime sourceOut;
    private MpsTime? oldIn; private MpsTime? oldOut;
    public TrimClipCommand(string clipId, MpsTime sourceIn, MpsTime sourceOut) { this.clipId = clipId; this.sourceIn = sourceIn; this.sourceOut = sourceOut; }
    public override string Description => "裁切片段";
    protected override void ApplyCore(MpsProjectDocument project) { var clip = MoveClipCommand.Find(project, clipId).Clip; oldIn ??= clip.SourceIn; oldOut ??= clip.SourceOut; clip.SourceIn = sourceIn; clip.SourceOut = sourceOut; }
    protected override void RevertCore(MpsProjectDocument project) { var clip = MoveClipCommand.Find(project, clipId).Clip; clip.SourceIn = oldIn!; clip.SourceOut = oldOut!; }
}

/// <summary>在源时间处分割片段，撤销时恢复原片段列表。</summary>
public sealed class SplitClipCommand : MpsEditCommand
{
    private readonly string clipId; private readonly MpsTime splitAt; private MpsClip? created;
    public SplitClipCommand(string clipId, MpsTime splitAt) { this.clipId = clipId; this.splitAt = splitAt; }
    public override string Description => "分割片段";
    protected override void ApplyCore(MpsProjectDocument project)
    {
        var (track, clip) = MoveClipCommand.Find(project, clipId);
        if (splitAt.ToRational() <= clip.SourceIn.ToRational() || splitAt.ToRational() >= clip.SourceOut.ToRational()) throw new InvalidDataException("分割点必须位于片段源范围内。");
        var oldOut = clip.SourceOut; clip.SourceOut = splitAt;
        var splitId = clip.Id.Length >= 121 ? clip.Id[..121] + "-split" : clip.Id + "-split";
        if (track.Clips.Any(item => item.Id == splitId && !ReferenceEquals(item, created))) throw new InvalidOperationException("分割后的片段标识已存在。");
        created ??= new MpsClip
        {
            Id = splitId, TrackId = clip.TrackId, AssetId = clip.AssetId,
            TimelineStart = MpsTime.From(new ClipTimeMapping(new SourceTime(clip.SourceIn.ToRational()), new SourceTime(splitAt.ToRational()), new ProjectTime(clip.TimelineStart.ToRational()), clip.SpeedNum, clip.SpeedDen).TimelineEnd.Value),
            SourceIn = splitAt, SourceOut = oldOut, SpeedNum = clip.SpeedNum, SpeedDen = clip.SpeedDen,
            Properties = CloneProperties(clip.Properties)
        };
        if (!track.Clips.Contains(created)) track.Clips.Add(created);
    }
    protected override void RevertCore(MpsProjectDocument project) { var (track, clip) = MoveClipCommand.Find(project, clipId); if (created is not null) track.Clips.Remove(created); clip.SourceOut = created?.SourceOut ?? clip.SourceOut; }

    private static MpsClipProperties CloneProperties(MpsClipProperties source) => new()
    {
        GainDb = source.GainDb,
        Opacity = source.Opacity,
        LipSynced = source.LipSynced,
        Transform = source.Transform is null ? null : new MpsTransform { X = source.Transform.X, Y = source.Transform.Y, Width = source.Transform.Width, Height = source.Transform.Height, Rotation = source.Transform.Rotation },
        Subtitle = source.Subtitle is null ? null : new MpsSubtitleProperties { Text = source.Subtitle.Text, FontFamily = source.Subtitle.FontFamily, FontSize = source.Subtitle.FontSize, Color = source.Subtitle.Color, Alignment = source.Subtitle.Alignment },
        PresenterOverlay = source.PresenterOverlay is null ? null : new MpsPresenterOverlay { KeyColor = source.PresenterOverlay.KeyColor, Similarity = source.PresenterOverlay.Similarity, Blend = source.PresenterOverlay.Blend }
    };
}

/// <summary>调整轨道排序；排序值重复时通过交换保持轨道唯一序号。</summary>
public sealed class ReorderTrackCommand : MpsEditCommand
{
    private readonly string trackId; private readonly int newOrder; private int? oldOrder;
    public ReorderTrackCommand(string trackId, int newOrder) { this.trackId = trackId; this.newOrder = newOrder; }
    public override string Description => "排序轨道";
    protected override void ApplyCore(MpsProjectDocument project) { var track = project.Tracks.Single(item => item.Id == trackId); oldOrder ??= track.Order; var other = project.Tracks.SingleOrDefault(item => item.Order == newOrder && item.Id != trackId); if (other is not null) other.Order = track.Order; track.Order = newOrder; }
    protected override void RevertCore(MpsProjectDocument project) { var track = project.Tracks.Single(item => item.Id == trackId); var other = project.Tracks.SingleOrDefault(item => item.Order == oldOrder && item.Id != trackId); if (other is not null) other.Order = track.Order; track.Order = oldOrder ?? track.Order; }
}

/// <summary>修改字幕文本，保持 Unicode 规范化。</summary>
public sealed class UpdateSubtitleCommand : MpsEditCommand
{
    private readonly string clipId; private readonly string text; private string? oldText;
    public UpdateSubtitleCommand(string clipId, string text) { this.clipId = clipId; this.text = text; }
    public override string Description => "编辑字幕";
    protected override void ApplyCore(MpsProjectDocument project) { var subtitle = MoveClipCommand.Find(project, clipId).Clip.Properties.Subtitle ?? throw new InvalidOperationException("片段没有字幕属性。"); oldText ??= subtitle.Text; subtitle.Text = MpsTextLayout.Normalize(text); }
    protected override void RevertCore(MpsProjectDocument project) { var subtitle = MoveClipCommand.Find(project, clipId).Clip.Properties.Subtitle ?? throw new InvalidOperationException("片段没有字幕属性。"); subtitle.Text = oldText ?? subtitle.Text; }
}

/// <summary>修改片段增益并可撤销。</summary>
public sealed class SetGainCommand : MpsEditCommand
{
    private readonly string clipId; private readonly double gain; private double? oldGain;
    public SetGainCommand(string clipId, double gain) { this.clipId = clipId; this.gain = gain; }
    public override string Description => "调整音量";
    protected override void ApplyCore(MpsProjectDocument project) { var clip = MoveClipCommand.Find(project, clipId).Clip; oldGain ??= clip.Properties.GainDb; clip.Properties.GainDb = gain; }
    protected override void RevertCore(MpsProjectDocument project) { MoveClipCommand.Find(project, clipId).Clip.Properties.GainDb = oldGain ?? 0; }
}
