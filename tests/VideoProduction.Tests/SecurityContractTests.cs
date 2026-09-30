using System.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>通过内存传输和真实记录序列化验证安全契约，不访问凭据库或网络。</summary>
internal static class SecurityContractTests
{
    /// <summary>在调用方总时限内验证外发边界及实际 Moark 白名单提取路径。</summary>
    internal static async Task RunAsync(CancellationToken ct)
    {
        await OutboundRejectsBeforeSendingAsync(ct);
        ResponsePersistenceExcludesSensitiveData(ct);
    }

    /// <summary>覆盖每个授权维度、素材与源码行范围、路径歧义和取消，拒绝时必须零发送。</summary>
    private static async Task OutboundRejectsBeforeSendingAsync(CancellationToken ct)
    {
        var now = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var asset = new OutboundDataReference(OutboundDataKind.Asset, "assets/source/demo.png");
        var source = new OutboundDataReference(OutboundDataKind.Source, "source/Program.cs", 10, 20);
        var authorization = new ProjectOutboundAuthorization("grant-a", "project-a", "moark", "account-a", "model-a",
            "vision", "feature-evidence", 10, now.AddMinutes(5), [asset, source]);
        var intent = new ProjectOutboundIntent("project-a", "moark", "account-a", "model-a", "vision", "feature-evidence",
            4, 1, [asset, source with { StartLine = 12, EndLine = 18 }]);
        var allowed = new MemoryTransport();
        await ProjectOutboundGate.SendAsync(authorization, intent, allowed, now, ct);
        Assert(allowed.SendCount == 1 && ReferenceEquals(allowed.LastIntent, intent), "有效交集必须按原意图仅发送一次");

        var cases = new (string Name, ProjectOutboundAuthorization? Authorization, ProjectOutboundIntent Intent)[]
        {
            ("默认拒绝", null, intent),
            ("项目", authorization, intent with { ProjectId = "project-b" }),
            ("提供商", authorization, intent with { Provider = "other" }),
            ("账号", authorization, intent with { AccountAlias = "account-b" }),
            ("锁定模型", authorization, intent with { Model = "model-b" }),
            ("能力", authorization, intent with { Capability = "video" }),
            ("用途", authorization, intent with { Purpose = "advertising" }),
            ("到期", authorization with { ExpiresAt = now }, intent),
            ("预算", authorization, intent with { CommittedCny = 10 }),
            ("负估算", authorization, intent with { EstimateCny = -1 }),
            ("缺失引用", authorization, intent with { References = [] }),
            ("缺失白名单", authorization with { Scopes = [] }, intent),
            ("65条引用", authorization, intent with { References = Enumerable.Repeat(asset, 65).ToImmutableArray() }),
            ("65条白名单", authorization with { Scopes = Enumerable.Repeat(asset, 65).ToImmutableArray() }, intent),
            ("其他素材", authorization, intent with { References = [asset with { ProjectPath = "assets/source/private.png" }] }),
            ("源码前越界", authorization, intent with { References = [source with { StartLine = 9 }] }),
            ("源码后越界", authorization, intent with { References = [source with { EndLine = 21 }] }),
            ("源码整文件", authorization, intent with { References = [source with { StartLine = null, EndLine = null }] }),
            ("引用类型", authorization, intent with { References = [asset with { Kind = OutboundDataKind.Text }] }),
            ("目录穿越", authorization, intent with { References = [asset with { ProjectPath = "assets/source/../private.png" }] }),
            ("编码穿越", authorization, intent with { References = [asset with { ProjectPath = "assets/source/%2e%2e/private.png" }] }),
            ("绝对路径", authorization, intent with { References = [asset with { ProjectPath = "C:/private.png" }] }),
            ("反斜线", authorization, intent with { References = [asset with { ProjectPath = @"assets\source\demo.png" }] }),
            ("数据流", authorization, intent with { References = [asset with { ProjectPath = "assets/source/demo.png:secret" }] }),
            ("尾随空格", authorization, intent with { References = [asset with { ProjectPath = "assets/source/demo.png " }] }),
            ("设备名", authorization, intent with { References = [asset with { ProjectPath = "assets/source/NUL.png" }] }),
            ("无效授权路径", authorization with { Scopes = [asset, source, asset with { ProjectPath = "../private.png" }] }, intent)
        };
        var timer = Stopwatch.StartNew();
        Assert(cases.Length <= 32, "负例批次数不得超过32");
        foreach (var item in cases)
        {
            CheckLimit(timer, ct);
            var transport = new MemoryTransport();
            var rejected = false;
            try { await ProjectOutboundGate.SendAsync(item.Authorization, item.Intent, transport, now, ct); }
            catch (UnauthorizedAccessException) { rejected = true; }
            Assert(rejected && transport.SendCount == 0, item.Name + "必须在发送前拒绝");
        }
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancelled.Cancel();
        var cancelledTransport = new MemoryTransport();
        var stopped = false;
        try { await ProjectOutboundGate.SendAsync(authorization, intent, cancelledTransport, now, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Assert(stopped && cancelledTransport.SendCount == 0, "预先取消必须零发送");

        using var duringCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cooperative = new CancellingTransport(duringCancellation);
        var interrupted = false;
        try { await ProjectOutboundGate.SendAsync(authorization, intent, cooperative, now, duringCancellation.Token); }
        catch (OperationCanceledException) { interrupted = true; }
        // Gate 的等待与传输清理可同时观察到取消；显式等候本例传输退出，禁止遗留后台任务。
        if (cooperative.Operation is not null)
        {
            try { await cooperative.Operation.WaitAsync(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) when (duringCancellation.IsCancellationRequested) { }
        }
        Assert(interrupted && cooperative.SendCount == 1 && cooperative.Exited && cooperative.Operation?.IsCompleted == true,
            "发送中取消必须使协作式传输退出且无残留任务");
        Console.WriteLine($"外发授权契约通过：{cases.Length} 个拒绝负例、1 个合法交集、2 个取消负例；离线发送且无残留任务。");
    }

    /// <summary>恶意响应通过生产共用策略后写入任务、预算台账及实际控制台输出，检查敏感信息缺席。</summary>
    private static void ResponsePersistenceExcludesSensitiveData(CancellationToken ct)
    {
        const string credential = "sk_SECRET_KEY_CANARY";
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mps-security-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var recordPath = Path.Combine(directory, "record.json");
        var ledgerPath = Path.Combine(directory, "ledger.json");
        var responses = new (string Body, int Http, bool Poll, string Status, string? TaskId, decimal? Price, string? Currency, bool Mismatch)[]
        {
            ("""{"task_id":"safe_task_1","status":"queued","price":1.25,"currency":"CNY","api_key":"sk_SECRET_KEY_CANARY","error":{"message":"RAW_ERROR_CANARY"},"prompt":"PROMPT_CANARY","output":{"url":"https://example.invalid/video?signature=SIGNED_URL_CANARY"}}""", 200, false, "pending", "safe_task_1", 1.25m, "CNY", false),
            ("""{"task_id":"sk_SECRET_KEY_CANARY","status":"RAW_STATUS_CANARY","price":-1,"currency":"RAW_CURRENCY_CANARY","raw_response":"RAW_RESPONSE_CANARY"}""", 200, false, "unknown", null, null, null, false),
            ("""{"task_id":"https://example.invalid/?signature=SIGNED_URL_CANARY","status":"queued","message":"RAW_ERROR_CANARY"}""", 200, false, "unknown", null, null, null, false),
            ("""{"task_id":"safe_task_1","status":"failed","price":2,"currency":"RAW_CURRENCY_CANARY","error":"RAW_ERROR_CANARY","secret":"sk_SECRET_KEY_CANARY"}""", 200, true, "failure", "safe_task_1", 2, "UNKNOWN", false),
            ("""{"task_id":"safe_task_1","status":"RAW_STATUS_CANARY","error":"RAW_ERROR_CANARY","secret":"sk_SECRET_KEY_CANARY"}""", 500, false, "unknown", "safe_task_1", null, null, false),
            ("""{"error":"RAW_ERROR_CANARY","secret":"sk_SECRET_KEY_CANARY","url":"https://example.invalid/?signature=SIGNED_URL_CANARY"}""", 403, true, "pending", "safe_task_1", null, null, false),
            ("""{"task_id":"different_task","status":"success","price":999,"currency":"USD","error":"RAW_ERROR_CANARY"}""", 200, true, "pending", "safe_task_1", 0.5m, "CNY", true)
        };
        var timer = Stopwatch.StartNew();
        try
        {
            Assert(responses.Length <= 8, "响应批次数不得超过8");
            foreach (var item in responses)
            {
                CheckLimit(timer, ct);
                using var response = new ProviderResponse { StatusCode = item.Http, Document = JsonDocument.Parse(item.Body) };
                var record = new ProviderRecord
                {
                    Kind = "voice", Model = "Qwen3-TTS", Fingerprint = new string('a', 64), EstimateCny = 3,
                    TaskId = item.Poll ? "safe_task_1" : null, Status = item.Poll ? "pending" : "unknown",
                    Price = item.Mismatch ? 0.5m : null, Currency = item.Mismatch ? "CNY" : null
                };
                if (item.Mismatch)
                {
                    var rejected = false;
                    try { ProviderResponsePolicy.ApplyPoll(record, response, credential); }
                    catch (InvalidDataException error) { rejected = error.Message == "任务查询响应编号不匹配。"; }
                    Assert(rejected, "轮询任务号不匹配必须拒绝，且错误不能回显响应");
                }
                else if (item.Poll) ProviderResponsePolicy.ApplyPoll(record, response, credential);
                else ProviderResponsePolicy.ApplySubmission(record, response, credential);
                Assert(record.Status == item.Status && record.TaskId == item.TaskId && record.Price == item.Price &&
                    record.Currency == item.Currency, "生产响应策略必须保留正确的任务状态与费用");
                JsonFiles.Write(recordPath, record);
                JsonFiles.Write(ledgerPath, new ProviderLedger { Entries = [new ProviderLedgerEntry { RecordPath = recordPath, Record = record }] });
                using var output = new StringWriter();
                using var errors = new StringWriter();
                var originalOutput = Console.Out;
                var originalError = Console.Error;
                try
                {
                    Console.SetOut(output);
                    Console.SetError(errors);
                    MoarkClient.Print(record, "离线响应验证。");
                }
                finally { Console.SetOut(originalOutput); Console.SetError(originalError); }
                var persisted = File.ReadAllText(recordPath) + File.ReadAllText(ledgerPath) + output + errors.ToString();
                Assert(!persisted.Contains("CANARY", StringComparison.Ordinal) && !persisted.Contains("signature=", StringComparison.Ordinal) &&
                    !persisted.Contains("example.invalid", StringComparison.Ordinal) && !persisted.Contains("raw_response", StringComparison.Ordinal),
                    "任务、台账或实际控制台输出泄漏了供应商敏感字段");
                var roundTrip = JsonFiles.Read<ProviderRecord>(recordPath);
                Assert(roundTrip.Status == item.Status && roundTrip.TaskId == item.TaskId && roundTrip.Price == item.Price,
                    "任务记录应能按相同白名单字段重新读取");
            }
        }
        finally
        {
            // 只删本例明确创建的两个文件；不递归删除目录，未知对象保留以暴露清理问题。
            DeleteOwnedFile(directory, recordPath);
            DeleteOwnedFile(directory, ledgerPath);
            Directory.Delete(directory, recursive: false);
        }
        Console.WriteLine($"响应持久化通过：{responses.Length} 个响应场景，任务记录、共享台账、实际控制台输出均无敏感字段；临时目录已清理。");
    }

    /// <summary>清理前解析绝对路径并确认文件直属本例独占目录。</summary>
    private static void DeleteOwnedFile(string directory, string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("测试临时路径不属于本例。");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>每批最多15秒并支持测试入口取消。</summary>
    private static void CheckLimit(Stopwatch timer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("安全契约测试批次超过15秒。");
    }

    /// <summary>统一断言，避免将完整响应加入失败信息。</summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>只记录调用次数和不可变意图，不创建任何 HTTP 请求。</summary>
    private sealed class MemoryTransport : IProjectOutboundTransport
    {
        public int SendCount { get; private set; }
        public ProjectOutboundIntent? LastIntent { get; private set; }

        /// <summary>观察真正越过校验边界的调用，并遵守取消请求。</summary>
        public Task SendAsync(ProjectOutboundIntent intent, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SendCount++;
            LastIntent = intent;
            return Task.CompletedTask;
        }
    }

    /// <summary>在已开始的模拟发送中触发取消，并暴露清理完成状态。</summary>
    private sealed class CancellingTransport(CancellationTokenSource cancellation) : IProjectOutboundTransport
    {
        public int SendCount { get; private set; }
        public bool Exited { get; private set; }
        public Task? Operation { get; private set; }

        /// <summary>保存本例唯一传输任务，便于测试确认其确实结束。</summary>
        public Task SendAsync(ProjectOutboundIntent intent, CancellationToken ct)
        {
            Operation = SendCoreAsync(ct);
            return Operation;
        }

        /// <summary>最多等待2秒，在发送开始25毫秒后触发取消，任何退出路径均记录完成。</summary>
        private async Task SendCoreAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SendCount++;
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            finally { Exited = true; }
        }
    }
}
