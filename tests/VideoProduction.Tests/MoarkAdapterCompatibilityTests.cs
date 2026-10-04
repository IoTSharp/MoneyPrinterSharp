using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>四种 Moark 请求与旧 CLI 的离线兼容回归，所有传输和凭据读取均为假实现。</summary>
public static class MoarkAdapterCompatibilityTests
{
    private const string CredentialCanary = "OFFLINE_CREDENTIAL_CANARY";

    /// <summary>在二十秒总边界内验证请求、预算锁、旧记录恢复、超时与脱敏。</summary>
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await FourKindsKeepLegacyCliAndRecovery(deadline.Token);
        await ExistingImageRejectsWrongHashAndOwner(deadline.Token);
        await FingerprintsAndRequestHandlesRemainStable(deadline.Token);
        await UnifiedBindingAndUnknownStatesRemainSafe(deadline.Token);
        await AmbiguousSubmissionIsNeverRepeated(deadline.Token);
        await NoncooperativeResponseIsBoundedAndDisposed(deadline.Token);
        Console.WriteLine("Moark 离线兼容通过：四类请求、旧记录/预算锁、统一账号/模型绑定、未知状态、取消/超时不重提、迟到响应释放与脱敏。");
    }

    /// <summary>已生成图片只接受原路径和原哈希，内容变化或无归属目标均不读取凭据、不追加提交。</summary>
    private static async Task ExistingImageRejectsWrongHashAndOwner(CancellationToken ct)
    {
        using var files = new OwnedFixture();
        var transport = new FakeTransport();
        var reads = 0;
        var runtime = new MoarkClientRuntime(transport, _ => { reads++; return CredentialCanary; });
        var request = files.Request("image", "output-integrity");
        var record = files.Track("image-record.json");
        var output = files.Track("image-output.bin");
        files.Track("image-record.json.lock");
        files.Track(".moark-budget.json");
        files.Track(".moark-budget.json.lock");
        var arguments = SubmitArguments("image", request, record, output);
        Assert(await MoarkClient.RunAsync("submit", arguments, ct, runtime) == 0, "图片完整性测试的初始提交未完成");
        var changed = new byte[32];
        changed[0] = 1;
        await File.WriteAllBytesAsync(output, changed, ct);
        await ExpectAsync<IOException>(() => MoarkClient.RunAsync("submit", arguments, ct, runtime), "错误图片哈希未拒绝");
        var unowned = files.Track("unowned-output.bin");
        await File.WriteAllBytesAsync(unowned, new byte[32], ct);
        await ExpectAsync<IOException>(() => MoarkClient.RunAsync("submit", SubmitArguments("image", request, record, unowned), ct, runtime),
            "不同归属路径即使内容哈希相同也不能复用");
        Assert(reads == 1 && transport.SubmitCount == 1 && transport.DownloadCount == 0 &&
            (await File.ReadAllBytesAsync(output, ct))[0] == 1 && JsonFiles.Read<ProviderRecord>(record).LocalOutput == output,
            "拒绝恢复后读取了凭据、重新提交、覆盖了输出或更改了旧记录");
    }

    /// <summary>四种公开 CLI 参数继续使用同一白名单记录和下载恢复路径。</summary>
    private static async Task FourKindsKeepLegacyCliAndRecovery(CancellationToken ct)
    {
        using var files = new OwnedFixture();
        var transport = new FakeTransport();
        var credentialReads = 0;
        var runtime = new MoarkClientRuntime(transport, _ => { credentialReads++; return CredentialCanary; });
        var timer = Stopwatch.StartNew();
        foreach (var kind in new[] { "image", "voice", "motion", "lipsync" })
        {
            CheckLimit(timer, ct);
            var request = files.Request(kind, kind);
            var record = files.Track(kind + "-record.json");
            var output = files.Track(kind + "-output.bin");
            files.Track(kind + "-record.json.lock");
            files.Track(".moark-budget.json");
            files.Track(".moark-budget.json.lock");
            var submit = SubmitArguments(kind, request, record, kind == "image" ? output : null);
            Assert(await MoarkClient.RunAsync("submit", submit, ct, runtime) == 0, "旧 CLI 提交未保持成功行为");
            var first = JsonFiles.Read<ProviderRecord>(record);
            var contract = MoarkRequestContract.ForKind(kind);
            Assert(first.Version == 1 && first.Model == contract.Model && first.Fingerprint.Length == 64 &&
                first.TaskId == (kind == "image" ? null : "offline-" + kind), "旧记录格式或供应商任务号改变");
            Assert(transport.LastEndpoint == contract.Endpoint && transport.LastModel == contract.Model,
                "既有用途没有映射到原模型与路径");
            if (kind != "image")
            {
                Assert(await MoarkClient.RunAsync("poll", PollArguments(record, output), ct, runtime) == 0, "旧异步任务未恢复下载");
                var queries = transport.StatusCount;
                var downloads = transport.DownloadCount;
                Assert(await MoarkClient.RunAsync("poll", PollArguments(record, output), ct, runtime) == 0 &&
                    transport.StatusCount == queries + 1 && transport.DownloadCount == downloads, "已校验输出没有复用");
            }
            var finished = JsonFiles.Read<ProviderRecord>(record);
            Assert(finished.Status == "success" && finished.LocalOutput == output && finished.OutputSha256 is not null &&
                File.Exists(output), "最终输出记录与旧 CLI 不兼容");
            var submissions = transport.SubmitCount;
            var reads = credentialReads;
            Assert(await MoarkClient.RunAsync("submit", submit, ct, runtime) == 0 && transport.SubmitCount == submissions &&
                credentialReads == reads, "已有记录不应重提或再次读取凭据");
            var duplicate = files.Track(kind + "-duplicate.json");
            files.Track(kind + "-duplicate.json.lock");
            var alternateOutput = kind == "image" ? files.Track(kind + "-alternate.bin") : null;
            Assert(await MoarkClient.RunAsync("submit", SubmitArguments(kind, request, duplicate, alternateOutput), ct, runtime) == 2 &&
                transport.SubmitCount == submissions && !File.Exists(duplicate), "跨记录同指纹请求绕过了旧预算台账锁");
            var persisted = File.ReadAllText(record) + File.ReadAllText(Path.Combine(files.Directory, ".moark-budget.json"));
            Assert(!persisted.Contains("CANARY", StringComparison.Ordinal) && !persisted.Contains("example.invalid", StringComparison.Ordinal) &&
                !persisted.Contains("signature", StringComparison.Ordinal), "兼容记录或预算台账含有原始响应信息");
        }
        Assert(transport.SubmitCount == 4 && credentialReads <= 16, "四类 CLI 兼容测试发生隐藏提交或无界读取");
        Assert(MoarkClient.CommittedCny(JsonFiles.Read<ProviderLedger>(Path.Combine(files.Directory, ".moark-budget.json"))) == 1m,
            "旧预算统计未使用四项白名单实付金额");
    }

    /// <summary>默认值、属性排序和同一上传句柄继续生成原有 SHA-256 指纹。</summary>
    private static async Task FingerprintsAndRequestHandlesRemainStable(CancellationToken ct)
    {
        using var files = new OwnedFixture();
        var imagePath = files.Write("image.json", """{"prompt":"offline image"}""");
        var alternate = files.Write("image-order.json", """{"response_format":"url","n":1,"prompt":"offline image","model":"qwen-image-2.0-pro"}""");
        using var first = await ProviderRequest.BuildAsync("image", imagePath, ct);
        using var second = await ProviderRequest.BuildAsync("image", alternate, ct);
        const string canonical = """{"kind":"image","payload":{"model":"qwen-image-2.0-pro","n":1,"prompt":"offline image","response_format":"url"}}""";
        Assert(first.Fingerprint == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))) &&
            first.Fingerprint == second.Fingerprint, "图片默认值或原有请求指纹算法发生不兼容变化");
        using var lipsync = await ProviderRequest.BuildAsync("lipsync", files.Request("lipsync", "stable"), ct);
        Assert(lipsync.Content.Headers.ContentType?.MediaType == "multipart/form-data", "口型请求未保持 multipart");
        var body = await lipsync.Content.ReadAsByteArrayAsync(ct);
        var multipart = Encoding.UTF8.GetString(body);
        Assert(body.Length < 4096 && multipart.Contains("video.mp4", StringComparison.Ordinal) &&
            multipart.Contains("audio.wav", StringComparison.Ordinal) && !multipart.Contains(files.Directory, StringComparison.Ordinal),
            "锁定上传句柄未保持固定文件名或泄漏本地绝对路径");
    }

    /// <summary>统一接口拒绝账号、模型、能力或估算越界，未知状态与取消契约不伪造生产能力。</summary>
    private static async Task UnifiedBindingAndUnknownStatesRemainSafe(CancellationToken ct)
    {
        using var files = new OwnedFixture();
        using var payload = await ProviderRequest.BuildAsync("voice", files.Request("voice", "binding"), ct);
        var record = Record(payload);
        var transport = new FakeTransport();
        var adapter = new MoarkProviderAdapter(transport, "offline-account", CredentialCanary, record, payload);
        var request = adapter.CreateSubmitRequest();
        foreach (var invalid in new[]
        {
            request with { AccountAlias = "other-account" }, request with { ModelId = "other-model" },
            request with { Capability = MpsCapabilityKind.VideoGeneration }, request with { InputFingerprint = new string('f', 64) },
            request with { EstimatedPrice = 2m }, request with { Currency = "USD" }
        })
        {
            ct.ThrowIfCancellationRequested();
            await ExpectAsync<InvalidDataException>(() => adapter.SubmitAsync(invalid, ct), "统一提交未拒绝锁定变化");
        }
        Assert(transport.SubmitCount == 0, "参数锁定失败仍触发了传输");
        IProviderTaskSubmissionAdapter submitAdapter = adapter;
        var submission = await submitAdapter.SubmitAsync(request, ct);
        Assert(submission.TaskId == "offline-voice" && submission.Status == ProviderAdapterTaskStatus.Running,
            "旧异步提交未投影到统一状态");
        await ExpectAsync<InvalidOperationException>(() => adapter.SubmitAsync(request, ct), "同一适配器允许重复提交");
        transport.StatusBody = """{"task_id":"offline-voice","status":"UNRECOGNIZED_CANARY"}""";
        IProviderTaskStatusAdapter statusAdapter = adapter;
        var state = await statusAdapter.GetStatusAsync(new("offline-account", submission.TaskId), ct);
        Assert(state.Status == ProviderAdapterTaskStatus.Unknown && state.ErrorCategory == ProviderAdapterErrorCategory.ResponseFormat,
            "未知供应商状态不应冒充运行中或成功");
        var beforeQueries = transport.StatusCount;
        var cancellation = await statusAdapter.CancelAsync(new("offline-account", submission.TaskId), ct);
        Assert(cancellation.Status == ProviderAdapterTaskStatus.Unknown && cancellation.ErrorCategory == ProviderAdapterErrorCategory.Unavailable &&
            transport.StatusCount == beforeQueries, "未经核实的取消端点触发了网络或伪造取消成功");
        await ExpectAsync<InvalidDataException>(() => statusAdapter.GetStatusAsync(new("other-account", submission.TaskId), ct), "跨账号恢复未拒绝");
        await ExpectAsync<InvalidDataException>(() => statusAdapter.GetStatusAsync(new("offline-account", "other-task"), ct), "跨任务恢复未拒绝");
        transport.StatusBody = """{"task_id":"different-task","status":"success","price":999,"currency":"USD"}""";
        await ExpectAsync<InvalidDataException>(() => adapter.QueryLegacyAsync(new("offline-account", submission.TaskId), true, ct),
            "配音输出查询没有校验原任务号");
        Assert(record.Price == 0.25m && record.Currency == "CNY", "错误任务响应污染了原任务费用");
        Assert(adapter.MapError(new(401, "RAW_CANARY")).Category == ProviderAdapterErrorCategory.Authentication &&
            adapter.MapError(new(403)).Category == ProviderAdapterErrorCategory.Permission &&
            adapter.MapError(new(429)).Category == ProviderAdapterErrorCategory.RateLimited &&
            adapter.MapError(new(500)).ProviderCode is null, "错误映射混淆权限或透传原始代码");
        using var image = await ProviderRequest.BuildAsync("image", files.Request("image", "unified"), ct);
        var imageRecord = Record(image);
        var imageAdapter = new MoarkProviderAdapter(new FakeTransport(), "offline-account", CredentialCanary, imageRecord, image);
        var imageResult = await imageAdapter.SubmitAsync(imageAdapter.CreateSubmitRequest(), ct);
        Assert(imageResult.Status == ProviderAdapterTaskStatus.Succeeded && imageResult.TaskId == "mps-image-" + image.Fingerprint &&
            imageRecord.TaskId is null, "同步图片的本地投影 ID 不应伪造供应商任务号");
    }

    /// <summary>提交取消、异常、无任务号与未知费用均保留预算占用，重开不再发送 POST。</summary>
    private static async Task AmbiguousSubmissionIsNeverRepeated(CancellationToken ct)
    {
        using var files = new OwnedFixture();
        var transport = new FakeTransport { SubmissionError = new OperationCanceledException() };
        var runtime = new MoarkClientRuntime(transport, _ => CredentialCanary);
        var request = files.Request("motion", "ambiguous");
        var record = files.Track("ambiguous.json");
        files.Track("ambiguous.json.lock");
        files.Track(".moark-budget.json");
        files.Track(".moark-budget.json.lock");
        var arguments = SubmitArguments("motion", request, record, null);
        Assert(await MoarkClient.RunAsync("submit", arguments, ct, runtime) == 2, "取消歧义没有进入恢复边界");
        var old = JsonFiles.Read<ProviderRecord>(record);
        Assert(old.TaskId is null && old.Status == "unknown" && old.Price is null && old.EstimateCny == 1m,
            "提交歧义被误报为未扣费或可自动重试");
        transport.SubmissionError = null;
        Assert(await MoarkClient.RunAsync("submit", arguments, ct, runtime) == 2 && transport.SubmitCount == 1,
            "重开歧义旧记录盲目重提了请求");
        Assert(await MoarkClient.RunAsync("poll", PollArguments(record, null), ct, runtime) == 2 && transport.StatusCount == 0,
            "没有任务号的旧记录不应猜测供应商任务");
        Assert(MoarkClient.CommittedCny(JsonFiles.Read<ProviderLedger>(Path.Combine(files.Directory, ".moark-budget.json"))) == 1m,
            "未知费用被误释放为零");
    }

    /// <summary>忽略取消的假传输只能延迟本例 120 毫秒；适配器及时退出并释放迟到文档。</summary>
    private static async Task NoncooperativeResponseIsBoundedAndDisposed(CancellationToken ct)
    {
        using var files = new OwnedFixture();
        using var payload = await ProviderRequest.BuildAsync("voice", files.Request("voice", "timeout"), ct);
        var record = Record(payload);
        var transport = new FakeTransport { SubmissionDelay = TimeSpan.FromMilliseconds(120) };
        var adapter = new MoarkProviderAdapter(transport, "offline-account", CredentialCanary, record, payload, TimeSpan.FromMilliseconds(15));
        var timer = Stopwatch.StartNew();
        try { await adapter.SubmitAsync(adapter.CreateSubmitRequest(), ct); throw new InvalidOperationException("非协作传输未超时"); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
        Assert(timer.Elapsed < TimeSpan.FromSeconds(1) && record.TaskId is null && record.Status == "unknown", "迟到响应改变了已返回的任务状态");
        await ExpectAsync<InvalidOperationException>(() => adapter.SubmitAsync(adapter.CreateSubmitRequest(), ct), "超时适配器允许再次 POST");
        await (transport.Operation ?? throw new InvalidOperationException("假传输没有记录操作")).WaitAsync(TimeSpan.FromSeconds(1), ct);
        // 让同步 continuation 完成，最多四次和一秒，避免泄漏本例的内存响应。
        var released = false;
        using var releaseLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        releaseLimit.CancelAfter(TimeSpan.FromSeconds(1));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            releaseLimit.Token.ThrowIfCancellationRequested();
            // RootElement 仅构造句柄；读取正文才会核对文档缓冲是否已释放。
            try { _ = transport.LastResponse!.Document.RootElement.GetRawText(); }
            catch (ObjectDisposedException) { released = true; break; }
            await Task.Delay(10, releaseLimit.Token);
        }
        Assert(released && transport.SubmitCount == 1, "非协作迟到响应没有被释放或发生隐藏重提");
    }

    /// <summary>构造旧 CLI 参数，不添加或改变公开选项。</summary>
    private static Arguments SubmitArguments(string kind, string request, string record, string? output)
    {
        string[] values = ["--kind", kind, "--request", request, "--record", record, "--budget-cny", "100", "--estimate-cny", "1"];
        return new Arguments(output is null ? values : [.. values, "--download", output]);
    }

    /// <summary>离线查询只允许一次和两秒，不会执行等待循环。</summary>
    private static Arguments PollArguments(string record, string? output)
    {
        string[] values = ["--record", record, "--max-attempts", "1", "--timeout-seconds", "2", "--interval-seconds", "1"];
        return new Arguments(output is null ? values : [.. values, "--download", output]);
    }

    /// <summary>用已锁定请求构造与旧 JSON 一致的白名单记录。</summary>
    private static ProviderRecord Record(ProviderRequest request) => new()
    { Kind = request.Kind, Model = request.Model, Fingerprint = request.Fingerprint, EstimateCny = 1m };

    /// <summary>断言固定异常类型，不向失败日志加入供应商正文。</summary>
    private static async Task ExpectAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    /// <summary>每个固定小批次有取消和五秒墙钟边界。</summary>
    private static void CheckLimit(Stopwatch timer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Moark 本地测试批次超过五秒。");
    }

    /// <summary>统一固定断言消息。</summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>假 transport 只创建内存响应与本地输出，每实例最多 32 次操作。</summary>
    private sealed class FakeTransport : IMoarkTransport
    {
        private int calls;
        public int SubmitCount { get; private set; }
        public int StatusCount { get; private set; }
        public int DownloadCount { get; private set; }
        public string? LastEndpoint { get; private set; }
        public string? LastModel { get; private set; }
        public string? StatusBody { get; set; }
        public Exception? SubmissionError { get; set; }
        public TimeSpan SubmissionDelay { get; init; }
        public Task<ProviderResponse>? Operation { get; private set; }
        public ProviderResponse? LastResponse { get; private set; }

        /// <summary>保存唯一假传输任务，方便本例回收等待。</summary>
        public Task<ProviderResponse> SubmitAsync(ProviderRequest request, string credential, CancellationToken cancellationToken)
        {
            CheckCall(cancellationToken);
            SubmitCount++;
            LastEndpoint = request.Endpoint;
            LastModel = request.Model;
            Assert(credential == CredentialCanary, "假凭据没有通过注入路径");
            return Operation = SubmitCoreAsync(request, cancellationToken);
        }

        /// <summary>无任何网络；可模拟取消歧义与有限的非协作迟到响应。</summary>
        private async Task<ProviderResponse> SubmitCoreAsync(ProviderRequest request, CancellationToken cancellationToken)
        {
            if (SubmissionError is not null) throw SubmissionError;
            if (SubmissionDelay > TimeSpan.Zero)
            {
                if (SubmissionDelay > TimeSpan.FromMilliseconds(250)) throw new InvalidOperationException("假传输延迟越界。");
                await Task.Delay(SubmissionDelay, CancellationToken.None);
            }
            else cancellationToken.ThrowIfCancellationRequested();
            var body = request.Kind == "image"
                ? JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(new byte[32]) } }, price = 0.25m, currency = "CNY", secret = CredentialCanary })
                : JsonSerializer.Serialize(new { task_id = "offline-" + request.Kind, status = "queued", price = 0.25m, currency = "CNY", secret = CredentialCanary });
            return LastResponse = Response(body);
        }

        /// <summary>返回仓库兼容结构，配音输出仍走旧 /get 分支。</summary>
        public Task<ProviderResponse> GetTaskAsync(string taskId, string credential, bool outputOnly, CancellationToken cancellationToken)
        {
            CheckCall(cancellationToken);
            StatusCount++;
            Assert(credential == CredentialCanary, "查询没有使用假凭据");
            var body = StatusBody ?? (outputOnly
                ? JsonSerializer.Serialize(new { task_id = taskId, output = new { result = new[] { new { audio_urls = new[] { new { url = "https://example.invalid/output" } } } } } })
                : taskId == "offline-voice"
                    ? JsonSerializer.Serialize(new { task_id = taskId, status = "success", price = 0.25m, currency = "CNY" })
                    : JsonSerializer.Serialize(new { task_id = taskId, status = "success", price = 0.25m, currency = "CNY", output = new { file_url = "https://example.invalid/output" } }));
            return Task.FromResult(Response(body));
        }

        /// <summary>只写测试本地字节，拒绝除固定假地址以外的输入。</summary>
        public Task<string> DownloadAsync(string url, string destination, CancellationToken cancellationToken)
        {
            CheckCall(cancellationToken);
            DownloadCount++;
            Assert(url == "https://example.invalid/output", "下载偏离固定假地址");
            return ProviderTransport.SaveBase64Async(Convert.ToBase64String(new byte[32]), destination, cancellationToken);
        }

        /// <summary>同步图片只使用本地原子保存方法。</summary>
        public Task<string> SaveBase64Async(string encoded, string destination, CancellationToken cancellationToken)
        {
            CheckCall(cancellationToken);
            return ProviderTransport.SaveBase64Async(encoded, destination, cancellationToken);
        }

        /// <summary>拒绝隐藏重试造成的无界假请求。</summary>
        private void CheckCall(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++calls > 32) throw new InvalidOperationException("假传输超过32次操作上限。");
        }

        /// <summary>创建有限、无网络的 JSON 文档。</summary>
        private static ProviderResponse Response(string body) => new() { StatusCode = 200, Document = JsonDocument.Parse(body) };
    }

    /// <summary>跟踪每个本例创建文件，只清理已核对直属路径，不递归扫描目录。</summary>
    private sealed class OwnedFixture : IDisposable
    {
        private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        public string Directory { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mps-moark-offline-" + Guid.NewGuid().ToString("N")));

        /// <summary>创建单个独占临时目录，不启动进程。</summary>
        public OwnedFixture() => System.IO.Directory.CreateDirectory(Directory);

        /// <summary>登记明确命名、直属本例目录的文件，最多64项。</summary>
        public string Track(string name)
        {
            var path = Path.GetFullPath(Path.Combine(Directory, name));
            if (Path.GetDirectoryName(path) != Directory || paths.Count >= 64 && !paths.Contains(path))
                throw new InvalidOperationException("测试临时路径越界或超过64项。");
            paths.Add(path);
            return path;
        }

        /// <summary>写入本地请求，不保存任何真实用户素材。</summary>
        public string Write(string name, string content)
        {
            var path = Track(name);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>创建四类固定小样例；口型只用本例32字节音视频头部。</summary>
        public string Request(string kind, string suffix)
        {
            if (kind == "lipsync")
            {
                File.WriteAllBytes(Track("video.mp4"), new byte[32]);
                var wave = new byte[32];
                "RIFF"u8.CopyTo(wave); "WAVE"u8.CopyTo(wave.AsSpan(8));
                File.WriteAllBytes(Track("audio.wav"), wave);
            }
            var content = kind switch
            {
                "image" => JsonSerializer.Serialize(new { prompt = "offline image " + suffix }),
                "voice" => JsonSerializer.Serialize(new { inputs = new[] { new { prompt = "offline voice " + suffix, speaker = "offline" } } }),
                "motion" => JsonSerializer.Serialize(new { prompt = "offline motion " + suffix, duration = 1 }),
                "lipsync" => """{"ref_video":"video.mp4","ref_audio":"audio.wav"}""",
                _ => throw new InvalidOperationException("测试用途无效。")
            };
            return Write(kind + "-" + suffix + ".json", content);
        }

        /// <summary>最多64项及五秒清理，只删除明确登记且归属本例的文件。</summary>
        public void Dispose()
        {
            var timer = Stopwatch.StartNew();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var path in paths)
            {
                cleanup.Token.ThrowIfCancellationRequested();
                if (timer.Elapsed > TimeSpan.FromSeconds(5) || Path.GetDirectoryName(Path.GetFullPath(path)) != Directory)
                    throw new InvalidOperationException("测试临时清理边界无效。");
                if (File.Exists(path)) File.Delete(path);
            }
            System.IO.Directory.Delete(Directory, recursive: false);
        }
    }
}
