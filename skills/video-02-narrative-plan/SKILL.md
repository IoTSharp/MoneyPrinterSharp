---
name: video-02-narrative-plan
description: Turn an audited software feature set into an audience-specific explainer narrative with a practical chapter order, scope, and risk-aware wording.
---

# 阶段 2：叙事规划

先读总入口的 [共享制作契约](../video-production-series/references/production-contract.md) 和前一阶段的 `feature-audit`。把“功能列表”改写成观众能完成的工作流，而不是逐页报菜单。

## 工作方式

- 明确一个主受众、一个观看目标和一个五分钟内能证明的结果；次要受众只作为旁白措辞调整，不无限加章节。
- 用“问题/场景 → 关键动作 → 可见证据 → 下一步”组织章节。每章只保留一个主信息和最多两个支撑点。
- 先按最终旁白字数估时，保留自然停顿、专有名词和界面阅读时间；不要预先承诺固定镜头秒数。
- 对高风险词建立替换表，例如“系统称重超限指标”与“行政执法认定”分开；演示状态与生产验证分开。
- 规划主持人、教鞭、任务栏和软件画面的分工：人物解释目的，教鞭指向证据，界面承担细节。

## 交付

输出 `narrative-plan.json`，包含受众、观看目标、章节顺序、每章主张、证据 ID、预估字符/秒数、删减优先级和披露语句。给出一个五分钟版本及超时时的删减顺序。

## 阻断条件

如果叙事需要未审计功能、需要把截图状态说成实时生产事实，或五分钟目标只能依赖不自然的高速播读，退回功能审计或压缩范围。
