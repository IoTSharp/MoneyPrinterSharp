---
name: video-03-screenplay
description: Write a production-ready Chinese software explainer screenplay, shot list, captions, presenter actions, and evidence-linked narration.
---

# 阶段 3：讲稿与分镜

先读叙事规划、[产物契约](../video-production-series/references/artifact-contract.md) 和 [质量门](../video-production-series/references/quality-gates.md)。

## 工作方式

- 为每章生成稳定的 `scene_id`、标题、最终朗读文本、画面路径、目标区域、主持人动作、字幕文案和证据 ID。
- 讲稿按普通话自然口语写；数字、单位、英文缩写、车牌或地名给出读法提示。不要让字幕承担旁白没有说过的新事实。
- 画面优先使用真实截图/录屏；需要强调时用后期高亮、裁切或教鞭，不让生成模型重绘界面文字。
- 设计 15–25 秒试片，覆盖人物站位、教鞭指向、字幕、软件可读性和一段完整口型；试片通过后才扩展全片。
- 把每章旁白拆成适合 Duix/Vidu 等服务的自然段，单段不超过 60 秒，默认约 40 秒并允许按语义调整。

## 交付

输出符合 `Models.cs` 的 `project.json`、逐章讲稿、镜头表和字幕草稿。所有镜头引用 `feature-audit` 中存在的证据；未冻结的文本标记为草稿，不能送入 TTS 或口型。

## 阻断条件

文本与功能证据不一致、镜头缺真实屏幕来源、人物会遮挡关键区域、或章节无法按自然语义切分时，不进入模型提交。
