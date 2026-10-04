---
name: video-production-series
version: 1.0.0
description: Orchestrate an evidence-based software explainer video from feature audit through script, presenter, narration, lip sync, composition, review, cost reconciliation, and delivery.
---

# 视频制作系列的步骤

把软件讲解视频作为一条有阶段门的制作链路管理。先识别用户当前已有的素材和已完成阶段，只补缺失部分；不要因为调用本技能而自动购买、提交外部生成任务或接触生产数据。

开始前读取：

- [共享制作契约](references/production-contract.md)：证据、模型边界、外部任务、时长、透明通道和安全约束。
- [命令行约定](references/cli-reference.md)：定位仓库与调用统一 C# 工具的方法。
- [产物契约](references/artifact-contract.md)：各阶段应交付的文件与可追溯关系。

## 路由

| 阶段 | 使用技能 | 主要出口 |
| --- | --- | --- |
| 1 功能审计 | `$video-01-feature-audit` | 有来源、边界和受众的功能证据表 |
| 2 叙事规划 | `$video-02-narrative-plan` | 讲解目标、信息主线和取舍 |
| 3 讲稿分镜 | `$video-03-screenplay` | 可朗读讲稿、镜头表、字幕与动作说明 |
| 4 模型选型 | `$video-04-model-selection` | 经验证的能力、成本与风险矩阵 |
| 5 主持人素材 | `$video-05-presenter` | 独立纯绿背景的稳定人物母版与动作素材 |
| 6 旁白 | `$video-06-narration` | 与最终讲稿一致的分段音频和实测时长 |
| 7 口型 | `$video-07-lip-sync` | 逐段对应同一旁白的口型视频与任务记录 |
| 8 合成 | `$video-08-composition` | 透明主持人中间件与最终背景合成视频 |
| 9 质量复核 | `$video-09-quality-review` | 媒体、内容、口型、画面和披露验收记录 |
| 10 成本交付 | `$video-10-cost-delivery` | 去重账单、来源说明和可复现交付包 |

## 编排方法

1. 读取现有项目清单和阶段产物，建立“已完成、需复核、缺失”状态，不重复生成已有资产。
2. 按阶段出口推进。任一讲解主张缺证据、最终旁白未冻结或口型未绑定同一音频时，不进入下一依赖阶段。
3. 外部提交前展示供应商、模型、预计费用、预算上限、请求次数和停止条件；只有得到本次授权才提交。
4. 每次外部任务先写记录，再轮询；超时按“状态未知”处理，不把重试当作免费或未执行。
5. 完成后运行项目校验和成片验证，最后按任务号对账并交付来源、披露和已知限制。

若用户只需要一个阶段，直接路由到对应技能；若请求“从头做完”或“做完整讲解视频”，由本技能维持阶段状态并逐项调用。

## 阶段执行契约

`video-production-series` / `1.0.0` 使用 [阶段执行契约](references/skill-execution-contract.md) 和共享核心 `MpsSkillExecutionContractSet`。输入为可重开项目及可选已有状态，前置证据为项目初始化；允许的 C# 工具为 `MpsSkillRegistry`、`MpsProductionPlan`。必需产物为 `production-plan.json`、`stage-status.json`，登记阶段状态、产物、阻断和恢复信息。固定注册表、可恢复性和无秘密质量门未通过时不得推进。总入口没有更早回退阶段，应暂停并路由到责任技能。
