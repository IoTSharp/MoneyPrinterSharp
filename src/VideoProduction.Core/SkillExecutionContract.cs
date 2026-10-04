using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

/// <summary>一个阶段输入的名称、类型和必填约束。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSkillInputRequirement
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";
}

/// <summary>阶段开始前必须存在或复核的证据。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSkillEvidenceRequirement
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";
}

/// <summary>阶段可以调用的 C# 核心工具或接口名称。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSkillToolPermission
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = "";
}

/// <summary>阶段产物的结构、格式和是否必须交付。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSkillOutputRequirement
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("format")]
    public string Format { get; init; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>JSON 产物顶层必须包含的字段；媒体和目录产物为空。</summary>
    [JsonPropertyName("required_fields")]
    public List<string> RequiredFields { get; init; } = [];
}

/// <summary>必须满足的质量门；阻断门未通过时阶段不得标记通过。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSkillQualityGate
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("blocking")]
    public bool Blocking { get; init; } = true;
}

/// <summary>一个技能的可执行阶段合同。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSkillExecutionContract
{
    public const string CurrentVersion = "1.0.0";

    [JsonPropertyName("skill_id")]
    public string SkillId { get; init; } = "";

    [JsonPropertyName("skill_version")]
    public string SkillVersion { get; init; } = CurrentVersion;

    [JsonPropertyName("stage")]
    public MpsProductionStage? Stage { get; init; }

    [JsonPropertyName("inputs")]
    public List<MpsSkillInputRequirement> Inputs { get; init; } = [];

    [JsonPropertyName("prerequisite_evidence")]
    public List<MpsSkillEvidenceRequirement> PrerequisiteEvidence { get; init; } = [];

    [JsonPropertyName("allowed_csharp_tools")]
    public List<MpsSkillToolPermission> AllowedCSharpTools { get; init; } = [];

    [JsonPropertyName("outputs")]
    public List<MpsSkillOutputRequirement> Outputs { get; init; } = [];

    [JsonPropertyName("quality_gates")]
    public List<MpsSkillQualityGate> QualityGates { get; init; } = [];

    /// <summary>失败后应回退复核的技能入口；总入口和第一阶段没有回退阶段。</summary>
    [JsonPropertyName("fallback_skill_id")]
    public string? FallbackSkillId { get; init; }

    [JsonPropertyName("document_markers")]
    public List<string> DocumentMarkers { get; init; } = [];

    /// <summary>对应 SKILL.md 的完整 SHA-256，文档修改必须同步更新合同。</summary>
    [JsonPropertyName("document_sha256")]
    public string DocumentSha256 { get; init; } = "";

    /// <summary>返回稳定的 JSON，供项目快照和文档校验使用。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, MpsSkillExecutionContractJson.Options);
}

/// <summary>11 个技能执行合同的固定集合。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MpsSkillExecutionContractSet
{
    public const string SchemaVersion = "mps.skill-execution-contract.v1";

    [JsonPropertyName("schema_version")]
    public string SchemaVersionValue { get; init; } = SchemaVersion;

    [JsonPropertyName("shared_document_sha256")]
    public string SharedDocumentSha256 { get; init; } = SkillContractDocumentDigests.Shared;

    [JsonPropertyName("contracts")]
    public List<MpsSkillExecutionContract> Contracts { get; init; } = [];

    /// <summary>创建与固定注册表顺序一致的 11 个默认合同。</summary>
    public static MpsSkillExecutionContractSet CreateDefault() => new()
    {
        Contracts = SkillContractDefaults.All.Select(CreateContract).ToList()
    };

    public static MpsSkillExecutionContractSet Default => CreateDefault();

    /// <summary>按固定技能 ID 读取合同，未知 ID 明确报错。</summary>
    public MpsSkillExecutionContract Get(string skillId) => Contracts.FirstOrDefault(item => string.Equals(item.SkillId, skillId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"技能执行合同不存在：{skillId}。");

    /// <summary>序列化完整合同集合供快照保存和离线校验。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, MpsSkillExecutionContractJson.Options);

    /// <summary>复制默认定义，隔离不同项目中的可变集合。</summary>
    private static MpsSkillExecutionContract CreateContract(SkillContractDefault value) => new()
    {
        SkillId = value.SkillId,
        SkillVersion = MpsSkillExecutionContract.CurrentVersion,
        Stage = value.Stage,
        // 每次复制集合，避免一个项目修改默认合同后影响其他项目。
        Inputs = value.Inputs.Select(item => new MpsSkillInputRequirement { Name = item.Name, Kind = item.Kind, Required = item.Required, Description = item.Description }).ToList(),
        PrerequisiteEvidence = value.Evidence.Select(item => new MpsSkillEvidenceRequirement { Id = item.Id, Kind = item.Kind, Required = item.Required, Description = item.Description }).ToList(),
        AllowedCSharpTools = value.Tools.Select(item => new MpsSkillToolPermission { Name = item.Name, Purpose = item.Purpose }).ToList(),
        Outputs = value.Outputs.Select(item => new MpsSkillOutputRequirement { Name = item.Name, Kind = item.Kind, Format = item.Format, Required = item.Required, Description = item.Description, RequiredFields = item.RequiredFields.ToList() }).ToList(),
        QualityGates = value.Gates.Select(item => new MpsSkillQualityGate { Id = item.Id, Description = item.Description, Blocking = item.Blocking }).ToList(),
        FallbackSkillId = value.FallbackSkillId,
        DocumentMarkers = value.DocumentMarkers.ToList(),
        DocumentSha256 = SkillContractDocumentDigests.BySkillId[value.SkillId]
    };

    private sealed record SkillContractDefault(
        string SkillId,
        MpsProductionStage? Stage,
        List<MpsSkillInputRequirement> Inputs,
        List<MpsSkillEvidenceRequirement> Evidence,
        List<MpsSkillToolPermission> Tools,
        List<MpsSkillOutputRequirement> Outputs,
        List<MpsSkillQualityGate> Gates,
        string? FallbackSkillId,
        List<string> DocumentMarkers);

    private static class SkillContractDefaults
    {
        /// <summary>创建有名称和类型的阶段输入。</summary>
        private static MpsSkillInputRequirement Input(string name, string kind, string description, bool required = true) => new() { Name = name, Kind = kind, Required = required, Description = description };
        /// <summary>创建可追溯的阶段前置证据。</summary>
        private static MpsSkillEvidenceRequirement Evidence(string id, string kind, string description, bool required = true) => new() { Id = id, Kind = kind, Required = required, Description = description };
        /// <summary>创建明确用途的 C# 工具许可。</summary>
        private static MpsSkillToolPermission Tool(string name, string purpose) => new() { Name = name, Purpose = purpose };
        /// <summary>创建阶段产物结构及最低字段要求。</summary>
        private static MpsSkillOutputRequirement Output(string name, string kind, string format, string description, bool required = true) => new() { Name = name, Kind = kind, Format = format, Required = required, Description = description, RequiredFields = OutputFields(name, format) };
        /// <summary>创建稳定 ID 的阶段质量门。</summary>
        private static MpsSkillQualityGate Gate(string id, string description, bool blocking = true) => new() { Id = id, Description = description, Blocking = blocking };

        /// <summary>JSON 产物保留明确字段，不能只由文件名表示完成。</summary>
        private static List<string> OutputFields(string name, string format) => format != "JSON" ? [] : name switch
        {
            "production-plan.json" => ["schema_version", "registry_schema_version", "entries", "artifacts"],
            "stage-status.json" => ["schema_version", "skills", "updated_utc"],
            "feature-audit.json" => ["features", "evidence", "excluded_claims"],
            "narrative-plan.json" => ["audience", "goal", "chapters", "cut_priority", "disclosures"],
            "screenplay.json" => ["version", "frozen", "scenes", "text_hash"],
            "shot-list.json" => ["scenes", "evidence_ids", "screens", "safe_regions"],
            "model-matrix.json" => ["models", "capability_evidence", "sources", "observed_utc", "selection", "budget"],
            "presenter-manifest.json" => ["identity", "assets", "source", "authorization", "samples"],
            "narration-manifest.json" => ["script_version", "text_hash", "voice", "segments", "review"],
            "timings.json" => ["scenes", "audio_hashes", "measured_durations", "subtitle_boundaries"],
            "lip-sync-map.json" => ["segments", "audio_hashes", "task_records", "lip_synced"],
            "lip-sync-report.json" => ["samples", "duration_checks", "blockers", "disclosures"],
            "composition-manifest.json" => ["input_hashes", "canvas", "encoding", "audio", "subtitles", "render_record"],
            "quality-review.json" => ["status", "probe", "decode", "samples", "issues", "reviewers"],
            "cost-report.json" => ["tasks", "currencies", "estimated", "confirmed", "unknown", "disclosures"],
            "delivery-manifest.json" => ["files", "checksums", "sources", "disclosures", "versions", "reproduce"],
            _ => ["schema_version", "items"]
        };

        public static IReadOnlyList<SkillContractDefault> All { get; } =
        [
            new("video-production-series", null,
                [Input("project", "mps-project", "可重开的项目目录与当前会话。"), Input("stage-status", "plan", "11 个技能的阶段状态与已有产物。", false)],
                [Evidence("project.initialized", "project", "项目清单可读取且未包含凭据。", true)],
                [Tool("MpsSkillRegistry", "读取固定 11 个技能入口和依赖。"), Tool("MpsProductionPlan", "创建、恢复和校验阶段计划。")],
                [Output("production-plan.json", "plan", "JSON", "可恢复的 11 项阶段计划。"), Output("stage-status.json", "status", "JSON", "阶段状态、产物引用和阻断原因。")],
                [Gate("registry-valid", "技能 ID、顺序和依赖与固定注册表一致。"), Gate("recoverable", "计划可序列化并在重开后继续。"), Gate("no-secrets", "计划不含凭据、原始响应或有效签名地址。")],
                null, ["## 路由", "## 编排方法"]),
            new("video-01-feature-audit", MpsProductionStage.FeatureAudit,
                [Input("source", "repository-or-runtime", "用户许可的源码仓库、运行页面或脱敏截图。"), Input("project-scope", "authorization", "允许读取与外发的素材范围。" )],
                [Evidence("source.trace", "evidence", "每条主张都能回到源码、运行页面或素材。"), Evidence("privacy.review", "privacy", "截图和日志已脱敏。" )],
                [Tool("ProjectDirectory", "读取项目与素材引用。"), Tool("ManifestValidator", "校验项目清单和路径。"), Tool("AssetIndexer", "记录素材哈希与技术参数。")],
                [Output("feature-audit.json", "evidence", "JSON", "功能主张、证据、状态和限制。"), Output("claim-disclosures.md", "disclosure", "Markdown", "不能在视频中宣称的内容清单。")],
                [Gate("evidence-traceable", "主张、屏幕来源、版本和限制可追溯。"), Gate("privacy-safe", "不含未脱敏生产数据。"), Gate("unsupported-separated", "代码证据、画面证据和待确认状态分开。")],
                null, ["## 工作方式", "## 交付", "## 阻断条件"]),
            new("video-02-narrative-plan", MpsProductionStage.IntroductionOrder,
                [Input("feature-audit", "feature-audit", "已审计的功能证据和不可宣称清单。"), Input("audience", "brief", "主受众、观看目标和时长预算。" )],
                [Evidence("audit.passed", "stage-artifact", "功能主张已通过证据门。"), Evidence("duration.budget", "constraint", "五分钟内的目标与删减顺序。" )],
                [Tool("MpsProductionPlan", "读取前置阶段状态。"), Tool("ProjectDirectory", "保存叙事产物引用。")],
                [Output("narrative-plan.json", "plan", "JSON", "受众、章节、证据、时长和披露。")],
                [Gate("single-audience", "每次计划只有一个主受众和观看目标。"), Gate("evidence-only", "章节主张只引用已审计证据。"), Gate("natural-duration", "估时包含自然停顿和界面阅读时间。")],
                "video-01-feature-audit", ["## 工作方式", "## 交付", "## 阻断条件"]),
            new("video-03-screenplay", MpsProductionStage.ScriptStoryboard,
                [Input("narrative-plan", "narrative", "已确定章节顺序、取舍和披露语句。"), Input("feature-audit", "evidence", "章节所引用的稳定证据 ID。" )],
                [Evidence("narrative.passed", "stage-artifact", "叙事规划已通过。"), Evidence("evidence.ids", "trace", "每个镜头都存在证据引用。" )],
                [Tool("MpsProductionPlan", "校验阶段依赖和失效传播。"), Tool("ProjectDirectory", "保存讲稿与镜头表。"), Tool("ClipTimeMapping", "验证镜头时间坐标。")],
                [Output("screenplay.json", "screenplay", "JSON", "逐章可朗读文本、字幕和动作。"), Output("shot-list.json", "shot-list", "JSON", "镜头、真实屏幕来源与目标区域。")],
                [Gate("frozen-text", "送入配音的文本已冻结并带版本。"), Gate("screen-source", "镜头引用真实界面或明确披露的替代素材。"), Gate("readable-layout", "人物、字幕和画面不遮挡关键区域。")],
                "video-02-narrative-plan", ["## 工作方式", "## 交付", "## 阻断条件"]),
            new("video-04-model-selection", MpsProductionStage.ModelSelection,
                [Input("screenplay", "screenplay", "冻结讲稿、镜头和输入媒体。"), Input("provider-catalog", "catalog", "公开目录、账号状态和能力证据。" )],
                [Evidence("capability.verified", "provider-evidence", "目标能力有公开或实测证据。"), Evidence("budget.authorized", "authorization", "提供商、模型、次数和预算已授权。" )],
                [Tool("ModelCatalogCache", "读取有来源和 TTL 的模型目录。"), Tool("ProviderModelRouter", "执行推荐或人工锁定解析。"), Tool("MpsBudgetLedger", "预检预算和币种。")],
                [Output("model-matrix.json", "matrix", "JSON", "能力、输入限制、费用、证据和候选方案。")],
                [Gate("capability-evidence", "不由模型名称猜测能力。"), Gate("budget-known", "费用未知或越界时不自动提交。"), Gate("locked-model", "锁定模型不可用时暂停交由用户选择。")],
                "video-03-screenplay", ["## 比较维度", "## 规则", "## 交付", "## 阻断条件"]),
            new("video-05-presenter", MpsProductionStage.Presenter,
                [Input("presenter-brief", "asset-brief", "人物身份、服装、站位、教鞭和镜头范围。"), Input("model-matrix", "selection", "已选且满足能力的模型或本地素材方案。" )],
                [Evidence("portrait.authorization", "license", "人物素材来源和授权说明。"), Evidence("green-screen.sample", "media", "纯绿背景样帧可验证。" )],
                [Tool("AssetIndexer", "建立人物母版和动作片段索引。"), Tool("MediaTools", "探测视频、alpha 和帧参数。"), Tool("ProviderAsyncTaskCoordinator", "读取已登记的生成任务状态。")],
                [Output("presenter-manifest.json", "manifest", "JSON", "人物母版、动作片段、来源和授权。"), Output("presenter-alpha.webm", "media", "WebM-alpha", "可供合成的透明中间件。")],
                [Gate("identity-stable", "人物身份、服装、手和教鞭在抽查中稳定。"), Gate("alpha-verifiable", "绿幕与透明输出可探测。"), Gate("licensed-source", "无未经授权的第三方肖像。")],
                "video-04-model-selection", ["## 工作方式", "## 交付", "## 阻断条件"]),
            new("video-06-narration", MpsProductionStage.Narration,
                [Input("screenplay", "frozen-screenplay", "冻结讲稿和读音提示。"), Input("voice-selection", "selection", "已锁定的声音、速度和提供商。" )],
                [Evidence("script.hash", "trace", "旁白文本哈希与讲稿版本一致。"), Evidence("audio.sample", "media", "试听样片可解码并通过人工复核。" )],
                [Tool("IProviderAdapter", "通过统一能力契约请求或读取音频任务。"), Tool("MediaTools", "探测 PCM 时长和音频参数。"), Tool("ProjectDirectory", "保存分章音频与时序。")],
                [Output("narration-manifest.json", "manifest", "JSON", "声音、文本哈希、请求摘要和实测时长。"), Output("timings.json", "timing", "JSON", "章节与字幕时间边界。"), Output("narration.wav", "media", "WAV", "最终旁白音频。")],
                [Gate("text-hash", "文本哈希与冻结讲稿一致。"), Gate("decoded-audio", "音频可解码且章节时长在叙事预算内。"), Gate("pronunciation-review", "术语、数字和中英文发音已复核。")],
                "video-03-screenplay", ["## 工作方式", "## 交付", "## 阻断条件"]),
            new("video-07-lip-sync", MpsProductionStage.LipSync,
                [Input("presenter", "presenter-media", "可合成的人物视频或透明片段。"), Input("final-narration", "audio", "最终旁白分段和哈希。" )],
                [Evidence("audio.final", "trace", "每段任务绑定同一最终旁白哈希。"), Evidence("task.recovery", "task-record", "任务号、状态、超时和下载校验可恢复。" )],
                [Tool("ProviderAsyncTaskCoordinator", "提交前登记并有界恢复异步任务。"), Tool("IProviderAdapter", "调用统一口型能力契约。"), Tool("MediaTools", "比较音视频时长与抽查帧。")],
                [Output("lip-sync-map.json", "mapping", "JSON", "段落、音频哈希、任务号、输出和同步状态。"), Output("lip-sync-report.json", "review", "JSON", "开头、中段、结尾同步抽查。")],
                [Gate("same-audio", "口型输入是最终旁白而非临时音频。"), Gate("bounded-recovery", "轮询有次数、总时限、取消和未知状态。"), Gate("decoded-output", "每段输出可解码且音画时长一致。")],
                "video-06-narration", ["## 工作方式", "## 交付", "## 阻断条件"]),
            new("video-08-composition", MpsProductionStage.Compositing,
                [Input("screen-capture", "real-screen-media", "真实软件截图或录屏。"), Input("presenter-alpha", "alpha-media", "已验证的透明人物中间件。"), Input("timeline", "timeline", "统一项目时间线和字幕。" )],
                [Evidence("alpha.probe", "media", "透明通道和关键像素已验证。"), Evidence("timeline.aligned", "timeline", "镜头、旁白、字幕和口型使用同一 scene_id。" )],
                [Tool("MediaWorkflows", "建立有界 FFmpeg 合成计划。"), Tool("MediaTools", "探测透明通道、流和时长。"), Tool("MpsTimelineValidation", "读取轨道和片段排序。")],
                [Output("composition-manifest.json", "manifest", "JSON", "滤镜、输入哈希、画幅、编码和音轨。"), Output("final-video.mp4", "media", "MP4", "背景合成最终视频。"), Output("subtitles.srt", "subtitle", "SRT", "与最终旁白对齐的字幕。")],
                [Gate("real-screen", "界面来自真实截图或录屏且文字可读。"), Gate("alpha-preserved", "透明人物、脚底和教鞭无异常。"), Gate("a-v-aligned", "音频、字幕、画面和口型时长一致。")],
                "video-07-lip-sync", ["## 工作方式", "## 交付", "## 阻断条件"]),
            new("video-09-quality-review", MpsProductionStage.QualityReview,
                [Input("composition", "rendered-media", "背景合成视频、字幕和渲染记录。"), Input("evidence-set", "evidence", "功能主张、来源和披露清单。" )],
                [Evidence("media.decode", "probe", "实际容器探测与完整解码结果。"), Evidence("content.review", "review", "人工抽帧、音频和口型复核记录。" )],
                [Tool("MediaTools", "执行 FFprobe 探测和 FFmpeg 解码验证。"), Tool("MpsProjectDiagnostics", "检查项目引用、证据和轨道诊断。"), Tool("CostAccounting", "检查费用去重与披露。")],
                [Output("quality-review.json", "review", "JSON", "阻断项、接受项、复现时间码和最终状态。"), Output("keyframes/", "evidence", "PNG", "首中尾帧和关键转场抽帧。")],
                [Gate("decoded", "视频完整解码，无黑帧和缺失音轨。"), Gate("readable", "交付分辨率下界面、字幕和人物不遮挡。"), Gate("disclosed", "模型、AI 素材、演示数据和备用片段已披露。")],
                "video-08-composition", ["## 检查", "## 交付", "## 阻断条件"]),
            new("video-10-cost-delivery", MpsProductionStage.CostDelivery,
                [Input("quality-review", "review", "已通过或明确阻断项的质量报告。"), Input("task-records", "task-ledger", "供应商任务记录、用量和成本。"), Input("delivery-files", "media-package", "视频、字幕、项目和校验和。" )],
                [Evidence("task.unique", "billing", "按稳定任务号去重并区分提交、查询和下载。"), Evidence("delivery.checksums", "integrity", "交付文件校验和可复核。" )],
                [Tool("MpsBudgetLedger", "按币种预留、确认、未知和对账。"), Tool("CostAccounting", "生成脱敏费用汇总。"), Tool("ProjectDirectory", "组装可重开的交付包。")],
                [Output("cost-report.json", "report", "JSON", "估算、实际、未知金额、币种和任务去重。"), Output("delivery-manifest.json", "manifest", "JSON", "文件、校验和、来源、披露和复现说明。"), Output("delivery/", "package", "directory", "可播放视频、字幕、项目清单和质量报告。")],
                [Gate("billing-deduplicated", "同一任务号不重复计费。"), Gate("currency-separated", "不同币种不直接相加。"), Gate("secret-free", "交付包不含凭据、原始响应或有效签名 URL。")],
                "video-09-quality-review", ["## 工作方式", "## 交付", "## 阻断条件"])
        ];
    }
}

internal static class MpsSkillExecutionContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>固定枚举字符串并拒绝未知 JSON 字段。</summary>
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonFiles.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}

/// <summary>人工复核技能说明后的固定摘要；文本改动必须同步更新。</summary>
internal static class SkillContractDocumentDigests
{
    public const string Shared = "5783afc5e476a76fcc95e9c58012a017bccb904846eaf48d2bff94369ed175d4";
    public static IReadOnlyDictionary<string, string> BySkillId { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["video-production-series"] = "f8cb3208a369c3fcc7343ba5cb6657f2adf9dcfe4bc0469402f4e34e61804395",
        ["video-01-feature-audit"] = "eff17d04949ddeb5d94abd09e1f035ee20ff7328734710913f115593058bd923",
        ["video-02-narrative-plan"] = "b89cb90131bca80a65eecb25d8cc8d9dcb3c91655ffd8e5af6a2a4fb321ca9bf",
        ["video-03-screenplay"] = "a8e67d3b243cfa17e47294a530452e6cceb7d7e71995b06d5ffec837464e04b7",
        ["video-04-model-selection"] = "2c1eb51d8fbf436c2e4054c42d41246e7e6fc49e67d6e305654f08a42be14413",
        ["video-05-presenter"] = "bd9a6e7f78a8817dad13e6fd49a8748e9b83304f9c6c8aafe6cb72ee898acf3e",
        ["video-06-narration"] = "4e1af568df7a00d9908a0a78c3e1c7f1b29afe500ac9d96ca48205a2f77ded78",
        ["video-07-lip-sync"] = "76d6d96a5773ba6ead838024d76048cb4d37c6dc3fc857ff1ed89912717150f6",
        ["video-08-composition"] = "1f1c2e232e75ee325ee26ff20ee172aa4c4d1094b57a9d2c664d1d02904c92ad",
        ["video-09-quality-review"] = "3544750ae19965881eabc9a2bc97f28f4547e3663ab6f1e56101b89e49c8acb4",
        ["video-10-cost-delivery"] = "9d76bed8027739744c69d5acf0ffa84cf71cbe6db93e1608f178079785b51cd1"
    };
}
