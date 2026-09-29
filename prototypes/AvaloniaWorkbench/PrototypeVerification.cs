using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace Mps.AvaloniaWorkbench;

public partial class MainWindow
{
    /// <summary>在三种逻辑尺寸及三种离屏 DPI 下验证布局，产物保存在独占诊断目录。</summary>
    public void CaptureSnapshots(CancellationToken cancellationToken = default, bool pilot = false)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(45));
        limit.Token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var directory = Path.Combine(Environment.CurrentDirectory, ".runs", "prototype-captures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var results = new List<object>(9);
        string? currentFile = null;
        var outcome = "failed";
        Console.WriteLine($"原型布局证据目录：{directory}");
        var count = pilot ? 1 : 3;
        var sizes = new[] { (1360, 840, 1, "wide"), (820, 680, 6, "narrow"), (760, 620, 6, "minimum") };
        try
        {
            // 固定 3×3 组合，每张图前后检查取消和总墙钟；不更改操作系统缩放设置。
            for (var sizeIndex = 0; sizeIndex < count; sizeIndex++)
            {
                for (var scaleIndex = 0; scaleIndex < count; scaleIndex++)
                {
                    limit.Token.ThrowIfCancellationRequested();
                    var (width, height, state, name) = sizes[sizeIndex];
                    var scale = 1 + scaleIndex * .5;
                    var file = $"{name}-{scale * 100:0}.png";
                    currentFile = file;
                    var colors = CaptureSnapshot(width, height, state, scale, Path.Combine(directory, file), limit.Token);
                    results.Add(new { file, logical_width = width, logical_height = height, scale,
                        pixel_width = (int)(width * scale), pixel_height = (int)(height * scale), sampled_colors = colors,
                        layout = "passed" });
                    Console.WriteLine($"布局通过 {results.Count}/{count * count}：{width}×{height}，离屏 DPI {scale * 100:0}%");
                }
            }
            limit.Token.ThrowIfCancellationRequested();
            outcome = "passed";
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        finally
        {
            // 成功、失败与取消都登记本次诊断产物；不删除或覆盖其他运行的证据。
            File.WriteAllText(Path.Combine(directory, "verification.json"), JsonSerializer.Serialize(new
            {
                schema_version = 1, verified_at = DateTimeOffset.UtcNow, elapsed_ms = timer.ElapsedMilliseconds,
                outcome, current_file = currentFile, scope = "offscreen-layout-only",
                limitation = "不代表真实显示器 DPI、输入命中、视频播放或录屏验收", results
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"原型布局证据：{directory}");
        }
    }

    /// <summary>渲染实际可视树并断言关键控件边界，图像尺寸和非空白采样必须一致。</summary>
    private int CaptureSnapshot(int width, int height, int state, double scale, string output, CancellationToken ct)
    {
        Width = width;
        Height = height;
        currentSession = 0;
        SessionTitle.Text = "主版本制作";
        PrimarySessionButton.Classes.Set("active", true);
        VerticalSessionButton.Classes.Set("active", false);
        selectedClipId = null;
        ScenarioBox.SelectedIndex = state;
        ApplyScenario(state);
        ApplyLayout();
        Measure(new Size(width, height));
        Arrange(new Rect(0, 0, width, height));
        UpdatePreviewSize();
        UpdateLayout();
        VerifyRenderedText(ct);
        var bounds = new Rect(0, 0, width, height);
        var prompt = RequireBounds(PromptBox, bounds);
        var preview = RequireBounds(PreviewFrame, bounds);
        var play = RequireBounds(PlayButton, bounds);
        var timeline = RequireBounds(TimelineViewport, bounds);
        RequireBounds(LandscapeButton, bounds);
        RequireBounds(PortraitButton, bounds);
        RequireBounds(state == 6 ? CompactMenuButton : PrimarySessionButton, bounds);
        if (prompt.Right > preview.Left || preview.Bottom > play.Top + 1 || play.Bottom > timeline.Top + 1)
            throw new InvalidOperationException("输入、预览、播放或时间线区域发生重叠。");
        if (state == 6 && (!CompactSidebar.IsVisible || FullSidebar.IsVisible))
            throw new InvalidOperationException("窄窗口导航未正确折叠。");
        var pixels = new PixelSize((int)(width * scale), (int)(height * scale));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        ct.ThrowIfCancellationRequested();
        bitmap.Save(output, PngBitmapEncoderOptions.Default);
        using var saved = new Bitmap(output);
        if (saved.PixelSize != pixels)
            throw new InvalidOperationException("PNG 实际像素尺寸与离屏 DPI 不匹配。");
        return CountSampleColors(saved, ct);
    }

    /// <summary>核对实际占位模板透明度和字形族；最多128个模板节点、四行各十六个字形段。</summary>
    private void VerifyRenderedText(CancellationToken ct)
    {
        var placeholder = PromptBox.GetVisualDescendants().Take(128).OfType<TextBlock>()
            .Single(item => item.Name == "PART_Placeholder");
        ct.ThrowIfCancellationRequested();
        if (placeholder.Opacity != 1 || placeholder.Foreground is not ISolidColorBrush brush || brush.Color != Color.Parse("#5D6672"))
            throw new InvalidOperationException("输入占位模板未应用可读的颜色与透明度。");
        var message = (ConversationList.Children[0] as Border)?.Child as TextBlock
            ?? throw new InvalidOperationException("原型缺少用于验证字体的对话正文。");
        var track = TimelineCanvas.Children.OfType<Border>().Take(16).Select(item => item.Child).OfType<TextBlock>().First();
        foreach (var text in new[] { message, track, placeholder })
        {
            ct.ThrowIfCancellationRequested();
            var families = text.TextLayout.TextLines.Take(4).SelectMany(line => line.TextRuns.Take(16))
                .OfType<ShapedTextRun>().Select(run => run.ShapedBuffer.GlyphTypeface.FamilyName).Distinct().ToArray();
            if (families.Length == 0 || families.Any(family => family.Contains("SimSun", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("原型正文的实际字体为空或回退至宋体。");
            Console.WriteLine($"字形验证 {text.Name ?? "正文/轨道"}：{string.Join(", ", families)}；占位透明度 {placeholder.Opacity}");
        }
    }

    /// <summary>把控件坐标转换至窗口，拒绝不可见、零尺寸或窗口外的关键操作入口。</summary>
    private Rect RequireBounds(Control control, Rect window)
    {
        var origin = control.TranslatePoint(default, this);
        if (!control.IsEffectivelyVisible || origin is null || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            throw new InvalidOperationException($"关键控件不可见：{control.Name}");
        var rect = new Rect(origin.Value, control.Bounds.Size);
        if (rect.Left < -1 || rect.Top < -1 || rect.Right > window.Right + 1 || rect.Bottom > window.Bottom + 1)
            throw new InvalidOperationException($"关键控件超出窗口：{control.Name} {rect}");
        return rect;
    }

    /// <summary>最多采样 256 个像素并检查取消，防止将纯黑或空白截图视为布局证据。</summary>
    private static int CountSampleColors(Bitmap bitmap, CancellationToken ct)
    {
        var stride = checked(bitmap.PixelSize.Width * 4);
        var buffer = new byte[checked(stride * bitmap.PixelSize.Height)];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                pin.AddrOfPinnedObject(), buffer.Length, stride);
            var colors = new HashSet<uint>();
            for (var index = 0; index < 256; index++)
            {
                ct.ThrowIfCancellationRequested();
                var x = (2 * (index % 16) + 1) * bitmap.PixelSize.Width / 32;
                var y = (2 * (index / 16) + 1) * bitmap.PixelSize.Height / 32;
                colors.Add(BitConverter.ToUInt32(buffer, y * stride + x * 4));
            }
            if (colors.Count < 8) throw new InvalidOperationException("截图采样缺少有效内容，不能作为验收证据。");
            return colors.Count;
        }
        finally { pin.Free(); }
    }
}
