using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Mps.AvaloniaWorkbench;

/// <summary>仅使用本地模拟数据的跨平台制作工作台原型。</summary>
public partial class MainWindow : Window
{
    private readonly List<DemoClip> clips = [];
    private readonly Stack<List<DemoClip>> history = new();
    private readonly DispatcherTimer playback = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string? selectedClipId;
    private string? draggedClipId;
    private Point dragOrigin;
    private double dragStart;
    private bool dragChanged;
    private bool portrait;
    private int sessionCount = 2;
    private int currentSession;

    /// <summary>初始化界面、演示轨道与有界播放计时器。</summary>
    public MainWindow()
    {
        InitializeComponent();
        SeedClips();
        playback.Tick += Playback_Tick;
        SizeChanged += (_, _) => ApplyLayout();
        PreviewFrame.SizeChanged += (_, _) => UpdatePreviewSize();
        ApplyScenario(1);
        ApplyLayout();
    }

    /// <summary>执行无需网络的状态与片段操作烟测。</summary>
    public void RunSmoke(CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        for (var state = 0; state < 7; state++)
        {
            limit.Token.ThrowIfCancellationRequested();
            ScenarioBox.SelectedIndex = state;
            if (ScenarioBox.SelectedIndex != state)
                throw new InvalidOperationException($"场景切换失败：{state}");
            if (state is 0 or 2 or 3 or 4 or 5 && !ConversationList.Children.OfType<Button>().Any())
                throw new InvalidOperationException($"场景缺少操作入口：{state}");
        }
        ScenarioBox.SelectedIndex = 1;
        Portrait_Click(null, new RoutedEventArgs());
        if (VideoSurface.Height <= VideoSurface.Width)
            throw new InvalidOperationException("竖屏预览画幅错误");
        Landscape_Click(null, new RoutedEventArgs());
        selectedClipId = clips[0].Id;
        var initial = clips.ToArray();
        SplitSelectedClip();
        if (clips.Count != initial.Length + 1 || clips[0].Duration + clips[1].Duration != initial[0].Duration ||
            clips[1].Start != clips[0].Start + clips[0].Duration)
            throw new InvalidOperationException("分割未保持连续时间范围");
        MoveSelectedClip(1);
        if (clips[0].Start != initial[0].Start + 1)
            throw new InvalidOperationException("片段移动没有改变位置");
        var beforeTrim = clips[0].Duration;
        TrimSelectedClip();
        if (clips[0].Duration != beforeTrim - 1)
            throw new InvalidOperationException("片段裁短没有改变时长");
        UndoEdit();
        UndoEdit();
        UndoEdit();
        if (!clips.SequenceEqual(initial))
            throw new InvalidOperationException("撤销未恢复完整片段状态");
        SwitchSession(1);
        if (currentSession != 1 || !clips.SequenceEqual(initial))
            throw new InvalidOperationException("会话切换未保持共享时间线");
        Width = 820;
        Height = 680;
        ApplyLayout();
        if (FullSidebar.IsVisible || !CompactSidebar.IsVisible)
            throw new InvalidOperationException("窄窗口导航未折叠");
        if (CompactMenuButton.ContextMenu is null)
            BuildCompactMenu();
        if (CompactMenuButton.ContextMenu?.Items.Count < 13)
            throw new InvalidOperationException("窄窗口缺少项目、会话或技能入口");
        NewSession_Click(null, new RoutedEventArgs());
        if (CompactMenuButton.ContextMenu?.Items.OfType<MenuItem>().All(item => item.Header?.ToString() != "制作会话 3") != false)
            throw new InvalidOperationException("新会话未加入窄窗口菜单");
        limit.Token.ThrowIfCancellationRequested();
        Console.WriteLine("原型烟测通过：七种状态、双画幅、片段分割/移动/裁短/完整撤销、共享会话与窄窗菜单。");
    }

    /// <summary>装入五条轨道上的可编辑演示片段。</summary>
    private void SeedClips()
    {
        clips.Clear();
        clips.AddRange([
            new("screen-a", 0, 0, 12, "功能总览"),
            new("screen-b", 0, 12, 14, "核心流程"),
            new("presenter", 1, 16, 9, "主持人替身"),
            new("voice-a", 2, 0, 15, "中文旁白"),
            new("voice-b", 2, 15, 12, "英文旁白"),
            new("music", 3, 0, 30, "背景音乐"),
            new("caption", 4, 2, 21, "双语字幕")
        ]);
    }

    /// <summary>切换原型状态并让阻断信息和恢复入口保持一致。</summary>
    private void ApplyScenario(int state)
    {
        playback.Stop();
        PlaybackSlider.Value = 0;
        PlayIcon.IsVisible = true;
        PauseIcon.IsVisible = false;
        PreviewImage.IsVisible = state is not 0;
        PreviewMessageBackground.IsVisible = state is 0 or 2 or 3 or 4;
        ProjectState.Text = state switch
        {
            0 => "空项目", 2 => "任务失败", 3 => "预算耗尽", 4 => "模型未知",
            5 => "导出完成（演示）", _ => "制作中（演示）"
        };
        PreviewMessage.Text = state switch
        {
            0 => "新建项目后可导入素材", 2 => "任务失败 · 请检查记录",
            3 => "预算已耗尽 · 等待调整", 4 => "模型能力未知 · 等待验证", _ => ""
        };
        ProjectMeta.Text = state == 0 ? "尚无项目素材" : $"本地演示项目  /  {(portrait ? "9:16" : "16:9")}  /  30 秒";
        ComposerHint.Text = state switch
        {
            2 => "失败任务可查看原任务记录", 3 => "当前预算不允许付费请求",
            4 => "未知能力不会自动调用", _ => "仅使用项目已授权素材"
        };
        ConversationList.Children.Clear();
        AddMessage(state switch
        {
            0 => "项目为空。请先添加源码、页面、录屏或截图。",
            2 => "演示任务失败：素材探测中断。可检查本地素材并重试。",
            3 => "演示预算已耗尽。制作任务暂停，等待预算调整。",
            4 => "当前账号的模型能力尚未验证。制作任务暂停。",
            5 => "本地演示流程已完成。此原型未实际导出视频。",
            6 => "窄窗口布局已启用。项目导航收起，时间线仍可编辑。",
            _ => "已整理软件功能证据。当前处于讲稿分镜阶段。"
        });
        if (state == 1)
        {
            AddMessage("讲解目标：用 30 秒展示项目列表、桌面录制和交付报告。当前内容来自本地合成样本。");
            AddMessage("镜头 01  ·  项目列表  00:00-00:08\n镜头 02  ·  桌面录制  00:08-00:21\n镜头 03  ·  交付报告  00:21-00:30");
            AddAction("复核讲稿分镜", () => Navigate("讲稿分镜"));
        }
        if (state is 2 or 3 or 4)
            AddAction(state == 2 ? "查看任务记录" : state == 3 ? "查看项目预算" : "查看模型目录",
                () => Navigate(state == 2 ? "任务中心" : state == 3 ? "项目预算" : "模型与账号"));
        if (state == 0)
            AddAction("打开演示项目", () => ScenarioBox.SelectedIndex = 1);
        if (state == 5)
            AddAction("查看交付", () => Navigate("交付"));
        RenderTimeline();
    }

    /// <summary>根据窗口宽度调整三栏，保持最小宽度的预览和时间线。</summary>
    private void ApplyLayout()
    {
        var compact = (Width > 0 ? Width : Bounds.Width) < 1110 || ScenarioBox.SelectedIndex == 6;
        FullSidebar.IsVisible = !compact;
        CompactSidebar.IsVisible = compact;
        ScenarioBox.IsVisible = !compact;
        TimelineSummary.IsVisible = !compact;
        ZoomControl.IsVisible = !compact;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 56 : 240);
        RootGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 300 : 392);
        UpdatePreviewSize();
    }

    /// <summary>按可用空间约束画幅，不让预览内容压住播放工具栏。</summary>
    private void UpdatePreviewSize()
    {
        var width = Math.Max(120, PreviewFrame.Bounds.Width - 24);
        var height = Math.Max(100, PreviewFrame.Bounds.Height - 18);
        var ratio = portrait ? 9d / 16 : 16d / 9;
        VideoSurface.Width = Math.Min(width, height * ratio);
        VideoSurface.Height = VideoSurface.Width / ratio;
        PreviewMetadata.Text = portrait ? "720 × 1280  ·  30 fps" : "1280 × 720  ·  30 fps";
        ProjectMeta.Text = ScenarioBox.SelectedIndex == 0 ? "尚无项目素材" : $"本地演示项目  /  {(portrait ? "9:16" : "16:9")}  /  30 秒";
    }

    /// <summary>重绘刻度和五轨演示片段。</summary>
    private void RenderTimeline()
    {
        if (TimelineCanvas is null || RulerCanvas is null)
            return;
        var scale = ZoomSlider?.Value ?? 24;
        var width = Math.Max(750, 30 * scale + 36);
        TimelineCanvas.Width = width;
        RulerCanvas.Width = width;
        TimelineCanvas.Children.Clear();
        RulerCanvas.Children.Clear();
        for (var second = 0; second <= 30; second += 5)
        {
            var label = new TextBlock { Text = $"00:{second:00}", FontSize = 10, Foreground = ThemeBrush("MpsSecondaryTextBrush") };
            Canvas.SetLeft(label, 14 + second * scale);
            Canvas.SetTop(label, 8);
            RulerCanvas.Children.Add(label);
        }
        for (var track = 0; track < 5; track++)
        {
            var line = new Border { Width = width, Height = 1, Background = ThemeBrush("MpsSubtleBorderBrush") };
            Canvas.SetTop(line, (track + 1) * 39 - 1);
            TimelineCanvas.Children.Add(line);
        }
        foreach (var clip in clips.OrderBy(c => c.Track).ThenBy(c => c.Start))
        {
            var color = clip.Track switch
            {
                0 => "MpsScreenTrackBrush", 1 => "MpsOverlayTrackBrush", 2 => "MpsVoiceTrackBrush",
                3 => "MpsMusicTrackBrush", _ => "MpsCaptionTrackBrush"
            };
            var item = new Border
            {
                Tag = clip.Id,
                Width = Math.Max(20, clip.Duration * scale - 3),
                Height = 30,
                CornerRadius = new CornerRadius(4),
                Background = ThemeBrush(color),
                BorderBrush = ThemeBrush(clip.Id == selectedClipId ? "MpsAccentBrush" : "MpsTrackBorderBrush"),
                BorderThickness = new Thickness(clip.Id == selectedClipId ? 2 : 1),
                Child = new TextBlock
                {
                    Text = clip.Title,
                    FontSize = 10,
                    FontWeight = FontWeight.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(7, 0),
                    Foreground = ThemeBrush("MpsTextBrush")
                }
            };
            item.PointerPressed += Clip_PointerPressed;
            item.PointerMoved += Clip_PointerMoved;
            item.PointerReleased += Clip_PointerReleased;
            Canvas.SetLeft(item, 16 + clip.Start * scale);
            Canvas.SetTop(item, clip.Track * 39 + 4);
            TimelineCanvas.Children.Add(item);
        }
        TimelineSummary.Text = $"5 条轨道  ·  {clips.Count} 个片段";
        var selected = clips.FirstOrDefault(c => c.Id == selectedClipId);
        SelectedClipText.Text = selected is null ? "选择片段查看属性" : $"{selected.Title}  ·  {selected.Start:0.#}s - {selected.Start + selected.Duration:0.#}s";
    }

    /// <summary>选择片段并记录拖动起点。</summary>
    private void Clip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border item || item.Tag is not string id)
            return;
        selectedClipId = id;
        draggedClipId = id;
        dragOrigin = e.GetPosition(TimelineCanvas);
        dragStart = clips.First(c => c.Id == id).Start;
        dragChanged = false;
        e.Pointer.Capture(item);
        SelectedClipText.Text = clips.First(c => c.Id == id).Title;
    }

    /// <summary>将拖动位置吸附到半秒并限制在三十秒演示范围。</summary>
    private void Clip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (draggedClipId is null || sender is not Border item || item.Tag as string != draggedClipId)
            return;
        var index = clips.FindIndex(c => c.Id == draggedClipId);
        if (index < 0)
            return;
        var clip = clips[index];
        var scale = ZoomSlider.Value;
        var next = Math.Clamp(Math.Round((dragStart + (e.GetPosition(TimelineCanvas).X - dragOrigin.X) / scale) * 2) / 2, 0, 30 - clip.Duration);
        if (Math.Abs(next - clip.Start) < .01)
            return;
        if (!dragChanged)
        {
            SaveUndo();
            dragChanged = true;
        }
        clips[index] = clip with { Start = next };
        Canvas.SetLeft(item, 16 + next * scale);
        SelectedClipText.Text = $"{clip.Title}  ·  {next:0.#}s - {next + clip.Duration:0.#}s";
    }

    /// <summary>结束鼠标拖动并刷新片段顺序。</summary>
    private void Clip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border item && e.Pointer.Captured == item)
            e.Pointer.Capture(null);
        draggedClipId = null;
        RenderTimeline();
    }

    /// <summary>保存编辑前快照，撤销只影响原型内存。</summary>
    private void SaveUndo() => history.Push([.. clips]);

    /// <summary>从中点分割选中片段。</summary>
    private void SplitSelectedClip()
    {
        var index = clips.FindIndex(c => c.Id == selectedClipId);
        if (index < 0 || clips[index].Duration < 2)
            return;
        SaveUndo();
        var clip = clips[index];
        var first = Math.Round(clip.Duration / 2 * 2) / 2;
        clips[index] = clip with { Duration = first };
        clips.Insert(index + 1, clip with { Id = Guid.NewGuid().ToString("N"), Start = clip.Start + first, Duration = clip.Duration - first, Title = clip.Title + " · 后段" });
        RenderTimeline();
    }

    /// <summary>把选中片段的尾部裁短一秒。</summary>
    private void TrimSelectedClip()
    {
        var index = clips.FindIndex(c => c.Id == selectedClipId);
        if (index < 0 || clips[index].Duration <= 1)
            return;
        SaveUndo();
        clips[index] = clips[index] with { Duration = clips[index].Duration - 1 };
        RenderTimeline();
    }

    /// <summary>在演示时长内移动选中片段。</summary>
    private void MoveSelectedClip(int seconds)
    {
        var index = clips.FindIndex(c => c.Id == selectedClipId);
        if (index < 0)
            return;
        var clip = clips[index];
        var start = Math.Clamp(clip.Start + seconds, 0, 30 - clip.Duration);
        if (Math.Abs(start - clip.Start) < .01)
            return;
        SaveUndo();
        clips[index] = clip with { Start = start };
        RenderTimeline();
    }

    /// <summary>恢复最近一次时间线编辑。</summary>
    private void UndoEdit()
    {
        if (history.Count == 0)
            return;
        clips.Clear();
        clips.AddRange(history.Pop());
        RenderTimeline();
    }

    /// <summary>最多播放三十秒模拟预览，避免无界计时。</summary>
    private void Playback_Tick(object? sender, EventArgs e)
    {
        PlaybackSlider.Value = Math.Min(30, PlaybackSlider.Value + .2);
        if (PlaybackSlider.Value >= 30)
        {
            playback.Stop();
            PlayIcon.IsVisible = true;
            PauseIcon.IsVisible = false;
        }
    }

    /// <summary>从应用共享资源读取画刷，保证动态轨道与 XAML 控件使用同一套颜色。</summary>
    private static IBrush ThemeBrush(string key) => Application.Current?.Resources[key] as IBrush
        ?? throw new InvalidOperationException($"界面颜色资源缺失：{key}");

    /// <summary>以平面段落呈现本地演示消息，避免重复卡片干扰长时间阅读。</summary>
    private void AddMessage(string message)
    {
        ConversationList.Children.Add(new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = ThemeBrush("MpsSubtleBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 16),
            Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                LineHeight = 21, Foreground = ThemeBrush("MpsTextBrush") }
        });
    }

    /// <summary>加入可执行的状态恢复或导航入口。</summary>
    private void AddAction(string title, Action action)
    {
        var button = new Button { Content = title, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        button.Click += (_, _) => action();
        ConversationList.Children.Add(button);
    }

    /// <summary>构建窄窗口项目、会话与阶段导航菜单。</summary>
    private void BuildCompactMenu()
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "桌面产品讲解", () => ScenarioBox.SelectedIndex = 1);
        AddMenuItem(menu, "新建项目", () => ScenarioBox.SelectedIndex = 0);
        AddMenuItem(menu, "主版本制作", () => SwitchSession(0));
        AddMenuItem(menu, "竖屏适配", () => SwitchSession(1));
        for (var index = 2; index < sessionCount; index++)
        {
            var selected = index;
            AddMenuItem(menu, $"制作会话 {index + 1}", () => SwitchSession(selected));
        }
        AddMenuItem(menu, "新建会话", () => NewSession_Click(null, new RoutedEventArgs()));
        var scenarios = new[] { "空项目", "制作中", "任务失败", "预算耗尽", "模型未知", "导出完成" };
        for (var index = 0; index < scenarios.Length; index++)
        {
            var selected = index;
            AddMenuItem(menu, $"场景：{scenarios[index]}", () => ScenarioBox.SelectedIndex = selected);
        }
        foreach (var stage in new[] { "功能审计", "叙事规划", "讲稿分镜", "模型选择", "主持人", "旁白", "口型", "合成", "质量复核", "费用交付" })
            AddMenuItem(menu, stage, () => Navigate(stage));
        CompactMenuButton.ContextMenu = menu;
    }

    /// <summary>加入单个窄窗口导航命令。</summary>
    private static void AddMenuItem(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    /// <summary>切换共享时间线的制作会话。</summary>
    private void SwitchSession(int index)
    {
        currentSession = index;
        SessionTitle.Text = index == 0 ? "主版本制作" : index == 1 ? "竖屏适配" : $"制作会话 {index + 1}";
        SessionMeta.Text = "讲稿分镜 · 第 3 / 10 阶段";
        PrimarySessionButton.Classes.Set("active", index == 0);
        VerticalSessionButton.Classes.Set("active", index == 1);
        AddMessage($"已切换到{SessionTitle.Text}；项目素材和时间线仍共享。 ");
    }

    /// <summary>显示当前导航位置，原型中未接入生产数据。</summary>
    private void Navigate(string destination) => AddMessage($"当前视图：{destination}（本地演示）");

    /// <summary>根据场景选择器刷新演示状态。</summary>
    private void ScenarioBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConversationList is not null && ScenarioBox.SelectedIndex >= 0)
        {
            ApplyScenario(ScenarioBox.SelectedIndex);
            ApplyLayout();
        }
    }

    /// <summary>更新预览时间码。</summary>
    private void PlaybackSlider_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && Timecode is not null)
            Timecode.Text = $"00:{Math.Clamp((int)Math.Floor(PlaybackSlider.Value), 0, 30):00} / 00:30";
    }

    /// <summary>缩放时间线。</summary>
    private void ZoomSlider_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
            RenderTimeline();
    }

    /// <summary>打开工作台导航。</summary>
    private void Workbench_Click(object? sender, RoutedEventArgs e) => Navigate("制作工作台");
    /// <summary>打开窄窗口项目、会话与技能菜单。</summary>
    private void CompactMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (CompactMenuButton.ContextMenu is null)
            BuildCompactMenu();
        CompactMenuButton.ContextMenu!.Open(CompactMenuButton);
    }
    /// <summary>打开素材导航。</summary>
    private void Assets_Click(object? sender, RoutedEventArgs e) => Navigate("素材库");
    /// <summary>打开任务导航。</summary>
    private void Tasks_Click(object? sender, RoutedEventArgs e) => Navigate("任务中心");
    /// <summary>打开模型导航。</summary>
    private void Models_Click(object? sender, RoutedEventArgs e) => Navigate("模型与账号");
    /// <summary>打开交付导航。</summary>
    private void Delivery_Click(object? sender, RoutedEventArgs e) => Navigate("交付");
    /// <summary>创建本地演示会话。</summary>
    private void NewSession_Click(object? sender, RoutedEventArgs e)
    {
        var index = sessionCount++;
        var button = new Button { Content = $"制作会话 {index + 1}", HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        button.Click += (_, _) => SwitchSession(index);
        SessionButtons.Children.Add(button);
        if (CompactMenuButton.ContextMenu is not null)
            BuildCompactMenu();
        SwitchSession(index);
    }
    /// <summary>选择主会话。</summary>
    private void PrimarySession_Click(object? sender, RoutedEventArgs e) => SwitchSession(0);
    /// <summary>选择竖屏会话。</summary>
    private void VerticalSession_Click(object? sender, RoutedEventArgs e) => SwitchSession(1);
    /// <summary>进入功能审计阶段。</summary>
    private void AuditSkill_Click(object? sender, RoutedEventArgs e) => Navigate("功能审计");
    /// <summary>进入叙事规划阶段。</summary>
    private void NarrativeSkill_Click(object? sender, RoutedEventArgs e) => Navigate("叙事规划");
    /// <summary>进入讲稿分镜阶段。</summary>
    private void ScreenplaySkill_Click(object? sender, RoutedEventArgs e) => Navigate("讲稿分镜");
    /// <summary>进入模型选择阶段。</summary>
    private void ModelSkill_Click(object? sender, RoutedEventArgs e) => Navigate("模型选择");
    /// <summary>进入主持人阶段。</summary>
    private void PresenterSkill_Click(object? sender, RoutedEventArgs e) => Navigate("主持人");
    /// <summary>进入旁白阶段。</summary>
    private void NarrationSkill_Click(object? sender, RoutedEventArgs e) => Navigate("旁白");
    /// <summary>进入口型阶段。</summary>
    private void LipSkill_Click(object? sender, RoutedEventArgs e) => Navigate("口型");
    /// <summary>进入合成阶段。</summary>
    private void CompositionSkill_Click(object? sender, RoutedEventArgs e) => Navigate("合成");
    /// <summary>进入复核阶段。</summary>
    private void ReviewSkill_Click(object? sender, RoutedEventArgs e) => Navigate("质量复核");
    /// <summary>进入交付阶段。</summary>
    private void CostSkill_Click(object? sender, RoutedEventArgs e) => Navigate("费用交付");
    /// <summary>保留用户输入为演示消息。</summary>
    private void Send_Click(object? sender, RoutedEventArgs e)
    {
        var value = PromptBox.Text?.Trim();
        if (string.IsNullOrEmpty(value))
            return;
        AddMessage(value);
        PromptBox.Clear();
    }
    /// <summary>说明素材授权入口尚待生产实现。</summary>
    private void Attach_Click(object? sender, RoutedEventArgs e) => AddMessage("素材附加将在项目外发授权接入后启用。");
    /// <summary>原型中仅显示导出演示状态，不写入成片。</summary>
    private void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (ScenarioBox.SelectedIndex is 0 or 2 or 3 or 4)
        {
            AddMessage("当前状态不可导出，请先处理项目阻断。");
            return;
        }
        ScenarioBox.SelectedIndex = 5;
    }
    /// <summary>切换横屏画幅。</summary>
    private void Landscape_Click(object? sender, RoutedEventArgs e)
    {
        portrait = false;
        LandscapeButton.Classes.Set("primary", true);
        PortraitButton.Classes.Set("primary", false);
        UpdatePreviewSize();
    }
    /// <summary>切换竖屏画幅。</summary>
    private void Portrait_Click(object? sender, RoutedEventArgs e)
    {
        portrait = true;
        PortraitButton.Classes.Set("primary", true);
        LandscapeButton.Classes.Set("primary", false);
        UpdatePreviewSize();
    }
    /// <summary>播放或暂停有界演示时钟。</summary>
    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        if (ScenarioBox.SelectedIndex == 0)
            return;
        if (playback.IsEnabled)
            playback.Stop();
        else
        {
            if (PlaybackSlider.Value >= 30)
                PlaybackSlider.Value = 0;
            playback.Start();
        }
        PlayIcon.IsVisible = !playback.IsEnabled;
        PauseIcon.IsVisible = playback.IsEnabled;
    }
    /// <summary>重新适配预览窗口。</summary>
    private void Fit_Click(object? sender, RoutedEventArgs e) => UpdatePreviewSize();
    /// <summary>撤销最近一次编辑。</summary>
    private void Undo_Click(object? sender, RoutedEventArgs e) => UndoEdit();
    /// <summary>分割当前片段。</summary>
    private void Split_Click(object? sender, RoutedEventArgs e) => SplitSelectedClip();
    /// <summary>裁短当前片段。</summary>
    private void Trim_Click(object? sender, RoutedEventArgs e) => TrimSelectedClip();
    /// <summary>向左移动当前片段。</summary>
    private void MoveLeft_Click(object? sender, RoutedEventArgs e) => MoveSelectedClip(-1);
    /// <summary>向右移动当前片段。</summary>
    private void MoveRight_Click(object? sender, RoutedEventArgs e) => MoveSelectedClip(1);

    private sealed record DemoClip(string Id, int Track, double Start, double Duration, string Title);
}
