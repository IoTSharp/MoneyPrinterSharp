namespace VideoProduction;

/// <summary>创建项目模板、校验交付清单，并安装可发现的技能入口。</summary>
public static class ProjectCommands
{
    /// <summary>初始化新的制作目录，不覆盖已有资料。</summary>
    public static int Initialize(Arguments args)
    {
        args.Allow("project", "title", "minutes");
        var root = Path.GetFullPath(args.Required("project"));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Take(1).Any()) throw new IOException("项目目录非空，拒绝覆盖。");
        var minutes = args.Number("minutes", 5);
        if (!double.IsFinite(minutes) || minutes is < 0.1 or > 30) throw new ArgumentException("时长应在0.1至30分钟之间。");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "assets", "records", "delivery", "planning" }) Directory.CreateDirectory(Path.Combine(root, name));
        JsonFiles.Write(Path.Combine(root, "manifest.json"), new VideoManifest { Title = args.Optional("title") ?? "软件功能讲解", TargetSeconds = minutes * 60 });
        File.WriteAllText(Path.Combine(root, "planning", "brief.md"), "# 制作简报\n\n填写受众、观看后的任务、目标时长、核心操作、主持人风格、配音和预算。\n\n先执行功能核验，再编写分镜；空清单不能渲染。\n");
        Console.WriteLine($"制作目录已创建：{root}");
        return 0;
    }

    /// <summary>保持既有命令行 API，委托共享清单校验器。</summary>
    public static List<string> Validate(VideoManifest manifest, string manifestPath, bool draft = false)
        => ManifestValidator.Validate(manifest, manifestPath, draft);

    /// <summary>保持既有命令行 API，委托共享素材解析器。</summary>
    public static string ResolveAsset(string asset, string manifestPath)
        => ManifestValidator.ResolveAsset(asset, manifestPath);

    /// <summary>查找当前工具对应的仓库根，最多上行八层。</summary>
    public static string FindRepository()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var current = new DirectoryInfo(start);
            for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
                if (Directory.Exists(Path.Combine(current.FullName, "skills")) && File.Exists(Path.Combine(current.FullName, "src", "VideoProduction", "VideoProduction.csproj"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("找不到技能仓库根，请在仓库中运行。");
    }

    /// <summary>逐个链接技能目录，保留独立目录的相对引用；已存在的非本仓库目录不覆盖。</summary>
    public static int InstallSkills(Arguments args, CancellationToken ct)
    {
        args.Allow("destination");
        var destination = args.Optional("destination") ?? Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "skills");
        destination = Path.GetFullPath(destination);
        var repo = FindRepository();
        Directory.CreateDirectory(destination);
        var started = DateTime.UtcNow;
        foreach (var source in Directory.EnumerateDirectories(Path.Combine(repo, "skills")).Order(StringComparer.Ordinal).Take(41))
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(30)) throw new TimeoutException("技能安装超过30秒。");
            if (!File.Exists(Path.Combine(source, "SKILL.md"))) continue;
            var target = Path.Combine(destination, Path.GetFileName(source));
            if (Directory.Exists(target) || File.Exists(target))
            {
                var existing = new DirectoryInfo(target).ResolveLinkTarget(true);
                if (existing?.FullName == source) { Console.WriteLine($"已安装：{target}"); continue; }
                throw new IOException($"技能目录已有内容，拒绝覆盖：{target}");
            }
            // Windows开发者模式允许非管理员符号链接；不擅自改系统设置。
            Directory.CreateSymbolicLink(target, source);
            Console.WriteLine($"已安装：{target}");
        }
        return 0;
    }
}
