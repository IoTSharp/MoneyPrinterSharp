---
name: video-10-cost-delivery
version: 1.0.0
description: Reconcile media-generation costs by task ID, preserve provider disclosures and checksums, and assemble a reproducible software-video delivery package.
---

# 阶段 10：成本与交付

先读 [成本核算口径](../video-production-series/references/cost-accounting.md)、[产物契约](../video-production-series/references/artifact-contract.md) 和质量复核结果。

## 工作方式

- 读取所有外部任务记录，以供应商稳定 `task_id` 去重；submit、poll、download 不重复计费。
- 按币种保留估算、实际、包含关系和未知值 `null`；没有汇率不混合币种。超时任务保持“状态未知/仍可能扣费”，直到供应商确认。
- 核对最终文件、字幕、alpha 中间件、项目清单、证据表、披露文案和 SHA-256；删除交付包中的密钥、临时签名 URL、生产数据和无关缓存。
- 写清回退素材、模型真实名称、AI 主持人/配音披露、演示数据性质、已知限制和复现命令。

## 交付

输出去重费用表、供应商来源表、校验和清单、版本/环境摘要、交付目录和简短复现说明。最终交付至少包含可播放背景合成视频、字幕、项目清单和质量报告；若承诺透明层，再附 alpha 文件。

## 阻断条件

任务号重复且无法判断包含关系、币种被错误相加、文件校验失败、缺少来源披露、或交付包含有秘密/生产数据时停止交付。

## 阶段执行契约

`video-10-cost-delivery` / `1.0.0` 使用 [阶段执行契约](../video-production-series/references/skill-execution-contract.md)。输入为质量报告、任务账本和交付文件；前置证据为任务去重及文件校验和。允许 C# 工具为 `MpsBudgetLedger`、`CostAccounting`、`ProjectDirectory`。必需产物 `cost-report.json` 包含 `tasks`、`currencies`、`estimated`、`confirmed`、`unknown`、`disclosures`；`delivery-manifest.json` 包含 `files`、`checksums`、`sources`、`disclosures`、`versions`、`reproduce`，并保留 `delivery/`。费用去重、币种分离和无秘密质量门未通过时回退 `video-09-quality-review`。
