namespace VideoProduction;

/// <summary>提供商契约的本地边界校验；拒绝秘密、原始响应和无界参数。</summary>
public static class ProviderAdapterValidation
{
    /// <summary>校验目录查询，并限制单页规模与游标长度。</summary>
    public static void Validate(ProviderAdapterCatalogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.PageSize is < 1 or > 256 || query.Cursor is not null && !Token(query.Cursor, 256))
            throw new InvalidDataException("提供商目录查询参数无效。");
        if (query.Capability is not null && !Enum.IsDefined(query.Capability.Value))
            throw new InvalidDataException("提供商能力分类无效。");
    }

    /// <summary>校验账号别名，不接受凭据内容或控制字符。</summary>
    public static void Validate(ProviderAdapterAccountQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(query.AccountAlias, 128, "账号别名");
    }

    /// <summary>校验能力探测参数。</summary>
    public static void Validate(ProviderAdapterCapabilityQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(query.AccountAlias, 128, "账号别名");
        ValidateToken(query.ModelId, 256, "模型 ID");
        if (!Enum.IsDefined(query.Capability) || query.Capability == MpsCapabilityKind.Unknown)
            throw new InvalidDataException("能力探测不能使用未知分类。");
    }

    /// <summary>校验提交请求，只允许已脱敏的输入指纹。</summary>
    public static void Validate(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(request.AccountAlias, 128, "账号别名");
        ValidateToken(request.ModelId, 256, "模型 ID");
        ValidateToken(request.InputFingerprint, 256, "输入指纹");
        if (!Enum.IsDefined(request.Capability) || request.Capability == MpsCapabilityKind.Unknown ||
            request.InputFingerprint.Contains("http", StringComparison.OrdinalIgnoreCase) || request.InputFingerprint.Contains('?', StringComparison.Ordinal))
            throw new InvalidDataException("提交参数无效或包含未脱敏输入。");
        if (request.IdempotencyKey is not null) ValidateToken(request.IdempotencyKey, 256, "幂等键");
        ValidateMoney(request.EstimatedPrice, request.Currency);
    }

    /// <summary>校验任务查询。</summary>
    public static void Validate(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(query.AccountAlias, 128, "账号别名");
        ValidateTaskId(query.TaskId);
    }

    /// <summary>校验产物下载请求和调用方流。</summary>
    public static void Validate(ProviderAdapterDownloadRequest request, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(request.AccountAlias, 128, "账号别名");
        ValidateTaskId(request.TaskId);
        ValidateToken(request.ArtifactId, 256, "产物 ID");
        if (!destination.CanWrite) throw new InvalidDataException("下载目标流不可写。");
    }

    /// <summary>校验用量查询的时间范围。</summary>
    public static void Validate(ProviderAdapterUsageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(query.AccountAlias, 128, "账号别名");
        if (query.PageSize is < 1 or > 256 || query.Cursor is not null && !Token(query.Cursor, 256) ||
            query.FromUtc is not null && query.ToUtc is not null && query.FromUtc > query.ToUtc)
            throw new InvalidDataException("用量查询参数无效。");
    }

    /// <summary>校验模型描述符并阻止未定义的证据状态。</summary>
    public static void Validate(MpsModelDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Validate(cancellationToken);
    }

    /// <summary>校验有界目录页的白名单元数据。</summary>
    public static void Validate<T>(ProviderAdapterPage<T> page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        if (page.Items is null || page.Items.Count > 256 || page.NextCursor is not null && !Token(page.NextCursor, 256) ||
            page.ObservedUtc == default || page.Source is not null && !Token(page.Source, 2048) ||
            page.ProviderVersion is not null && !Token(page.ProviderVersion, 256))
            throw new InvalidDataException("提供商分页结果无效。");
    }

    /// <summary>校验任务号，禁止路径、URL 和控制字符。</summary>
    public static void ValidateTaskId(string taskId)
    {
        if (!Token(taskId, 256) || taskId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidDataException("提供商任务号无效。");
    }

    private static void ValidateToken(string value, int maxLength, string name)
    {
        if (!Token(value, maxLength)) throw new InvalidDataException($"{name}无效。");
    }

    private static bool Token(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);

    private static void ValidateMoney(decimal? amount, string? currency)
    {
        if (amount is < 0 or > 1_000_000_000m || currency is not null && !Token(currency, 16))
            throw new InvalidDataException("费用或币种无效。");
    }
}
