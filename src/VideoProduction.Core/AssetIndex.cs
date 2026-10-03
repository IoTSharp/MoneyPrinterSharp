using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>素材的媒体属性；无法探测的字段保留默认值并由索引诊断说明。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssetMediaMetadata(
    string Container,
    string VideoCodec,
    string AudioCodec,
    int Width,
    int Height,
    double DurationSeconds,
    double FrameRate,
    bool HasAudio,
    int AudioChannels,
    int AudioSampleRate);

/// <summary>单个素材的媒体探测器。探测失败不会删除索引项，而由调用方记录诊断。</summary>
public interface IAssetMediaProbe
{
    Task<AssetMediaMetadata?> ProbeAsync(string fullPath, CancellationToken cancellationToken);
}

/// <summary>索引中的诊断，供界面展示失联、权限及媒体探测问题。</summary>
public sealed record AssetIndexDiagnostic(string Path, string Code, string Message);

/// <summary>一个文件的内容身份和媒体信息。Path 始终是相对于索引根目录的正斜杠路径。</summary>
public sealed record AssetIndexEntry
{
    public required string AssetKey { get; init; }
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required DateTimeOffset IndexedAtUtc { get; init; }
    public required string Source { get; init; }
    public required bool IsReachable { get; init; }
    public AssetMediaMetadata? Media { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>一次索引的完整结果；历史中已失联的项保留在 Entries 中并标为不可达。</summary>
public sealed record AssetIndexSnapshot
{
    public required string Root { get; init; }
    public required DateTimeOffset IndexedAtUtc { get; init; }
    public required string Source { get; init; }
    public required IReadOnlyList<AssetIndexEntry> Entries { get; init; }
    public required IReadOnlyList<AssetIndexDiagnostic> Diagnostics { get; init; }
}

/// <summary>素材索引边界。文件数、目录数和墙钟上限共同限制扫描负载。</summary>
public sealed record AssetIndexOptions
{
    public string Source { get; init; } = "local";
    public int MaxFiles { get; init; } = 10_000;
    public int MaxDirectories { get; init; } = 10_000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>在本地项目根内建立可恢复的素材索引，并保留历史失联项。</summary>
public sealed class AssetIndexer
{
    private const int MaxEntriesPerDirectory = 4096;
    private const int MaxHistoryEntries = 100_000;
    private readonly IAssetMediaProbe? mediaProbe;

    /// <summary>使用可选媒体探测器；传入 null 时仍索引文件身份，只跳过媒体字段。</summary>
    public AssetIndexer(IAssetMediaProbe? mediaProbe = null)
    {
        this.mediaProbe = mediaProbe;
    }

    /// <summary>把索引元数据回写到项目素材引用；找不到的素材只标记失联，不删除引用。</summary>
    public static void ApplyToProject(MpsProjectDocument project, AssetIndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(snapshot);
        var byPath = snapshot.Entries.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.IsReachable).ThenByDescending(entry => entry.IndexedAtUtc).First(), StringComparer.OrdinalIgnoreCase);
        foreach (var asset in project.Assets)
        {
            if (asset is null) continue;
            if (!byPath.TryGetValue(asset.Path, out var entry))
            {
                asset.IsReachable = false;
                continue;
            }
            asset.Sha256 = entry.Sha256.Length == 0 ? asset.Sha256 : entry.Sha256;
            asset.SizeBytes = entry.SizeBytes;
            asset.Media = entry.Media;
            asset.Source = entry.Source;
            asset.IndexedUtc = entry.IndexedAtUtc;
            asset.IsReachable = entry.IsReachable;
        }
    }

    /// <summary>首次建立索引的便捷重载，参数顺序便于 CLI 或桌面调用。</summary>
    public Task<AssetIndexSnapshot> BuildAsync(string root, AssetIndexOptions options, CancellationToken cancellationToken = default) =>
        BuildAsync(root, previous: null, options: options, cancellationToken: cancellationToken);

    /// <summary>首次建立索引的语义别名；刷新时请传入 previous 保留失联项。</summary>
    public Task<AssetIndexSnapshot> IndexAsync(string root, AssetIndexOptions? options = null, CancellationToken cancellationToken = default) =>
        BuildAsync(root, previous: null, options: options, cancellationToken: cancellationToken);

    /// <summary>
    /// 扫描根目录并生成索引。相同文件名通过路径和内容哈希组成独立 AssetKey；
    /// previous 中本次未见的项只标记为失联，不会从结果删除。
    /// </summary>
    public async Task<AssetIndexSnapshot> BuildAsync(
        string root,
        AssetIndexSnapshot? previous = null,
        AssetIndexOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AssetIndexOptions();
        ValidateOptions(options);
        root = ValidateRoot(root);
        if (previous is not null && previous.Entries.Count > MaxHistoryEntries)
            throw new InvalidDataException($"历史素材索引超过 {MaxHistoryEntries} 项，拒绝无界合并。");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var clock = Stopwatch.StartNew();
        var entries = (previous?.Entries ?? Array.Empty<AssetIndexEntry>()).Take(MaxHistoryEntries)
            .ToDictionary(entry => entry.AssetKey, StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<AssetIndexDiagnostic>();
        var pending = new Stack<string>();
        pending.Push(root);
        var directories = 0;
        var files = 0;
        var entriesSeen = 0;

        try
        {
            while (pending.Count > 0)
            {
                CheckBudget(clock, options.Timeout, budget.Token);
                if (++directories > options.MaxDirectories)
                {
                    diagnostics.Add(new AssetIndexDiagnostic(root, "directory_limit", $"目录数超过上限 {options.MaxDirectories}。"));
                    break;
                }

                var directory = pending.Pop();
                IEnumerable<string> children;
                try
                {
                    var limitedChildren = Directory.EnumerateFileSystemEntries(directory).Take(MaxEntriesPerDirectory + 1).ToArray();
                    if (limitedChildren.Length > MaxEntriesPerDirectory)
                        diagnostics.Add(new AssetIndexDiagnostic(ToRelativePath(root, directory), "directory_entry_limit", $"单目录项超过 {MaxEntriesPerDirectory}，其余项未扫描。"));
                    children = limitedChildren.Take(MaxEntriesPerDirectory).ToArray();
                }
                catch (Exception error) when (IsFileSystemDiagnostic(error))
                {
                    diagnostics.Add(new AssetIndexDiagnostic(ToRelativePath(root, directory), "directory_unreadable", error.Message));
                    continue;
                }

                foreach (var child in children)
                {
                    CheckBudget(clock, options.Timeout, budget.Token);
                    if (++entriesSeen > options.MaxDirectories * 2L + options.MaxFiles)
                    {
                        diagnostics.Add(new AssetIndexDiagnostic(ToRelativePath(root, directory), "entry_limit", "目录项数量超过边界。"));
                        pending.Clear();
                        break;
                    }

                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(child); }
                    catch (Exception error) when (IsFileSystemDiagnostic(error))
                    {
                        diagnostics.Add(new AssetIndexDiagnostic(ToRelativePath(root, child), "attributes_unreadable", error.Message));
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (directories + pending.Count < options.MaxDirectories) pending.Push(child);
                        else diagnostics.Add(new AssetIndexDiagnostic(ToRelativePath(root, child), "directory_limit", "目录数达到上限。"));
                        continue;
                    }

                    if (++files > options.MaxFiles)
                    {
                        diagnostics.Add(new AssetIndexDiagnostic(ToRelativePath(root, child), "file_limit", $"文件数超过上限 {options.MaxFiles}。"));
                        pending.Clear();
                        break;
                    }

                    var entry = await IndexFileAsync(root, child, options.Source, clock, options.Timeout, budget.Token, diagnostics);
                    seenPaths.Add(entry.Path);
                    seenKeys.Add(entry.AssetKey);
                    entries[entry.AssetKey] = entry;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new TimeoutException($"素材索引超过 {options.Timeout.TotalSeconds:0} 秒。", new OperationCanceledException(budget.Token));
        }

        // 未再次出现的历史项仍保留，避免刷新索引造成破坏性删除。
        if (previous is not null)
        {
            foreach (var old in previous.Entries.Take(MaxHistoryEntries))
            {
                CheckBudget(clock, options.Timeout, budget.Token);
                if (seenKeys.Contains(old.AssetKey)) continue;
                var pathStillPresent = seenPaths.Contains(old.Path);
                var code = pathStillPresent ? "content_changed" : "missing";
                var message = pathStillPresent ? "同一路径内容已变化，保留旧内容身份供诊断。" : "本次扫描未找到素材，保留索引项供恢复。";
                var mergedDiagnostics = old.Diagnostics.Concat([code]).Distinct(StringComparer.Ordinal).ToArray();
                entries[old.AssetKey] = old with { IsReachable = false, Diagnostics = mergedDiagnostics };
                diagnostics.Add(new AssetIndexDiagnostic(old.Path, code, message));
            }
        }

        return new AssetIndexSnapshot
        {
            Root = root,
            IndexedAtUtc = DateTimeOffset.UtcNow,
            Source = options.Source,
            Entries = entries.Values.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.AssetKey, StringComparer.Ordinal).ToArray(),
            Diagnostics = diagnostics.ToArray()
        };
    }

    /// <summary>读取并哈希单个文件；文件在扫描期间消失时产生不可达诊断项。</summary>
    private async Task<AssetIndexEntry> IndexFileAsync(
        string root,
        string fullPath,
        string source,
        Stopwatch clock,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ICollection<AssetIndexDiagnostic> diagnostics)
    {
        var relative = ToRelativePath(root, fullPath);
        var now = DateTimeOffset.UtcNow;
        long size = 0;
        string hash = "";
        var messages = new List<string>();
        try
        {
            var info = new FileInfo(fullPath);
            size = info.Exists ? info.Length : 0;
            hash = await HashFileAsync(fullPath, clock, timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsFileSystemDiagnostic(error))
        {
            messages.Add("unreachable");
            diagnostics.Add(new AssetIndexDiagnostic(relative, "file_unreachable", error.Message));
        }

        AssetMediaMetadata? media = null;
        if (hash.Length > 0 && mediaProbe is not null)
        {
            try
            {
                media = await mediaProbe.ProbeAsync(fullPath, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                messages.Add("media_probe_failed");
                diagnostics.Add(new AssetIndexDiagnostic(relative, "media_probe_failed", error.Message));
            }
        }
        else if (hash.Length > 0 && mediaProbe is null)
        {
            messages.Add("media_probe_unconfigured");
        }

        var key = BuildAssetKey(relative, hash);
        return new AssetIndexEntry
        {
            AssetKey = key,
            Path = relative,
            FileName = Path.GetFileName(fullPath),
            SizeBytes = size,
            Sha256 = hash,
            IndexedAtUtc = now,
            Source = source,
            IsReachable = hash.Length > 0,
            Media = media,
            Diagnostics = messages.ToArray()
        };
    }

    /// <summary>分块哈希并在每块检查取消和墙钟，避免长时间读取失去边界。</summary>
    private static async Task<string> HashFileAsync(string path, Stopwatch clock, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var chunks = 0L;
        var maxChunks = Math.Max(1L, stream.Length / buffer.Length + (stream.Length % buffer.Length == 0 ? 0 : 1));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= timeout) throw new TimeoutException("单个素材哈希超过索引墙钟上限。");
            if (++chunks > maxChunks + 1) throw new IOException("素材读取块数超过文件长度推算上限。");
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            hasher.AppendData(buffer, 0, count);
        }
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    /// <summary>使用路径与内容哈希组成稳定身份，使同名不同内容同时存在。</summary>
    private static string BuildAssetKey(string path, string sha256)
    {
        var bytes = Encoding.UTF8.GetBytes(path + "\n" + sha256);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>拒绝重解析根目录、空来源以及过大的索引边界。</summary>
    private static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("索引根目录不能为空。", nameof(root));
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("索引根目录不存在：" + root);
        var attributes = File.GetAttributes(root);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("索引根目录不能是重解析点。");
        return root;
    }

    /// <summary>校验全部扫描边界，避免无界目录遍历。</summary>
    private static void ValidateOptions(AssetIndexOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Source) || options.Source.Length > 256) throw new ArgumentException("素材来源长度无效。", nameof(options));
        if (options.MaxFiles < 1 || options.MaxFiles > 100_000) throw new ArgumentOutOfRangeException(nameof(options.MaxFiles), "文件上限必须在1到100000之间。");
        if (options.MaxDirectories < 1 || options.MaxDirectories > 100_000) throw new ArgumentOutOfRangeException(nameof(options.MaxDirectories), "目录上限必须在1到100000之间。");
        if (options.Timeout < TimeSpan.FromSeconds(1) || options.Timeout > TimeSpan.FromHours(2)) throw new ArgumentOutOfRangeException(nameof(options.Timeout), "索引时限必须在1秒到2小时之间。");
    }

    /// <summary>检查取消与总墙钟上限。</summary>
    private static void CheckBudget(Stopwatch clock, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clock.Elapsed >= timeout) throw new TimeoutException($"素材索引超过 {timeout.TotalSeconds:0} 秒。");
    }

    /// <summary>统一相对路径格式，防止索引文件随平台分隔符变化。</summary>
    private static string ToRelativePath(string root, string path) =>
        Path.GetRelativePath(root, Path.GetFullPath(path)).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    /// <summary>只将常见文件系统错误作为单项诊断，其余异常继续暴露。</summary>
    private static bool IsFileSystemDiagnostic(Exception error) => error is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or NotSupportedException;
}

/// <summary>通过显式 ffprobe 路径获取容器、编解码、画面、音频、时长和帧率。</summary>
public sealed class FfprobeAssetMediaProbe : IAssetMediaProbe
{
    private readonly string executable;
    private readonly TimeSpan timeout;

    /// <summary>要求调用方提供已确认的 ffprobe 路径，避免递归扫描磁盘发现依赖。</summary>
    public FfprobeAssetMediaProbe(string ffprobePath, TimeSpan? timeout = null)
    {
        executable = Path.GetFullPath(ffprobePath);
        if (!File.Exists(executable)) throw new FileNotFoundException("ffprobe 不存在。", executable);
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (this.timeout < TimeSpan.FromSeconds(1) || this.timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout), "ffprobe 时限必须在1秒到2分钟之间。");
    }

    /// <summary>运行一次受限 ffprobe；进程生命周期由 ProcessRunner 记录并在超时后回收。</summary>
    public async Task<AssetMediaMetadata?> ProbeAsync(string fullPath, CancellationToken cancellationToken)
    {
        fullPath = MediaTools.ExistingFile(fullPath);
        var result = await ProcessRunner.RunAsync(executable,
            new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", fullPath }, timeout, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidDataException("ffprobe 无法读取素材：" + result.Stderr.Trim());
        if (result.Stdout.Length > 2 * 1024 * 1024) throw new InvalidDataException("ffprobe 响应超过2MiB。");
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var formatNode) ? formatNode : default;
        var streams = root.TryGetProperty("streams", out var streamNode) ? streamNode.EnumerateArray().Take(32).ToArray() : Array.Empty<JsonElement>();
        var video = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "video");
        var audio = streams.FirstOrDefault(stream => Text(stream, "codec_type") == "audio");
        var hasVideo = video.ValueKind != JsonValueKind.Undefined;
        var hasAudio = audio.ValueKind != JsonValueKind.Undefined;
        var duration = Number(format, "duration");
        if (duration <= 0 && hasVideo) duration = Number(video, "duration");
        if (duration <= 0 && hasAudio) duration = Number(audio, "duration");
        return new AssetMediaMetadata(
            Text(format, "format_name"),
            hasVideo ? Text(video, "codec_name") : "",
            hasAudio ? Text(audio, "codec_name") : "",
            hasVideo ? Integer(video, "width") : 0,
            hasVideo ? Integer(video, "height") : 0,
            duration,
            hasVideo ? FrameRate(video) : 0,
            hasAudio,
            hasAudio ? Integer(audio, "channels") : 0,
            hasAudio ? Integer(audio, "sample_rate") : 0);
    }

    /// <summary>读取 JSON 文本字段，缺失时返回空字符串。</summary>
    private static string Text(JsonElement item, string key) => item.ValueKind != JsonValueKind.Undefined && item.TryGetProperty(key, out var value) ? value.ToString() : "";

    /// <summary>读取有限整数，拒绝无效或溢出值。</summary>
    private static int Integer(JsonElement item, string key) => int.TryParse(Text(item, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>读取有限浮点数，排除 NaN 和无穷值。</summary>
    private static double Number(JsonElement item, string key) => double.TryParse(Text(item, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;

    /// <summary>解析 ffprobe 的分数字段，优先 avg_frame_rate，再取 r_frame_rate。</summary>
    private static double FrameRate(JsonElement stream)
    {
        var value = Text(stream, "avg_frame_rate");
        if (value is "" or "0/0") value = Text(stream, "r_frame_rate");
        var parts = value.Split('/', 2);
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0 && double.IsFinite(numerator / denominator)) return numerator / denominator;
        return Number(stream, "frame_rate");
    }
}
