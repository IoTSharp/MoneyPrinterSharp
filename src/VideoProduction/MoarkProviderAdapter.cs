namespace VideoProduction;

/// <summary>Moark 既有四种请求的本地兼容契约；不代表当前账号权限、价格或生产接口已验证。</summary>
internal sealed record MoarkRequestContract(string Kind, string Model, string Endpoint, MpsCapabilityKind Capability)
{
    internal const string Version = "moark.legacy-offline.v1";

    /// <summary>按显式用途返回仓库已有请求映射，未知用途不得猜测模型。</summary>
    internal static MoarkRequestContract ForKind(string kind) => kind switch
    {
        "image" => new(kind, "qwen-image-2.0-pro", "/images/generations", MpsCapabilityKind.ImageGeneration),
        "voice" => new(kind, "Qwen3-TTS", "/async/audio/speech", MpsCapabilityKind.SpeechSynthesis),
        "motion" => new(kind, "ViduQ2-Turbo", "/async/videos/generations", MpsCapabilityKind.VideoGeneration),
        "lipsync" => new(kind, "Duix-Avatar", "/async/videos/audio-video-to-video", MpsCapabilityKind.LipSync),
        _ => throw new ArgumentException("kind 只支持 image、voice、motion、lipsync。")
    };
}

/// <summary>可注入的 Moark 传输边界，离线测试可替换全部网络和下载操作。</summary>
internal interface IMoarkTransport
{
    /// <summary>发送一次既有请求，不在此接口暗中重提。</summary>
    Task<ProviderResponse> SubmitAsync(ProviderRequest request, string credential, CancellationToken cancellationToken);
    /// <summary>只查询既有任务号，输出查询与状态查询明确分开。</summary>
    Task<ProviderResponse> GetTaskAsync(string taskId, string credential, bool outputOnly, CancellationToken cancellationToken);
    /// <summary>保存输出；签名地址只作为内存参数传递。</summary>
    Task<string> DownloadAsync(string url, string destination, CancellationToken cancellationToken);
    /// <summary>保存同步图片内存数据，不访问供应商。</summary>
    Task<string> SaveBase64Async(string encoded, string destination, CancellationToken cancellationToken);
}

/// <summary>保留既有 HTTPS、认证和下载边界的生产传输实现。</summary>
internal sealed class MoarkHttpTransport : IMoarkTransport
{
    /// <summary>委托既有单次 POST 实现。</summary>
    public Task<ProviderResponse> SubmitAsync(ProviderRequest request, string credential, CancellationToken cancellationToken) =>
        ProviderTransport.SubmitAsync(request, credential, cancellationToken);
    /// <summary>委托既有有界任务查询实现。</summary>
    public Task<ProviderResponse> GetTaskAsync(string taskId, string credential, bool outputOnly, CancellationToken cancellationToken) =>
        ProviderTransport.GetTaskAsync(taskId, credential, outputOnly, cancellationToken);
    /// <summary>委托既有无 Bearer 媒体下载实现。</summary>
    public Task<string> DownloadAsync(string url, string destination, CancellationToken cancellationToken) =>
        ProviderTransport.DownloadAsync(url, destination, cancellationToken);
    /// <summary>委托既有原子图片保存实现。</summary>
    public Task<string> SaveBase64Async(string encoded, string destination, CancellationToken cancellationToken) =>
        ProviderTransport.SaveBase64Async(encoded, destination, cancellationToken);
}

/// <summary>CLI 的可注入运行依赖；离线测试无需读取 Windows 凭据。</summary>
internal sealed record MoarkClientRuntime(IMoarkTransport Transport, Func<string, string> ReadCredential)
{
    internal static MoarkClientRuntime Default { get; } = new(new MoarkHttpTransport(), CredentialStore.Read);
}

/// <summary>统一任务适配层与旧 Moark 记录的桥接；正文和响应只保留在本次实例内。</summary>
internal sealed class MoarkProviderAdapter : IProviderTaskSubmissionAdapter, IProviderTaskStatusAdapter, IProviderErrorMapperAdapter
{
    private readonly IMoarkTransport transport;
    private readonly string accountAlias;
    private readonly string credential;
    private readonly ProviderRecord record;
    private readonly ProviderRequest? payload;
    private readonly TimeSpan operationTimeout;
    private int submissionStarted;

    /// <summary>将一个账号及旧记录锁定到适配器；请求句柄由原 CLI 生命周期管理。</summary>
    internal MoarkProviderAdapter(IMoarkTransport transport, string accountAlias, string credential,
        ProviderRecord record, ProviderRequest? payload = null, TimeSpan? operationTimeout = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ProviderAdapterValidation.Validate(new ProviderAdapterAccountQuery(accountAlias));
        this.accountAlias = accountAlias;
        this.credential = credential ?? throw new ArgumentNullException(nameof(credential));
        this.record = record ?? throw new ArgumentNullException(nameof(record));
        this.payload = payload;
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        if (this.operationTimeout <= TimeSpan.Zero || this.operationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "单操作时限必须大于零且不超过五分钟。");
        var contract = MoarkRequestContract.ForKind(record.Kind);
        if (record.Provider != "moark" || record.Model != contract.Model ||
            payload is not null && (payload.Kind != record.Kind || payload.Model != record.Model || payload.Fingerprint != record.Fingerprint))
            throw new InvalidDataException("Moark 适配器请求与既有记录不一致。");
    }

    /// <summary>构造统一指纹请求，CLI 的估算仍由既有目录预算锁负责预留。</summary>
    internal ProviderAdapterSubmitRequest CreateSubmitRequest() => new(accountAlias, record.Model,
        MoarkRequestContract.ForKind(record.Kind).Capability, record.Fingerprint,
        EstimatedPrice: record.EstimateCny, Currency: "CNY");

    /// <summary>发送并提取旧版白名单；兼容 CLI 继续从本次内存响应保存产物。</summary>
    internal async Task<ProviderResponse> SubmitLegacyAsync(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken)
    {
        ValidateBinding(request, cancellationToken);
        if (payload is null) throw new InvalidOperationException("恢复适配器没有待提交正文，只能查询已有任务。");
        // 一旦越过提交边界，异常和取消均不允许本实例再次发送同一付费请求。
        if (Interlocked.CompareExchange(ref submissionStarted, 1, 0) != 0)
            throw new InvalidOperationException("当前请求已尝试提交，必须先恢复查询而不能重复发送。");
        var response = await ExecuteAsync(token => transport.SubmitAsync(payload, credential, token), cancellationToken).ConfigureAwait(false);
        try { ProviderResponsePolicy.ApplySubmission(record, response, credential); return response; }
        catch { response.Dispose(); throw; }
    }

    /// <summary>统一接口提交；同步图片仅返回本地投影 ID，不写入旧记录的供应商任务号。</summary>
    public async Task<ProviderAdapterSubmission> SubmitAsync(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await SubmitLegacyAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) throw new IOException(MapError(new(response.StatusCode)).Message);
        if (record.Kind == "image")
        {
            if (!response.Document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array || data.GetArrayLength() != 1)
                throw new InvalidDataException("同步图片响应缺少唯一产物，结果需要人工核对。");
            return new("mps-image-" + record.Fingerprint, ProviderAdapterTaskStatus.Succeeded, record.Price, record.Currency, DateTimeOffset.UtcNow);
        }
        if (record.TaskId is null) throw new InvalidDataException("提交响应没有可恢复任务号，禁止重新提交。");
        return new(record.TaskId, ToStatus(record.Status), record.Price, record.Currency, DateTimeOffset.UtcNow);
    }

    /// <summary>查询并更新同一旧记录；原始响应仅供本次调用方下载，不进入持久化。</summary>
    internal async Task<ProviderResponse> QueryLegacyAsync(ProviderAdapterTaskQuery query, bool outputOnly, CancellationToken cancellationToken)
    {
        ValidateQuery(query, cancellationToken);
        var response = await ExecuteAsync(token => transport.GetTaskAsync(query.TaskId, credential, outputOnly, token), cancellationToken).ConfigureAwait(false);
        try
        {
            if (!outputOnly) ProviderResponsePolicy.ApplyPoll(record, response, credential);
            else if (response.IsSuccess) ProviderResponsePolicy.ValidateTaskIdentity(record, response, credential);
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    /// <summary>将查询结果投影为统一状态，HTTP 错误不冒充任务终态。</summary>
    public async Task<ProviderAdapterTaskState> GetStatusAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
    {
        using var response = await QueryLegacyAsync(query, false, cancellationToken).ConfigureAwait(false);
        var error = response.IsSuccess ? record.Status == "failure" ? ProviderAdapterErrorCategory.Unknown :
            record.Status == "unknown" ? ProviderAdapterErrorCategory.ResponseFormat : ProviderAdapterErrorCategory.Unknown : MapError(new(response.StatusCode)).Category;
        return new(query.TaskId, response.IsSuccess ? ToStatus(record.Status) : ProviderAdapterTaskStatus.Unknown,
            record.Price, record.Currency, error, DateTimeOffset.UtcNow, response.IsSuccess ? null : MapError(new(response.StatusCode)).Message);
    }

    /// <summary>仓库没有已核实的 Moark 取消端点，明确返回未知且不发送请求。</summary>
    public Task<ProviderAdapterCancellation> CancelAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
    {
        ValidateQuery(query, cancellationToken);
        return Task.FromResult(new ProviderAdapterCancellation(query.TaskId, ProviderAdapterTaskStatus.Unknown,
            ProviderAdapterErrorCategory.Unavailable, DateTimeOffset.UtcNow));
    }

    /// <summary>只按 HTTP 状态及本地超时/取消事实映射错误，不接收供应商正文。</summary>
    public ProviderAdapterError MapError(ProviderAdapterErrorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.HttpStatus is < 100 or > 599) throw new InvalidDataException("供应商 HTTP 状态无效。");
        var category = input.IsCancelled ? ProviderAdapterErrorCategory.Cancelled : input.IsTimeout ? ProviderAdapterErrorCategory.Timeout : input.HttpStatus switch
        {
            401 => ProviderAdapterErrorCategory.Authentication,
            403 => ProviderAdapterErrorCategory.Permission,
            400 or 422 => ProviderAdapterErrorCategory.InvalidRequest,
            404 => ProviderAdapterErrorCategory.NotFound,
            409 => ProviderAdapterErrorCategory.Conflict,
            429 => ProviderAdapterErrorCategory.RateLimited,
            >= 500 => ProviderAdapterErrorCategory.Unavailable,
            _ => ProviderAdapterErrorCategory.Unknown
        };
        var message = category switch
        {
            ProviderAdapterErrorCategory.Authentication => "Moark 认证未通过。",
            ProviderAdapterErrorCategory.Permission => "Moark 当前账号权限未通过。",
            ProviderAdapterErrorCategory.InvalidRequest => "Moark 拒绝当前请求参数。",
            ProviderAdapterErrorCategory.NotFound => "Moark 未找到当前任务。",
            ProviderAdapterErrorCategory.Conflict => "Moark 请求与现有状态冲突。",
            ProviderAdapterErrorCategory.RateLimited => "Moark 请求达到限流边界。",
            ProviderAdapterErrorCategory.Unavailable => "Moark 暂未确认可用，需恢复查询。",
            ProviderAdapterErrorCategory.Timeout => "Moark 操作等待超时，需恢复查询。",
            ProviderAdapterErrorCategory.Cancelled => "Moark 操作等待已中止，需恢复查询。",
            _ => "Moark 返回未知状态，需人工核对。"
        };
        // 任意供应商错误代码都不透传；是否可重试也不授权自动重发付费提交。
        return new(category, input.HttpStatus, null, category is ProviderAdapterErrorCategory.RateLimited or ProviderAdapterErrorCategory.Unavailable,
            null, message);
    }

    /// <summary>核对账号、模型、能力和哈希，防止锁定请求被跨账号或模型替换。</summary>
    private void ValidateBinding(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken)
    {
        ProviderAdapterValidation.Validate(request, cancellationToken);
        if (request.AccountAlias != accountAlias || request.ModelId != record.Model ||
            request.Capability != MoarkRequestContract.ForKind(record.Kind).Capability || request.InputFingerprint != record.Fingerprint ||
            request.EstimatedPrice != record.EstimateCny || request.Currency != "CNY")
            throw new InvalidDataException("Moark 统一提交与账号、模型、指纹或预算估算锁定不一致。");
    }

    /// <summary>恢复查询必须使用当前账号与原任务号，不能把任意外部任务并入记录。</summary>
    private void ValidateQuery(ProviderAdapterTaskQuery query, CancellationToken cancellationToken)
    {
        ProviderAdapterValidation.Validate(query, cancellationToken);
        ProviderTransport.ValidateTaskId(query.TaskId);
        if (query.AccountAlias != accountAlias || record.TaskId is null || record.TaskId != query.TaskId)
            throw new InvalidDataException("Moark 恢复查询与既有账号或任务号不一致。");
    }

    /// <summary>单次调用同时约束等待与令牌；不配合取消的响应会被观察并释放。</summary>
    private async Task<ProviderResponse> ExecuteAsync(Func<CancellationToken, Task<ProviderResponse>> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(operationTimeout);
        var pending = operation(deadline.Token);
        var delivered = false;
        try
        {
            var response = await pending.WaitAsync(operationTimeout, deadline.Token).ConfigureAwait(false);
            delivered = true;
            return response;
        }
        finally
        {
            if (!delivered)
            {
                deadline.Cancel();
                _ = pending.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose();
                    else if (task.IsFaulted) _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    /// <summary>保留旧状态含义；没有映射的状态一律未知。</summary>
    internal static ProviderAdapterTaskStatus ToStatus(string status) => status switch
    {
        "accepted" => ProviderAdapterTaskStatus.Queued,
        "pending" => ProviderAdapterTaskStatus.Running,
        "success" => ProviderAdapterTaskStatus.Succeeded,
        "failure" or "rejected" => ProviderAdapterTaskStatus.Failed,
        "cancelled" => ProviderAdapterTaskStatus.Cancelled,
        _ => ProviderAdapterTaskStatus.Unknown
    };
}
