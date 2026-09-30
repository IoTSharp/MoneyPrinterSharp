using AtomUI;
using Avalonia;

namespace Mps.AvaloniaWorkbench;

/// <summary>跨平台桌面原型入口，界面与交互均使用 .NET。</summary>
internal static class Program
{
    internal static string[] LaunchArguments { get; private set; } = [];

    /// <summary>启动原生窗口；烟测通过应用参数在无网络条件下执行。</summary>
    [STAThread]
    public static void Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        LaunchArguments = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>注册 AtomUI 桌面后端。</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseAtomUIPlatformDetect()
        .WithAtomUIDefaultOptions();
}
