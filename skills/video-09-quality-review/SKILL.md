---
name: video-09-quality-review
description: Review a software explainer video for decodability, readable UI, narrative evidence, presenter consistency, audio quality, lip-sync provenance, and required disclosures.
---

# 阶段 9：质量复核

先读 [质量门](../video-production-series/references/quality-gates.md) 和 [共享制作契约](../video-production-series/references/production-contract.md)。本阶段是独立验收，不以“文件生成成功”作为通过。

## 检查

- 使用 `doctor`、`probe`、`verify` 读取实际容器和解码结果；抽查首帧、中段、尾帧、所有转场和关键操作区域。
- 逐章对照证据与旁白，确认演示数据、AI 主持人/配音、模型来源、备用素材和指标边界已披露。
- 试听音频并检查削波、静音、错读、音量跳变和字幕边界；核对每段口型与最终音频指纹。
- 在交付分辨率检查软件文字、任务栏、教鞭、主持人脚底、标题和字幕是否重叠；黑底/白底抽查 alpha。
- 把问题分为阻断、需用户接受、建议优化；每项写复现时间码、证据、责任阶段和修复结果。

## 交付

输出 `quality-review.json`、抽帧清单、媒体探测结果、音频/口型抽查结果和最终通过状态。未通过时只回退到责任阶段，不盲目重渲染全片。

## 阻断条件

见共享质量门：无法解码、黑帧、缺音轨、明显口型错配、关键界面被遮挡、无证据主张、未披露备用素材、费用重复或凭据泄露均不得标记通过。
