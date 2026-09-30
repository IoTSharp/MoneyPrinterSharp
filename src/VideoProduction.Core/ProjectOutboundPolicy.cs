using System.Security.Cryptography;

namespace VideoProduction;

/// <summary>检查项目素材的精确外发范围；实际提交仍须经过费用授权与传输边界。</summary>
public static class ProjectOutboundPolicy
{
    /// <summary>默认拒绝；只在账号、能力、用途、素材及当前文件哈希全部匹配时放行。</summary>
    public static async Task<bool> IsAllowedAsync(
        MpsProjectDocument project, string root, string provider, string accountAlias,
        string capability, string purpose, string assetId, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        if (project.Assets is null || project.Authorizations is null ||
            project.Assets.Count > 4096 || project.Authorizations.Count > 4096)
            return false;
        var matches = project.Assets.Where(item => item?.Id == assetId).Take(2).ToArray();
        if (matches.Length != 1) return false;
        var asset = matches[0];
        if (asset.Path is null || asset.Sha256 is null || asset.Sha256.Length != 64 ||
            !asset.Path.StartsWith("assets/source/", StringComparison.Ordinal) &&
            !asset.Path.StartsWith("assets/generated/", StringComparison.Ordinal))
            return false;
        var now = DateTimeOffset.UtcNow;
        var grant = project.Authorizations.FirstOrDefault(item => item is not null &&
            item.AssetId == assetId && item.Provider == provider && item.AccountAlias == accountAlias &&
            item.Capability == capability && item.Purpose == purpose &&
            string.Equals(item.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase) &&
            item.ExpiresUtc > now);
        if (grant is null) return false;
        try
        {
            var path = ProjectDirectory.ResolvePath(root, asset.Path, deadline.Token);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length is < 1 or > 512L * 1024 * 1024) return false;
            var currentHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, deadline.Token));
            return grant.ExpiresUtc > DateTimeOffset.UtcNow &&
                currentHash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
