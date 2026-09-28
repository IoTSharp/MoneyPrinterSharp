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
            builder.UseAlibabaSansFont();
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
            if (Program.LaunchArguments.Contains("--smoke") || Program.LaunchArguments.Contains("--capture"))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (Program.LaunchArguments.Contains("--smoke"))
                            window.RunSmoke();
                        if (Program.LaunchArguments.Contains("--capture"))
                            window.CaptureSnapshots();
                        desktop.Shutdown(0);
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(error);
                        desktop.Shutdown(1);
                    }
                }, DispatcherPriority.Background);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
