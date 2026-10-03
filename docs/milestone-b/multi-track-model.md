# 多轨项目模型

里程碑 B 的多轨模型已接入 `VideoProduction.Core`。`project.mps.json` 的 `profiles[]`、`active_profile_id`、`tracks[]` 和每条轨道的 `clips[]` 是预览与最终渲染共同读取的编辑事实来源。旧版 `manifest.json` 仍由 CLI 使用，并可通过 `MpsProjectMigration` 生成新项目及不可迁移字段报告。

## 数据形状

- `profiles[]` 保存画布宽高、`fps_num/fps_den` 和音频采样率；`active_profile_id` 在存在画布时必须引用其中一个配置。
- `tracks[]` 保存稳定 ID、`kind`、`order`、可选的 `parent_track_id`、静音/隐藏状态及片段。支持 `screen`、`presenter`、`narration`、`music`、`subtitle` 和 `overlay`。
- `clips[]` 保存 `track_id`、媒体 `asset_id`（字幕可为空）、`timeline_start`、`source_in`、`source_out`、`speed_num/speed_den` 和受限的 `properties`。时间统一为 `{ "num": 整数, "den": 正整数 }`，片段长度由 `source_out - source_in` 按速度映射得到，字幕和纯叠加也使用逻辑源区间表达持续时间。
- `properties` 保存增益、透明度、布局、字幕样式、主持人抠像参数及口型声明。字幕轨道必须有字幕文字，主持人轨道必须有叠加参数；口型同步仍可为未知声明。

轨道 `order` 在项目内唯一，允许不同轨道的片段重叠；同轨道重叠策略留给后续项目校验器任务。父轨道必须存在，不能自引用或形成超过 32 层的循环。所有媒体片段的 `asset_id` 必须指向根文件素材索引，素材内容和路径校验仍由项目目录契约负责。

模型类型标记未知字段拒绝，轨道枚举只接受小写字符串，禁止以数字写入。坐标和变速复用 `RationalTime`、`ClipTimeMapping` 的有界规则：时间为 0 至 12 小时，速度为 0.01 至 100，所有离散帧舍入延后到输出边界。

## 验证证据

`tests/VideoProduction.Tests/TimelineModelTests.cs` 覆盖五类轨道的 JSON 往返、1/3 秒精确值、父子轨道、静音/隐藏、字幕/主持人属性、项目目录保存重开、跨轨重叠、缺失素材、重复 ID、非法速度/坐标/帧率、父级循环及嵌套未知字段拒绝。测试只使用本地对象和临时目录，不访问网络或付费服务。
