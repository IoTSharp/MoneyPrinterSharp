using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>异步任务轮询边界；次数、总时长和取消令牌同时生效。</summary>
public sealed record ProviderAsyncTaskPollingOptions(
    int MaxPolls = 32,
    TimeSpan? MaxDuration = null,
    TimeSpan? Delay = null)
{
    public TimeSpan EffectiveDuration => MaxDuration ?? TimeSpan.FromSeconds(30);
    public TimeSpan EffectiveDelay => Delay ?? TimeSpan.Zero;

    /// <summary>校验轮询边界，阻止无界等待或休眠。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MaxPolls is < 1 or > 1024 || EffectiveDuration <= TimeSpan.Zero || EffectiveDuration > TimeSpan.FromHours(1) ||
            EffectiveDelay < TimeSpan.Zero || EffectiveDelay > TimeSpan.FromMinutes(1))
            throw new InvalidDataException("异步任务轮询边界无效。");
    }
}

/// <summary>可持久化的异步任务记录；只保存脱敏指纹和状态，不保存请求正文或签名地址。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderTaskRecoveryEntry
{
    public int SchemaVersion { get; set; } = 1;
    public string ProviderId { get; set; } = string.Empty;
    public string AccountAlias { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public MpsCapabilityKind Capability { get; set; }
    public string InputFingerprint { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? TaskId { get; set; }
    public ProviderAdapterTaskStatus Status { get; set; } = ProviderAdapterTaskStatus.Unknown;
    public int PollCount { get; set; }
    public decimal? Price { get; set; }
    public string? Currency { get; set; }
    public ProviderAdapterErrorCategory ErrorCategory { get; set; } = ProviderAdapterErrorCategory.Unknown;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>验证快照字段并拒绝可能包含秘密或原始 URL 的值。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || !Enum.IsDefined(Capability) || Capability == MpsCapabilityKind.Unknown ||
            !Enum.IsDefined(Status) || !Enum.IsDefined(ErrorCategory) || PollCount is < 0 or > 100_000 ||
            CreatedUtc == default || UpdatedUtc == default || UpdatedUtc < CreatedUtc)
            throw new InvalidDataException("异步任务恢复记录无效。");
        ValidateText(ProviderId, 128, "提供商");
        ValidateText(AccountAlias, 128, "账号");
        ValidateText(ModelId, 256, "模型");
        ValidateFingerprint(InputFingerprint, "输入指纹");
        ValidateFingerprint(IdempotencyKey, "幂等键");
        if (TaskId is not null) ValidateTaskId(TaskId);
        if (Price is < 0 or > 1_000_000_000m) throw new InvalidDataException("异步任务费用无效。");
        if (Price is not null && (string.IsNullOrWhiteSpace(Currency) || Currency.Length > 16 || Currency.Any(char.IsControl)))
            throw new InvalidDataException("异步任务费用币种无效。");
        if (ErrorMessage is not null) ValidateSafeMessage(ErrorMessage);
    }

    /// <summary>创建与请求对应的脱敏记录。</summary>
    public static ProviderTaskRecoveryEntry FromRequest(string providerId, ProviderAdapterSubmitRequest request, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;
        var entry = new ProviderTaskRecoveryEntry
        {
            ProviderId = providerId,
            AccountAlias = request.AccountAlias,
            ModelId = request.ModelId,
            Capability = request.Capability,
            InputFingerprint = request.InputFingerprint,
            IdempotencyKey = idempotencyKey,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        entry.Validate();
        return entry;
    }

    private static void ValidateFingerprint(string value, string name)
    {
        ValidateText(value, 512, name);
        if (value.Contains("http", StringComparison.OrdinalIgnoreCase) || value.Contains("://", StringComparison.Ordinal) ||
            value.Contains("bearer", StringComparison.OrdinalIgnoreCase) || value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token", StringComparison.OrdinalIgnoreCase) || value.Contains("authorization", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{name}不能包含地址或敏感字段。");
    }

    private static void ValidateTaskId(string value) =>
        ValidateText(value, 256, "任务号", taskIdOnly: true);

    private static void ValidateText(string value, int max, string name, bool taskIdOnly = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value != value.Trim() || value.Any(char.IsControl) ||
            (taskIdOnly && value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))))
            throw new InvalidDataException($"{name}无效。");
    }

    private static void ValidateSafeMessage(string value)
    {
        if (value.Length > 256 || value.Any(char.IsControl) || value.Contains("http", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret", StringComparison.OrdinalIgnoreCase) || value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("signature", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("异步任务错误消息包含敏感内容。");
    }
}

/// <summary>任务恢复文件的有界快照；只保存能够安全重开的字段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProviderTaskRecoverySnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset SavedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<ProviderTaskRecoveryEntry> Tasks { get; set; } = [];

    /// <summary>校验版本、数量和任务键唯一性。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SchemaVersion is < 1 or > 100 || SavedUtc == default || Tasks is null || Tasks.Count > ProviderAsyncTaskRecoveryStore.MaxTasks)
            throw new InvalidDataException("异步任务恢复快照无效。");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Tasks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = Tasks[index] ?? throw new InvalidDataException("异步任务恢复项为空。");
            task.Validate(cancellationToken);
            if (!keys.Add(task.IdempotencyKey) || task.TaskId is not null && !ids.Add(task.TaskId))
                throw new InvalidDataException("异步任务恢复快照包含重复键。");
        }
    }
}

/// <summary>线程安全的异步任务恢复仓库；保存前执行完整校验和数量上限检查。</summary>
public sealed class ProviderAsyncTaskRecoveryStore
{
    public const int MaxTasks = 512;
    private readonly object gate = new();
    private readonly Dictionary<string, ProviderTaskRecoveryEntry> entries = new(StringComparer.Ordinal);

    /// <summary>根据幂等键查找任务的脱敏副本。</summary>
    public ProviderTaskRecoveryEntry? Find(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(idempotencyKey);
        lock (gate)
        {
            return entries.TryGetValue(idempotencyKey, out var value) ? Clone(value) : null;
        }
    }

    /// <summary>添加或更新任务；更新只替换同一个幂等键。</summary>
    public void Upsert(ProviderTaskRecoveryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Validate(cancellationToken);
        lock (gate)
        {
            if (!entries.ContainsKey(entry.IdempotencyKey) && entries.Count >= MaxTasks)
                throw new InvalidDataException("异步任务恢复数量超过上限。");
            if (entry.TaskId is not null && entries.Values.Any(item => !string.Equals(item.IdempotencyKey, entry.IdempotencyKey, StringComparison.Ordinal) &&
                                                                       string.Equals(item.TaskId, entry.TaskId, StringComparison.Ordinal)))
                throw new InvalidDataException("异步任务号已关联其他幂等键。");
            entries[entry.IdempotencyKey] = Clone(entry);
        }
    }

    /// <summary>把人工或供应商查询得到的旧任务号附加到已有幂等记录。</summary>
    public ProviderTaskRecoveryEntry AttachTaskId(string idempotencyKey, string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(idempotencyKey);
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 256 || taskId.Any(char.IsControl) ||
            taskId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            throw new InvalidDataException("供应商任务号无效。");
        var entry = Find(idempotencyKey, cancellationToken) ?? throw new InvalidDataException("未找到异步任务恢复记录。");
        entry.TaskId = taskId;
        entry.Status = ProviderAdapterTaskStatus.Unknown;
        entry.ErrorCategory = ProviderAdapterErrorCategory.Unknown;
        entry.ErrorMessage = null;
        entry.UpdatedUtc = DateTimeOffset.UtcNow < entry.CreatedUtc ? entry.CreatedUtc : DateTimeOffset.UtcNow;
        Upsert(entry, cancellationToken);
        return entry;
    }

    /// <summary>导出可重开的脱敏快照。</summary>
    public ProviderTaskRecoverySnapshot ToSnapshot(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProviderTaskRecoverySnapshot snapshot;
        lock (gate)
        {
            snapshot = new ProviderTaskRecoverySnapshot
            {
                SavedUtc = DateTimeOffset.UtcNow,
                Tasks = entries.Values.OrderBy(item => item.IdempotencyKey, StringComparer.Ordinal).Select(Clone).ToList()
            };
        }
        snapshot.Validate(cancellationToken);
        return snapshot;
    }

    /// <summary>从脱敏快照重开仓库，不访问凭据或网络。</summary>
    public static ProviderAsyncTaskRecoveryStore Reopen(ProviderTaskRecoverySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate(cancellationToken);
        var store = new ProviderAsyncTaskRecoveryStore();
        foreach (var item in snapshot.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            store.Upsert(item, cancellationToken);
        }
        return store;
    }

    /// <summary>按项目 JSON 选项序列化快照；输出不包含秘密、原始响应或签名地址。</summary>
    public string ExportJson(CancellationToken cancellationToken = default)
    {
        var snapshot = ToSnapshot(cancellationToken);
        return System.Text.Json.JsonSerializer.Serialize(snapshot, JsonFiles.Options);
    }

    /// <summary>原子写入脱敏恢复文件；文件内容不包含凭据、原始响应或签名地址。</summary>
    public void Save(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4_096 || path.Any(char.IsControl))
            throw new InvalidDataException("异步任务恢复路径无效。");
        JsonFiles.Write(path, ToSnapshot(cancellationToken));
    }

    /// <summary>在有界项目写入租约内原子保存恢复快照，避免多个窗口互相覆盖。</summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4_000 || path.Any(char.IsControl))
            throw new InvalidDataException("异步任务恢复路径无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var lockPath = path + ".lock";
        await using var lease = await ProviderLocks.AcquireAsync(lockPath, cancellationToken).ConfigureAwait(false);
        JsonFiles.Write(path, ToSnapshot(cancellationToken));
    }

    /// <summary>从本地脱敏恢复文件重开任务仓库。</summary>
    public static ProviderAsyncTaskRecoveryStore Load(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4_096 || path.Any(char.IsControl))
            throw new InvalidDataException("异步任务恢复路径无效。");
        return Reopen(JsonFiles.Read<ProviderTaskRecoverySnapshot>(path), cancellationToken);
    }

    /// <summary>从 JSON 重开并再次执行严格校验。</summary>
    public static ProviderAsyncTaskRecoveryStore ReopenJson(string json, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("异步任务恢复 JSON 为空或超过大小上限。");
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<ProviderTaskRecoverySnapshot>(json, JsonFiles.Options)
            ?? throw new InvalidDataException("异步任务恢复 JSON 为空。");
        return Reopen(snapshot, cancellationToken);
    }

    private static ProviderTaskRecoveryEntry Clone(ProviderTaskRecoveryEntry value) => new()
    {
        SchemaVersion = value.SchemaVersion,
        ProviderId = value.ProviderId,
        AccountAlias = value.AccountAlias,
        ModelId = value.ModelId,
        Capability = value.Capability,
        InputFingerprint = value.InputFingerprint,
        IdempotencyKey = value.IdempotencyKey,
        TaskId = value.TaskId,
        Status = value.Status,
        PollCount = value.PollCount,
        Price = value.Price,
        Currency = value.Currency,
        ErrorCategory = value.ErrorCategory,
        ErrorMessage = value.ErrorMessage,
        CreatedUtc = value.CreatedUtc,
        UpdatedUtc = value.UpdatedUtc
    };

    private static void ValidateKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new InvalidDataException("异步任务幂等键无效。");
    }
}

/// <summary>异步任务提交或轮询结果，包含是否发生新提交的本地事实。</summary>
public sealed record ProviderAsyncTaskResult(
    ProviderTaskRecoveryEntry Task,
    bool Submitted,
    bool ReusedExistingTask,
    bool RequiresRecoveryQuery = false);

/// <summary>统一异步任务协调器：幂等提交、先查询旧任务、有限轮询和取消。</summary>
public sealed class ProviderAsyncTaskCoordinator
{
    private readonly string providerId;
    private readonly IProviderTaskSubmissionAdapter submission;
    private readonly IProviderTaskStatusAdapter status;
    private readonly ProviderAsyncTaskRecoveryStore recovery;
    private readonly TimeSpan operationTimeout;

    /// <summary>保留既有构造入口，单次供应商操作默认最多等待三十秒。</summary>
    public ProviderAsyncTaskCoordinator(string providerId, IProviderTaskSubmissionAdapter submission,
        IProviderTaskStatusAdapter status, ProviderAsyncTaskRecoveryStore? recovery = null)
        : this(providerId, submission, status, recovery, TimeSpan.FromSeconds(30))
    {
    }

    /// <summary>设置每次提交、查询和取消的时限，禁止超过五分钟。</summary>
    public ProviderAsyncTaskCoordinator(string providerId, IProviderTaskSubmissionAdapter submission,
        IProviderTaskStatusAdapter status, ProviderAsyncTaskRecoveryStore? recovery, TimeSpan operationTimeout)
    {
        if (operationTimeout <= TimeSpan.Zero || operationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "单操作时限必须大于零且不超过五分钟。");
        ProviderAccountConfiguration.ValidateProviderId(providerId);
        this.providerId = providerId;
        this.submission = submission ?? throw new ArgumentNullException(nameof(submission));
        this.status = status ?? throw new ArgumentNullException(nameof(status));
        this.recovery = recovery ?? new ProviderAsyncTaskRecoveryStore();
        this.operationTimeout = operationTimeout;
    }

    /// <summary>返回共享恢复仓库，供项目关闭时导出快照。</summary>
    public ProviderAsyncTaskRecoveryStore Recovery => recovery;

    /// <summary>提交或恢复任务；已有任务号时先查询，避免超时后重复提交。</summary>
    public async Task<ProviderAsyncTaskResult> SubmitOrResumeAsync(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var key = request.IdempotencyKey ?? BuildIdempotencyKey(providerId, request);
        var existing = recovery.Find(key, cancellationToken);
        // 没有任务号的既有记录也可能已产生费用，必须找回旧任务而不能盲目重发。
        if (existing is not null && existing.TaskId is null)
            return new ProviderAsyncTaskResult(existing, false, true, true);
        if (existing?.TaskId is not null)
        {
            if (existing.Status is ProviderAdapterTaskStatus.Succeeded or ProviderAdapterTaskStatus.Failed or ProviderAdapterTaskStatus.Cancelled)
                return new ProviderAsyncTaskResult(existing, false, true);
            var queried = await QueryAndStoreAsync(existing, cancellationToken).ConfigureAwait(false);
            if (queried.Status is ProviderAdapterTaskStatus.Queued or ProviderAdapterTaskStatus.Running or ProviderAdapterTaskStatus.Unknown)
                return new ProviderAsyncTaskResult(queried, false, true, queried.Status == ProviderAdapterTaskStatus.Unknown);
            return new ProviderAsyncTaskResult(queried, false, true);
        }

        var entry = existing ?? ProviderTaskRecoveryEntry.FromRequest(providerId, request, key);
        // 先登记提交意图；提交期间中止或关闭后仍能阻止重复付费。
        recovery.Upsert(entry, cancellationToken);
        try
        {
            var result = await ExecuteOperationAsync(token => submission.SubmitAsync(request with { IdempotencyKey = key }, token),
                cancellationToken).ConfigureAwait(false);
            ApplySubmission(entry, result);
            recovery.Upsert(entry);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderAsyncTaskResult(entry, true, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkUnknown(entry, ProviderAdapterErrorCategory.Cancelled, "提交等待已中止，恢复时必须先查询旧任务。");
            throw;
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            return new ProviderAsyncTaskResult(MarkUnknown(entry, ProviderAdapterErrorCategory.Timeout,
                "提交超时，重开时必须先查询旧任务。"), false, false, true);
        }
    }

    /// <summary>按次数、墙钟总时长和取消令牌轮询；超时仅保留 Unknown 以便下次查询。</summary>
    public async Task<ProviderAsyncTaskResult> PollUntilTerminalAsync(string idempotencyKey,
        ProviderAsyncTaskPollingOptions? options = null, CancellationToken cancellationToken = default)
    {
        var pollOptions = options ?? new ProviderAsyncTaskPollingOptions();
        pollOptions.Validate(cancellationToken);
        var entry = recovery.Find(idempotencyKey, cancellationToken) ?? throw new InvalidDataException("未找到异步任务恢复记录。");
        if (entry.TaskId is null) throw new InvalidDataException("恢复记录缺少供应商任务号。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(pollOptions.EffectiveDuration);
        try
        {
            for (var attempt = 0; attempt < pollOptions.MaxPolls; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                entry = await QueryAndStoreAsync(entry, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (entry.Status is ProviderAdapterTaskStatus.Succeeded or ProviderAdapterTaskStatus.Failed or ProviderAdapterTaskStatus.Cancelled)
                    return new ProviderAsyncTaskResult(entry, false, true);
                if (entry.Status == ProviderAdapterTaskStatus.Unknown && entry.ErrorCategory == ProviderAdapterErrorCategory.Timeout)
                    return new ProviderAsyncTaskResult(entry, false, true, true);
                if (attempt + 1 < pollOptions.MaxPolls && pollOptions.EffectiveDelay > TimeSpan.Zero)
                    await Task.Delay(pollOptions.EffectiveDelay, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkUnknown(entry, ProviderAdapterErrorCategory.Cancelled, "轮询等待已中止，恢复时先查询旧任务。");
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new ProviderAsyncTaskResult(MarkUnknown(entry, ProviderAdapterErrorCategory.Timeout,
                "轮询超时，重开时先查询旧任务。"), false, true, true);
        }
        return new ProviderAsyncTaskResult(MarkUnknown(entry, ProviderAdapterErrorCategory.Timeout, "轮询达到次数上限，重开时先查询旧任务。"), false, true, true);
    }

    /// <summary>请求取消并将统一取消状态写入恢复记录。</summary>
    public async Task<ProviderAsyncTaskResult> CancelAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var entry = recovery.Find(idempotencyKey, cancellationToken) ?? throw new InvalidDataException("未找到异步任务恢复记录。");
        if (entry.TaskId is null) return new ProviderAsyncTaskResult(MarkUnknown(entry, ProviderAdapterErrorCategory.Cancelled,
            "任务尚无可查询的供应商任务号。"), false, true, true);
        try
        {
            var result = await ExecuteOperationAsync(token => status.CancelAsync(new ProviderAdapterTaskQuery(entry.AccountAlias, entry.TaskId), token),
                cancellationToken).ConfigureAwait(false);
            entry.Status = result.Status;
            entry.ErrorCategory = result.ErrorCategory;
            entry.UpdatedUtc = DateTimeOffset.UtcNow;
            recovery.Upsert(entry);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderAsyncTaskResult(entry, false, true, entry.Status == ProviderAdapterTaskStatus.Unknown);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkUnknown(entry, ProviderAdapterErrorCategory.Cancelled, "取消等待已中止，恢复时先查询旧任务。");
            throw;
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            return new ProviderAsyncTaskResult(MarkUnknown(entry, ProviderAdapterErrorCategory.Timeout,
                "取消请求超时，恢复时先查询旧任务。"), false, true, true);
        }
    }

    /// <summary>稳定生成幂等键；只使用脱敏请求字段。</summary>
    public static string BuildIdempotencyKey(string providerId, ProviderAdapterSubmitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProviderAccountConfiguration.ValidateProviderId(providerId);
        ProviderAdapterValidation.Validate(request);
        var canonical = string.Join("\n", providerId, request.AccountAlias, request.ModelId, request.Capability, request.InputFingerprint);
        return "mps-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..40];
    }

    /// <summary>查询在独立时限内等待；取消和超时均保留同一任务号。</summary>
    private async Task<ProviderTaskRecoveryEntry> QueryAndStoreAsync(ProviderTaskRecoveryEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var state = await ExecuteOperationAsync(token => status.GetStatusAsync(new ProviderAdapterTaskQuery(entry.AccountAlias, entry.TaskId!), token),
                cancellationToken).ConfigureAwait(false);
            entry.Status = state.Status;
            entry.PollCount = Math.Min(entry.PollCount + 1, 100_000);
            entry.Price = state.Price;
            entry.Currency = state.Currency;
            entry.ErrorCategory = state.ErrorCategory;
            entry.ErrorMessage = ToSafeMessage(state.Status, state.ErrorCategory);
            entry.UpdatedUtc = DateTimeOffset.UtcNow;
            recovery.Upsert(entry);
            cancellationToken.ThrowIfCancellationRequested();
            return entry;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkUnknown(entry, ProviderAdapterErrorCategory.Cancelled, "查询等待已中止，恢复时先查询旧任务。");
            throw;
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            return MarkUnknown(entry, ProviderAdapterErrorCategory.Timeout, "任务状态查询超时，需稍后恢复查询。");
        }
    }

    /// <summary>同时约束适配器取消令牌和调用方等待，即使适配器忽略取消也会终止本次等待。</summary>
    private async Task<T> ExecuteOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(operationTimeout);
        var pending = operation(deadline.Token);
        try
        {
            return await pending.WaitAsync(operationTimeout, deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!pending.IsCompleted)
            {
                deadline.Cancel();
                // 非协作适配器稍后失败时只观察异常，不更新已返回的恢复状态。
                _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    /// <summary>不使用已取消的令牌保存未知状态，保证可能发生过的提交仍可恢复。</summary>
    private ProviderTaskRecoveryEntry MarkUnknown(ProviderTaskRecoveryEntry entry, ProviderAdapterErrorCategory category, string message)
    {
        entry.Status = ProviderAdapterTaskStatus.Unknown;
        entry.ErrorCategory = category;
        entry.ErrorMessage = message;
        entry.UpdatedUtc = DateTimeOffset.UtcNow;
        recovery.Upsert(entry);
        return entry;
    }

    private static void ApplySubmission(ProviderTaskRecoveryEntry entry, ProviderAdapterSubmission result)
    {
        if (string.IsNullOrWhiteSpace(result.TaskId)) throw new InvalidDataException("供应商返回空任务号。");
        entry.TaskId = result.TaskId;
        entry.Status = result.Status;
        entry.Price = result.Price;
        entry.Currency = result.Currency;
        entry.ErrorCategory = ProviderAdapterErrorCategory.Unknown;
        entry.ErrorMessage = null;
        entry.UpdatedUtc = result.ObservedUtc == default || result.ObservedUtc < entry.CreatedUtc ? entry.CreatedUtc : result.ObservedUtc;
    }

    private static string? ToSafeMessage(ProviderAdapterTaskStatus taskStatus, ProviderAdapterErrorCategory category) =>
        taskStatus switch
        {
            ProviderAdapterTaskStatus.Failed => "供应商异步任务失败。",
            ProviderAdapterTaskStatus.Cancelled => "供应商异步任务已取消。",
            ProviderAdapterTaskStatus.Unknown when category == ProviderAdapterErrorCategory.Timeout => "任务状态查询超时，需稍后恢复查询。",
            _ => null
        };
}
