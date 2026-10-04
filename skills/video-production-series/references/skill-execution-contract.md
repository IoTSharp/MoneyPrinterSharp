# 技能阶段执行契约

契约版本为 `mps.skill-execution-contract.v1`，当前 11 个技能版本均为 `1.0.0`。共享 C# 核心的 `MpsSkillExecutionContractSet.CreateDefault()` 是结构化合同来源；`MpsSkillExecutionContractValidator` 校验固定注册表、文档、输入、前置证据、产物结构及质量门。既有技能 ID、十阶段顺序与 `VideoProduction` CLI 入口保持兼容。

本合同描述可执行阶段需要的信息，不表示所有阶段已接通生产执行器。提供商真实接口、账号权限、价格和样片能力须有各自证据；仅离线合同通过不能把能力标为已实测。

## 合同字段

| 字段 | 约束 |
| --- | --- |
| `skill_id` / `skill_version` | 固定技能 ID 与 `major.minor.patch` 版本，SKILL.md 的 YAML 头必须一致 |
| `stage` | 总入口为 `null`，其余入口对应固定十阶段 |
| `inputs` | 输入名称、类型、必填状态与说明；开始前缺少必需输入时阻断 |
| `prerequisite_evidence` | 前置证据 ID、类别、必需状态与说明；只有实际复核通过的证据可登记为可用 |
| `allowed_csharp_tools` | 当前共享核心的 C# 类型或接口许可集合；不允许 Python、JavaScript 或未知工具 |
| `outputs` | 项目相对产物名称、类型、格式、必需状态、说明及 JSON 顶层必需字段 |
| `quality_gates` | 稳定质量门 ID、检查说明和阻断状态；未提供或未通过阻断门时不得标记阶段通过 |
| `fallback_skill_id` | 失败后最先复核的更早责任阶段；总入口和功能审计为 `null` |
| `document_markers` / `document_sha256` | 必需说明段落及完整文档摘要；任何技能说明变化都需复核合同并更新摘要 |

产物字段校验只确认结构。真实文件、哈希、媒体解码、人工审核和费用对账仍须由相应质量门验证。`false`、空集合或“待确认”不得自动转换为通过证据；质量门结果必须由执行器或人工复核明确提供。

## 阶段对应

| 入口 | 必需输入 | 必需前置证据 | 默认回退入口 |
| --- | --- | --- | --- |
| `video-production-series` | 项目，可选已有状态 | 项目初始化且可重开 | 无 |
| `video-01-feature-audit` | 源码/运行页面/媒体，许可范围 | 主张来源、隐私复核 | 无，暂停对应主张 |
| `video-02-narrative-plan` | 功能审计，受众与目标 | 审计通过、时长预算 | `video-01-feature-audit` |
| `video-03-screenplay` | 叙事规划，证据表 | 叙事通过、镜头证据 ID | `video-02-narrative-plan` |
| `video-04-model-selection` | 讲稿，目录与账号事实 | 能力证据，项目授权预算 | `video-03-screenplay` |
| `video-05-presenter` | 人物说明，模型矩阵 | 肖像授权，绿幕样帧 | `video-04-model-selection` |
| `video-06-narration` | 冻结讲稿，声音选择 | 讲稿哈希，音频试听 | `video-03-screenplay` |
| `video-07-lip-sync` | 人物素材，最终旁白 | 最终音频指纹，恢复任务记录 | `video-06-narration` |
| `video-08-composition` | 真实屏幕，透明人物，时间线 | alpha 探测，同一 scene_id 对齐 | `video-07-lip-sync` |
| `video-09-quality-review` | 成片，证据与披露 | 实际探测/解码，人工复核 | `video-08-composition` |
| `video-10-cost-delivery` | 质量报告，任务账本，交付文件 | 稳定任务去重，文件校验和 | `video-09-quality-review` |

默认回退入口是修复起点。质量复核的问题记录还应保存实际责任阶段；若确认问题来自更早输入，应先使相关下游失效再回退复核，不能盲目重渲染或重提外部任务。

## C# 工具边界

`ProjectDirectory`、`ManifestValidator`、`AssetIndexer`、`MpsProductionPlan`、`ModelCatalogCache`、`ProviderModelRouter`、`MpsBudgetLedger`、`ProviderAsyncTaskCoordinator`、`IProviderAdapter`、`MediaTools`、`MediaWorkflows`、`MpsTimelineValidation`、`MpsProjectDiagnostics`、`CostAccounting` 与 `ClipTimeMapping` 均是当前核心中已有的 C# 类型或接口。具体技能只获准使用其合同列出的子集，阶段执行器调用前仍须通过模型锁定、外发授权和预算预检。

工具许可不会产生供应商授权。提交意图必须先登记；等待默认 30 秒，最高 5 分钟；轮询同时有次数、墙钟、取消和退避边界。超时、中止、未知费用或任务状态未知时，只能恢复查询已有任务，不能自动重发。

## 文档变更校验

校验只读取固定 11 个 SKILL.md 及本说明，不递归扫描。每份文档默认最大 64 KiB，总校验默认最多 5 秒并支持取消。摘要采用 UTF-8、统一 LF 换行的完整文档 SHA-256，所以常规 Windows/Unix 换行转换不影响合同。

修改技能说明时同步复核输入、证据、工具、输出、质量门与回退入口，再更新结构化合同和 `SkillContractDocumentDigests`。离线测试检查合同 JSON 往返、错误字段、固定入口、缺失证据、阻断门、产物字段、文档变化与取消；任一文档变化若没有同步摘要会报 `document.changed` 或 `document.shared_changed`。

旧 CLI 的 `project.json` 仍是 `Models.cs` 的兼容清单。阶段产物可同时保存 `screenplay.json` 和 `shot-list.json`，通过稳定 scene_id 与清单关联；不得把完整新阶段产物硬塞进旧清单的未知字段。
