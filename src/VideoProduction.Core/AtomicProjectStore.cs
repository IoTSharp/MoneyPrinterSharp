using System.Text.Json;

namespace VideoProduction;

/// <summary>项目 JSON 与有界版本快照的原子存储。</summary>
public sealed class MpsAtomicProjectStore
{
    public const int DefaultMaxSnapshots = 32;
    public const long DefaultMaxSnapshotBytes = 256L * 1024 * 1024;

    public MpsAtomicProjectStore(int maxSnapshots = DefaultMaxSnapshots, long maxSnapshotBytes = DefaultMaxSnapshotBytes)
    {
        if (maxSnapshots is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxSnapshots));
        if (maxSnapshotBytes is < 1 or > 4L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxSnapshotBytes));
        MaxSnapshots = maxSnapshots; MaxSnapshotBytes = maxSnapshotBytes;
    }

    public int MaxSnapshots { get; }
    public long MaxSnapshotBytes { get; }

    /// <summary>验证后原子保存项目，并写入带时间戳的版本快照。</summary>
    public void Save(string root, MpsProjectDocument project, string? versionName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var lease = MpsProjectConcurrency.Acquire(root, TimeSpan.FromSeconds(5), cancellationToken);
        ProjectDirectory.Save(root, project, cancellationToken);
        var versions = ProjectDirectory.ResolvePath(root, "versions", cancellationToken);
        Directory.CreateDirectory(versions);
        var safeName = SanitizeVersionName(versionName);
        var file = Path.Combine(versions, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{safeName}-{Guid.NewGuid():N}.mps.json");
        JsonFiles.Write(file, project);
        TrimSnapshots(versions, cancellationToken);
    }

    /// <summary>按修改时间恢复最近的完整快照；不存在快照时返回当前项目。</summary>
    public MpsProjectDocument RecoverLatest(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var versions = ProjectDirectory.ResolvePath(root, "versions", cancellationToken);
        var candidates = Directory.Exists(versions) ? Directory.EnumerateFiles(versions, "*.mps.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(MaxSnapshots).ToArray() : [];
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { var project = JsonFiles.Read<MpsProjectDocument>(candidate); MpsTimelineValidation.Validate(project, cancellationToken); return project; }
            catch (Exception error) when (error is JsonException or InvalidDataException) { }
        }
        return ProjectDirectory.Open(root, cancellationToken);
    }

    private void TrimSnapshots(string versions, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(versions, "*.mps.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(Math.Min(MaxSnapshots + 1024, 2048)).ToArray();
        long total = 0;
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(files[index]);
            var remove = index >= MaxSnapshots || total + info.Length > MaxSnapshotBytes;
            if (remove) { File.Delete(files[index]); continue; }
            total += info.Length;
        }
    }

    private static string SanitizeVersionName(string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "autosave" : value.Trim();
        if (value.Length > 64) value = value[..64];
        if (value.Any(char.IsControl) || value.Any(character => Path.GetInvalidFileNameChars().Contains(character))) throw new ArgumentException("版本名称包含无效字符。", nameof(value));
        return value.Replace(' ', '_');
    }
}
