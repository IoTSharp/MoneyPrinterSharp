namespace VideoProduction;

/// <summary>定位到项目、轨道或片段的可修复校验诊断。</summary>
public sealed record MpsProjectDiagnostic(string Code, string Path, string Message, bool IsBlocking = true);

/// <summary>在抛出异常前收集完整项目诊断，供 CLI 和桌面属性区复用。</summary>
public static class MpsProjectDiagnostics
{
    private const int MaxDiagnostics = 4096;

    /// <summary>校验项目引用、时间线、授权和证据；每条问题包含稳定路径。</summary>
    public static IReadOnlyList<MpsProjectDiagnostic> Validate(MpsProjectDocument project, string? root = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<MpsProjectDiagnostic>();
        if (project is null)
        {
            diagnostics.Add(new("project.null", "project", "项目对象为空。"));
            return diagnostics;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(project.Title)) Add(diagnostics, "project.title", "title", "项目标题不能为空。");
        if (project.Assets is null) Add(diagnostics, "asset.collection", "assets", "素材集合为空。");
        else
        {
            for (var index = 0; index < project.Assets.Count && diagnostics.Count < MaxDiagnostics; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asset = project.Assets[index];
                if (asset is null) { Add(diagnostics, "asset.null", $"assets[{index}]", "素材引用为空。"); continue; }
                if (string.IsNullOrWhiteSpace(asset.Path)) Add(diagnostics, "asset.path", $"assets[{index}].path", "素材路径不能为空。");
                if (root is not null && !string.IsNullOrWhiteSpace(asset.Path))
                {
                    try { _ = ProjectDirectory.ResolvePath(root, asset.Path, cancellationToken); }
                    catch (Exception error) when (error is InvalidDataException or ArgumentException)
                    { Add(diagnostics, "asset.path.out_of_scope", $"assets[{index}].path", error.Message); }
                }
            }
        }
        try { MpsTimelineValidation.Validate(project, cancellationToken); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        { Add(diagnostics, "timeline.invalid", "tracks", error.Message); }
        try { MpsEvidenceValidation.Validate(project, cancellationToken); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        { Add(diagnostics, "evidence.invalid", "evidence", error.Message); }
        if (project.Authorizations is null) Add(diagnostics, "authorization.collection", "authorizations", "外发授权集合为空。");
        else
        {
            for (var index = 0; index < project.Authorizations.Count && diagnostics.Count < MaxDiagnostics; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var authorization = project.Authorizations[index];
                if (authorization is null) Add(diagnostics, "authorization.null", $"authorizations[{index}]", "外发授权为空。");
                else if (string.IsNullOrWhiteSpace(authorization.AssetId)) Add(diagnostics, "authorization.asset", $"authorizations[{index}].asset_id", "授权必须引用素材。");
            }
        }
        return diagnostics;
    }

    /// <summary>存在任一阻断问题时抛出带路径的单一异常，便于 CLI 展示。</summary>
    public static void ThrowIfInvalid(MpsProjectDocument project, string? root = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = Validate(project, root, cancellationToken);
        if (diagnostics.Count == 0) return;
        var summary = string.Join("；", diagnostics.Take(8).Select(item => $"{item.Path}: {item.Message}"));
        throw new InvalidDataException($"项目校验失败（{diagnostics.Count} 项）：{summary}");
    }

    private static void Add(ICollection<MpsProjectDiagnostic> diagnostics, string code, string path, string message)
    {
        if (diagnostics.Count < MaxDiagnostics) diagnostics.Add(new MpsProjectDiagnostic(code, path, message));
    }
}
