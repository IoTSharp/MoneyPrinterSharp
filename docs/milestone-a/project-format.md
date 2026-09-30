# 项目格式决策草案

## 编辑事实来源

新项目以 `project.mps.json` 为唯一编辑事实来源，格式标识 `mps.project`，从 `schema_version: 1` 开始。预览和最终渲染都读取同一轨道、片段与画布数据；不维护第二份可编辑清单。JSON 使用 UTF-8、稳定 ID、相对项目根目录的 URI 风格路径和整数时间单位。素材文件只读，编辑只更新片段引用及属性。

| 结构 | 最小字段 | 不变量 |
| --- | --- | --- |
| `project` | `id`, `title`, `created_utc`, `schema_version` | ID 稳定；升级只增加版本 |
| `profiles[]` | `id`, `width`, `height`, `fps_num`, `fps_den` | 16:9、9:16 分别保存布局；帧率为有理数 |
| `assets[]` | `id`, `path`, `sha256`, `source`, `media_info` | 路径相对；哈希核对后才能重定位 |
| `tracks[]` | `id`, `kind`, `order`, `muted`, `hidden` | 至少支持屏幕、主持人、旁白、音乐、字幕 |
| `clips[]` | `id`, `track_id`, `asset_id`, `timeline_start`, `source_in`, `source_out`, `speed`, `properties` | 坐标统一为有理数时间或整数音频采样；范围非负且有界 |
| `sessions[]` | `id`, `title`, `record_ref` | 多会话引用同一资产和轨道 |
| `stages[]` | `skill_id`, `state`, `artifact_refs`, `invalidated_by` | 保持 11 个已有技能 ID |
| `claims[]` | `id`, `source_ref`, `limitations` | 成片主张可回溯 |
| `authorizations[]` | `provider`, `account_alias`, `asset_scope`, `source_scope`, `budget_ref` | 默认无外发权限；不存密钥 |
| `model_routes[]` | `capability`, `provider`, `account_alias`, `model_id`, `locked` | 锁定不可用时暂停 |

会话正文、任务摘要、账本、版本快照采用项目根下分文件，并由 `project.mps.json` 的相对引用连接。[项目目录契约](../milestone-b/project-directory.md)已实现根文件和素材相对路径的最小模型；原子版本快照与完整迁移仍属于里程碑 B。未知扩展字段在读取、保存时应保留或给出不可迁移报告，不能静默丢失。

## v1 `manifest.json` 迁移

旧清单见 `src/VideoProduction/Models.cs`。`version: 1` 代表 CLI 章节清单，与新项目的 `schema_version` 互不混淆。导入必须保留旧文件原样并记录 SHA-256，输出迁移报告。

| 旧字段 | 新项目映射 | 风险/处理 |
| --- | --- | --- |
| `width`, `height`, `fps`, `target_seconds` | 画布配置和目标时长 | 超出 V1 15-300 秒或 1080p 时允许导入，标记不符合当前交付预设 |
| `scenes[].id/title` | 章节标记与稳定来源 ID | 重复 ID 阻断迁移 |
| `screen` | 屏幕资产与片段 | 静态截图按旁白时长展示；原路径与哈希保留 |
| `audio`, `audio_rate` | 旁白资产、时间伸缩 | 源秒数转换成项目时间；记录原值避免累计舍入 |
| `clips[].video/offset/duration` | 主持人资产、片段入点/出点 | `lip_synced` 与原音频哈希建立关系；无法确认哈希时状态未知 |
| `captions[].start/end/text` | 字幕片段 | 原时间除以有效 `audio_rate`，保留原始时间值 |
| `narration`, `evidence` | 讲稿与主张证据 | 旧自由文本不伪装成可机器验证的来源链 |
| `presenter`、`taskbar_height` | 叠加样式和旧版合成参数 | 与新画幅不兼容时原值进迁移报告，等待人工布局 |

`manifest.json` 继续由当前 `VideoProduction` CLI 读取；新项目文件不会直接冒充旧清单。后续 CLI 需要显式的 `export-legacy` 或新项目读取入口，输出不可表达字段警告，保持现有命令和技能 ID 可用。历史样本读入、迁移前后时长核对与 CLI 回归列为里程碑 B 的清单迁移和核心测试实施门。
