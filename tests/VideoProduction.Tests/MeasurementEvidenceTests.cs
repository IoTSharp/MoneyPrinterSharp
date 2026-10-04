using System.Text.Json;
using VideoProduction;

public static class MeasurementEvidenceTests
{
    /// <summary>四种实测结论、时间、成本和失效条件必须独立保存。</summary>
    public static void Run()
    {
        LevelsRemainSeparate();
        EvidenceRoundTripPreservesUnknowns();
        InvalidCostsAndSensitiveFieldsAreRejected();
    }

    /// <summary>只读目录不冒充调用成功，免费实测只能使用确认的零成本。</summary>
    private static void LevelsRemainSeparate()
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = MpsMeasurementEvidence.ReadOnly("offline", MpsCapabilityKind.ImageGeneration,
            "https://public.example.test/models", now, now.AddHours(1), "public-v1");
        metadata.Validate();
        Assert(metadata.RequestCount == 0 && !metadata.Cost.IsKnown && metadata.TaskId is null, "目录证据不能冒充调用或免费价格");
        Assert(metadata.IsActiveAt(now.AddMinutes(59)) && !metadata.IsActiveAt(now.AddHours(2)), "过期证据不能保持有效");

        var free = MpsMeasurementEvidence.Call("offline", "lab", "offline-image", MpsCapabilityKind.ImageGeneration,
            MpsMeasurementLevel.FreeMinimalCall, MpsMeasurementOutcome.Passed, "离线免费契约",
            "sha256:free", MpsPriceMetadata.Confirmed(0m, "CNY", "request", "离线确认"), now, "free-task");
        free.Validate();
        var paid = MpsMeasurementEvidence.Call("offline", "lab", "offline-image", MpsCapabilityKind.ImageGeneration,
            MpsMeasurementLevel.BillableMinimalCall, MpsMeasurementOutcome.Passed, "离线小额契约",
            "sha256:paid", MpsPriceMetadata.Estimate(.1m, "CNY", "request", "离线估算"), now, "paid-task");
        paid.Validate();
        var real = MpsMeasurementEvidence.Call("offline", "lab", "offline-image", MpsCapabilityKind.ImageGeneration,
            MpsMeasurementLevel.RealMaterialSample, MpsMeasurementOutcome.Unknown, "本地已许可样片",
            "sha256:real", MpsPriceMetadata.Unknown("尚未对账"), now, limitations: "离线样片不证明供应商真实可用。");
        real.Validate();
        Assert(real.Level != paid.Level && !real.IsActiveAt(now), "真实素材样片与最小计费调用必须独立");
    }

    /// <summary>快照保留未知成本和失效条件，并拒绝未知 JSON 字段。</summary>
    private static void EvidenceRoundTripPreservesUnknowns()
    {
        var evidence = MpsMeasurementEvidence.Call("offline", "lab", "offline-audio", MpsCapabilityKind.SpeechSynthesis,
            MpsMeasurementLevel.BillableMinimalCall, MpsMeasurementOutcome.TimedOut, "离线超时",
            "sha256:timedout", MpsPriceMetadata.Unknown("超时不代表未扣费"), DateTimeOffset.UtcNow, "old-task");
        var store = new MpsMeasurementEvidenceStore();
        store.Upsert(evidence);
        var json = store.ExportJson();
        var reopened = MpsMeasurementEvidenceStore.ReopenJson(json);
        var roundTrip = reopened.Find(evidence.EvidenceId)!;
        Assert(roundTrip.Level == MpsMeasurementLevel.BillableMinimalCall && roundTrip.Outcome == MpsMeasurementOutcome.TimedOut &&
            !roundTrip.Cost.IsKnown && roundTrip.TaskId == "old-task" && roundTrip.InvalidationConditions.Length > 0,
            "实测证据 JSON 往返丢失成本、时间或失效字段");
        ExpectJson(() => MpsMeasurementEvidenceStore.ReopenJson(json.TrimEnd('}') + ",\"raw_response\":\"forbidden\"}"));
    }

    /// <summary>不完整免费价格、签名来源和敏感成本摘要均不进入证据仓库。</summary>
    private static void InvalidCostsAndSensitiveFieldsAreRejected()
    {
        var free = MpsMeasurementEvidence.Call("offline", "lab", "offline-image", MpsCapabilityKind.ImageGeneration,
            MpsMeasurementLevel.FreeMinimalCall, MpsMeasurementOutcome.Passed, "离线契约",
            "sha256:free", MpsPriceMetadata.Estimate(0m, "CNY", "request", "非实付"), DateTimeOffset.UtcNow);
        ExpectInvalid(() => free.Validate());
        var metadata = MpsMeasurementEvidence.ReadOnly("offline", MpsCapabilityKind.ImageGeneration,
            "https://public.example.test/models?sig=forbidden", DateTimeOffset.UtcNow);
        ExpectInvalid(() => metadata.Validate());
        free.Level = MpsMeasurementLevel.BillableMinimalCall;
        free.Cost = MpsPriceMetadata.Confirmed(.1m, "CNY", "request", "secret value");
        ExpectInvalid(() => free.Validate());
        free.Cost = MpsPriceMetadata.Unknown("未确认");
        free.Outcome = MpsMeasurementOutcome.Unknown;
        free.InputFingerprint = "https://private.example.test?token=forbidden";
        ExpectInvalid(() => free.Validate());
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); throw new InvalidOperationException("无效证据没有被拒绝"); }
        catch (InvalidDataException) { }
    }

    private static void ExpectJson(Action action)
    {
        try { action(); throw new InvalidOperationException("证据未知 JSON 字段没有被拒绝"); }
        catch (JsonException) { }
    }
}
