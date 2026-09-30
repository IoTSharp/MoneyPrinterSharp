using System.Diagnostics;

namespace VideoProduction;

/// <summary>供应商任务的白名单记录；不保存请求正文、密钥或远程下载地址。</summary>
public sealed class ProviderRecord
{
    public int Version { get; set; } = 1;
    public string Provider { get; set; } = "moark";
    public string Kind { get; set; } = "";
    public string Model { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string? TaskId { get; set; }
    public string Status { get; set; } = "unknown";
    public decimal? Price { get; set; }
    public string? Currency { get; set; }
    public decimal EstimateCny { get; set; }
    public string? ErrorCode { get; set; }
    public int? HttpStatus { get; set; }
    public string? LocalOutput { get; set; }
    public string? OutputSha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int SubmitProcessId { get; set; } = Environment.ProcessId;
}

/// <summary>目录内共享费用台账；未知和空费用始终保留估算额度。</summary>
public sealed class ProviderLedger
{
    public int Version { get; set; } = 1;
    public List<ProviderLedgerEntry> Entries { get; set; } = [];
}

/// <summary>按记录路径关联预算占用与指纹，用于跨进程去重。</summary>
public sealed class ProviderLedgerEntry
{
    public string RecordPath { get; set; } = "";
    public ProviderRecord Record { get; set; } = new();
}

/// <summary>通过不共享的文件句柄建立跨进程锁，锁文件保留以避免删除竞争。</summary>
public static class ProviderLocks
{
    /// <summary>最多等待60次和15秒；取消或超时不进入提交临界区。</summary>
    public static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        for (var attempt = 0; attempt < 60 && timer.Elapsed < TimeSpan.FromSeconds(15); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (attempt % 20 == 0) Console.Error.WriteLine("等待任务记录独占锁……");
                await Task.Delay(250, ct);
            }
        }
        throw new TimeoutException("等待任务记录独占锁超过15秒。");
    }
}
