using System.Text.Json;

namespace VideoProduction;

/// <summary>只从供应商响应提取任务号、状态和费用，不返回错误原文或下载地址。</summary>
public static class ProviderResponseMapper
{
    /// <summary>把已知状态名称归一化，陌生值保留未知。</summary>
    public static string? NormalizeStatus(JsonElement body) => Text(body, "status")?.ToLowerInvariant() switch
    {
        "success" or "succeeded" or "completed" => "success",
        "failure" or "failed" => "failure",
        "cancelled" or "canceled" => "cancelled",
        "accepted" => "accepted",
        "waiting" or "queued" or "pending" or "in_progress" or "running" or "processing" => "pending",
        _ => null
    };

    /// <summary>仅保留数字金额和有限币种；价格缺失时保留原预留状态。</summary>
    public static void ApplyPrice(ProviderRecord record, JsonElement body, decimal? headerCost)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("price", out var price) &&
            price.ValueKind == JsonValueKind.Number && price.TryGetDecimal(out var amount) && amount is >= 0 and <= 1000000)
        {
            record.Price = amount;
            record.Currency = Text(body, "currency")?.ToUpperInvariant() switch
            {
                "CNY" => "CNY", "USD" => "USD", _ => "UNKNOWN"
            };
        }
        else if (headerCost is >= 0 and <= 1000000)
        {
            record.Price = headerCost;
            record.Currency = "CNY";
        }
    }

    /// <summary>拒绝把密钥或控制字符回显为任务号。</summary>
    public static string? SafeTaskId(JsonElement body, string credential)
    {
        var value = Text(body, "task_id");
        if (value is null || !string.IsNullOrEmpty(credential) && value.Contains(credential, StringComparison.Ordinal)) return null;
        if (value.Length is < 1 or > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            return null;
        return value;
    }

    /// <summary>只读取指定的标量字段，不复制响应对象。</summary>
    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
