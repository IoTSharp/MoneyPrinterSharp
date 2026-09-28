using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;

namespace DesktopWorkbench;

/// <summary>使用本地模拟数据走查桌面布局、状态与非破坏性时间线编辑。</summary>
public partial class MainWindow : Window
{
    private const double PixelsPerSecond = 25;
    private static readonly string[] TrackNames = ["屏幕画面", "主持人/贴图", "旁白", "音乐", "字幕"];
    private static readonly Brush[] TrackColors =
    [
        new SolidColorBrush(Color.FromRgb(38, 126, 117)),
        new SolidColorBrush(Color.FromRgb(84, 119, 163)),
        new SolidColorBrush(Color.FromRgb(174, 103, 81)),
        new SolidColorBrush(Color.FromRgb(135, 119, 82)),
        new SolidColorBrush(Color.FromRgb(89, 112, 96))
    ];
    private readonly List<DemoClip> clips = [];
    private readonly Stack<List<DemoClip>> history = new();
    private readonly DispatcherTimer playback = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DemoClip? selectedClip;
    private Border? draggedElement;
    private double dragOriginX;
    private double clipOriginSeconds;
    private int playbackTicks;
    private bool navigationOpen = true;

    /// <summary>初始化七个可切换场景和有界预览计时器。</summary>
    public MainWindow()
    {
        InitializeComponent();
        playback.Tick += Playback_Tick;
        SizeChanged += MainWindow_SizeChanged;
        ScenarioBox.SelectedIndex = 0;
    }

    /// <summary>无网络烟测遍历场景、会话与分割/移动/撤销操作。</summary>
    public void RunSmoke()
    {
        for (var state = 0; state < ScenarioBox.Items.Count; state++)
        {
            ScenarioBox.SelectedIndex = state;
            if (PreviewStatus.Text.Length == 0) throw new InvalidOperationException("状态没有预览诊断。");
            if (state == 6 && NavigationColumn.Width.Value != 0) throw new InvalidOperationException("窄窗口未折叠导航。");
        }
        NavigationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu = NavigationButton.ContextMenu;
        if (menu is null || menu.Items.Count < 15) throw new InvalidOperationException("窄窗口导航入口不完整。");
        menu.IsOpen = false;
        ScenarioBox.SelectedIndex = 1;
        foreach (var state in new[] { 0, 2, 3, 4, 5 })
        {
            ScenarioBox.SelectedIndex = state;
            var action = ((StackPanel)Conversation.Children[0]).Children.OfType<Button>().FirstOrDefault();
            if (action is null) throw new InvalidOperationException("状态缺少继续操作。");
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (ScenarioBox.SelectedIndex != 1) throw new InvalidOperationException("状态操作没有返回制作流程。");
        }
        SessionList.SelectedIndex = 1;
        if (SessionSubtitle.Text != "竖屏版本") throw new InvalidOperationException("会话切换失败。");
        var before = clips.Count;
        selectedClip = clips[0];
        SplitSelectedClip();
        if (clips.Count != before + 1) throw new InvalidOperationException("片段分割失败。");
        MoveSelectedClip(1);
        UndoEdit();
        UndoEdit();
        if (clips.Count != before) throw new InvalidOperationException("撤销未恢复片段数量。");
        AspectBox.SelectedIndex = 1;
        if (PreviewFrame.Height <= PreviewFrame.Width) throw new InvalidOperationException("竖屏画幅未生效。");
        playback.Stop();
    }

    /// <summary>离屏渲染宽、窄窗口并检查图像至少含有效非黑像素。</summary>
    public void CaptureSnapshots()
    {
        CaptureSnapshot(1360, 840, 1, "wide");
        CaptureSnapshot(820, 680, 6, "narrow");
        CaptureSnapshot(760, 620, 6, "minimum");
    }

    /// <summary>把 WPF 可视树保存到临时目录用于本机布局走查。</summary>
    private void CaptureSnapshot(int width, int height, int state, string name)
    {
        Width = width; Height = height;
        ScenarioBox.SelectedIndex = state;
        Measure(new Size(width, height));
        Arrange(new Rect(0, 0, width, height));
        UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var nonBlack = 0;
        for (var index = 0; index < pixels.Length; index += 4)
            if (pixels[index] > 20 || pixels[index + 1] > 20 || pixels[index + 2] > 20) nonBlack++;
        if (nonBlack < width * height / 3) throw new InvalidOperationException("渲染图像几乎为空白或黑屏。");
        var output = Path.Combine(Path.GetTempPath(), $"mps-prototype-{name}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(output, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
        Console.WriteLine($"{output}，非黑像素 {nonBlack}/{width * height}");
    }

    /// <summary>场景切换时只改变原型视图，不写任何项目文件。</summary>
    private void ScenarioBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimelineCanvas is null || ScenarioBox.SelectedIndex < 0) return;
        ApplyScenario(ScenarioBox.SelectedIndex);
    }

    /// <summary>为六类状态及窄窗口场景提供明确的下一步入口。</summary>
    private void ApplyScenario(int state)
    {
        playback.Stop();
        playbackTicks = 0;
        Conversation.Children.Clear();
        if (state == 0)
        {
            clips.Clear(); history.Clear(); selectedClip = null;
            ProjectTitle.Text = "未命名项目";
            PreviewStatus.Text = "尚无素材";
            PreviewCaption.Text = "";
            AddMessage("尚未创建制作内容。", "创建项目", () => ScenarioBox.SelectedIndex = 1);
        }
        else
        {
            if (clips.Count == 0) SeedClips();
            ProjectTitle.Text = "产品讲解演示";
            PreviewCaption.Text = "开始制作您的演示视频";
            switch (state)
            {
                case 1:
                    PreviewStatus.Text = "制作中 · 00:30";
                    AddMessage("功能审计已完成，讲稿待复核。", "查看技能", () => SkillList.SelectedIndex = 2);
                    break;
                case 2:
                    PreviewStatus.Text = "任务失败 · 可恢复";
                    AddMessage("视频任务返回明确失败；原素材和时间线仍可编辑。", "回到制作", () => ScenarioBox.SelectedIndex = 1);
                    break;
                case 3:
                    PreviewStatus.Text = "预算耗尽 · 已暂停";
                    AddMessage("项目预算不足，待调整授权后继续。", "模拟调整预算", () => ScenarioBox.SelectedIndex = 1);
                    break;
                case 4:
                    PreviewStatus.Text = "模型能力未知 · 已暂停";
                    AddMessage("当前账号的模型能力没有实测证据。", "模拟手动选择", () => ScenarioBox.SelectedIndex = 1);
                    break;
                case 5:
                    PreviewStatus.Text = "导出完成 · 待验收";
                    AddMessage("本地模拟导出完成。", "返回项目", () => ScenarioBox.SelectedIndex = 1);
                    break;
                default:
                    PreviewStatus.Text = "窄窗口";
                    AddMessage("当前会话仍可操作时间线。", "打开导航", ShowNavigationMenu);
                    break;
            }
        }
        ApplyLayout();
        RenderTimeline();
    }

    /// <summary>构造五种轨道的短片段，数据只存于当前原型进程。</summary>
    private void SeedClips()
    {
        clips.Clear(); history.Clear();
        clips.AddRange([
            new DemoClip("screen-1", 0, 0, 12, "界面录屏"),
            new DemoClip("screen-2", 0, 12, 18, "功能页面"),
            new DemoClip("presenter-1", 1, 3, 8, "主持人"),
            new DemoClip("narration-1", 2, 0, 30, "中英旁白"),
            new DemoClip("music-1", 3, 0, 30, "背景音乐"),
            new DemoClip("caption-1", 4, 1, 7, "字幕 01"),
            new DemoClip("caption-2", 4, 12, 9, "字幕 02")
        ]);
        selectedClip = clips[0];
    }

    /// <summary>绘制固定轨道与可选、可拖拽的模拟片段。</summary>
    private void RenderTimeline()
    {
        TimelineCanvas.Children.Clear();
        for (var track = 0; track < TrackNames.Length; track++)
        {
            var row = new Border { Width = 940, Height = 48, Background = track % 2 == 0 ? Brushes.White : new SolidColorBrush(Color.FromRgb(243, 247, 247)), BorderBrush = new SolidColorBrush(Color.FromRgb(221, 229, 229)), BorderThickness = new Thickness(0, 0, 0, 1) };
            Canvas.SetTop(row, track * 48);
            TimelineCanvas.Children.Add(row);
            var label = new TextBlock { Text = TrackNames[track], Width = 106, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Canvas.SetTop(label, track * 48 + 15);
            TimelineCanvas.Children.Add(label);
        }
        foreach (var clip in clips)
        {
            var border = new Border { Width = Math.Max(28, clip.Duration * PixelsPerSecond - 3), Height = 32, Background = TrackColors[clip.Track], BorderBrush = selectedClip?.Id == clip.Id ? Brushes.Black : Brushes.Transparent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3), Tag = clip, Cursor = Cursors.Hand, Child = new TextBlock { Text = clip.Title, Foreground = Brushes.White, Margin = new Thickness(6, 6, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis } };
            Canvas.SetLeft(border, 110 + clip.Start * PixelsPerSecond);
            Canvas.SetTop(border, clip.Track * 48 + 8);
            border.MouseLeftButtonDown += Clip_MouseLeftButtonDown;
            border.MouseMove += Clip_MouseMove;
            border.MouseLeftButtonUp += Clip_MouseLeftButtonUp;
            TimelineCanvas.Children.Add(border);
        }
        TimelineStatus.Text = selectedClip is null ? "选择片段可编辑" : $"{selectedClip.Title} · {selectedClip.Start:0.0}s - {selectedClip.Start + selectedClip.Duration:0.0}s";
    }

    /// <summary>记录拖拽起点，鼠标释放时形成一次可撤销编辑。</summary>
    private void Clip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        draggedElement = (Border)sender;
        selectedClip = (DemoClip)draggedElement.Tag;
        dragOriginX = e.GetPosition(TimelineCanvas).X;
        clipOriginSeconds = selectedClip.Start;
        draggedElement.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>拖动时限制片段不越过零点或 30 秒演示范围。</summary>
    private void Clip_MouseMove(object sender, MouseEventArgs e)
    {
        if (draggedElement != sender || e.LeftButton != MouseButtonState.Pressed || selectedClip is null) return;
        var delta = (e.GetPosition(TimelineCanvas).X - dragOriginX) / PixelsPerSecond;
        var seconds = Math.Clamp(Math.Round(clipOriginSeconds + delta), 0, 30 - selectedClip.Duration);
        Canvas.SetLeft(draggedElement, 110 + seconds * PixelsPerSecond);
    }

    /// <summary>释放后提交拖拽结果，并保留撤销所需的旧片段列表。</summary>
    private void Clip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (draggedElement != sender || selectedClip is null) return;
        var seconds = Math.Clamp(Math.Round((Canvas.GetLeft(draggedElement) - 110) / PixelsPerSecond), 0, 30 - selectedClip.Duration);
        if (seconds != clipOriginSeconds) { history.Push(CloneClips()); selectedClip.Start = seconds; }
        draggedElement.ReleaseMouseCapture();
        draggedElement = null;
        RenderTimeline();
        e.Handled = true;
    }

    /// <summary>保存片段快照以供原型撤销。</summary>
    private List<DemoClip> CloneClips() => clips.Select(item => item.Clone()).ToList();

    /// <summary>在片段中点分割，保留原片段且生成新的稳定模拟 ID。</summary>
    private void SplitSelectedClip()
    {
        if (selectedClip is null || selectedClip.Duration < 2) return;
        history.Push(CloneClips());
        var half = Math.Floor(selectedClip.Duration / 2);
        var second = new DemoClip(Guid.NewGuid().ToString("N"), selectedClip.Track, selectedClip.Start + half, selectedClip.Duration - half, selectedClip.Title + " 2");
        selectedClip.Duration = half;
        clips.Add(second);
        selectedClip = second;
        RenderTimeline();
    }

    /// <summary>裁短片段末尾，不修改任何源素材。</summary>
    private void TrimSelectedClip()
    {
        if (selectedClip is null || selectedClip.Duration <= 1) return;
        history.Push(CloneClips());
        selectedClip.Duration -= 1;
        RenderTimeline();
    }

    /// <summary>在演示时间范围内移动当前片段。</summary>
    private void MoveSelectedClip(int delta)
    {
        if (selectedClip is null) return;
        var start = Math.Clamp(selectedClip.Start + delta, 0, 30 - selectedClip.Duration);
        if (start == selectedClip.Start) return;
        history.Push(CloneClips());
        selectedClip.Start = start;
        RenderTimeline();
    }

    /// <summary>恢复最近一次原型编辑的完整片段列表。</summary>
    private void UndoEdit()
    {
        if (history.Count == 0) return;
        var selectedId = selectedClip?.Id;
        clips.Clear(); clips.AddRange(history.Pop());
        selectedClip = clips.FirstOrDefault(item => item.Id == selectedId) ?? clips.FirstOrDefault();
        RenderTimeline();
    }

    /// <summary>预览最多运行三十秒，暂停或场景变化立即停止计时。</summary>
    private void Playback_Tick(object? sender, EventArgs e)
    {
        playbackTicks++;
        if (playbackTicks >= 120) playback.Stop();
        TimecodeText.Text = $"00:{Math.Min(30, playbackTicks / 4):00} / 00:30";
    }

    /// <summary>根据窗口宽度与窄窗口场景折叠侧栏。</summary>
    private void ApplyLayout()
    {
        if (NavigationPanel is null) return;
        var narrow = ActualWidth < 1100 || ScenarioBox.SelectedIndex == 6;
        var show = navigationOpen && !narrow;
        NavigationColumn.Width = show ? new GridLength(232) : new GridLength(0);
        LeftSplitterColumn.Width = show ? new GridLength(5) : new GridLength(0);
        NavigationPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreviewSize();
    }

    /// <summary>保持横竖画幅比例且不越过窄窗口预览区域。</summary>
    private void UpdatePreviewSize()
    {
        if (PreviewFrame is null || AspectBox is null) return;
        if (AspectBox.SelectedIndex == 1) { PreviewFrame.Width = 130; PreviewFrame.Height = 230; }
        else { PreviewFrame.Width = Math.Min(390, Math.Max(260, WorkspaceColumn.ActualWidth - 70)); PreviewFrame.Height = PreviewFrame.Width * 9 / 16; }
    }

    /// <summary>在会话区添加可继续点击的状态操作。</summary>
    private void AddMessage(string message, string? action = null, Action? next = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) });
        if (action is not null && next is not null)
        {
            var button = new Button { Content = action, HorizontalAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => next();
            panel.Children.Add(button);
        }
        Conversation.Children.Add(panel);
    }

    /// <summary>打开或关闭导航；自动窄窗仍遵守最小内容宽度。</summary>
    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActualWidth < 1100 || ScenarioBox.SelectedIndex == 6) { ShowNavigationMenu(); return; }
        navigationOpen = !navigationOpen;
        ApplyLayout();
    }
    /// <summary>窄窗口以菜单保留项目、会话和技能入口。</summary>
    private void ShowNavigationMenu()
    {
        var menu = new ContextMenu();
        AddMenuAction(menu, "新建项目", () => ScenarioBox.SelectedIndex = 0);
        AddMenuAction(menu, "产品讲解演示", () => ScenarioBox.SelectedIndex = 1);
        menu.Items.Add(new Separator());
        foreach (ListBoxItem session in SessionList.Items)
        {
            var target = session;
            AddMenuAction(menu, "会话 · " + target.Content, () => SessionList.SelectedItem = target);
        }
        menu.Items.Add(new Separator());
        foreach (ListBoxItem skill in SkillList.Items)
        {
            var target = skill;
            AddMenuAction(menu, "技能 · " + target.Content, () => SkillList.SelectedItem = target);
        }
        menu.PlacementTarget = NavigationButton;
        NavigationButton.ContextMenu = menu;
        menu.IsOpen = true;
    }
    /// <summary>把导航命令绑定到窄窗口菜单项。</summary>
    private static void AddMenuAction(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
    /// <summary>创建一个空的模拟项目。</summary>
    private void NewProjectButton_Click(object sender, RoutedEventArgs e) => ScenarioBox.SelectedIndex = 0;
    /// <summary>选择示例项目并进入制作场景。</summary>
    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ScenarioBox is not null && ProjectList.SelectedIndex >= 0) ScenarioBox.SelectedIndex = 1; }
    /// <summary>添加独立会话，但继续共用项目片段。</summary>
    private void NewSessionButton_Click(object sender, RoutedEventArgs e) { SessionList.Items.Add(new ListBoxItem { Content = $"制作会话 {SessionList.Items.Count + 1}" }); SessionList.SelectedIndex = SessionList.Items.Count - 1; }
    /// <summary>切换会话标题，不复制时间线。</summary>
    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (SessionSubtitle is not null && SessionList.SelectedItem is ListBoxItem item) SessionSubtitle.Text = item.Content.ToString() ?? ""; }
    /// <summary>从技能导航返回对应会话上下文。</summary>
    private void SkillList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (Conversation is not null && SkillList.SelectedItem is ListBoxItem item) AddMessage($"当前阶段：{item.Content}"); }
    /// <summary>将用户输入显示于原型会话并激活示例项目。</summary>
    private void SendButton_Click(object sender, RoutedEventArgs e) { var message = PromptBox.Text.Trim(); if (message.Length == 0) return; if (ScenarioBox.SelectedIndex == 0) ScenarioBox.SelectedIndex = 1; AddMessage(message); PromptBox.Clear(); }
    /// <summary>切换横竖画幅预览。</summary>
    private void AspectBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (PreviewFrame is not null) UpdatePreviewSize(); }
    /// <summary>仅模拟导出状态，不创建虚假的成片文件。</summary>
    private void ExportButton_Click(object sender, RoutedEventArgs e) { if (ScenarioBox.SelectedIndex is 0 or 2 or 3 or 4) { AddMessage("当前状态不能导出，请先完成项目或解除阻断。"); return; } ScenarioBox.SelectedIndex = 5; }
    /// <summary>控制最多三十秒的预览计时。</summary>
    private void PlayButton_Click(object sender, RoutedEventArgs e) { if (ScenarioBox.SelectedIndex == 0) return; if (playback.IsEnabled) playback.Stop(); else { playbackTicks = 0; playback.Start(); } }
    /// <summary>提交分割命令。</summary>
    private void SplitButton_Click(object sender, RoutedEventArgs e) => SplitSelectedClip();
    /// <summary>提交裁切命令。</summary>
    private void TrimButton_Click(object sender, RoutedEventArgs e) => TrimSelectedClip();
    /// <summary>提交左移命令。</summary>
    private void MoveLeftButton_Click(object sender, RoutedEventArgs e) => MoveSelectedClip(-1);
    /// <summary>提交右移命令。</summary>
    private void MoveRightButton_Click(object sender, RoutedEventArgs e) => MoveSelectedClip(1);
    /// <summary>撤销最近一次片段操作。</summary>
    private void UndoButton_Click(object sender, RoutedEventArgs e) => UndoEdit();
    /// <summary>窗口变化时重新约束三区布局。</summary>
    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    /// <summary>隔离的原型片段，不承诺最终项目格式。</summary>
    private sealed class DemoClip(string id, int track, double start, double duration, string title)
    {
        public string Id { get; } = id;
        public int Track { get; } = track;
        public double Start { get; set; } = start;
        public double Duration { get; set; } = duration;
        public string Title { get; } = title;
        /// <summary>复制编辑前的片段值。</summary>
        public DemoClip Clone() => new(Id, Track, Start, Duration, Title);
    }
}
