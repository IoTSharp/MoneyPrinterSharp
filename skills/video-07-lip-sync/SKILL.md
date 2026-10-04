---
name: video-07-lip-sync
version: 1.0.0
description: Drive presenter motion and mouth movement from the exact final narration in bounded segments, preserving task provenance and synchronization evidence.
---

# 阶段 7：口型同步

先读 [共享制作契约](../video-production-series/references/production-contract.md)、旁白清单和 [质量门](../video-production-series/references/quality-gates.md)。

## 工作方式

- 按章节或自然语义段提交；每段引用唯一音频文件哈希、人物片段、供应商、模型和任务记录。Duix 单段不超过 60 秒，默认约 40 秒。
- 口型输入必须是最终旁白，不能先用临时声音生成再替换音轨。音频改动后，旧口型全部标为失效并重新生成。
- 同时记录动作强度、镜头范围、是否透明输出、时长限制、失败原因和下载校验。轮询有最大次数、墙钟和退避。
- 检查开头、句中、句尾和闭口；逐段比较音频/视频时长，禁止用另一段视频或简单循环冒充同步。

## 交付

输出逐段视频、段落映射表、任务记录、音频指纹、下载校验和同步抽查报告。无法同步的备用片段必须设置 `lip_synced: false` 并在片尾或交付说明披露。

## 阻断条件

任务状态未知、下载内容不可解码、输入音频不是最终版本、口型明显错位、人物/教鞭漂移，或供应商输出不支持后续透明合成时暂停。

## 阶段执行契约

`video-07-lip-sync` / `1.0.0` 使用 [阶段执行契约](../video-production-series/references/skill-execution-contract.md)。输入为人物素材和最终旁白；前置证据为同一最终音频指纹和可恢复任务记录。允许 C# 工具为 `ProviderAsyncTaskCoordinator`、`IProviderAdapter`、`MediaTools`。必需产物 `lip-sync-map.json` 包含 `segments`、`audio_hashes`、`task_records`、`lip_synced`；`lip-sync-report.json` 包含 `samples`、`duration_checks`、`blockers`、`disclosures`。同一音频、有界恢复和输出解码质量门未通过时回退 `video-06-narration`；超时先查询旧任务，禁止自动重提。
