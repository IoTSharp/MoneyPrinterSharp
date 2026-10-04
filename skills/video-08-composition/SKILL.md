---
name: video-08-composition
version: 1.0.0
description: Composite verified software screens, transparent presenter footage, pointer, taskbar, captions, and final audio into a readable explainer video.
---

# 阶段 8：透明合成

先读 [产物契约](../video-production-series/references/artifact-contract.md)、[质量门](../video-production-series/references/quality-gates.md) 和项目清单。

## 工作方式

- 先用 alpha 主持人视频作为中间件，单独验证透明背景、人物脚底、教鞭和字幕层；中间件交付 WebM alpha 或其他实际探测通过的透明格式。
- 软件界面必须来自真实截图/录屏，保持原始文字比例；按镜头表做裁切、平移和高亮，不重绘 UI。
- 任务栏是合成层的一部分，主持人脚底与其上沿对齐；字幕和教鞭避开关键字段。必要时为人物预留安全区。
- 旁白、字幕、口型视频和镜头用同一 `scene_id` 对齐；以最终音频实测时长驱动画面，不用固定五分钟硬截断。
- 最终背景合成输出 MP4/WebM，并保留项目清单、渲染日志和中间 alpha 文件。

## 交付

输出 alpha 中间件、最终视频、字幕、封面/抽帧、渲染记录和可复现命令。记录分辨率、帧率、编码器、音轨、透明通道和变速参数。

## 阻断条件

alpha 丢失、界面不可读、人物遮挡关键内容、章节跳切、音画时长不一致、或中间件被误导出为普通 MP4 时不交付。

## 阶段执行契约

`video-08-composition` / `1.0.0` 使用 [阶段执行契约](../video-production-series/references/skill-execution-contract.md)。输入为真实屏幕、透明人物和统一时间线；前置证据为 alpha 探测及同一 scene_id 对齐。允许 C# 工具为 `MediaWorkflows`、`MediaTools`、`MpsTimelineValidation`。必需产物 `composition-manifest.json` 包含 `input_hashes`、`canvas`、`encoding`、`audio`、`subtitles`、`render_record`，并交付 `final-video.mp4` 和 `subtitles.srt`。真实屏幕、透明保持和音画对齐质量门未通过时回退 `video-07-lip-sync`，根据报告先修复实际责任输入。
