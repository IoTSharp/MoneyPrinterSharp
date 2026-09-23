using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace VideoProduction;

/// <summary>HTTP 响应仅在内存解析，持久化由上层的白名单字段控制。</summary>
internal sealed class ProviderResponse : IDisposable
{
    public required int StatusCode { get; init; }
    public required JsonDocument Document { get; init; }
    public decimal? InferenceCost { get; init; }
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>及时释放响应文档，其中可能包含临时签名下载地址。</summary>
    public void Dispose() => Document.Dispose();
}

/// <summary>认证仅发往固定 API；付费 POST 一次，GET 最多回退本地代理一次。</summary>
internal static class ProviderTransport
{
    private const string Api = "https://api.moark.com/v1";
    private const int BufferSize = 65536;
    private const long MediaLimit = 512L * 1024 * 1024;

    /// <summary>提交一次付费请求；异常由上层记为 unknown，绝不回退或重试 POST。</summary>
    public static async Task<ProviderResponse> SubmitAsync(ProviderRequest payload, string credential, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(180));
        using var client = CreateClient(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Api + payload.Endpoint)) { Content = payload.Content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Add("X-Failover-Enabled", "false");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        return await ReadResponseAsync(response, limit.Token);
    }

    /// <summary>读取任务；每次查询网络失败仅进行一次代理回退，不跟随认证重定向。</summary>
    public static async Task<ProviderResponse> GetTaskAsync(string taskId, string credential, bool outputOnly, CancellationToken ct)
    {
        ValidateTaskId(taskId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                using var client = CreateClient(attempt == 1);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Api + "/task/" + taskId + (outputOnly ? "/get" : "")));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                return await ReadResponseAsync(response, limit.Token);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt == 1) throw new IOException("任务查询网络失败，已完成一次本机代理回退。");
                Console.Error.WriteLine("任务查询连接失败，一秒后回退本机代理一次。");
                await Task.Delay(1000, ct);
            }
        }
        throw new IOException("任务查询未完成。");
    }

    /// <summary>创建无自动认证重定向的独立客户端，禁用环境代理隐式继承。</summary>
    private static HttpClient CreateClient(bool proxy)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, UseProxy = proxy,
            Proxy = proxy ? new WebProxy("http://127.0.0.1:7890") : null,
            UseCookies = false, Credentials = null
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>响应最多64MiB和1025个读取块；所有错误正文均不输出或持久化。</summary>
    private static async Task<ProviderResponse> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[BufferSize];
        var complete = false;
        for (var block = 0; block < 1025; block++)
        {
            ct.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) { complete = true; break; }
            if (memory.Length + read > 64L * 1024 * 1024) throw new InvalidDataException("供应商响应超过64MiB限制。");
            memory.Write(buffer, 0, read);
        }
        if (!complete) throw new InvalidDataException("供应商响应读取块数超过限制。");
        JsonDocument document;
        try { document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 24 }); }
        catch (JsonException)
        {
            if (response.IsSuccessStatusCode) throw new InvalidDataException("供应商成功响应无法解析，需核对任务状态。");
            document = JsonDocument.Parse("{}");
        }
        decimal? cost = null;
        if (response.Headers.TryGetValues("inference-cost", out var values) &&
            decimal.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 0)
            cost = number;
        return new ProviderResponse { StatusCode = (int)response.StatusCode, Document = document, InferenceCost = cost };
    }

    /// <summary>限制任务编号字符，禁止路径穿越、查询串以及日志控制字符。</summary>
    public static void ValidateTaskId(string taskId)
    {
        if (taskId.Length is < 1 or > 128 || taskId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new InvalidDataException("供应商任务编号格式无效。");
    }

    /// <summary>下载最多512MiB、180秒；对象存储请求从不携带 Bearer，且不跟随重定向。</summary>
    public static async Task<string> DownloadAsync(string url, string destination, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            throw new InvalidDataException("供应商下载地址必须是无用户凭据的 HTTPS 地址。");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("下载目标已存在，拒绝覆盖。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(180));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using var client = CreateClient(attempt == 1);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                if (!response.IsSuccessStatusCode) throw new InvalidDataException($"媒体下载 HTTP {(int)response.StatusCode}，未跟随重定向。");
                if (response.Content.Headers.ContentLength > MediaLimit) throw new InvalidDataException("媒体下载超过512MiB限制。");
                await using var source = await response.Content.ReadAsStreamAsync(limit.Token);
                string hash;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true))
                using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[BufferSize];
                    long total = 0;
                    var complete = false;
                    for (var block = 0; block < 8193; block++)
                    {
                        limit.Token.ThrowIfCancellationRequested();
                        var read = await source.ReadAsync(buffer, limit.Token);
                        if (read == 0) { complete = true; break; }
                        total += read;
                        if (total > MediaLimit) throw new InvalidDataException("媒体下载超过512MiB限制。");
                        await output.WriteAsync(buffer.AsMemory(0, read), limit.Token);
                        digest.AppendData(buffer, 0, read);
                        if (block > 0 && block % 256 == 0) Console.Error.WriteLine($"已下载 {total / (1024 * 1024)} MiB。");
                    }
                    if (!complete || total < 16) throw new InvalidDataException("媒体下载未完整结束或文件过小。");
                    await output.FlushAsync(limit.Token);
                    hash = Convert.ToHexStringLower(digest.GetHashAndReset());
                }
                File.Move(temporary, destination, false);
                return hash;
            }
            catch (Exception error) when (error is HttpRequestException or IOException && !limit.IsCancellationRequested)
            {
                if (attempt == 1) throw new IOException("媒体下载网络或文件读取失败，未保存完整文件。");
                Console.Error.WriteLine("媒体下载失败，一秒后回退本机代理一次。");
                await Task.Delay(1000, limit.Token);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        throw new IOException("媒体下载未完成。");
    }

    /// <summary>原子保存同步图片的 Base64 内容，只清理本次创建的临时文件。</summary>
    public static async Task<string> SaveBase64Async(string encoded, string destination, CancellationToken ct)
    {
        if (encoded.Length > 64 * 1024 * 1024) throw new InvalidDataException("Base64 图片超过限制。");
        var data = Convert.FromBase64String(encoded);
        if (data.Length < 16) throw new InvalidDataException("图片输出过小。");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("图片目标已存在，拒绝覆盖。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, ct);
            File.Move(temporary, destination, false);
            return Convert.ToHexStringLower(SHA256.HashData(data));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
