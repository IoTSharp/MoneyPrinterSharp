using System.Net;
using System.Net.Http;
using System.Security.Authentication;

namespace VideoProduction;

/// <summary>提供商连接代理配置；代理只允许显式配置的本机回环地址。</summary>
public sealed record ProviderProxyOptions(Uri Address);

/// <summary>
/// 提供商 HTTP 连接的安全边界。连接只允许 HTTPS、已列入白名单的主机、有限响应和有限请求时间。
/// </summary>
public sealed record ProviderConnectionOptions
{
    /// <summary>提供商 API 根地址，必须是无凭据、无查询串的 HTTPS 地址。</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>允许访问的精确主机名集合；不支持通配符。</summary>
    public IReadOnlyCollection<string> AllowedHosts { get; init; } = Array.Empty<string>();

    /// <summary>可选的本机回环 HTTP(S) 代理。</summary>
    public ProviderProxyOptions? Proxy { get; init; }

    /// <summary>连接允许使用的 TLS 协议；默认只允许 TLS 1.2 与 TLS 1.3。</summary>
    public SslProtocols TlsProtocols { get; init; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>响应正文上限，默认64MiB。</summary>
    public long MaxResponseBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>单个 HTTP 请求上限；必须是有限的正时间。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>认证请求永远不允许跟随重定向；此字段为真时配置无效。</summary>
    public bool AllowCredentialRedirects { get; init; }
}

/// <summary>
/// 创建 HTTP 客户端前的统一校验与读取边界。此类型不保存或接收凭据，调用方需自行在已校验请求上添加认证头。
/// </summary>
public static class ProviderConnectionBoundary
{
    private const long MaximumResponseBytes = 64L * 1024 * 1024;
    private const int MaximumHosts = 16;
    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan ResponseReadTimeout = TimeSpan.FromMinutes(3);
    private static readonly SslProtocols SupportedTls = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>校验连接配置；错误信息为固定文案，不回显地址、代理或其他输入。</summary>
    public static void Validate(ProviderConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BaseAddress is null || !options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != Uri.UriSchemeHttps ||
            options.BaseAddress.UserInfo.Length != 0 || !string.IsNullOrEmpty(options.BaseAddress.Query) ||
            !string.IsNullOrEmpty(options.BaseAddress.Fragment) || string.IsNullOrWhiteSpace(options.BaseAddress.Host))
            throw Invalid("提供商 API 地址必须是无凭据的 HTTPS 地址。");

        if (options.AllowedHosts is null || options.AllowedHosts.Count is < 1 or > MaximumHosts)
            throw Invalid("提供商目标主机白名单无效。");
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in options.AllowedHosts)
        {
            if (!ValidHost(host) || !hosts.Add(host.Trim().ToLowerInvariant()))
                throw Invalid("提供商目标主机白名单无效。");
        }
        if (!hosts.Contains(options.BaseAddress.Host))
            throw Invalid("提供商 API 主机不在已验证白名单中。");

        if (options.AllowCredentialRedirects)
            throw Invalid("认证请求禁止重定向。");
        if (options.TlsProtocols == SslProtocols.None || (options.TlsProtocols & ~SupportedTls) != 0)
            throw Invalid("TLS 配置只允许 TLS 1.2 或 TLS 1.3。");
        if (options.MaxResponseBytes is < 1 or > MaximumResponseBytes)
            throw Invalid("供应商响应大小超出限制。");
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout == Timeout.InfiniteTimeSpan ||
            options.RequestTimeout > TimeSpan.FromMinutes(3) || options.RequestTimeout == TimeSpan.MaxValue)
            throw Invalid("供应商请求超时必须是有限的正时间。");

        if (options.Proxy is not null) ValidateProxy(options.Proxy);
    }

    /// <summary>
    /// 校验即将发送的目标 URI。绝对 URI 必须与已验证的 API 主机和端口完全一致，避免凭据被送往其他主机。
    /// </summary>
    public static Uri ValidateTarget(ProviderConnectionOptions options, Uri target)
    {
        Validate(options);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps || target.UserInfo.Length != 0 ||
            !string.IsNullOrEmpty(target.Fragment) || !string.Equals(target.Host, options.BaseAddress.Host, StringComparison.OrdinalIgnoreCase) ||
            EffectivePort(target) != EffectivePort(options.BaseAddress))
            throw Invalid("提供商请求目标未通过主机与 TLS 校验。");
        return target;
    }

    /// <summary>创建已校验目标请求；相对路径按 API 根地址解析，绝对地址仍须通过主机校验。</summary>
    public static HttpRequestMessage CreateRequest(ProviderConnectionOptions options, HttpMethod method, string path)
    {
        Validate(options);
        ArgumentNullException.ThrowIfNull(method);
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            throw Invalid("提供商请求路径无效。");
        Uri target;
        try { target = new Uri(options.BaseAddress, path); }
        catch (UriFormatException) { throw Invalid("提供商请求路径无效。"); }
        return new HttpRequestMessage(method, ValidateTarget(options, target));
    }

    /// <summary>
    /// 验证重定向策略。认证请求不接受任何重定向；调用方必须丢弃原请求并重新建立已校验的请求。
    /// </summary>
    public static void ValidateRedirect(ProviderConnectionOptions options, Uri redirect, bool carriesCredentials)
    {
        Validate(options);
        ArgumentNullException.ThrowIfNull(redirect);
        if (carriesCredentials || !redirect.IsAbsoluteUri || redirect.Scheme != Uri.UriSchemeHttps)
            throw Invalid("提供商认证请求禁止重定向。");
        _ = ValidateTarget(options, redirect);
    }

    /// <summary>创建关闭自动重定向、Cookie 和环境代理继承的客户端。</summary>
    public static HttpClient CreateClient(ProviderConnectionOptions options)
    {
        Validate(options);
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            CheckCertificateRevocationList = true,
            SslProtocols = options.TlsProtocols,
            UseProxy = options.Proxy is not null,
            Proxy = options.Proxy is null ? null : new WebProxy(options.Proxy.Address)
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = options.BaseAddress,
            Timeout = options.RequestTimeout,
            MaxResponseContentBufferSize = options.MaxResponseBytes
        };
    }

    /// <summary>按响应大小上限读取正文；读取块数、内存和取消均有明确边界。</summary>
    public static async Task<byte[]> ReadResponseAsync(HttpContent content, long maxResponseBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxResponseBytes is < 1 or > MaximumResponseBytes)
            throw Invalid("供应商响应大小超出限制。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ResponseReadTimeout);
        var readCancellation = deadline.Token;
        readCancellation.ThrowIfCancellationRequested();
        if (content.Headers.ContentLength is > MaximumResponseBytes || content.Headers.ContentLength is > 0 &&
            content.Headers.ContentLength > maxResponseBytes)
            throw Invalid("供应商响应大小超出限制。");

        await using var source = await content.ReadAsStreamAsync(readCancellation).ConfigureAwait(false);
        using var memory = new MemoryStream((int)Math.Min(maxResponseBytes, 1024 * 1024));
        var blocks = 0;
        var maxBlocks = checked((int)Math.Min((maxResponseBytes + BufferSize - 1) / BufferSize + 1, int.MaxValue));
        var buffer = new byte[BufferSize];
        while (blocks++ < maxBlocks)
        {
            readCancellation.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), readCancellation).ConfigureAwait(false);
            if (read == 0) return memory.ToArray();
            if (memory.Length > maxResponseBytes - read) throw Invalid("供应商响应大小超出限制。");
            memory.Write(buffer, 0, read);
        }
        throw Invalid("供应商响应读取块数超出限制。");
    }

    private static void ValidateProxy(ProviderProxyOptions proxy)
    {
        if (proxy.Address is null || !proxy.Address.IsAbsoluteUri ||
            proxy.Address.Scheme != Uri.UriSchemeHttp && proxy.Address.Scheme != Uri.UriSchemeHttps ||
            proxy.Address.UserInfo.Length != 0 || !string.IsNullOrEmpty(proxy.Address.Query) ||
            !string.IsNullOrEmpty(proxy.Address.Fragment) || proxy.Address.AbsolutePath is not ("" or "/") ||
            !IsLoopback(proxy.Address.Host))
            throw Invalid("代理必须是无凭据的本机回环 HTTP(S) 地址。");
    }

    private static bool ValidHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Length <= 253 && host.Trim() == host &&
        !host.Contains('*') && !host.Any(char.IsControl) && Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static int EffectivePort(Uri uri) => uri.IsDefaultPort ? 443 : uri.Port;

    private static InvalidDataException Invalid(string message) => new(message);
}
