using System.Diagnostics;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>新项目的根文件；只保存可移植引用，不保存账号凭据或供应商响应。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsProjectDocument
{
    public string Format { get; set; } = "mps.project";
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "未命名项目";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<MpsAssetReference> Assets { get; set; } = [];
    public List<MpsOutboundAuthorization> Authorizations { get; set; } = [];
}

/// <summary>项目内素材的稳定引用；失联时仍保留路径及原哈希。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsAssetReference
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string? Sha256 { get; set; }
}

/// <summary>一次明确的项目素材外发授权；账号、用途和素材哈希均须匹配。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsOutboundAuthorization
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "";
    public string AccountAlias { get; set; } = "";
    public string Capability { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string AssetId { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset ExpiresUtc { get; set; }
}

/// <summary>创建并解析可复制、可移动的 MPS 项目目录。</summary>
public static class ProjectDirectory
{
    public const string ProjectFileName = "project.mps.json";
    private static readonly string[] Folders =
    [
        "assets/source", "assets/generated", "cache", "records", "sessions", "versions", "delivery"
    ];

    /// <summary>仅在空目录创建项目；已有文件绝不覆盖。</summary>
    public static MpsProjectDocument Create(string root, string title, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Take(1).Any())
            throw new IOException("项目目录非空，拒绝覆盖。");
        var project = new MpsProjectDocument { Title = title };
        Validate(project, root, deadline.Token);
        Directory.CreateDirectory(root);
        foreach (var folder in Folders)
        {
            deadline.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(ResolvePath(root, folder, deadline.Token));
        }
        deadline.Token.ThrowIfCancellationRequested();
        JsonFiles.WriteNew(Path.Combine(root, ProjectFileName), project);
        return project;
    }

    /// <summary>读取已知版本；未知字段明确报错，避免日后保存时静默丢失。</summary>
    public static MpsProjectDocument Open(string root, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        root = Path.GetFullPath(root);
        _ = ResolvePath(root, ProjectFileName, deadline.Token);
        var project = JsonFiles.Read<MpsProjectDocument>(Path.Combine(root, ProjectFileName));
        Validate(project, root, deadline.Token);
        return project;
    }

    /// <summary>原子保存根文件；素材路径必须保持项目相对路径。</summary>
    public static void Save(string root, MpsProjectDocument project, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        root = Path.GetFullPath(root);
        Validate(project, root, deadline.Token);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("项目目录不存在。");
        deadline.Token.ThrowIfCancellationRequested();
        JsonFiles.Write(Path.Combine(root, ProjectFileName), project);
    }

    /// <summary>拒绝绝对路径、上行段及重解析点，确保引用留在项目目录内。</summary>
    public static string ResolvePath(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 512 ||
            relativePath.Contains('\\') || relativePath.Contains(':') || relativePath.StartsWith('/') ||
            relativePath.Contains('?') || relativePath.Contains('#'))
            throw new InvalidDataException("项目路径必须是无查询参数的相对 URI 路径。");
        var segments = relativePath.Split('/');
        if (segments.Length > 32 || segments.Any(segment => segment.Length is < 1 or > 255 || segment is "." or ".." ||
            segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("项目路径包含无效段。");
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("项目根目录不能是符号链接或重解析点。");
        var current = root;
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("项目路径不能穿过符号链接或重解析点。");
        }
        var resolved = Path.GetFullPath(current);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!resolved.StartsWith(prefix, comparison)) throw new InvalidDataException("项目路径越界。");
        return resolved;
    }

    /// <summary>限制根文件与素材引用规模，并保留失联素材的引用。</summary>
    private static void Validate(MpsProjectDocument project, string root, CancellationToken cancellationToken)
    {
        _ = ResolvePath(root, ProjectFileName, cancellationToken);
        if (project.Format != "mps.project" || project.SchemaVersion != 1 ||
            !Guid.TryParseExact(project.Id, "N", out _) ||
            string.IsNullOrWhiteSpace(project.Title) || project.Title.Length > 200 ||
            project.Title.Any(char.IsControl) || project.CreatedUtc == default ||
            project.Assets is null || project.Assets.Count > 4096 ||
            project.Authorizations is null || project.Authorizations.Count > 4096)
            throw new InvalidDataException("项目根文件格式或规模无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        foreach (var asset in project.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("项目素材引用校验超时。");
            if (asset is null || !Guid.TryParseExact(asset.Id, "N", out _) || !ids.Add(asset.Id) ||
                asset.Sha256 is not null && (asset.Sha256.Length != 64 || asset.Sha256.Any(c => !char.IsAsciiHexDigit(c))))
                throw new InvalidDataException("项目素材引用无效或重复。");
            _ = ResolvePath(root, asset.Path, cancellationToken);
        }
        var authorizationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in project.Authorizations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("项目外发授权校验超时。");
            if (grant is null || !Guid.TryParseExact(grant.Id, "N", out _) || !authorizationIds.Add(grant.Id) ||
                !ids.Contains(grant.AssetId) || !ValidLabel(grant.Provider) || !ValidLabel(grant.AccountAlias) ||
                !ValidLabel(grant.Capability) || !ValidLabel(grant.Purpose) ||
                grant.Sha256 is null || grant.Sha256.Length != 64 || grant.Sha256.Any(c => !char.IsAsciiHexDigit(c)) ||
                grant.ExpiresUtc == default)
                throw new InvalidDataException("项目外发授权无效。");
        }
    }

    /// <summary>限制授权标识长度，拒绝控制字符和可误入日志的多行文本。</summary>
    private static bool ValidLabel(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
}
