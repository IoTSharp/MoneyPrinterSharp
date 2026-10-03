using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>连接边界离线回归；不创建网络请求、不访问供应商，也不包含凭据。</summary>
public static class ProviderConnectionBoundaryTests
{
    /// <summary>执行有限的配置、目标、重定向、代理和响应大小断言。</summary>
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var options = ValidOptions();
        ProviderConnectionBoundary.Validate(options);
        ExpectInvalid(() => ProviderConnectionBoundary.Validate(options with { BaseAddress = new Uri("http://api.example.test/v1") }));
        ExpectInvalid(() => ProviderConnectionBoundary.Validate(options with { AllowedHosts = ["other.example.test"] }));
        // TLS 1.1 的枚举成员在 .NET 10 标记为过时；使用其协议值验证旧协议会被拒绝。
        ExpectInvalid(() => ProviderConnectionBoundary.Validate(options with { TlsProtocols = (SslProtocols)768 }));
        ExpectInvalid(() => ProviderConnectionBoundary.Validate(options with { MaxResponseBytes = 0 }));
        ExpectInvalid(() => ProviderConnectionBoundary.Validate(options with { RequestTimeout = Timeout.InfiniteTimeSpan }));
        ExpectInvalid(() => ProviderConnectionBoundary.Validate(options with { AllowCredentialRedirects = true }));
        ExpectInvalid(() => ProviderConnectionBoundary.Validate(options with
        {
            Proxy = new ProviderProxyOptions(new Uri("http://proxy.example.test:8080"))
        }));
        ExpectInvalid(() => ProviderConnectionBoundary.Validate(options with
        {
            Proxy = new ProviderProxyOptions(new Uri("http://127.0.0.1:7890/?credential=canary"))
        }));
        ExpectInvalid(() => ProviderConnectionBoundary.Validate(options with
        {
            Proxy = new ProviderProxyOptions(new Uri("http://127.0.0.1:7890/proxy"))
        }));

        var target = ProviderConnectionBoundary.ValidateTarget(options, new Uri("https://api.example.test/v1/tasks"));
        Assert(target.Host == "api.example.test", "已配置主机应通过目标校验。");
        ExpectInvalid(() => ProviderConnectionBoundary.ValidateTarget(options, new Uri("https://other.example.test/v1/tasks")));
        ExpectInvalid(() => ProviderConnectionBoundary.ValidateTarget(options, new Uri("http://api.example.test/v1/tasks")));
        ExpectInvalid(() => ProviderConnectionBoundary.ValidateTarget(options, new Uri("https://api.example.test:444/v1/tasks")));
        ExpectInvalid(() => ProviderConnectionBoundary.ValidateTarget(options, new Uri("https://user:secret@api.example.test/v1/tasks")));
        using var request = ProviderConnectionBoundary.CreateRequest(options, HttpMethod.Get, "/v1/tasks");
        Assert(request.RequestUri == target, "已校验请求应固定到 API 主机。");
        request.Dispose();
        ExpectInvalid(() => ProviderConnectionBoundary.CreateRequest(options, HttpMethod.Get, "https://other.example.test/v1/tasks"));
        ExpectInvalid(() => ProviderConnectionBoundary.ValidateRedirect(options, new Uri("https://api.example.test/v1/tasks"), carriesCredentials: true));
        ExpectInvalid(() => ProviderConnectionBoundary.ValidateRedirect(options, new Uri("https://other.example.test/v1/tasks"), carriesCredentials: false));

        using var client = ProviderConnectionBoundary.CreateClient(options);
        Assert(client.Timeout == options.RequestTimeout, "客户端应使用有限请求超时。");
        Assert(client.BaseAddress == options.BaseAddress, "客户端应锁定已校验的 API 根地址。");

        using var small = new StringContent("offline-response");
        var bytes = await ProviderConnectionBoundary.ReadResponseAsync(small, 1024, cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert(System.Text.Encoding.UTF8.GetString(bytes) == "offline-response", "有限响应应能完整读取。");
        using var oversized = new StringContent(new string('x', 2048));
        ExpectInvalidAsync(() => ProviderConnectionBoundary.ReadResponseAsync(oversized, 1024, cancellationToken));
        Console.WriteLine("连接边界通过：HTTPS/TLS、精确主机、认证重定向、回环代理、响应大小和有限超时均已离线验证。");
    }

    private static ProviderConnectionOptions ValidOptions() => new()
    {
        BaseAddress = new Uri("https://api.example.test/v1"),
        AllowedHosts = ["api.example.test"],
        TlsProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        MaxResponseBytes = 1024 * 1024,
        RequestTimeout = TimeSpan.FromSeconds(20)
    };

    private static void ExpectInvalid(Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("应拒绝无效连接配置或目标。");
        }
        catch (InvalidDataException error)
        {
            Assert(!error.Message.Contains("canary", StringComparison.OrdinalIgnoreCase) &&
                !error.Message.Contains("secret", StringComparison.OrdinalIgnoreCase), "连接边界错误不得回显秘密。");
        }
    }

    private static void ExpectInvalidAsync(Func<Task> action)
    {
        try
        {
            action().Wait(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("应拒绝超出响应大小的正文。");
        }
        catch (AggregateException error) when (error.InnerException is InvalidDataException invalid)
        {
            Assert(!invalid.Message.Contains("canary", StringComparison.OrdinalIgnoreCase), "响应边界错误不得回显秘密。");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
