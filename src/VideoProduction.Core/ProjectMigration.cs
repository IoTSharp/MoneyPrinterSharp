namespace VideoProduction;

/// <summary>旧版 VideoManifest 到多轨项目的离线迁移结果。</summary>
public sealed record MpsMigrationResult(MpsProjectDocument Project, IReadOnlyList<string> UnmigratedFields);

/// <summary>把章节、截图、旁白、主持人和字幕映射到可重开的多轨项目。</summary>
public static class MpsProjectMigration
{
    public static MpsMigrationResult FromManifest(VideoManifest manifest, string projectTitle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Scenes is null || manifest.Scenes.Count > 4096) throw new InvalidDataException("旧清单章节数量无效。");
        var project = new MpsProjectDocument { Title = MpsTextLayout.Normalize(projectTitle) };
        project.Profiles.Add(new MpsProjectProfile
        {
            Id = manifest.Width >= manifest.Height ? "landscape" : "portrait",
            Width = Math.Clamp(manifest.Width, 320, 3840),
            Height = Math.Clamp(manifest.Height, 240, 2160),
            FpsNum = Math.Clamp(manifest.Fps, 1, 240),
            FpsDen = 1
        });
        project.ActiveProfileId = project.Profiles[0].Id;
        var tracks = new Dictionary<MpsTrackKind, MpsTrack>();
        foreach (var kind in Enum.GetValues<MpsTrackKind>())
        {
            var track = new MpsTrack { Id = kind.ToString().ToLowerInvariant(), Kind = kind, Order = tracks.Count };
            project.Tracks.Add(track);
            tracks.Add(kind, track);
        }
        var assetsByPath = new Dictionary<string, MpsAssetReference>(StringComparer.OrdinalIgnoreCase);
        var unmigrated = new List<string>();
        long cursorMilliseconds = 0;
        foreach (var scene in manifest.Scenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scene is null || string.IsNullOrWhiteSpace(scene.Id)) { unmigrated.Add("scene without id"); continue; }
            var duration = EstimateSceneDuration(scene);
            AddMediaClip(project, tracks[MpsTrackKind.Screen], scene.Screen, cursorMilliseconds, duration, assetsByPath, unmigrated, cancellationToken);
            AddMediaClip(project, tracks[MpsTrackKind.Narration], scene.Audio, cursorMilliseconds, duration, assetsByPath, unmigrated, cancellationToken);
            foreach (var presenter in scene.Clips)
            {
                if (presenter is null || string.IsNullOrWhiteSpace(presenter.Video)) { unmigrated.Add($"scene:{scene.Id}:presenter"); continue; }
                AddMediaClip(project, tracks[MpsTrackKind.Presenter], presenter.Video, cursorMilliseconds + ToMilliseconds(presenter.Offset), presenter.Duration is null ? duration : ToMilliseconds(presenter.Duration.Value), assetsByPath, unmigrated, cancellationToken,
                    presenter.LipSynced);
            }
            for (var captionIndex = 0; captionIndex < scene.Captions.Count; captionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var caption = scene.Captions[captionIndex];
                if (caption is null || caption.End <= caption.Start || string.IsNullOrWhiteSpace(caption.Text)) { unmigrated.Add($"scene:{scene.Id}:caption"); continue; }
                tracks[MpsTrackKind.Subtitle].Clips.Add(new MpsClip
                {
                    Id = StableId("caption", scene.Id, captionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), caption.Start.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                    TrackId = tracks[MpsTrackKind.Subtitle].Id,
                    TimelineStart = MpsTime.From(new RationalTime(ToMilliseconds(caption.Start), 1000)),
                    SourceIn = new MpsTime(),
                    SourceOut = MpsTime.From(new RationalTime(ToMilliseconds(caption.End - caption.Start), 1000)),
                    Properties = new MpsClipProperties { Subtitle = new MpsSubtitleProperties { Text = MpsTextLayout.Normalize(caption.Text) } }
                });
            }
            if (!string.IsNullOrWhiteSpace(scene.Evidence)) project.Evidence.Add(new MpsEvidenceRecord { Kind = "claim", SubjectId = scene.Id, Source = MpsTextLayout.Normalize(scene.Evidence) });
            if (!string.IsNullOrWhiteSpace(scene.Narration)) project.Evidence.Add(new MpsEvidenceRecord { Kind = "narration", SubjectId = scene.Id, Source = MpsTextLayout.Normalize(scene.Narration) });
            cursorMilliseconds += duration;
        }
        MpsTimelineValidation.Validate(project, cancellationToken);
        MpsEvidenceValidation.Validate(project, cancellationToken);
        return new MpsMigrationResult(project, unmigrated);
    }

    private static void AddMediaClip(MpsProjectDocument project, MpsTrack track, string? path, long startMs, long durationMs,
        Dictionary<string, MpsAssetReference> assets, List<string> unmigrated, CancellationToken cancellationToken, bool? lipSynced = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = MpsTextLayout.Normalize(path).Replace('\\', '/');
        if (normalized.Contains("..", StringComparison.Ordinal) || normalized.StartsWith('/') || normalized.Contains(':')) { unmigrated.Add($"path:{path}"); return; }
        if (!assets.TryGetValue(normalized, out var asset))
        {
            asset = new MpsAssetReference { Path = normalized };
            assets.Add(normalized, asset);
            project.Assets.Add(asset);
        }
        track.Clips.Add(new MpsClip
        {
            Id = StableId(track.Id, asset.Id, startMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            TrackId = track.Id,
            AssetId = asset.Id,
            TimelineStart = MpsTime.From(new RationalTime(startMs, 1000)),
            SourceIn = new MpsTime(),
            SourceOut = MpsTime.From(new RationalTime(Math.Max(durationMs, 1), 1000)),
            Properties = new MpsClipProperties
            {
                LipSynced = lipSynced,
                PresenterOverlay = track.Kind == MpsTrackKind.Presenter ? new MpsPresenterOverlay() : null
            }
        });
    }

    private static long EstimateSceneDuration(VideoScene scene)
    {
        var captionEnd = scene.Captions?.Where(caption => caption is not null).Select(caption => caption.End).DefaultIfEmpty(0).Max() ?? 0;
        var clipEnd = scene.Clips?.Where(clip => clip is not null).Select(clip => clip.Offset + (clip.Duration ?? 0)).DefaultIfEmpty(0).Max() ?? 0;
        return Math.Max(1, ToMilliseconds(Math.Max(captionEnd, clipEnd)));
    }

    private static long ToMilliseconds(double seconds) => checked((long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero));
    private static string StableId(params string[] parts)
    {
        var safeParts = parts.Select(part =>
        {
            var builder = new System.Text.StringBuilder(Math.Min(part.Length, 24));
            foreach (var character in part)
            {
                if (builder.Length >= 24) break;
                builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_');
            }
            return builder.Length == 0 ? "item" : builder.ToString();
        });
        return string.Join('-', safeParts);
    }
}
