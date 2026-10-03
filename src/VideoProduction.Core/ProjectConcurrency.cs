using System.Diagnostics;
using System.Text.Json;

namespace VideoProduction;

/// <summary>项目写入锁的可审计拥有者信息；不包含凭据或素材内容。</summary>
public sealed record MpsProjectLockInfo(string ProjectId, int ProcessId, string Machine, DateTimeOffset AcquiredUtc);

/// <summary>项目目录的进程级独占写入租约。租约文件保留，独占句柄决定当前持有者。</summary>
public sealed class MpsProjectLease : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private MpsProjectLease(string path, FileStream stream, MpsProjectLockInfo owner)
    {
        Path = path;
        _stream = stream;
        Owner = owner;
    }

    public string Path { get; }
    public MpsProjectLockInfo Owner { get; }

    /// <summary>释放句柄但保留锁文件，避免删除竞争和意外删除用户文件。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }

    internal static MpsProjectLease Create(string path, FileStream stream, MpsProjectLockInfo owner)
        => new(path, stream, owner);
}

/// <summary>为同一项目的多窗口写入提供有界、可取消的独占租约。</summary>
public static class MpsProjectConcurrency
{
    private const int MaxAttempts = 240;
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>尝试取得写入锁；超时后返回可操作错误，不覆盖已有窗口的项目。</summary>
    public static MpsProjectLease Acquire(string projectRoot, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout), "项目锁等待必须在 1 毫秒至 2 分钟内。");
        var root = System.IO.Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(root);
        var lockPath = System.IO.Path.Combine(root, ".mps.write.lock");
        var owner = new MpsProjectLockInfo(
            TryReadProjectId(root) ?? "unknown",
            Environment.ProcessId,
            Environment.MachineName,
            DateTimeOffset.UtcNow);
        var attempts = 0;
        var stopwatch = Stopwatch.StartNew();
        while (attempts < MaxAttempts && stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            try
            {
                // 允许诊断读取拥有者元数据，但拒绝任何第二个写入者取得独占句柄。
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                    bufferSize: 256, options: FileOptions.WriteThrough);
                stream.SetLength(0);
                using (var writer = new StreamWriter(stream, leaveOpen: true))
                {
                    writer.Write(JsonSerializer.Serialize(owner));
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
                return MpsProjectLease.Create(lockPath, stream, owner);
            }
            catch (IOException) when (stopwatch.Elapsed < timeout && attempts < MaxAttempts)
            {
                Thread.Sleep(PollDelay);
            }
        }
        throw new TimeoutException($"项目已被其他窗口占用，等待 {stopwatch.Elapsed.TotalMilliseconds:F0} ms 后未取得写入锁。锁文件：{lockPath}");
    }

    /// <summary>读取锁文件中的诊断信息；读取失败时返回空，不影响独占校验。</summary>
    public static MpsProjectLockInfo? ReadOwner(string projectRoot)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetFullPath(projectRoot), ".mps.write.lock");
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 256, options: FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<MpsProjectLockInfo>(json);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string? TryReadProjectId(string root)
    {
        try
        {
            var path = System.IO.Path.Combine(root, ProjectDirectory.ProjectFileName);
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
}
