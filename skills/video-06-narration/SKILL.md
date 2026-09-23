---
name: video-06-narration
description: Produce or plan segmented Chinese narration from the frozen screenplay, with measured duration, pronunciation notes, subtitles, and provider disclosure.
---

# 阶段 6：旁白

先读最终讲稿、[共享制作契约](../video-production-series/references/production-contract.md) 和 [质量门](../video-production-series/references/quality-gates.md)。

## 工作方式

- 只使用已冻结讲稿；每章单独合成并保留原始音频、请求摘要、模型/声音、速度和实际 PCM 时长。
- 为地名、数字、单位、英文缩写和专业词提供读音提示；普通话女性职业语气优先自然停顿，不追求机械逐字。
- 先试听开场样片，确认音色、音量、发音和速度，再合成全片。任何速度调整都逐章记录，不能用全片 1.4 倍速掩盖脚本超长。
- 依据音频实测章节边界生成字幕；句内若按字数估算，明确标记为估算，必要时用强制对齐工具复核。

## 交付

输出分章 WAV、完整旁白、SRT/VTT、`timings.json`、语音清单和试听结论。旁白清单中的文本哈希必须与讲稿版本一致。

## 阻断条件

服务返回模型/声音不明、音频不可解码、文本哈希不一致、明显错读或音频时长超出叙事预算而未重新规划时，不进入口型。
