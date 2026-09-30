using System.Diagnostics;
using System.Text;

namespace VideoProduction;

/// <summary>集中管理本工具创建的外部进程、超时和完整子进程树回收。</summary>
public static class ProcessRunner
{
    /// <summary>用结构化参数执行命令，限时读取输出并在取消时只回收本次进程树。</summary>
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken ct, string? workingDirectory = null)
    {
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromHours(2))
            throw new ArgumentOutOfRangeException(nameof(timeout), "进程时限必须在1秒到2小时之间。");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments.Take(1001))
        {
            if (info.ArgumentList.Count >= 1000) throw new ArgumentException("进程参数超过1000项。");
            info.ArgumentList.Add(argument);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(timeout);
        using var process = new Process { StartInfo = info };
        var created = DateTimeOffset.UtcNow;
        var logDirectory = Path.Combine(Environment.CurrentDirectory, ".runs", "processes");
        Directory.CreateDirectory(logDirectory);
        string? log = null;
        DateTime start = default;
        Task<string>? stdout = null, stderr = null;
        var outcome = "starting";
        try
        {
            if (!process.Start()) throw new InvalidOperationException("外部进程未启动。");
            start = process.StartTime.ToUniversalTime();
            log = Path.Combine(logDirectory, $"{created:yyyyMMddTHHmmssfff}-{process.Id}.json");
            // 参数必须是媒体或构建参数；密钥不可传给该接口。
            JsonFiles.Write(log, new { pid = process.Id, parent_pid = Environment.ProcessId, started_at = start,
                executable, arguments = info.ArgumentList.ToArray(), state = "running" });
            stdout = ReadBoundedAsync(process.StandardOutput, stop.Token);
            stderr = ReadBoundedAsync(process.StandardError, stop.Token);
            await process.WaitForExitAsync(stop.Token);
            var result = new ProcessResult(process.ExitCode, await stdout, await stderr);
            outcome = "exited";
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = "timeout";
            throw new TimeoutException($"{Path.GetFileName(executable)} 超过 {timeout.TotalSeconds:0} 秒。");
        }
        finally
        {
            // Process对象、PID和创建时间共同确定归属，不按进程名称批量结束。
            if (log is not null)
            {
                if (!process.HasExited && process.StartTime.ToUniversalTime() == start)
                {
                    process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(cleanup.Token);
                }
                stop.Cancel();
                if (stdout is not null && stderr is not null)
                    try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException) { }
                JsonFiles.Write(log, new { pid = process.Id, parent_pid = Environment.ProcessId, started_at = start,
                    executable, arguments = info.ArgumentList.ToArray(), state = outcome == "starting" ? "cancelled-or-failed" : outcome,
                    finished_at = DateTimeOffset.UtcNow, exited = process.HasExited });
            }
        }
    }

    /// <summary>持续排空管道，最多读取128MiB并只保留末尾512KiB诊断内容。</summary>
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        for (var chunk = 0; chunk < 16384; chunk++)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return text.ToString();
            text.Append(buffer, 0, count);
            if (text.Length > 524288) text.Remove(0, text.Length - 524288);
        }
        throw new IOException("外部进程输出超过128MiB，已达到日志读取上限。");
    }
}
