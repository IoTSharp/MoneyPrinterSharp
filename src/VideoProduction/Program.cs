using System.Text;

namespace VideoProduction;

/// <summary>视频制作系列的统一命令入口；每个阶段都可单独复用并留下可审计记录。</summary>
public static class Program
{
    /// <summary>解析首个命令，绑定取消处理器，并把异常转换为简洁中文诊断。</summary>
    public static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { PrintHelp(); return 0; }
            var command = args[0];
            var action = args.Length > 1 ? args[1] : null;
            // credentials/moark 的第一个位置参数是子动作，不应交给选项解析器。
            var commandArgs = command is "credentials" or "moark" ? null : new Arguments(args[1..]);
            return command switch
            {
                "init" => ProjectCommands.Initialize(commandArgs!),
                "features" => FeatureAudit.Run(commandArgs!, cancel.Token),
                "validate" => Validate(commandArgs!),
                "credentials" => Credentials(action, new Arguments(args.Length > 2 ? args[2..] : [])),
                "costs" => CostReport.Run(commandArgs!, cancel.Token),
                "install-skills" => ProjectCommands.InstallSkills(commandArgs!, cancel.Token),
                "moark" => action is null ? throw new ArgumentException("Moark 用法：moark submit ... 或 moark poll ...。") : await Moark(action, new Arguments(args.Length > 2 ? args[2..] : []), cancel.Token),
                "doctor" or "probe" or "split" or "key" or "render" or "verify" => await MediaPipeline.RunAsync(command, commandArgs!, cancel.Token),
                _ => throw new ArgumentException($"未知命令：{command}。运行 help 查看用法。")
            };
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("操作已取消。"); return 130; }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or TimeoutException or DirectoryNotFoundException or FileNotFoundException or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine("错误：" + error.Message);
            return 2;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    /// <summary>读取清单并报告所有错误；成功时输出可继续渲染的确认。</summary>
    private static int Validate(Arguments args)
    {
        args.Allow("manifest", "draft");
        var path = Path.GetFullPath(args.Required("manifest"));
        var errors = ProjectCommands.Validate(JsonFiles.Read<VideoManifest>(path), path, args.Has("draft"));
        foreach (var error in errors) Console.Error.WriteLine("- " + error);
        if (errors.Count > 0) return 2;
        Console.WriteLine("清单校验通过。");
        return 0;
    }

    /// <summary>执行 Moark 子命令并保持提交/轮询参数的严格边界。</summary>
    private static Task<int> Moark(string action, Arguments args, CancellationToken ct)
    {
        if (action is not ("submit" or "poll")) throw new ArgumentException("Moark 用法：moark submit ... 或 moark poll ...。");
        return MoarkClient.RunAsync(action, args, ct);
    }

    /// <summary>隐藏读取凭据并写入当前 Windows 用户凭据管理器，不打印密钥。</summary>
    private static int Credentials(string? action, Arguments args)
    {
        args.Allow("target");
        var target = args.Optional("target") ?? CredentialStore.DefaultTarget;
        if (action == "status")
        {
            Console.WriteLine(CredentialStore.Exists(target) ? $"已配置：{target}" : $"未配置：{target}");
            return 0;
        }
        if (action != "set") throw new ArgumentException("凭据用法：credentials status 或 credentials set。");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("凭据管理器仅支持 Windows。");
        Console.Write($"请输入 {target}（输入不回显）：");
        var secret = ReadHiddenSecret();
        Console.WriteLine();
        CredentialStore.Write(target, secret);
        Console.WriteLine("凭据已保存到当前 Windows 用户凭据管理器。");
        return 0;
    }

    /// <summary>逐字符隐藏读取密钥，最多4096字符并支持退格；不进入日志或参数。</summary>
    private static string ReadHiddenSecret()
    {
        var builder = new StringBuilder();
        while (builder.Length < 4096)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (builder.Length > 0) builder.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) builder.Append(key.KeyChar);
        }
        if (builder.Length == 0) throw new ArgumentException("凭据不能为空。");
        return builder.ToString();
    }

    /// <summary>输出所有阶段的可复制命令和安全边界。</summary>
    private static void PrintHelp()
    {
        Console.WriteLine("MoneyPrinter#（MPS / MP#）— 视频制作系列（C# / .NET 10）\n");
        Console.WriteLine("  init --project DIR --title TITLE --minutes 5");
        Console.WriteLine("  features --source SOFTWARE --output planning/features.json");
        Console.WriteLine("  validate --manifest PROJECT/manifest.json [--draft]");
        Console.WriteLine("  doctor | probe | split | key | render | verify  （见各命令 --help 不可用时请查 README）");
        Console.WriteLine("  moark submit|poll ...  （付费接口默认只提交一次，凭据来自 Windows Credential Manager）");
        Console.WriteLine("  costs --input records --output delivery/costs.json [--markdown delivery/costs.md]");
        Console.WriteLine("  install-skills [--destination %USERPROFILE%/.codex/skills]");
    }
}
