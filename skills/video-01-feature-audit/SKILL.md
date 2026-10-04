---
name: video-01-feature-audit
version: 1.0.0
description: Audit a software repository and its rendered pages to build evidence-backed feature, audience, workflow, and demonstration claims for an explainer video.
---

# 阶段 1：功能审计

为视频建立可追溯的事实底稿。先读总入口的 [共享制作契约](../video-production-series/references/production-contract.md) 和 [产物契约](../video-production-series/references/artifact-contract.md)。

## 工作方式

- 先查看仓库说明、路由、页面组件、API 契约、测试和已有脱敏截图；按用户受众拆分值守、复核、运维和管理场景。
- 每条功能写成“用户目标 → 操作入口 → 可见结果 → 证据路径 → 版本/限制”，区分已实现、原型、配置依赖和待确认。
- 对源码存在但未能在页面重现的能力标记“代码证据”；对截图可见但无法确认当前代码版本的能力标记“画面证据”，不得互相替代。
- 把不可宣称的内容单列：端口可达不等于视频可用，指标不等于行政认定，生产配置不等于演示数据。
- 为每个候选镜头记录屏幕来源、隐私处理、建议时长、观众收益和可能遮挡区域。

## 交付

输出 `feature-audit.json` 或等价表格，至少包含 `feature_id`、`audience`、`claim`、`evidence`、`status`、`limits`、`screen`、`privacy`。同时写一份“不能在视频中说什么”的清单。

## 阻断条件

若功能没有证据、证据含未脱敏生产数据、或只能靠猜测供应商/现场状态解释，则暂停该主张并标为待确认，不进入讲稿阶段。

## 阶段执行契约

`video-01-feature-audit` / `1.0.0` 使用 [阶段执行契约](../video-production-series/references/skill-execution-contract.md)。输入为许可的源码/运行页面/媒体与许可范围；前置证据为来源可追溯及隐私复核。允许 C# 工具为 `ProjectDirectory`、`ManifestValidator`、`AssetIndexer`。必需产物为 `feature-audit.json`（`features`、`evidence`、`excluded_claims`）及 `claim-disclosures.md`。主张来源、隐私安全和状态分层质量门全部通过才能推进；无更早回退阶段，暂停对应主张。
