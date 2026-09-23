---
name: video-04-model-selection
description: Compare image, presenter-motion, TTS, lip-sync, and compositing providers using verified capability, input, duration, privacy, cost, and failure evidence.
---

# 阶段 4：模型选型

先读 [共享制作契约](../video-production-series/references/production-contract.md) 和 [成本核算口径](../video-production-series/references/cost-accounting.md)。本阶段做能力核验，不替用户提交付费任务。

## 比较维度

- 输入：静态图、绿幕视频、音频、外部图片、外部音频、界面视频、透明输出。
- 输出：静态图、自然动作、上半身/全身、中文口型、alpha、时长上限、批量/分段能力。
- 一致性：人物身份、服装、教鞭、镜头、动作和章节间连续性；写明官方文档、实测样片或未知。
- 工程条件：API/网页入口、区域、认证、超时、轮询、下载有效期、失败重试和数据保留。
- 费用：按请求、秒、分辨率和输出格式记录，估算与实际分开，币种不混加。

## 规则

- 官方文档没有证明的能力写“未验证”，不要由产品名称推断音频驱动 Avatar、中文唇形或长视频。
- Moark 的 Qwen、Vidu、Duix 记录实际后端名称；不能把代理调度结果写成 GPT。
- 先用一个 15–25 秒样片验证，再决定整片供应商。不得把静态图片或无口型动作循环当作数字人视频。

## 交付

输出 `model-matrix.json`、来源 URL/观察日期、样片结论、候选/备用方案和提交前需要用户确认的授权、预算与隐私问题。

## 阻断条件

若目标能力只有营销描述、输入限制不满足、费用无法估算到预算，或供应商不能提供可核验的任务状态，则保持未选定。
