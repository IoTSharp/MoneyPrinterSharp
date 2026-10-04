namespace VideoProduction;

/// <summary>小额探测的项目预检：复用账号、模型锁定和精确素材授权，不读取凭据。</summary>
public sealed class MpsProbeProjectPreflight
{
    private readonly MpsProjectDocument project;
    private readonly string root;
    private readonly IProviderAccountConfigurationStore accounts;
    private readonly ProviderModelLock modelLock;
    private readonly IReadOnlyList<ProviderModelCandidate> candidates;
    private readonly string purpose;
    private readonly string inputFingerprint;
    private readonly IReadOnlyList<string> assetIds;

    /// <summary>绑定已选项目、锁定模型及有限素材集；预检不自动切换账号或模型。</summary>
    public MpsProbeProjectPreflight(MpsProjectDocument project, string root,
        IProviderAccountConfigurationStore accounts, ProviderModelLock modelLock,
        IReadOnlyList<ProviderModelCandidate> candidates, string purpose, string inputFingerprint,
        IReadOnlyList<string> assetIds)
    {
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        this.root = Path.GetFullPath(root);
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.modelLock = modelLock ?? throw new ArgumentNullException(nameof(modelLock));
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(assetIds);
        if (candidates.Count > 256 || assetIds.Count > 64 || assetIds.Distinct(StringComparer.Ordinal).Count() != assetIds.Count ||
            assetIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)) ||
            string.IsNullOrWhiteSpace(purpose) || purpose.Length > 128 || purpose.Any(char.IsControl))
            throw new InvalidDataException("探测项目预检范围无效。");
        this.candidates = candidates.ToArray();
        this.purpose = purpose;
        this.inputFingerprint = inputFingerprint;
        this.assetIds = assetIds.ToArray();
    }

    /// <summary>在三十秒内核对锁定身份及全部本地素材；失败发生在提交意图和预算预留之前。</summary>
    public async Task CheckAsync(MpsProbeAuthorization authorization, MpsProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        authorization.Validate(deadline.Token);
        request.Validate(deadline.Token);
        if (authorization.ProjectId != project.Id || request.InputFingerprint != inputFingerprint ||
            modelLock.Scope != "project:" + project.Id ||
            modelLock.ProviderId != authorization.ProviderId || modelLock.AccountAlias != authorization.AccountAlias ||
            modelLock.ModelId != authorization.ModelId || modelLock.Capability != authorization.Capability)
            throw new UnauthorizedAccessException("探测项目、输入或账号模型锁定与授权不一致。");
        var account = accounts.Find(authorization.ProviderId, authorization.AccountAlias, deadline.Token);
        if (account is null || !account.Enabled) throw new UnauthorizedAccessException("探测账号未配置或已停用。");
        account.Validate(deadline.Token);
        if (!ProviderModelLockResolver.Resolve(modelLock, candidates, deadline.Token).IsUsable)
            throw new UnauthorizedAccessException("锁定模型当前不可用，需由用户选择后恢复。");
        if (authorization.Level == MpsMeasurementLevel.RealMaterialSample && assetIds.Count == 0)
            throw new UnauthorizedAccessException("真实素材样片必须指定已授权的项目素材。");
        var capability = authorization.Capability switch
        {
            MpsCapabilityKind.TextPlanning => "text_planning",
            MpsCapabilityKind.VisualUnderstanding => "visual_understanding",
            MpsCapabilityKind.ImageGeneration => "image_generation",
            MpsCapabilityKind.SpeechTranscription => "speech_transcription",
            MpsCapabilityKind.SpeechSynthesis => "speech_synthesis",
            MpsCapabilityKind.VideoGeneration => "video_generation",
            MpsCapabilityKind.LipSync => "lip_sync",
            _ => throw new InvalidDataException("探测能力未知。")
        };
        for (var index = 0; index < assetIds.Count && index < 64; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!await ProjectOutboundPolicy.IsAllowedAsync(project, root, authorization.ProviderId,
                authorization.AccountAlias, capability, purpose, assetIds[index], deadline.Token).ConfigureAwait(false))
                throw new UnauthorizedAccessException("探测素材不在有效的精确外发授权范围内。");
        }
        deadline.Token.ThrowIfCancellationRequested();
    }
}
