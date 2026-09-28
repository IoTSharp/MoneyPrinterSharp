using System.Windows;

namespace DesktopWorkbench;

/// <summary>交互原型入口，只载入本地模拟数据。</summary>
public partial class App : Application
{
    /// <summary>烟测模式在同一交互模型中遍历状态与编辑命令。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        if (e.Args.Contains("--smoke", StringComparer.Ordinal))
        {
            try { window.RunSmoke(); Console.WriteLine("桌面原型烟测通过。"); Shutdown(0); }
            catch (Exception error) { Console.Error.WriteLine("桌面原型烟测失败：" + error.Message); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--capture", StringComparer.Ordinal))
        {
            try { window.Show(); window.CaptureSnapshots(); Shutdown(0); }
            catch (Exception error) { Console.Error.WriteLine("原型图像检查失败：" + error.Message); Shutdown(1); }
            return;
        }
        window.Show();
    }
}
