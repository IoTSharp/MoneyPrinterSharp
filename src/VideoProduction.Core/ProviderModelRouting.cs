namespace VideoProduction;

/// <summary>模型候选的账号与性能证据；缺失的数值保持未知。</summary>
public sealed record ProviderModelCandidate(
    string ProviderId,
    string AccountAlias,
    MpsModelDescriptor Descriptor,
    ProviderAdapterAvailability AccountAvailability,
    double? QualityScore = null,
    double? SpeedScore = null,
    decimal? EstimatedPrice = null,
    string? Currency = null)
{
    /// <summary>校验候选边界，不根据模型名称猜测能力。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(ProviderId) || ProviderId.Length > 128 || ProviderId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(AccountAlias) || AccountAlias.Length > 128 || AccountAlias.Any(char.IsControl))
            throw new InvalidDataException("模型候选的提供商或账号无效。");
        ArgumentNullException.ThrowIfNull(Descriptor);
        Descriptor.Validate(cancellationToken);
        if (AccountAvailability is not (ProviderAdapterAvailability.Available or ProviderAdapterAvailability.Unavailable or ProviderAdapterAvailability.Unknown))
            throw new InvalidDataException("模型候选的账号状态无效。");
        ValidateScore(QualityScore, "质量");
        ValidateScore(SpeedScore, "速度");
        if (EstimatedPrice is < 0) throw new InvalidDataException("模型估算费用无效。");
        if (Currency is not null && (Currency.Length is < 1 or > 16 || Currency.Any(char.IsControl)))
            throw new InvalidDataException("模型费用币种无效。");
        if (EstimatedPrice is not null && string.IsNullOrWhiteSpace(Currency))
            throw new InvalidDataException("有金额时必须同时提供币种。");
    }

    private static void ValidateScore(double? value, string name)
    {
        if (value is double.NaN or double.PositiveInfinity or double.NegativeInfinity or < 0 or > 100)
            throw new InvalidDataException($"模型{name}评分无效。");
    }
}

/// <summary>模型推荐的受限输入；候选数量和预算均有明确上限。</summary>
public sealed record ProviderModelRecommendationRequest(
    MpsCapabilityKind Capability,
    IReadOnlyList<ProviderModelCandidate> Candidates,
    decimal? HardBudget = null,
    string? Currency = null,
    int MaxCandidates = 256)
{
    /// <summary>验证推荐输入并拒绝未知能力。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(Capability) || Capability == MpsCapabilityKind.Unknown)
            throw new InvalidDataException("模型推荐不能使用未知能力。");
        if (MaxCandidates is < 1 or > 256 || Candidates is null || Candidates.Count > MaxCandidates)
            throw new InvalidDataException("模型候选数量超出上限。");
        if (HardBudget is < 0) throw new InvalidDataException("模型推荐预算无效。");
        if (HardBudget is not null && string.IsNullOrWhiteSpace(Currency))
            throw new InvalidDataException("设置硬预算时必须提供币种。");
        if (Currency is not null && (Currency.Length is < 1 or > 16 || Currency.Any(char.IsControl)))
            throw new InvalidDataException("模型推荐币种无效。");
        foreach (var candidate in Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidate.Validate(cancellationToken);
        }
    }
}

/// <summary>推荐结果；没有满足硬约束的候选时返回空结果并说明原因。</summary>
public sealed record ProviderModelRecommendation(
    ProviderModelCandidate? Candidate,
    string Reason,
    bool RequiresUserChoice)
{
    /// <summary>是否选择了可直接调用的候选。</summary>
    public bool IsSelected => Candidate is not null;
}

/// <summary>按能力、账号状态、证据、质量、速度和费用做确定性推荐。</summary>
public static class ProviderModelRouter
{
    /// <summary>推荐只读候选，不发起探测或生成请求。</summary>
    public static ProviderModelRecommendation Recommend(ProviderModelRecommendationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(cancellationToken);
        var eligible = request.Candidates
            .Where(candidate => candidate.AccountAvailability == ProviderAdapterAvailability.Available)
            .Where(candidate => candidate.Descriptor.Capabilities.Contains(request.Capability))
            .Where(candidate => candidate.Descriptor.EvidenceStatus is MpsModelEvidenceStatus.AccountCallable or MpsModelEvidenceStatus.Measured)
            .Where(candidate => request.HardBudget is null || candidate.EstimatedPrice is not null &&
                                string.Equals(candidate.Currency, request.Currency, StringComparison.OrdinalIgnoreCase) &&
                                candidate.EstimatedPrice <= request.HardBudget)
            .OrderByDescending(candidate => candidate.Descriptor.EvidenceStatus == MpsModelEvidenceStatus.Measured)
            .ThenByDescending(candidate => candidate.QualityScore ?? -1)
            .ThenByDescending(candidate => candidate.SpeedScore ?? -1)
            .ThenBy(candidate => candidate.EstimatedPrice ?? decimal.MaxValue)
            .ThenBy(candidate => candidate.ProviderId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.AccountAlias, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Descriptor.ModelId, StringComparer.Ordinal)
            .Take(1)
            .ToArray();
        if (eligible.Length == 1)
            return new ProviderModelRecommendation(eligible[0], "候选具备账号可用性和能力证据，且满足预算边界。", false);
        var reason = request.HardBudget is not null
            ? "没有同时满足账号可用性、能力证据、币种和硬预算的模型。"
            : "没有同时满足账号可用性和能力证据的模型。";
        return new ProviderModelRecommendation(null, reason, true);
    }
}

/// <summary>人工锁定的模型引用；保存白名单标识，不保存凭据。</summary>
public sealed record ProviderModelLock(
    string Scope,
    MpsCapabilityKind Capability,
    string ProviderId,
    string AccountAlias,
    string ModelId)
{
    /// <summary>校验锁定引用。</summary>
    public void Validate(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateText(Scope, 64, "锁定范围");
        ValidateText(ProviderId, 128, "提供商");
        ValidateText(AccountAlias, 128, "账号");
        ValidateText(ModelId, 256, "模型");
        if (!Enum.IsDefined(Capability) || Capability == MpsCapabilityKind.Unknown)
            throw new InvalidDataException("锁定能力不能为未知。");
    }

    private static void ValidateText(string value, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
            throw new InvalidDataException($"{name}无效。");
    }
}

/// <summary>锁定解析状态；不可用时暂停，绝不自动跨提供商切换。</summary>
public sealed record ProviderModelLockResolution(
    ProviderModelLock Lock,
    ProviderModelCandidate? Candidate,
    bool Paused,
    string Reason)
{
    /// <summary>锁定可执行时返回候选。</summary>
    public bool IsUsable => !Paused && Candidate is not null;
}

/// <summary>按当前目录和账号快照解析人工锁定。</summary>
public static class ProviderModelLockResolver
{
    /// <summary>只解析完全匹配的锁定；缺失或失效时返回暂停状态。</summary>
    public static ProviderModelLockResolution Resolve(ProviderModelLock modelLock, IReadOnlyList<ProviderModelCandidate> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelLock);
        ArgumentNullException.ThrowIfNull(candidates);
        modelLock.Validate(cancellationToken);
        if (candidates.Count > 256) throw new InvalidDataException("锁定解析候选数量超出上限。");
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates[index].Validate(cancellationToken);
        }
        var matches = candidates.Where(item =>
            string.Equals(item.ProviderId, modelLock.ProviderId, StringComparison.Ordinal) &&
            string.Equals(item.AccountAlias, modelLock.AccountAlias, StringComparison.Ordinal) &&
            string.Equals(item.Descriptor.ModelId, modelLock.ModelId, StringComparison.Ordinal) &&
            item.Descriptor.Capabilities.Contains(modelLock.Capability)).Take(2).ToArray();
        if (matches.Length == 0)
            return new ProviderModelLockResolution(modelLock, null, true, "锁定模型不在当前目录中，请由用户选择其他模型。");
        if (matches.Length > 1)
            return new ProviderModelLockResolution(modelLock, null, true, "锁定模型存在重复候选，请由用户选择明确账号。");
        var candidate = matches[0];
        candidate.Validate(cancellationToken);
        if (candidate.AccountAvailability != ProviderAdapterAvailability.Available ||
            candidate.Descriptor.EvidenceStatus is not (MpsModelEvidenceStatus.AccountCallable or MpsModelEvidenceStatus.Measured))
            return new ProviderModelLockResolution(modelLock, candidate, true, "锁定模型当前不可用或缺少能力证据，请由用户确认后恢复。");
        return new ProviderModelLockResolution(modelLock, candidate, false, "锁定模型当前可用。");
    }
}
