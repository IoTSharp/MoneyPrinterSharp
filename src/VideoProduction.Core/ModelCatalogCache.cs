namespace VideoProduction;

/// <summary>公开模型目录缓存的有界配置；刷新不会发起目录以外的请求。</summary>
public sealed record ModelCatalogCacheOptions(
    TimeSpan TimeToLive,
    int MaxPages = 64,
    int MaxModels = 4096,
    TimeSpan? MaxRefreshDuration = null)
{
    /// <summary>默认配置适合桌面只读发现；所有上限均可在调用方进一步收紧。</summary>
    public static ModelCatalogCacheOptions Default { get; } = new(TimeSpan.FromMinutes(15));

    /// <summary>校验缓存边界，避免失控分页、模型数量或等待时间。</summary>
    public void Validate()
    {
        if (TimeToLive <= TimeSpan.Zero || TimeToLive > TimeSpan.FromDays(30) ||
            MaxPages is < 1 or > 4096 || MaxModels is < 1 or > 100_000 ||
            MaxRefreshDuration is not null && (MaxRefreshDuration <= TimeSpan.Zero || MaxRefreshDuration > TimeSpan.FromMinutes(10)))
            throw new InvalidDataException("模型目录缓存边界无效。");
    }
}

/// <summary>一次公开模型目录发现的不可变快照；过期快照仍可用于诊断但不能作为新鲜事实。</summary>
public sealed class ModelCatalogSnapshot
{
    private readonly IReadOnlyList<MpsModelDescriptor> models;

    /// <summary>创建目录快照并复制模型描述符引用集合。</summary>
    public ModelCatalogSnapshot(
        string providerId,
        IEnumerable<MpsModelDescriptor> models,
        DateTimeOffset observedUtc,
        DateTimeOffset expiresUtc,
        string? source,
        string? providerVersion)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 128 || providerId.Any(char.IsControl))
            throw new InvalidDataException("提供商标识无效。");
        if (observedUtc == default || expiresUtc <= observedUtc)
            throw new InvalidDataException("模型目录快照时间无效。");
        if (source is not null && (source.Length > 2048 || source.Any(char.IsControl)))
            throw new InvalidDataException("模型目录来源无效。");
        if (providerVersion is not null && (providerVersion.Length > 256 || providerVersion.Any(char.IsControl)))
            throw new InvalidDataException("提供商版本无效。");

        ArgumentNullException.ThrowIfNull(models);
        var sourceModels = models.ToArray();
        if (sourceModels.Length > 100_000) throw new InvalidDataException("模型目录数量超过上限。");
        foreach (var model in sourceModels)
        {
            ArgumentNullException.ThrowIfNull(model);
            model.Validate();
        }
        this.models = Array.AsReadOnly(sourceModels.Select(CloneDescriptor).ToArray());

        ProviderId = providerId;
        ObservedUtc = observedUtc;
        ExpiresUtc = expiresUtc;
        Source = source;
        ProviderVersion = providerVersion;
    }

    /// <summary>提供商稳定标识。</summary>
    public string ProviderId { get; }

    /// <summary>公开目录中观察到的模型，返回只读视图。</summary>
    public IReadOnlyList<MpsModelDescriptor> Models => models;

    /// <summary>目录页提供的最晚观察时间。</summary>
    public DateTimeOffset ObservedUtc { get; }

    /// <summary>按缓存 TTL 计算的过期时间。</summary>
    public DateTimeOffset ExpiresUtc { get; }

    /// <summary>目录来源或脱敏来源标记。</summary>
    public string? Source { get; }

    /// <summary>供应商目录版本。</summary>
    public string? ProviderVersion { get; }

    /// <summary>以指定时钟判断缓存是否仍新鲜。</summary>
    public bool IsFresh(DateTimeOffset utcNow) => utcNow < ExpiresUtc;

    /// <summary>复制目录描述符及其可变集合，避免适配器后续复用对象改写缓存事实。</summary>
    private static MpsModelDescriptor CloneDescriptor(MpsModelDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new MpsModelDescriptor
        {
            SchemaVersion = value.SchemaVersion,
            ModelId = value.ModelId,
            DisplayName = value.DisplayName,
            ModelVersion = value.ModelVersion,
            Modalities = value.Modalities.ToList(),
            Capabilities = value.Capabilities.ToList(),
            Limits = new MpsModelLimits
            {
                ContextTokens = value.Limits.ContextTokens,
                MaxDurationSeconds = value.Limits.MaxDurationSeconds,
                AspectRatios = value.Limits.AspectRatios?.ToList(),
                Resolutions = value.Limits.Resolutions?.ToList(),
                Formats = value.Limits.Formats?.ToList()
            },
            ExecutionMode = value.ExecutionMode,
            EvidenceStatus = value.EvidenceStatus,
            Price = value.Price is null ? null : new MpsModelPrice
            {
                Amount = value.Price.Amount,
                Currency = value.Price.Currency,
                Unit = value.Price.Unit,
                Basis = value.Price.Basis
            },
            Source = value.Source,
            ObservedUtc = value.ObservedUtc
        };
    }
}

/// <summary>目录刷新结果；失败时返回并保留上一份快照，不暴露供应商原始正文。</summary>
public sealed record ModelCatalogRefreshResult(
    bool Succeeded,
    ModelCatalogSnapshot? Snapshot,
    bool CacheRetained,
    ProviderAdapterErrorCategory ErrorCategory,
    string? FailureReason = null);

/// <summary>公开模型目录发现缓存；分页、数量、耗时和取消均有明确上限。</summary>
public sealed class ModelCatalogCache : IDisposable
{
    private readonly ModelCatalogCacheOptions options;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private ModelCatalogSnapshot? snapshot;
    private bool disposed;

    /// <summary>创建内存目录缓存；不保存凭据、原始响应或网络地址。</summary>
    public ModelCatalogCache(ModelCatalogCacheOptions? options = null, TimeProvider? clock = null)
    {
        this.options = options ?? ModelCatalogCacheOptions.Default;
        this.options.Validate();
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>最近一次成功刷新的快照；刷新失败时保持不变。</summary>
    public ModelCatalogSnapshot? Current => Volatile.Read(ref snapshot);

    /// <summary>读取仍在 TTL 内的目录快照，不触发网络或其他外部操作。</summary>
    public bool TryGetFresh(out ModelCatalogSnapshot value)
        => TryGetFresh(clock.GetUtcNow(), out value);

    /// <summary>用调用方时钟读取仍在 TTL 内的目录快照。</summary>
    public bool TryGetFresh(DateTimeOffset utcNow, out ModelCatalogSnapshot value)
    {
        var current = Volatile.Read(ref snapshot);
        if (current is not null && current.IsFresh(utcNow))
        {
            value = current;
            return true;
        }

        value = null!;
        return false;
    }

    /// <summary>
    /// 通过公开目录适配器刷新全部有界分页。目录失败只返回失败结果并保留旧快照；
    /// 调用方取消会抛出取消异常，内部超时则返回有界失败结果。
    /// </summary>
    public async Task<ModelCatalogRefreshResult> RefreshAsync(
        IProviderCatalogAdapter adapter,
        ProviderAdapterCatalogQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(adapter);
        var request = query ?? new ProviderAdapterCatalogQuery();
        ProviderAdapterValidation.Validate(request, cancellationToken);

        var old = Volatile.Read(ref snapshot);
        var acquired = false;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(options.MaxRefreshDuration ?? TimeSpan.FromSeconds(30));
        try
        {
            await refreshGate.WaitAsync(bounded.Token).ConfigureAwait(false);
            acquired = true;

            var items = new Dictionary<string, MpsModelDescriptor>(StringComparer.Ordinal);
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            var cursor = request.Cursor;
            DateTimeOffset observedUtc = default;
            string? source = null;
            string? providerVersion = null;
            var pages = 0;
            var completed = false;
            for (; pages < options.MaxPages; pages++)
            {
                bounded.Token.ThrowIfCancellationRequested();

                var pageQuery = new ProviderAdapterCatalogQuery(request.Capability, cursor, request.PageSize);
                var page = await adapter.ListModelsAsync(pageQuery, bounded.Token).ConfigureAwait(false);
                ProviderAdapterValidation.Validate(page, bounded.Token);
                if (page.Items.Count + items.Count > options.MaxModels)
                    throw new InvalidDataException("模型目录条目超过上限。");
                observedUtc = page.ObservedUtc > observedUtc ? page.ObservedUtc : observedUtc;
                source ??= page.Source;
                providerVersion ??= page.ProviderVersion;
                foreach (var item in page.Items)
                {
                    bounded.Token.ThrowIfCancellationRequested();
                    item.Validate(bounded.Token);
                    // 目录只记录供应商显式给出的能力；不根据模型名称推断能力。
                    items[item.ModelId] = item;
                }

                if (page.NextCursor is null)
                {
                    completed = true;
                    break;
                }
                if (!seenCursors.Add(page.NextCursor))
                    throw new InvalidDataException("模型目录分页游标重复。");
                cursor = page.NextCursor;
            }
            if (!completed) throw new InvalidDataException("模型目录分页超过上限。");

            if (observedUtc == default) observedUtc = clock.GetUtcNow();
            var expires = observedUtc + options.TimeToLive;
            var next = new ModelCatalogSnapshot(adapter.ProviderId, items.Values, observedUtc, expires, source, providerVersion);
            Volatile.Write(ref snapshot, next);
            return new ModelCatalogRefreshResult(true, next, old is not null, ProviderAdapterErrorCategory.Unknown);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(old, ProviderAdapterErrorCategory.Timeout, "模型目录刷新达到时间上限。");
        }
        catch (InvalidDataException)
        {
            return Failure(old, ProviderAdapterErrorCategory.ResponseFormat, "模型目录响应不符合有界契约。");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(old, ProviderAdapterErrorCategory.Unknown, "模型目录读取失败。");
        }
        finally
        {
            if (acquired) refreshGate.Release();
        }
    }

    /// <summary>释放刷新闸门；不负责终止调用方创建的外部进程。</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        refreshGate.Dispose();
    }

    private static ModelCatalogRefreshResult Failure(
        ModelCatalogSnapshot? old,
        ProviderAdapterErrorCategory category,
        string reason)
        => new(false, old, old is not null, category, reason);
}
