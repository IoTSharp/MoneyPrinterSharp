namespace VideoProduction;

/// <summary>提供商适配器统一错误类别；原始响应正文不进入此契约。</summary>
public enum ProviderAdapterErrorCategory
{
    Unknown,
    Authentication,
    Permission,
    InvalidRequest,
    NotFound,
    RateLimited,
    QuotaExceeded,
    Billing,
    Timeout,
    Unavailable,
    Conflict,
    ResponseFormat,
    Cancelled
}

/// <summary>跨提供商任务状态；未知状态必须停在人工可恢复边界。</summary>
public enum ProviderAdapterTaskStatus
{
    Unknown,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>账号可用性与目录存在性分离。</summary>
public enum ProviderAdapterAvailability
{
    Unknown,
    Available,
    Unavailable
}

/// <summary>有界目录页；游标由适配器解释，调用方不得拼接或猜测。</summary>
public sealed record ProviderAdapterPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    DateTimeOffset ObservedUtc,
    string? Source,
    string? ProviderVersion);

/// <summary>公开模型目录查询条件。</summary>
public sealed record ProviderAdapterCatalogQuery(
    MpsCapabilityKind? Capability = null,
    string? Cursor = null,
    int PageSize = 50);

/// <summary>账号状态查询条件；只传递凭据别名，不传密钥。</summary>
public sealed record ProviderAdapterAccountQuery(string AccountAlias);

/// <summary>账号状态结果；权限、余额和目录存在性不混为一个布尔值。</summary>
public sealed record ProviderAdapterAccountState(
    string AccountAlias,
    ProviderAdapterAvailability Availability,
    ProviderAdapterErrorCategory ErrorCategory,
    DateTimeOffset ObservedUtc,
    string? Message = null);

/// <summary>单项能力探测条件。</summary>
public sealed record ProviderAdapterCapabilityQuery(
    string AccountAlias,
    string ModelId,
    MpsCapabilityKind Capability);

/// <summary>能力探测结果；证据状态沿用统一能力描述符。</summary>
public sealed record ProviderAdapterCapabilityResult(
    MpsCapabilityDescriptor Descriptor,
    MpsCapabilityEvidenceStatus EvidenceStatus,
    ProviderAdapterErrorCategory ErrorCategory,
    DateTimeOffset ObservedUtc,
    string? Message = null);

/// <summary>提交请求；输入只能是脱敏指纹，稳定请求 ID用于幂等恢复。</summary>
public sealed record ProviderAdapterSubmitRequest(
    string AccountAlias,
    string ModelId,
    MpsCapabilityKind Capability,
    string InputFingerprint,
    string? IdempotencyKey = null,
    decimal? EstimatedPrice = null,
    string? Currency = null);

/// <summary>提交结果；不保存请求正文和签名下载地址。</summary>
public sealed record ProviderAdapterSubmission(
    string TaskId,
    ProviderAdapterTaskStatus Status,
    decimal? Price,
    string? Currency,
    DateTimeOffset ObservedUtc);

/// <summary>任务状态查询条件。</summary>
public sealed record ProviderAdapterTaskQuery(string AccountAlias, string TaskId);

/// <summary>任务状态结果；超时或格式未知时必须返回 Unknown。</summary>
public sealed record ProviderAdapterTaskState(
    string TaskId,
    ProviderAdapterTaskStatus Status,
    decimal? Price,
    string? Currency,
    ProviderAdapterErrorCategory ErrorCategory,
    DateTimeOffset ObservedUtc,
    string? Message = null);

/// <summary>取消结果；供应商不支持取消时以统一错误类别返回。</summary>
public sealed record ProviderAdapterCancellation(
    string TaskId,
    ProviderAdapterTaskStatus Status,
    ProviderAdapterErrorCategory ErrorCategory,
    DateTimeOffset ObservedUtc);

/// <summary>下载请求；适配器直接写入调用方提供的流，避免暴露签名 URL。</summary>
public sealed record ProviderAdapterDownloadRequest(string AccountAlias, string TaskId, string ArtifactId);

/// <summary>下载结果；只返回本地写入统计与白名单媒体信息。</summary>
public sealed record ProviderAdapterDownloadResult(
    string TaskId,
    string ArtifactId,
    long BytesWritten,
    string Sha256,
    string? MediaType,
    DateTimeOffset ObservedUtc);

/// <summary>用量查询条件。</summary>
public sealed record ProviderAdapterUsageQuery(
    string AccountAlias,
    string? Cursor = null,
    int PageSize = 50,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);

/// <summary>脱敏用量条目，以稳定任务号去重。</summary>
public sealed record ProviderAdapterUsageEntry(
    string TaskId,
    string ModelId,
    MpsCapabilityKind Capability,
    decimal? Price,
    string? Currency,
    ProviderAdapterTaskStatus Status,
    DateTimeOffset ObservedUtc);

/// <summary>错误映射输入；不接受供应商原始正文。</summary>
public sealed record ProviderAdapterErrorInput(
    int? HttpStatus,
    string? ProviderCode = null,
    bool IsTimeout = false,
    bool IsCancelled = false);

/// <summary>统一错误输出；消息必须是本地固定文案或已脱敏摘要。</summary>
public sealed record ProviderAdapterError(
    ProviderAdapterErrorCategory Category,
    int? HttpStatus,
    string? ProviderCode,
    bool IsTransient,
    TimeSpan? RetryAfter,
    string Message);

/// <summary>公开模型目录适配器。</summary>
public interface IProviderCatalogAdapter
{
    string ProviderId { get; }
    /// <summary>读取一页公开模型目录。</summary>
    Task<ProviderAdapterPage<MpsModelDescriptor>> ListModelsAsync(ProviderAdapterCatalogQuery query, CancellationToken cancellationToken = default);
}

/// <summary>账号可用性适配器。</summary>
public interface IProviderAccountAdapter
{
    /// <summary>读取账号可用性，不返回凭据。</summary>
    Task<ProviderAdapterAccountState> GetAccountStateAsync(ProviderAdapterAccountQuery query, CancellationToken cancellationToken = default);
}

/// <summary>能力只读探测适配器。</summary>
public interface IProviderCapabilityAdapter
{
    /// <summary>以只读方式探测单项能力。</summary>
    Task<ProviderAdapterCapabilityResult> ProbeCapabilityAsync(ProviderAdapterCapabilityQuery query, CancellationToken cancellationToken = default);
}

/// <summary>异步任务提交适配器。</summary>
public interface IProviderTaskSubmissionAdapter
{
    /// <summary>提交一次可恢复的异步任务。</summary>
    Task<ProviderAdapterSubmission> SubmitAsync(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken = default);
}

/// <summary>任务查询与取消适配器。</summary>
public interface IProviderTaskStatusAdapter
{
    /// <summary>按稳定任务号查询状态。</summary>
    Task<ProviderAdapterTaskState> GetStatusAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default);
    /// <summary>请求取消任务并返回供应商确认状态。</summary>
    Task<ProviderAdapterCancellation> CancelAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default);
}

/// <summary>产物下载适配器；调用方负责目标流生命周期。</summary>
public interface IProviderArtifactDownloadAdapter
{
    /// <summary>把产物写入调用方提供的目标流。</summary>
    Task<ProviderAdapterDownloadResult> DownloadAsync(ProviderAdapterDownloadRequest request, Stream destination, CancellationToken cancellationToken = default);
}

/// <summary>用量查询适配器。</summary>
public interface IProviderUsageAdapter
{
    /// <summary>读取一页脱敏用量。</summary>
    Task<ProviderAdapterPage<ProviderAdapterUsageEntry>> GetUsageAsync(ProviderAdapterUsageQuery query, CancellationToken cancellationToken = default);
}

/// <summary>错误映射适配器。</summary>
public interface IProviderErrorMapperAdapter
{
    /// <summary>将有限的状态输入映射为本地错误类别。</summary>
    ProviderAdapterError MapError(ProviderAdapterErrorInput input);
}

/// <summary>完整提供商适配器；新增提供商只实现这些能力契约，不修改技能或时间线模型。</summary>
public interface IProviderAdapter :
    IProviderCatalogAdapter,
    IProviderAccountAdapter,
    IProviderCapabilityAdapter,
    IProviderTaskSubmissionAdapter,
    IProviderTaskStatusAdapter,
    IProviderArtifactDownloadAdapter,
    IProviderUsageAdapter,
    IProviderErrorMapperAdapter
{
}
