# 工作流

## 阶段门

1. `feature-audit` 为每个主张保存证据和限制。
2. `narrative-plan` 选择受众、主线和删减顺序。
3. `screenplay` 冻结朗读文本、镜头、字幕和主持人动作。
4. `model-selection` 只比较已验证能力，不从模型名称猜功能。
5. `presenter` 交付稳定人物和独立绿幕动作层。
6. `narration` 交付与冻结文本一致的实测音频。
7. `lip-sync` 用同一最终音频逐段生成并保存任务映射。
8. `composition` 先交 alpha 中间件，再交背景合成视频。
9. `quality-review` 独立探测媒体并抽查内容、音频和口型。
10. `cost-delivery` 去重费用并清理秘密后打包交付。

任何阶段失败都回退到责任阶段；不能用剪辑补偿缺证据、错口型或不可解码媒体。
