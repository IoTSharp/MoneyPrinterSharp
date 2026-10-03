using VideoProduction;

namespace VideoProductionTests;

/// <summary>素材索引的离线回归测试，不调用网络或真实付费媒体服务。</summary>
public static class AssetIndexTests
{
    /// <summary>验证同名内容区分、媒体字段写入以及失联项保留。</summary>
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "mps-asset-index-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "one"));
        Directory.CreateDirectory(Path.Combine(root, "two"));
        try
        {
            var first = Path.Combine(root, "one", "clip.bin");
            var second = Path.Combine(root, "two", "clip.bin");
            await File.WriteAllTextAsync(first, "first-content", cancellationToken);
            await File.WriteAllTextAsync(second, "second-content", cancellationToken);

            var indexer = new AssetIndexer(new FixtureProbe());
            var options = new AssetIndexOptions { Source = "test-fixture", MaxFiles = 10, MaxDirectories = 10, Timeout = TimeSpan.FromSeconds(20) };
            var snapshot = await indexer.BuildAsync(root, options: options, cancellationToken: cancellationToken);
            Assert(snapshot.Entries.Count == 2, "两个同名素材均应进入索引");
            Assert(snapshot.Entries.Select(entry => entry.AssetKey).Distinct(StringComparer.Ordinal).Count() == 2, "同名不同内容必须有不同身份");
            Assert(snapshot.Entries.All(entry => entry.IsReachable && entry.Media is { Width: 1920, FrameRate: 30 }), "媒体探测字段未保留");

            File.Delete(first);
            var refreshed = await indexer.BuildAsync(root, snapshot, options, cancellationToken);
            var missing = refreshed.Entries.Single(entry => entry.Path == "one/clip.bin");
            Assert(!missing.IsReachable && missing.Diagnostics.Contains("missing", StringComparer.Ordinal), "失联素材应仅标记诊断并保留");
            Assert(refreshed.Entries.Count == 2, "刷新索引不应删除失联项");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>离线探测器返回固定参数，隔离 ffprobe 可执行文件依赖。</summary>
    private sealed class FixtureProbe : IAssetMediaProbe
    {
        public Task<AssetMediaMetadata?> ProbeAsync(string fullPath, CancellationToken cancellationToken) =>
            Task.FromResult<AssetMediaMetadata?>(new AssetMediaMetadata("fixture", "h264", "aac", 1920, 1080, 2, 30, true, 2, 48_000));
    }

    /// <summary>统一断言消息，避免引入额外测试框架。</summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
