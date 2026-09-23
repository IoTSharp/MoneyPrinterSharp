using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VideoProduction;

/// <summary>在有限源码范围发现界面功能线索；候选必须由智能体结合页面验证。</summary>
public static partial class FeatureAudit
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", "bin", "obj", "node_modules", "output", "downloads", "dist", "coverage", ".runs", "graphify-out" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".razor", ".cshtml", ".vue", ".tsx", ".ts", ".jsx", ".html", ".md" };
    private static readonly Regex EvidencePattern = new(
        "@page|\\[(?:Route|HttpGet|HttpPost|Authorize)|Map(?:Get|Post|Group)\\(|<(?:h[1-3]|title)|(?:title|label|菜单|功能|报表|核验|导出|查询|导航)\\s*[:=：]|NavLink|MenuItem",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Sensitive = new("password|secret|token|connectionstring|api[_-]?key|authorization|sk-[A-Za-z0-9_-]{12,}|https?://[^\\s]+[?]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    /// <summary>扫描用户指定项目，记录相对路径和行号，不宣称所有候选已实现。</summary>
    public static int Run(Arguments args, CancellationToken ct)
    {
        args.Allow("source", "output", "max-files", "timeout-seconds");
        var root = Path.GetFullPath(args.Required("source"));
        if (!Directory.Exists(root) || Path.GetPathRoot(root) == root) throw new ArgumentException("请指定具体软件源码目录。");
        var maximum = args.Int("max-files", 500);
        var seconds = args.Int("timeout-seconds", 60);
        if (maximum is < 1 or > 5000 || seconds is < 1 or > 300) throw new ArgumentException("扫描最多5000文件、300秒。");
        var clock = Stopwatch.StartNew();
        var directories = new Queue<(string Path, int Depth)>();
        directories.Enqueue((root, 0));
        var candidates = new List<object>();
        int inspected = 0, entries = 0, visited = 0;
        for (; visited < 2000 && directories.Count > 0 && inspected < maximum && clock.Elapsed.TotalSeconds < seconds; visited++)
        {
            ct.ThrowIfCancellationRequested();
            var directory = directories.Dequeue();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path).Take(2001))
            {
                if (++entries > 20000 || inspected >= maximum || clock.Elapsed.TotalSeconds >= seconds) break;
                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (directory.Depth < 10 && !SkippedDirectories.Contains(Path.GetFileName(path)) && !Path.GetFileName(path).StartsWith('.'))
                        directories.Enqueue((path, directory.Depth + 1));
                    continue;
                }
                if (!Extensions.Contains(Path.GetExtension(path)) || Path.GetFileName(path).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > 512000) continue;
                inspected++;
                var lines = File.ReadLines(path);
                int lineNumber = 0, hits = 0;
                foreach (var line in lines.Take(10000))
                {
                    if (clock.Elapsed.TotalSeconds >= seconds) break;
                    ct.ThrowIfCancellationRequested();
                    lineNumber++;
                    if (line.Length > 1500 || !EvidencePattern.IsMatch(line) || Sensitive.IsMatch(line)) continue;
                    candidates.Add(new { source = Path.GetRelativePath(root, path).Replace('\\', '/'), line = lineNumber,
                        excerpt = line.Trim()[..Math.Min(line.Trim().Length, 240)], verification = "candidate-needs-ui-review" });
                    if (++hits >= 8 || candidates.Count >= 500) break;
                }
                if (candidates.Count >= 500) break;
            }
            if (entries > 20000 || candidates.Count >= 500) break;
            if (visited % 50 == 0) Console.Error.WriteLine($"功能扫描：{inspected}/{maximum} 文件，{candidates.Count} 条待核验线索。");
        }
        JsonFiles.Write(args.Required("output"), new { status = "requires-human-or-agent-validation", inspected_files = inspected,
            partial_scan = inspected >= maximum || entries > 20000 || candidates.Count >= 500 || directories.Count > 0,
            elapsed_seconds = clock.Elapsed.TotalSeconds, candidates,
            next_step = "逐条核对路由、权限、真实页面截图与业务行为；未验证项不要写进成片承诺。" });
        Console.WriteLine($"已输出 {candidates.Count} 条候选，扫描 {inspected} 个文件。");
        return 0;
    }
}
