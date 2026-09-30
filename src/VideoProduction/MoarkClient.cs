using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace VideoProduction;

/// <summary>提供可恢复的模力方舟提交和轮询命令，预算以记录所在目录为共同范围。</summary>
public static class MoarkClient
{
    private const string LedgerName = ".moark-budget.json";

    /// <summary>执行 submit 或 poll；返回0成功、2未完成或需人工核对、3明确失败。</summary>
    public static Task<int> RunAsync(string action, Arguments args, CancellationToken ct) => action switch
    {
        "submit" => SubmitAsync(args, ct),
        "poll" => PollAsync(args, ct),
        _ => throw new ArgumentException("moark 只支持 submit 和 poll。")
    };

    /// <summary>先锁记录、校验预算并预留未知状态，然后仅发送一次付费请求。</summary>
    private static async Task<int> SubmitAsync(Arguments args, CancellationToken ct)
    {
        args.Allow("kind", "request", "record", "budget-cny", "estimate-cny", "credential-target", "download");
        var kind = args.Required("kind");
        var recordPath = RecordPath(args.Required("record"));
        var budget = Money(args.Required("budget-cny"));
        var estimate = Money(args.Required("estimate-cny"));
        if (estimate > budget) throw new ArgumentException("本次估算费用超过目录累计预算。");
        var download = args.Has("download") ? Path.GetFullPath(args.Required("download")) : null;
        if (kind == "image" && download is null) throw new ArgumentException("同步图片必须指定 --download 本地输出路径。");
        if (kind != "image" && download is not null) throw new ArgumentException("异步任务请在 poll 命令使用 --download。");
        if (download is not null && (File.Exists(download) || Directory.Exists(download))) throw new IOException("输出目标已存在，拒绝覆盖。");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(480));
        using var recordLock = await ProviderLocks.AcquireAsync(recordPath + ".lock", limit.Token);
        // 先拿记录锁，再读取和哈希请求文件，避免两个进程同时通过去重后各自付费提交。
        using var payload = await ProviderRequest.BuildAsync(kind, args.Required("request"), limit.Token);
        if (File.Exists(recordPath))
        {
            var previous = ReadRecord(recordPath);
            if (previous.Fingerprint != payload.Fingerprint) throw new InvalidDataException("已有记录对应不同请求，拒绝覆盖。");
            Print(previous, "已有记录，未重新提交；可使用 poll 查询任务。");
            // 图片同步接口没有任务编号；只有本地输出存在且哈希已记录才算完成。
            return previous.Status == "success" && (kind != "image" || previous.LocalOutput is not null && previous.OutputSha256 is not null && File.Exists(previous.LocalOutput)) ? 0 : 2;
        }
        var credential = CredentialStore.Read(args.Optional("credential-target") ?? CredentialStore.DefaultTarget);
        try
        {
            var record = new ProviderRecord { Kind = kind, Model = payload.Model, Fingerprint = payload.Fingerprint, EstimateCny = estimate };
            if (!await ReserveAsync(recordPath, record, budget, limit.Token)) return 2;
            // 从此刻开始，崩溃、超时及取消均按可能已扣费处理，禁止自动重新 POST。
            ProviderResponse response;
            try { response = await ProviderTransport.SubmitAsync(payload, credential, limit.Token); }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
            {
                record.Status = "unknown";
                record.ErrorCode = ct.IsCancellationRequested ? "submission_cancelled_unknown" : "submission_outcome_unknown";
                await PersistAfterSubmissionAsync(recordPath, record);
                Console.Error.WriteLine("提交结果存在歧义，记录已保留为 unknown；请核对供应商任务，禁止重发。");
                return 2;
            }
            using (response)
            {
                ProviderResponsePolicy.ApplySubmission(record, response, credential);
                if (!response.IsSuccess)
                {
                    // 4xx 为明确拒绝；5xx 或重定向仍可能已创建付费任务，继续占用预算。
                    await PersistAfterSubmissionAsync(recordPath, record);
                    Print(record, "提交未正常完成，未自动重试。");
                    return record.Status == "rejected" ? 3 : 2;
                }
                if (kind == "image")
                {
                    var root = response.Document.RootElement;
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1)
                    {
                        record.ErrorCode = "image_response_unrecognized";
                        await PersistAfterSubmissionAsync(recordPath, record);
                        return 2;
                    }
                    record.Status = "success";
                    await PersistAfterSubmissionAsync(recordPath, record);
                    try
                    {
                        record.OutputSha256 = await SaveOutputAsync(data[0], download!, limit.Token);
                        record.LocalOutput = download;
                    }
                    catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or FormatException or InvalidDataException)
                    {
                        record.ErrorCode = "image_output_not_saved";
                        await PersistAfterSubmissionAsync(recordPath, record);
                        Console.Error.WriteLine("图片已生成，但本地保存失败；同步接口没有可恢复任务编号，请勿重新提交同一请求。");
                        return 2;
                    }
                }
                await PersistAfterSubmissionAsync(recordPath, record);
                Print(record, kind == "image" ? "同步图片已保存。" : "任务已记录；使用 poll 继续查询。");
                return record.Status is "failure" or "cancelled" ? 3 : record.Status == "unknown" ? 2 : 0;
            }
        }
        finally { credential = string.Empty; }
    }

    /// <summary>在次数与墙钟双重边界内查询已有任务，超时保留 pending 状态便于恢复。</summary>
    private static async Task<int> PollAsync(Arguments args, CancellationToken ct)
    {
        args.Allow("record", "download", "credential-target", "max-attempts", "timeout-seconds", "interval-seconds");
        var path = RecordPath(args.Required("record"));
        var attempts = args.Int("max-attempts", 60);
        var seconds = args.Int("timeout-seconds", 600);
        var interval = args.Number("interval-seconds", 10);
        if (attempts is < 1 or > 300 || seconds is < 1 or > 1800 || !double.IsFinite(interval) || interval is < 1 or > 60)
            throw new ArgumentException("轮询要求 attempts=1至300、timeout=1至1800秒、interval=1至60秒。");
        var download = args.Has("download") ? Path.GetFullPath(args.Required("download")) : null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var recordLock = await ProviderLocks.AcquireAsync(path + ".lock", deadline.Token);
        var record = ReadRecord(path);
        if (record.Kind == "image")
        {
            Print(record, "同步图片没有异步轮询任务；请检查记录中的本地输出。");
            return record.Status == "success" && record.LocalOutput is not null && File.Exists(record.LocalOutput) ? 0 : 2;
        }
        if (record.TaskId is null) { Print(record, "记录没有任务编号，需要在供应商控制台核对；不可重新提交。"); return 2; }
        var credential = CredentialStore.Read(args.Optional("credential-target") ?? CredentialStore.DefaultTarget);
        try
        {
            for (var index = 0; index < attempts; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var response = await ProviderTransport.GetTaskAsync(record.TaskId, credential, false, deadline.Token);
                ProviderResponsePolicy.ApplyPoll(record, response, credential);
                if (!response.IsSuccess)
                {
                    await PersistAfterSubmissionAsync(path, record);
                    Console.Error.WriteLine($"查询 HTTP {response.StatusCode}，保留原任务状态。");
                    return 2;
                }
                await PersistAsync(path, record, deadline.Token);
                Console.Error.WriteLine($"Moark {record.Kind}: {record.Status}，查询 {index + 1}/{attempts}。");
                if (record.Status == "success")
                {
                    if (download is not null)
                    {
                        if (await ExistingOutputAsync(record, download, deadline.Token)) { Print(record, "复用已校验的本地输出。"); return 0; }
                        // 本次 GET 的成功响应提供新签名地址；不会读取旧记录中的远程地址。
                        var output = response.Document.RootElement;
                        if (output.TryGetProperty("output", out var nested)) output = nested;
                        if (record.Kind == "voice" && FindOutputUrl(output) is null)
                        {
                            using var result = await ProviderTransport.GetTaskAsync(record.TaskId, credential, true, deadline.Token);
                            if (!result.IsSuccess) throw new IOException("成功配音任务的输出查询失败。");
                            record.OutputSha256 = await SaveOutputAsync(result.Document.RootElement, download, deadline.Token);
                        }
                        else record.OutputSha256 = await SaveOutputAsync(output, download, deadline.Token);
                        record.LocalOutput = download;
                        await PersistAsync(path, record, deadline.Token);
                    }
                    Print(record, "任务成功。");
                    return 0;
                }
                if (record.Status is "failure" or "cancelled" or "rejected") { Print(record, "任务已终止；null 费用继续预留估算额。"); return 3; }
                if (index + 1 < attempts) await Task.Delay(TimeSpan.FromSeconds(interval), deadline.Token);
            }
            record.ErrorCode = "poll_attempt_limit";
            await PersistAfterSubmissionAsync(path, record);
            Print(record, "已达到查询次数上限；再次 poll 可恢复，不要重复 submit。");
            return 2;
        }
        catch (OperationCanceledException)
        {
            record.ErrorCode = ct.IsCancellationRequested ? "poll_cancelled" : "poll_timeout";
            await PersistAfterSubmissionAsync(path, record);
            Print(record, "查询已停止，原任务及预算预留保留。");
            return 2;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException)
        {
            record.ErrorCode = "poll_or_download_failed";
            await PersistAfterSubmissionAsync(path, record);
            Console.Error.WriteLine("任务查询或下载未完成，已保留状态；可再次 poll 恢复。");
            return 2;
        }
        finally { credential = string.Empty; }
    }

    /// <summary>在共享台账锁内执行跨记录指纹去重、累计预算检查及预留。</summary>
    private static async Task<bool> ReserveAsync(string path, ProviderRecord record, decimal budget, CancellationToken ct)
    {
        var ledgerPath = Path.Combine(Path.GetDirectoryName(path)!, LedgerName);
        using var ledgerLock = await ProviderLocks.AcquireAsync(ledgerPath + ".lock", ct);
        var ledger = ReadLedger(ledgerPath);
        var duplicate = ledger.Entries.FirstOrDefault(e => e.Record.Fingerprint == record.Fingerprint || string.Equals(e.RecordPath, path, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            Print(duplicate.Record, "共享台账已有同一请求或记录名，未进行付费提交。");
            return false;
        }
        var committed = CommittedCny(ledger);
        if (committed + record.EstimateCny > budget) throw new InvalidOperationException($"目录已占用 {committed} CNY，本次估算 {record.EstimateCny} CNY 超出累计预算 {budget} CNY。");
        if (ledger.Entries.Count >= 10000) throw new InvalidDataException("目录台账已达到10000项上限。");
        ledger.Entries.Add(new ProviderLedgerEntry { RecordPath = path, Record = record });
        // 先持久化台账再写任务记录；任何写入异常都在 POST 之前终止并保留预算占用。
        JsonFiles.Write(ledgerPath, ledger);
        JsonFiles.Write(path, record);
        Console.Error.WriteLine($"累计费用及预留 {committed + record.EstimateCny} / {budget} CNY；即将提交一次 {record.Kind} 请求。");
        return true;
    }

    /// <summary>更新白名单任务与共享台账；预算始终使用实际费用或尚未结算的估算额。</summary>
    private static async Task PersistAsync(string path, ProviderRecord record, CancellationToken ct)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        var ledgerPath = Path.Combine(Path.GetDirectoryName(path)!, LedgerName);
        using var ledgerLock = await ProviderLocks.AcquireAsync(ledgerPath + ".lock", ct);
        var ledger = ReadLedger(ledgerPath);
        var entry = ledger.Entries.SingleOrDefault(e => string.Equals(e.RecordPath, path, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Record.Fingerprint != record.Fingerprint) throw new InvalidDataException("共享预算台账与任务记录不一致，拒绝更新。");
        // 任务编号先落盘，避免台账写入失败使已接收任务无法再查询。
        JsonFiles.Write(path, record);
        entry.Record = record;
        JsonFiles.Write(ledgerPath, ledger);
    }

    /// <summary>取消后仍给状态记录最多16秒写入机会，避免将已提交任务误判为未提交。</summary>
    private static async Task PersistAfterSubmissionAsync(string path, ProviderRecord record)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        await PersistAsync(path, record, cleanup.Token);
    }

    /// <summary>读取并验证台账，最多10000项、5秒，不扫描目录内其他文件。</summary>
    private static ProviderLedger ReadLedger(string path)
    {
        if (!File.Exists(path)) return new ProviderLedger();
        var ledger = JsonFiles.Read<ProviderLedger>(path);
        if (ledger.Version != 1 || ledger.Entries.Count > 10000) throw new InvalidDataException("共享预算台账版本或大小无效。");
        var timer = Stopwatch.StartNew();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ledger.Entries)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("预算台账校验超过5秒。");
            ValidateRecord(entry.Record);
            if (!Path.IsPathFullyQualified(entry.RecordPath) || !paths.Add(entry.RecordPath)) throw new InvalidDataException("预算台账记录路径无效或重复。");
        }
        return ledger;
    }

    /// <summary>累计已知人民币实际费用；未返回费用的失败任务也不能擅自按零计算。</summary>
    public static decimal CommittedCny(ProviderLedger ledger)
    {
        if (ledger.Entries.Count > 10000) throw new InvalidDataException("预算台账超过10000项。");
        decimal total = 0;
        var timer = Stopwatch.StartNew();
        foreach (var entry in ledger.Entries)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("预算累计超过5秒。");
            ValidateRecord(entry.Record);
            if (entry.Record.Price is not null && entry.Record.Currency is not (null or "CNY"))
                throw new InvalidDataException("台账存在非人民币费用，需要核对后再提交。");
            total += entry.Record.Price is not null && entry.Record.Currency == "CNY" ? entry.Record.Price.Value : entry.Record.EstimateCny;
        }
        return total;
    }

    /// <summary>读取供应商任务记录，只允许已知用途、状态、哈希及安全任务编号。</summary>
    private static ProviderRecord ReadRecord(string path)
    {
        var record = JsonFiles.Read<ProviderRecord>(path);
        ValidateRecord(record);
        return record;
    }

    /// <summary>防止手工损坏记录参与预算或输出未经校验的供应商字段。</summary>
    private static void ValidateRecord(ProviderRecord record)
    {
        var expected = record.Kind switch { "image" => "qwen-image-2.0-pro", "voice" => "Qwen3-TTS", "motion" => "ViduQ2-Turbo", "lipsync" => "Duix-Avatar", _ => null };
        if (record.Version != 1 || record.Provider != "moark" || expected is null || record.Model != expected ||
            record.Fingerprint.Length != 64 || record.Fingerprint.Any(c => !char.IsAsciiHexDigit(c)) ||
            record.Status is not ("unknown" or "accepted" or "pending" or "success" or "failure" or "rejected" or "cancelled") ||
            record.EstimateCny is <= 0 or > 1000000 || record.Price is < 0 or > 1000000 ||
            record.Currency is not (null or "CNY" or "USD" or "UNKNOWN")) throw new InvalidDataException("供应商任务记录无效。");
        if (record.TaskId is not null) ProviderTransport.ValidateTaskId(record.TaskId);
    }

    /// <summary>只按已经实测的输出结构取首个媒体地址，不递归遍历任意供应商字段。</summary>
    private static string? FindOutputUrl(JsonElement output)
    {
        var direct = Text(output, "file_url") ?? Text(output, "url");
        if (direct is not null) return direct;
        if (output.ValueKind != JsonValueKind.Object) return null;
        if (output.TryGetProperty("output", out var nested)) output = nested;
        if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0)
        {
            var first = result[0];
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("audio_urls", out var urls) && urls.ValueKind == JsonValueKind.Array && urls.GetArrayLength() > 0)
                return Text(urls[0], "url");
        }
        return null;
    }

    /// <summary>按 Base64 图片或新查询得到的签名地址下载，不保存签名地址。</summary>
    private static Task<string> SaveOutputAsync(JsonElement output, string destination, CancellationToken ct)
    {
        var encoded = Text(output, "b64_json");
        if (encoded is not null) return ProviderTransport.SaveBase64Async(encoded, destination, ct);
        var url = FindOutputUrl(output) ?? throw new InvalidDataException("成功响应没有可识别的媒体输出。");
        return ProviderTransport.DownloadAsync(url, destination, ct);
    }

    /// <summary>重复下载时只复用记录中路径与 SHA-256 都匹配的本地文件。</summary>
    private static async Task<bool> ExistingOutputAsync(ProviderRecord record, string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        if (record.LocalOutput is null || !string.Equals(Path.GetFullPath(record.LocalOutput), path, StringComparison.OrdinalIgnoreCase) || record.OutputSha256 is null)
            throw new IOException("下载目标存在但不属于当前已校验输出，拒绝覆盖。");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (file.Length > 512L * 1024 * 1024) throw new IOException("本地输出超过512MiB校验上限。");
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, limit.Token));
        if (hash != record.OutputSha256) throw new IOException("已有本地输出哈希变化，拒绝覆盖。");
        return true;
    }

    /// <summary>安全读取 JSON 字符串；不把整个响应或错误正文用于日志。</summary>
    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    /// <summary>解析正人民币金额，拒绝零估算、负值及异常大额度。</summary>
    private static decimal Money(string value) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount is > 0 and <= 1000000
        ? amount : throw new ArgumentException("预算与估算额必须为0至1000000之间的正数。");

    /// <summary>任务只写明确命名的 JSON，不覆盖共享台账或锁文件。</summary>
    private static string RecordPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals(LedgerName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("任务记录必须是独立的 .json 文件。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>只输出用途、状态、编号及费用，不输出服务商原始响应或请求正文。</summary>
    internal static void Print(ProviderRecord record, string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { record.Kind, record.Model, record.TaskId, record.Status, record.Price, record.Currency, record.EstimateCny, record.LocalOutput, message }, JsonFiles.Options));
    }
}
