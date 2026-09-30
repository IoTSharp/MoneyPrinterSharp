using System.Globalization;
using System.Text.Json;

namespace VideoProduction;

/// <summary>生产提交和查询共用的白名单提取；原始响应、错误正文与下载地址不写记录。</summary>
internal static class ProviderResponsePolicy
{
    /// <summary>从提交响应中只提取受限任务号、费用、HTTP 状态和已知状态。</summary>
    internal static void ApplySubmission(ProviderRecord record, ProviderResponse response, string credential)
    {
        ValidateHttpStatus(response.StatusCode);
        record.HttpStatus = response.StatusCode;
        ApplyPrice(record, response.Document.RootElement, response.InferenceCost);
        var taskId = SafeTaskId(response.Document.RootElement, credential);
        if (taskId is not null) record.TaskId = taskId;
        if (!response.IsSuccess)
        {
            record.Status = response.StatusCode is >= 400 and < 500 ? "rejected" : "unknown";
            record.ErrorCode = "submit_http_" + response.StatusCode.ToString(CultureInfo.InvariantCulture);
        }
        else if (record.Kind != "image")
        {
            record.Status = record.TaskId is null ? "unknown" : NormalizeStatus(response.Document.RootElement) ?? "accepted";
            record.ErrorCode = record.TaskId is null ? "accepted_without_task_id" : null;
        }
    }

    /// <summary>查询响应只更新既有任务；编号不匹配时使用固定错误且不采信费用。</summary>
    internal static void ApplyPoll(ProviderRecord record, ProviderResponse response, string credential)
    {
        ValidateHttpStatus(response.StatusCode);
        if (!response.IsSuccess)
        {
            record.ErrorCode = "poll_http_" + response.StatusCode.ToString(CultureInfo.InvariantCulture);
            return;
        }
        var returnedId = SafeTaskId(response.Document.RootElement, credential);
        if (returnedId is not null && returnedId != record.TaskId) throw new InvalidDataException("任务查询响应编号不匹配。");
        ApplyPrice(record, response.Document.RootElement, response.InferenceCost);
        record.Status = NormalizeStatus(response.Document.RootElement) ?? "pending";
        record.ErrorCode = record.Status == "failure" ? "provider_task_failed" : null;
    }

    /// <summary>限制状态数字，防止异常适配器把任意内容作为 HTTP 诊断字段。</summary>
    private static void ValidateHttpStatus(int status)
    {
        if (status is < 100 or > 599) throw new InvalidDataException("供应商 HTTP 状态无效。");
    }

    /// <summary>规范化已知状态；响应中的任意状态文本不会进入记录。</summary>
    private static string? NormalizeStatus(JsonElement body) => Text(body, "status")?.ToLowerInvariant() switch
    {
        "success" or "succeeded" or "completed" => "success",
        "failure" or "failed" => "failure",
        "cancelled" or "canceled" => "cancelled",
        "accepted" => "accepted",
        "waiting" or "queued" or "pending" or "in_progress" or "running" or "processing" => "pending",
        _ => null
    };

    /// <summary>只保留数字费用与允许币种；缺失价格继续保持原有未知状态。</summary>
    private static void ApplyPrice(ProviderRecord record, JsonElement body, decimal? headerCost)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("price", out var price) &&
            price.ValueKind == JsonValueKind.Number && price.TryGetDecimal(out var amount) && amount is >= 0 and <= 1000000)
        {
            record.Price = amount;
            record.Currency = Text(body, "currency")?.ToUpperInvariant() switch { "CNY" => "CNY", "USD" => "USD", _ => "UNKNOWN" };
        }
        else if (headerCost is >= 0 and <= 1000000) { record.Price = headerCost; record.Currency = "CNY"; }
    }

    /// <summary>拒绝无效任务号及凭据原样回显，不允许错误消息或 URL 混入。</summary>
    private static string? SafeTaskId(JsonElement body, string credential)
    {
        var value = Text(body, "task_id");
        if (value is null || !string.IsNullOrEmpty(credential) && value.Contains(credential, StringComparison.Ordinal)) return null;
        try { ProviderTransport.ValidateTaskId(value); return value; }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>安全读取单个字符串字段，响应仅由上层有界解析器载入内存。</summary>
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;
}
