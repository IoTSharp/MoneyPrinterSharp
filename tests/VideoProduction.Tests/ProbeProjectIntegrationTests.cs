using System.Security.Cryptography;
using System.Text;
using VideoProduction;

namespace VideoProductionTests;

/// <summary>授权探测与现有账号、模型锁定及项目外发规则的离线集成验证。</summary>
public static class ProbeProjectIntegrationTests
{
    /// <summary>使用一份本地文本素材，拒绝路径不调用 transport，允许路径只调用一次。</summary>
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "mps-probe-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = ProjectDirectory.Create(root, "离线授权探测");
            var bytes = Encoding.UTF8.GetBytes("本地合成素材，仅用于离线授权合同测试。");
            var asset = new MpsAssetReference { Path = "assets/source/fixture.txt", Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
            project.Assets.Add(asset);
            var assetPath = ProjectDirectory.ResolvePath(root, asset.Path);
            await File.WriteAllBytesAsync(assetPath, bytes, cancellationToken);
            var accounts = new ProviderAccountConfigurationSet();
            accounts.Add(new ProviderAccountConfiguration { ProviderId = "offline", AccountAlias = "demo", CredentialTarget = new("MPS:Offline:Fixture") });
            var descriptor = new MpsModelDescriptor
            {
                ModelId = "offline-image", DisplayName = "离线模型", Modalities = [MpsModelModality.Image],
                Capabilities = [MpsCapabilityKind.ImageGeneration], EvidenceStatus = MpsModelEvidenceStatus.Measured,
                ExecutionMode = MpsModelExecutionMode.Asynchronous, Source = "offline", ObservedUtc = DateTimeOffset.UtcNow
            };
            var candidates = new[] { new ProviderModelCandidate("offline", "demo", descriptor, ProviderAdapterAvailability.Available) };
            var modelLock = new ProviderModelLock("project:" + project.Id, MpsCapabilityKind.ImageGeneration, "offline", "demo", "offline-image");
            var authorization = MpsProbeAuthorization.GrantFor(project.Id, "offline", "demo", "offline-image", MpsCapabilityKind.ImageGeneration,
                MpsMeasurementLevel.RealMaterialSample, "本地合成素材最小探测", 1m, 2m, 2, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var request = new MpsProbeRequest { InputFingerprint = "sha256:" + asset.Sha256, RequestSummary = authorization.RequestSummary,
                Cost = MpsPriceMetadata.Estimate(.5m, "CNY", "request", "offline-estimate") };
            var ledger = new MpsBudgetLedger(2m);
            var plan = new MpsAuthorizedProbePlan(authorization, ledger);
            var gate = new MpsProbeProjectPreflight(project, root, accounts, modelLock, candidates, "capability_probe", request.InputFingerprint, [asset.Id]);
            var adapter = new CountingAdapter();
            var persisted = false;
            MpsAuthorizedProbeCoordinator? coordinator = null;
            coordinator = new(plan, adapter, adapter, gate.CheckAsync, token =>
            {
                token.ThrowIfCancellationRequested();
                Assert(coordinator!.TaskRecovery.ToSnapshot(token).Tasks.Count == 1 && plan.Recovery.ToSnapshot(token).Entries.Count == 1,
                    "transport 调用前必须登记两个提交意图");
                File.WriteAllText(Path.Combine(root, "records", "probe-plan.json"), plan.ExportJson(token));
                File.WriteAllText(Path.Combine(root, "records", "probe-tasks.json"), coordinator.TaskRecovery.ExportJson(token));
                persisted = true;
                return Task.CompletedTask;
            });
            await ExpectDeniedAsync(() => coordinator.ExecuteOrResumeAsync(request, cancellationToken));
            Assert(adapter.SubmitCount == 0 && ledger.EntryCount == 0 && !persisted, "无素材授权必须零请求/零预算/零意图");
            var grant = new MpsOutboundAuthorization
            {
                Provider = "offline", AccountAlias = "demo", Capability = "image_generation", Purpose = "capability_probe",
                AssetId = asset.Id, Sha256 = asset.Sha256, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            project.Authorizations.Add(grant);
            accounts.Accounts[0].Enabled = false;
            await ExpectDeniedAsync(() => coordinator.ExecuteOrResumeAsync(request, cancellationToken));
            accounts.Accounts[0].Enabled = true;
            descriptor.EvidenceStatus = MpsModelEvidenceStatus.PubliclyListed;
            await ExpectDeniedAsync(() => coordinator.ExecuteOrResumeAsync(request, cancellationToken));
            descriptor.EvidenceStatus = MpsModelEvidenceStatus.Measured;
            grant.AccountAlias = "other";
            await ExpectDeniedAsync(() => coordinator.ExecuteOrResumeAsync(request, cancellationToken));
            grant.AccountAlias = "demo";
            await File.WriteAllTextAsync(assetPath, "changed", cancellationToken);
            await ExpectDeniedAsync(() => coordinator.ExecuteOrResumeAsync(request, cancellationToken));
            await File.WriteAllBytesAsync(assetPath, bytes, cancellationToken);
            var result = await coordinator.ExecuteOrResumeAsync(request, cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert(persisted && adapter.SubmitCount == 1 && result.Submitted && result.Attempt?.State == MpsProbeExecutionState.Succeeded &&
                ledger.Confirmed == .2m && ledger.Snapshot().Single().TaskId == "project-probe-task", "允许路径应先保存意图再单次提交并按任务号对账");
            var resumed = await coordinator.ExecuteOrResumeAsync(request, cancellationToken);
            Assert(!resumed.Submitted && adapter.SubmitCount == 1, "同一素材实测重开不得自动重提");
        }
        finally
        {
            // 只删除本测试明确创建的独占项目根，不触碰共享临时目录。
            var verified = Path.GetFullPath(root);
            if (Path.GetDirectoryName(verified) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(verified).StartsWith("mps-probe-integration-", StringComparison.Ordinal))
                throw new InvalidOperationException("测试临时根归属校验失败。");
            if (Directory.Exists(verified)) Directory.Delete(verified, recursive: true);
        }
    }

    /// <summary>只接受授权拒绝，不把其他失败或测试断言当成成功。</summary>
    private static async Task ExpectDeniedAsync(Func<Task<MpsProbeExecutionResult>> action)
    {
        try { await action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("项目预检没有阻止越界请求。");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>立即成功的本地假适配器，返回值只有稳定任务号和确认费用。</summary>
    private sealed class CountingAdapter : IProviderTaskSubmissionAdapter, IProviderTaskStatusAdapter
    {
        public int SubmitCount { get; private set; }

        /// <summary>记录调用次数，不读取真实账号或网络。</summary>
        public Task<ProviderAdapterSubmission> SubmitAsync(ProviderAdapterSubmitRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmitCount++;
            return Task.FromResult(new ProviderAdapterSubmission("project-probe-task", ProviderAdapterTaskStatus.Succeeded, .2m, "CNY", DateTimeOffset.UtcNow));
        }

        /// <summary>只查询已有本地任务。</summary>
        public Task<ProviderAdapterTaskState> GetStatusAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderAdapterTaskState(query.TaskId, ProviderAdapterTaskStatus.Succeeded, .2m, "CNY", ProviderAdapterErrorCategory.Unknown, DateTimeOffset.UtcNow));
        }

        /// <summary>本测试不请求供应商取消。</summary>
        public Task<ProviderAdapterCancellation> CancelAsync(ProviderAdapterTaskQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderAdapterCancellation(query.TaskId, ProviderAdapterTaskStatus.Cancelled, ProviderAdapterErrorCategory.Cancelled, DateTimeOffset.UtcNow));
        }
    }
}
