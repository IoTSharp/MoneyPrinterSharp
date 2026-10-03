using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace VideoProduction;

/// <summary>离线模拟器使用的可注入时钟；实现不得访问网络或系统凭据。</summary>
public interface IOfflineClock
{
    /// <summary>返回当前 UTC 时间。</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>使用系统 UTC 时钟的默认实现。</summary>
public sealed class SystemOfflineClock : IOfflineClock
{
    /// <summary>读取当前 UTC 时间。</summary>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>测试用的手动时钟；不会启动计时器或线程。</summary>
public sealed class ManualOfflineClock : IOfflineClock
{
    private DateTimeOffset current;

    /// <summary>以指定时间创建手动时钟。</summary>
    public ManualOfflineClock(DateTimeOffset? initial = null) => current = initial ?? DateTimeOffset.UnixEpoch;

    /// <summary>当前手动时间。</summary>
    public DateTimeOffset UtcNow => current;

    /// <summary>向前推进有限时间，禁止回拨以保持限流判定确定。</summary>
    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(duration), "时间推进必须在0至365天之间。");
        current = current.Add(duration);
    }
}

/// <summary>模拟响应格式，用于验证调用方对版本差异的容错。</summary>
public enum OfflineResponseFormat
{
    StableV1,
    LegacyV0,
    WrappedV2
}

/// <summary>离线任务的最终结果预设。</summary>
public enum OfflineTaskOutcome
{
    Success,
    Failure
}

/// <summary>任务状态；签名下载地址不会出现在状态对象或内部任务记录中。</summary>
public enum OfflineTaskStatusKind
{
    Queued,
    Running,
    Succeeded,
    Failed
}

/// <summary>模拟器的错误类别；消息只包含本地固定文案。</summary>
public enum OfflineProviderErrorCode
{
    InvalidRequest,
    Unauthorized,
    Forbidden,
    RateLimited,
    RequestLimitExceeded,
    NotFound,
    InvalidState,
    GenerationFailed,
    PollTimeout,
    Cancelled
}

/// <summary>离线模拟器异常；不携带供应商原始正文、凭据或签名地址。</summary>
public sealed class OfflineProviderException : Exception
{
    /// <summary>创建固定文案的模拟器异常。</summary>
    public OfflineProviderException(OfflineProviderErrorCode code, int statusCode, TimeSpan? retryAfter = null)
        : base(GetMessage(code))
    {
        Code = code;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>本地归类的错误。</summary>
    public OfflineProviderErrorCode Code { get; }

    /// <summary>对应的模拟 HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>限流时建议等待的时间。</summary>
    public TimeSpan? RetryAfter { get; }

    private static string GetMessage(OfflineProviderErrorCode code) => code switch
    {
        OfflineProviderErrorCode.InvalidRequest => "离线请求参数无效。",
        OfflineProviderErrorCode.Unauthorized => "离线账号未配置。",
        OfflineProviderErrorCode.Forbidden => "离线账号无权使用此模型。",
        OfflineProviderErrorCode.RateLimited => "离线请求触发限流。",
        OfflineProviderErrorCode.RequestLimitExceeded => "离线请求次数达到上限。",
        OfflineProviderErrorCode.NotFound => "离线任务不存在。",
        OfflineProviderErrorCode.InvalidState => "离线任务尚未产生可用输出。",
        OfflineProviderErrorCode.GenerationFailed => "离线生成任务失败。",
        OfflineProviderErrorCode.PollTimeout => "离线任务轮询达到边界。",
        OfflineProviderErrorCode.Cancelled => "离线操作已取消。",
        _ => "离线供应商操作失败。"
    };
}

/// <summary>模拟器的上限和响应配置；默认值均为小而有界的离线测试值。</summary>
public sealed class OfflineProviderSimulatorOptions
{
    /// <summary>供应商展示名。</summary>
    public string Provider { get; init; } = "offline";

    /// <summary>目录默认页大小。</summary>
    public int CatalogPageSize { get; init; } = 2;

    /// <summary>限流窗口内允许的请求数。</summary>
    public int RateLimitRequests { get; init; } = 8;

    /// <summary>限流窗口长度。</summary>
    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>单个任务在状态查询前需经历的查询次数。</summary>
    public int PollsBeforeCompletion { get; init; } = 2;

    /// <summary>模拟器允许的总请求次数，防止错误循环持续运行。</summary>
    public int MaxRequests { get; init; } = 128;

    /// <summary>单次轮询允许的最大次数。</summary>
    public int MaxPolls { get; init; } = 16;

    /// <summary>单次轮询允许的最长墙钟时间。</summary>
    public TimeSpan MaxPollDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>签名输出地址的有效期。</summary>
    public TimeSpan SignedUrlLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>默认响应格式。</summary>
    public OfflineResponseFormat ResponseFormat { get; init; } = OfflineResponseFormat.StableV1;
}

/// <summary>目录查询；游标是不透明的本地页号，不保存供应商响应。</summary>
public sealed record OfflineCatalogQuery(string? Capability = null, string? Cursor = null, int? PageSize = null,
    OfflineResponseFormat? ResponseFormat = null);

/// <summary>目录中的模型描述；费用未知时保持 null。</summary>
public sealed record OfflineModelDescriptor(
    string Id,
    string DisplayName,
    string Version,
    IReadOnlyList<string> Capabilities,
    string? Modality,
    string? Duration,
    string? AspectRatios,
    string? Resolutions,
    string? Formats,
    bool Async,
    decimal? KnownPrice,
    string? Currency);

/// <summary>分页目录结果；格式标记用于模拟字段包装或旧字段名。</summary>
public sealed record OfflineCatalogPage(
    IReadOnlyList<OfflineModelDescriptor> Models,
    string? NextCursor,
    int Page,
    bool HasMore,
    OfflineResponseFormat ResponseFormat);

/// <summary>账号能力查询结果；目录存在与账号可调用状态分开返回。</summary>
public sealed record OfflineAccountAccess(
    string AccountAlias,
    string ModelId,
    string Capability,
    bool AccountKnown,
    bool Allowed,
    OfflineProviderErrorCode? Error,
    OfflineResponseFormat ResponseFormat);

/// <summary>提交任务请求；输入正文只允许以不透明指纹传入，模拟器不保留原文。</summary>
public sealed record OfflineSubmitRequest(
    string AccountAlias,
    string Capability,
    string ModelId,
    string InputFingerprint,
    OfflineTaskOutcome Outcome = OfflineTaskOutcome.Success,
    decimal? Price = null,
    bool IncludeSignedOutput = true,
    OfflineResponseFormat? ResponseFormat = null);

/// <summary>提交结果；任务号可重算且同一指纹不会重复创建任务。</summary>
public sealed record OfflineSubmission(
    string TaskId,
    OfflineTaskStatusKind Status,
    decimal? EstimatedPrice,
    string? Currency,
    OfflineResponseFormat ResponseFormat);

/// <summary>脱敏任务状态；只含白名单状态和费用。</summary>
public sealed record OfflineTaskStatus(
    string TaskId,
    OfflineTaskStatusKind Status,
    decimal? Price,
    string? Currency,
    OfflineProviderErrorCode? Error,
    int PollCount,
    OfflineResponseFormat ResponseFormat);

/// <summary>异步轮询的最终结果；超时不会伪造成功或免费。</summary>
public sealed record OfflinePollResult(
    OfflineTaskStatus Status,
    int RequestsUsed,
    TimeSpan Elapsed);

/// <summary>一次性输出响应；签名地址仅在内存中返回，不写入任务或日志。</summary>
public sealed record OfflineOutputResponse(
    string TaskId,
    string SignedUrl,
    DateTimeOffset ExpiresAt,
    OfflineResponseFormat ResponseFormat);

/// <summary>用量页的脱敏费用记录。</summary>
public sealed record OfflineUsageEntry(
    string TaskId,
    string Capability,
    string ModelId,
    decimal? Price,
    string? Currency,
    OfflineTaskStatusKind Status);

/// <summary>用量分页结果；原始响应和签名地址永远不会进入集合。</summary>
public sealed record OfflineUsagePage(
    IReadOnlyList<OfflineUsageEntry> Entries,
    string? NextCursor,
    OfflineResponseFormat ResponseFormat);

/// <summary>完全离线的供应商模拟器，提供目录、权限、异步任务和费用边界。</summary>
public sealed class OfflineProviderSimulator
{
    private sealed class TaskState
    {
        public required string TaskId { get; init; }
        public required string AccountAlias { get; init; }
        public required string Capability { get; init; }
        public required string ModelId { get; init; }
        public required OfflineTaskOutcome Outcome { get; init; }
        public required decimal? Price { get; init; }
        public required string? Currency { get; init; }
        public required bool IncludeSignedOutput { get; init; }
        public int PollCount { get; set; }
        public OfflineTaskStatusKind Status { get; set; } = OfflineTaskStatusKind.Queued;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, TaskState> tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> permissions = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> requestTimes = new();
    private readonly List<OfflineModelDescriptor> models;
    private readonly IOfflineClock clock;
    private readonly OfflineProviderSimulatorOptions options;
    private int requestCount;

    /// <summary>创建离线模拟器；仅初始化内存目录和账号权限。</summary>
    public OfflineProviderSimulator(
        OfflineProviderSimulatorOptions? options = null,
        IOfflineClock? clock = null,
        IEnumerable<OfflineModelDescriptor>? models = null)
    {
        this.options = options ?? new OfflineProviderSimulatorOptions();
        this.clock = clock ?? new SystemOfflineClock();
        ValidateOptions(this.options);
        this.models = (models ?? CreateDefaultModels()).Take(256).ToList();
        if (this.models.Count == 0) throw new ArgumentException("至少需要一个离线模型。", nameof(models));
        permissions["demo"] = this.models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>当前模拟供应商名称。</summary>
    public string Provider => options.Provider;

    /// <summary>已接受的请求数；用于断言没有隐藏重试。</summary>
    public int RequestCount
    {
        get { lock (gate) return requestCount; }
    }

    /// <summary>授予账号使用指定模型；空模型集合表示拒绝所有模型。</summary>
    public void SetAccountPermissions(string accountAlias, IEnumerable<string> modelIds)
    {
        ValidateText(accountAlias, nameof(accountAlias), 64);
        var values = modelIds?.Take(256).ToHashSet(StringComparer.Ordinal)
            ?? throw new ArgumentNullException(nameof(modelIds));
        lock (gate) permissions[accountAlias] = values;
    }

    /// <summary>分页读取公开目录；游标只包含本地页号，不含凭据或签名地址。</summary>
    public Task<OfflineCatalogPage> ListCatalogAsync(OfflineCatalogQuery? query = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = query ?? new OfflineCatalogQuery();
        var size = request.PageSize ?? options.CatalogPageSize;
        if (size is < 1 or > 64) throw new OfflineProviderException(OfflineProviderErrorCode.InvalidRequest, 400);
        var page = ParseCursor(request.Cursor);
        EnsureRequestAllowed();
        var source = string.IsNullOrWhiteSpace(request.Capability)
            ? models
            : models.Where(model => model.Capabilities.Contains(request.Capability, StringComparer.OrdinalIgnoreCase)).ToList();
        var values = source.Skip(page * size).Take(size).ToArray();
        var hasMore = source.Count > (page + 1) * size;
        var next = hasMore ? (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new OfflineCatalogPage(values, next, page, hasMore, request.ResponseFormat ?? options.ResponseFormat));
    }

    /// <summary>查询账号是否可调用某模型；401 与403保持为独立本地类别。</summary>
    public Task<OfflineAccountAccess> CheckAccessAsync(string accountAlias, string capability, string modelId,
        OfflineResponseFormat? responseFormat = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateText(accountAlias, nameof(accountAlias), 64);
        ValidateText(capability, nameof(capability), 64);
        ValidateText(modelId, nameof(modelId), 128);
        EnsureRequestAllowed();
        var knownModel = models.Any(model => model.Id == modelId && model.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase));
        lock (gate)
        {
            var knownAccount = permissions.TryGetValue(accountAlias, out var allowedModels);
            var allowed = knownAccount && knownModel && allowedModels!.Contains(modelId);
            var error = !knownAccount ? OfflineProviderErrorCode.Unauthorized : !allowed ? OfflineProviderErrorCode.Forbidden : (OfflineProviderErrorCode?)null;
            return Task.FromResult(new OfflineAccountAccess(accountAlias, modelId, capability, knownAccount, allowed, error,
                responseFormat ?? options.ResponseFormat));
        }
    }

    /// <summary>提交一次幂等离线任务；不会访问网络、读取凭据或发送付费请求。</summary>
    public Task<OfflineSubmission> SubmitAsync(OfflineSubmitRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        EnsureRequestAllowed();
        EnsureAllowed(request.AccountAlias, request.Capability, request.ModelId);
        var taskId = ComputeTaskId(request);
        lock (gate)
        {
            if (!tasks.TryGetValue(taskId, out var state))
            {
                var price = request.Price ?? GetDefaultPrice(request.ModelId, request.Capability);
                state = new TaskState
                {
                    TaskId = taskId, AccountAlias = request.AccountAlias, Capability = request.Capability,
                    ModelId = request.ModelId, Outcome = request.Outcome, Price = price, Currency = price.HasValue ? "CNY" : null,
                    IncludeSignedOutput = request.IncludeSignedOutput
                };
                tasks.Add(taskId, state);
            }
            return Task.FromResult(new OfflineSubmission(taskId, state.Status, state.Price, state.Currency,
                request.ResponseFormat ?? options.ResponseFormat));
        }
    }

    /// <summary>读取一次状态并推进有限的异步状态机。</summary>
    public Task<OfflineTaskStatus> GetStatusAsync(string taskId, OfflineResponseFormat? responseFormat = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTaskId(taskId);
        EnsureRequestAllowed();
        lock (gate)
        {
            if (!tasks.TryGetValue(taskId, out var state)) throw new OfflineProviderException(OfflineProviderErrorCode.NotFound, 404);
            state.PollCount++;
            if (state.Status is OfflineTaskStatusKind.Queued && state.PollCount >= options.PollsBeforeCompletion)
                state.Status = OfflineTaskStatusKind.Running;
            if (state.Status is OfflineTaskStatusKind.Running && state.PollCount > options.PollsBeforeCompletion)
                state.Status = state.Outcome == OfflineTaskOutcome.Success ? OfflineTaskStatusKind.Succeeded : OfflineTaskStatusKind.Failed;
            var error = state.Status == OfflineTaskStatusKind.Failed ? (OfflineProviderErrorCode?)OfflineProviderErrorCode.GenerationFailed : null;
            var price = state.Status is OfflineTaskStatusKind.Succeeded or OfflineTaskStatusKind.Failed ? state.Price : null;
            return Task.FromResult(new OfflineTaskStatus(state.TaskId, state.Status, price, state.Currency, error, state.PollCount,
                responseFormat ?? options.ResponseFormat));
        }
    }

    /// <summary>以次数、时钟和取消令牌共同限制异步轮询。</summary>
    public async Task<OfflinePollResult> PollUntilCompletedAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ValidateTaskId(taskId);
        var started = clock.UtcNow;
        OfflineTaskStatus? latest = null;
        for (var poll = 0; poll < options.MaxPolls; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.UtcNow - started > options.MaxPollDuration)
                throw new OfflineProviderException(OfflineProviderErrorCode.PollTimeout, 408);
            latest = await GetStatusAsync(taskId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (latest.Status is OfflineTaskStatusKind.Succeeded) return new OfflinePollResult(latest, RequestCount, clock.UtcNow - started);
            if (latest.Status is OfflineTaskStatusKind.Failed)
                throw new OfflineProviderException(OfflineProviderErrorCode.GenerationFailed, 422);
            await Task.Yield();
        }
        throw new OfflineProviderException(OfflineProviderErrorCode.PollTimeout, 408);
    }

    /// <summary>为已完成任务生成短期签名地址；地址只存在返回值内存中。</summary>
    public Task<OfflineOutputResponse> GetOutputAsync(string taskId, OfflineResponseFormat? responseFormat = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTaskId(taskId);
        EnsureRequestAllowed();
        lock (gate)
        {
            if (!tasks.TryGetValue(taskId, out var state)) throw new OfflineProviderException(OfflineProviderErrorCode.NotFound, 404);
            if (state.Status != OfflineTaskStatusKind.Succeeded || !state.IncludeSignedOutput)
                throw new OfflineProviderException(OfflineProviderErrorCode.InvalidState, 409);
            var expires = clock.UtcNow.Add(options.SignedUrlLifetime);
            var signature = CreateSignature(taskId, expires);
            var url = $"https://offline.invalid/output/{taskId}?expires={expires.ToUnixTimeSeconds()}&signature={signature}";
            return Task.FromResult(new OfflineOutputResponse(taskId, url, expires, responseFormat ?? options.ResponseFormat));
        }
    }

    /// <summary>按稳定任务号读取脱敏费用，用量分页同样受请求边界限制。</summary>
    public Task<OfflineUsagePage> GetUsageAsync(string? cursor = null, int? pageSize = null,
        OfflineResponseFormat? responseFormat = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var size = pageSize ?? options.CatalogPageSize;
        if (size is < 1 or > 64) throw new OfflineProviderException(OfflineProviderErrorCode.InvalidRequest, 400);
        var page = ParseCursor(cursor);
        EnsureRequestAllowed();
        lock (gate)
        {
            var source = tasks.Values.OrderBy(state => state.TaskId, StringComparer.Ordinal).ToList();
            var values = source.Skip(page * size).Take(size).Select(state => new OfflineUsageEntry(state.TaskId, state.Capability,
                state.ModelId, state.Status is OfflineTaskStatusKind.Succeeded or OfflineTaskStatusKind.Failed ? state.Price : null,
                state.Currency, state.Status)).ToArray();
            var next = source.Count > (page + 1) * size ? (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Task.FromResult(new OfflineUsagePage(values, next, responseFormat ?? options.ResponseFormat));
        }
    }

    /// <summary>验证所有上限，避免配置造成无界内存或轮询。</summary>
    private static void ValidateOptions(OfflineProviderSimulatorOptions value)
    {
        ValidateText(value.Provider, nameof(value.Provider), 64);
        if (value.CatalogPageSize is < 1 or > 64 || value.RateLimitRequests is < 1 or > 1024 ||
            value.RateLimitWindow <= TimeSpan.Zero || value.RateLimitWindow > TimeSpan.FromHours(1) ||
            value.PollsBeforeCompletion is < 1 or > 64 || value.MaxRequests is < 1 or > 100_000 ||
            value.MaxPolls is < 1 or > 1024 || value.MaxPollDuration <= TimeSpan.Zero || value.MaxPollDuration > TimeSpan.FromHours(1) ||
            value.SignedUrlLifetime <= TimeSpan.Zero || value.SignedUrlLifetime > TimeSpan.FromDays(7))
            throw new ArgumentException("离线模拟器边界配置无效。", nameof(value));
    }

    /// <summary>固定默认目录，显式标出能力而不从模型名推断。</summary>
    private static IReadOnlyList<OfflineModelDescriptor> CreateDefaultModels() =>
    [
        new("offline-text", "Offline Text", "1", ["text-planning"], "text", null, null, null, "json", false, 0m, "CNY"),
        new("offline-vision", "Offline Vision", "1", ["visual-understanding"], "image", null, "16:9", "1080p", "png", false, 0m, "CNY"),
        new("offline-voice", "Offline Voice", "1", ["speech-synthesis", "speech-transcription"], "audio", "300s", null, null, "wav", true, 0m, "CNY"),
        new("offline-video", "Offline Video", "1", ["video-generation", "lip-sync"], "video", "60s", "16:9,9:16", "1080p", "mp4", true, null, null)
    ];

    /// <summary>计算稳定任务号；只使用账号、能力、模型和指纹的哈希。</summary>
    private static string ComputeTaskId(OfflineSubmitRequest request)
    {
        var canonical = string.Join("\n", request.AccountAlias, request.Capability, request.ModelId, request.InputFingerprint);
        return "offline-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }

    /// <summary>根据稳定输入返回可重复的模拟签名，不保存签名密钥。</summary>
    private static string CreateSignature(string taskId, DateTimeOffset expires)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(taskId + ":" + expires.ToUnixTimeSeconds()));
        return Convert.ToHexStringLower(bytes)[..32];
    }

    /// <summary>校验请求并拒绝把疑似原文、地址或过长字段带入模拟器。</summary>
    private static void ValidateRequest(OfflineSubmitRequest request)
    {
        if (request is null) throw new OfflineProviderException(OfflineProviderErrorCode.InvalidRequest, 400);
        ValidateText(request.AccountAlias, nameof(request.AccountAlias), 64);
        ValidateText(request.Capability, nameof(request.Capability), 64);
        ValidateText(request.ModelId, nameof(request.ModelId), 128);
        ValidateText(request.InputFingerprint, nameof(request.InputFingerprint), 256);
        if (request.InputFingerprint.Contains("http", StringComparison.OrdinalIgnoreCase) || request.InputFingerprint.Contains('?', StringComparison.Ordinal))
            throw new OfflineProviderException(OfflineProviderErrorCode.InvalidRequest, 400);
        if (request.Price is < 0 or > 1_000_000m) throw new OfflineProviderException(OfflineProviderErrorCode.InvalidRequest, 400);
    }

    /// <summary>校验任务号字符，阻断路径穿越和控制字符。</summary>
    private static void ValidateTaskId(string taskId) => ValidateText(taskId, nameof(taskId), 128, allowEmpty: false, taskIdOnly: true);

    /// <summary>统一限制文本长度和字符集。</summary>
    private static void ValidateText(string value, string name, int max, bool allowEmpty = false, bool taskIdOnly = false)
    {
        if (value is null || value.Length > max || (!allowEmpty && value.Length == 0) || value.Any(char.IsControl) ||
            (taskIdOnly && value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            throw new OfflineProviderException(OfflineProviderErrorCode.InvalidRequest, 400);
    }

    /// <summary>解析有限页游标，拒绝负数、超长数值和隐藏字段。</summary>
    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        if (!int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page) || page is < 0 or > 10_000)
            throw new OfflineProviderException(OfflineProviderErrorCode.InvalidRequest, 400);
        return page;
    }

    /// <summary>限流与总请求数检查；时间窗口内不启动等待线程。</summary>
    private void EnsureRequestAllowed()
    {
        lock (gate)
        {
            if (requestCount >= options.MaxRequests) throw new OfflineProviderException(OfflineProviderErrorCode.RequestLimitExceeded, 429);
            var now = clock.UtcNow;
            // 队列最多保存限流窗口内允许的请求数，因此清理循环有明确上限。
            var evictionLimit = Math.Min(requestTimes.Count, options.RateLimitRequests);
            for (var eviction = 0; eviction < evictionLimit && requestTimes.Count > 0 &&
                 now - requestTimes.Peek() >= options.RateLimitWindow; eviction++) requestTimes.Dequeue();
            if (requestTimes.Count >= options.RateLimitRequests)
            {
                var retry = options.RateLimitWindow - (now - requestTimes.Peek());
                throw new OfflineProviderException(OfflineProviderErrorCode.RateLimited, 429, retry > TimeSpan.Zero ? retry : TimeSpan.Zero);
            }
            requestTimes.Enqueue(now);
            requestCount++;
        }
    }

    /// <summary>区分未知账号、无权限模型和不存在能力。</summary>
    private void EnsureAllowed(string accountAlias, string capability, string modelId)
    {
        lock (gate)
        {
            if (!permissions.TryGetValue(accountAlias, out var allowedModels))
                throw new OfflineProviderException(OfflineProviderErrorCode.Unauthorized, 401);
            if (!models.Any(model => model.Id == modelId && model.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)) ||
                !allowedModels.Contains(modelId))
                throw new OfflineProviderException(OfflineProviderErrorCode.Forbidden, 403);
        }
    }

    /// <summary>为默认模型返回脱敏的固定价格；未知模型价格保持空值。</summary>
    private decimal? GetDefaultPrice(string modelId, string capability) =>
        models.FirstOrDefault(model => model.Id == modelId && model.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))?.KnownPrice;
}
