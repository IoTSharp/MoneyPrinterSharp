using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Mps.AvaloniaWorkbench;

/// <summary>装入样式并创建单窗口交互原型。</summary>
public partial class App : Application
{
    /// <summary>加载 XAML 资源并初始化中文桌面控件主题。</summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        this.UseAtomUI(builder =>
        {
            builder.UseLanguages(LanguageTags.ZhCN, [LanguageTags.ZhCN, LanguageTags.EnUS]);
            builder.UseDesktopControls();
        });
    }

    /// <summary>经典桌面生命周期只创建当前工作台窗口。</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (Program.LaunchArguments.Contains("--smoke") || Program.LaunchArguments.Contains("--capture") ||
                Program.LaunchArguments.Contains("--capture-pilot"))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; limit.Cancel(); };
                    Console.CancelKeyPress += cancel;
                    try
                    {
                        if (Program.LaunchArguments.Contains("--smoke"))
                            window.RunSmoke(limit.Token);
                        if (Program.LaunchArguments.Contains("--capture") || Program.LaunchArguments.Contains("--capture-pilot"))
                            window.CaptureSnapshots(limit.Token, Program.LaunchArguments.Contains("--capture-pilot"));
                        desktop.Shutdown(0);
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(error);
                        desktop.Shutdown(1);
                    }
                    finally { Console.CancelKeyPress -= cancel; }
                }, DispatcherPriority.Background);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
