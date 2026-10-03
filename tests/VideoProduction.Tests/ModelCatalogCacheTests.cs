using VideoProduction;

namespace VideoProductionTests;

/// <summary>公开目录缓存的离线回归；适配器只返回本地固定对象，不访问网络。</summary>
public static class ModelCatalogCacheTests
{
    /// <summary>验证分页、来源/版本/观察时间、TTL、失败保留和分页上限。</summary>
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await RefreshesPagedCatalogAndExpiresByTtl(cancellationToken);
        await FailedRefreshRetainsPreviousSnapshot(cancellationToken);
        await PaginationLimitRetainsSnapshot(cancellationToken);
    }

    private static async Task RefreshesPagedCatalogAndExpiresByTtl(CancellationToken cancellationToken)
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 3, 2, 0, 0, TimeSpan.Zero));
        var observed = clock.GetUtcNow();
        var adapter = new PagingAdapter("offline", observed,
        [
            [Descriptor("vision-model"), Descriptor("unknown-by-name")],
            [Descriptor("voice-model")]
        ]);
        using var cache = new ModelCatalogCache(new ModelCatalogCacheOptions(TimeSpan.FromMinutes(10), MaxPages: 4, MaxModels: 8), clock);

        var result = await cache.RefreshAsync(adapter, cancellationToken: cancellationToken);
        Assert(result.Succeeded && result.Snapshot is not null, "分页目录刷新应成功");
        Assert(result.Snapshot!.Models.Count == 3, "分页目录条目数量错误");
        Assert(result.Snapshot.Source == "offline-public" && result.Snapshot.ProviderVersion == "v1", "目录来源或版本未保留");
        Assert(result.Snapshot.ObservedUtc == observed, "目录观察时间未保留");
        Assert(result.Snapshot.Models.Single(model => model.ModelId == "unknown-by-name").Capabilities.Single() == MpsCapabilityKind.Unknown,
            "不能按模型名称推断能力");
        Assert(cache.TryGetFresh(out var fresh) && ReferenceEquals(fresh, result.Snapshot), "TTL 内应读取新鲜缓存");

        clock.Set(observed.AddMinutes(11));
        Assert(!cache.TryGetFresh(out _), "TTL 过期后不应报告新鲜缓存");
    }

    private static async Task FailedRefreshRetainsPreviousSnapshot(CancellationToken cancellationToken)
    {
        var observed = new DateTimeOffset(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);
        var adapter = new PagingAdapter("offline", observed, [[Descriptor("first")]]);
        using var cache = new ModelCatalogCache(new ModelCatalogCacheOptions(TimeSpan.FromMinutes(10)), new ManualClock(observed));
        var initial = await cache.RefreshAsync(adapter, cancellationToken: cancellationToken);
        Assert(initial.Succeeded && initial.Snapshot is not null, "初次目录刷新失败");

        adapter.Fail = true;
        var failed = await cache.RefreshAsync(adapter, cancellationToken: cancellationToken);
        Assert(!failed.Succeeded && failed.CacheRetained && ReferenceEquals(failed.Snapshot, initial.Snapshot), "目录失败应保留旧缓存");
        Assert(cache.TryGetFresh(out var retained) && ReferenceEquals(retained, initial.Snapshot), "目录失败不应清除仍新鲜的旧缓存");
    }

    private static async Task PaginationLimitRetainsSnapshot(CancellationToken cancellationToken)
    {
        var observed = new DateTimeOffset(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);
        var adapter = new PagingAdapter("offline", observed, [[Descriptor("loop")]], repeatCursor: true);
        using var cache = new ModelCatalogCache(new ModelCatalogCacheOptions(TimeSpan.FromMinutes(1), MaxPages: 2, MaxModels: 8), new ManualClock(observed));
        var failed = await cache.RefreshAsync(adapter, cancellationToken: cancellationToken);
        Assert(!failed.Succeeded && failed.ErrorCategory == ProviderAdapterErrorCategory.ResponseFormat, "分页达到上限应返回响应格式失败");
        Assert(adapter.Calls == 2, "分页上限应精确阻止第三页请求");
    }

    private static MpsModelDescriptor Descriptor(string id) => new()
    {
        ModelId = id,
        DisplayName = id,
        Modalities = [MpsModelModality.Unknown],
        Capabilities = [MpsCapabilityKind.Unknown],
        Limits = new(),
        ExecutionMode = MpsModelExecutionMode.Unknown,
        EvidenceStatus = MpsModelEvidenceStatus.PubliclyListed
    };

    private sealed class PagingAdapter : IProviderCatalogAdapter
    {
        private readonly IReadOnlyList<IReadOnlyList<MpsModelDescriptor>> pages;
        private readonly DateTimeOffset observed;
        private readonly bool repeatCursor;

        public PagingAdapter(string providerId, DateTimeOffset observed, IReadOnlyList<IReadOnlyList<MpsModelDescriptor>> pages, bool repeatCursor = false)
        {
            ProviderId = providerId;
            this.observed = observed;
            this.pages = pages;
            this.repeatCursor = repeatCursor;
        }

        public string ProviderId { get; }
        public int Calls { get; private set; }
        public bool Fail { get; set; }

        public Task<ProviderAdapterPage<MpsModelDescriptor>> ListModelsAsync(ProviderAdapterCatalogQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Fail) throw new InvalidOperationException("offline fixture failure");
            var index = query.Cursor is null ? 0 : int.Parse(query.Cursor, System.Globalization.CultureInfo.InvariantCulture);
            var page = index < pages.Count ? pages[index] : Array.Empty<MpsModelDescriptor>();
            string? next = index + 1 < pages.Count || repeatCursor ? (repeatCursor ? "0" : (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
            return Task.FromResult(new ProviderAdapterPage<MpsModelDescriptor>(page, next, observed, "offline-public", "v1"));
        }
    }

    private sealed class ManualClock(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset now = value;
        public override DateTimeOffset GetUtcNow() => now;
        public void Set(DateTimeOffset value) => now = value;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
