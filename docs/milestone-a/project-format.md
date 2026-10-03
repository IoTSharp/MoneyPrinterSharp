# 项目格式决策草案

## 编辑事实来源

新项目以 `project.mps.json` 为唯一编辑事实来源，格式标识 `mps.project`，从 `schema_version: 1` 开始。预览和最终渲染都读取同一轨道、片段与画布数据；不维护第二份可编辑清单。JSON 使用 UTF-8、稳定 ID、以 `/` 分隔的项目相对逻辑路径和无浮点累计漂移的时间表达。阶段 B 固定整数分子/时间基准的序列化格式与 JSON Schema，并验证不同帧率、采样率及可变帧率转换；此处不把两个备选存储形式视为已实现格式。素材文件只读，编辑只更新片段引用及属性。

| 结构 | 最小字段 | 不变量 |
| --- | --- | --- |
| `project` | `id`, `title`, `created_utc`, `schema_version` | ID 稳定；升级只增加版本 |
| `profiles[]` | `id`, `width`, `height`, `fps_num`, `fps_den` | 16:9、9:16 分别保存布局；帧率为有理数 |
| `assets[]` | `id`, `path`, `sha256`, `source`, `media`, `size_bytes`, `indexed_utc`, `is_reachable` | 路径相对；哈希核对后才能重定位 |
| `tracks[]` | `id`, `kind`, `order`, `muted`, `hidden` | 至少支持屏幕、主持人、旁白、音乐、字幕 |
| `clips[]` | `id`, `track_id`, `asset_id`, `timeline_start`, `source_in`, `source_out`, `speed`, `properties` | 坐标统一为有理数时间或整数音频采样；范围非负且有界 |
| `sessions/` | `mps.project.sessions` 快照中的 `id`, `title`, `messages` | 多会话引用同一共享资产、轨道、预算和阶段状态 |
| `stages[]` | `skill_id`, `state`, `artifact_refs`, `invalidated_by` | 保持 11 个已有技能 ID |
| `claims[]` | `id`, `source_ref`, `limitations` | 成片主张可回溯 |
| `authorizations[]` | `id`, `project_id`, `provider`, `account_alias`, `model`, `capability`, `purpose`, `expires_at`, `scopes[]`, `budget_ref` | 默认无外发权限；逐维度匹配；不存密钥 |
| `model_routes[]` | `capability`, `provider`, `account_alias`, `model_id`, `locked` | 锁定不可用时暂停 |

会话正文、任务摘要、账本、版本快照采用项目根下分文件，并由 `project.mps.json` 的相对引用连接。[项目目录契约](../milestone-b/project-directory.md)已实现根文件、素材相对路径、原子版本、会话快照和旧清单迁移。未知扩展字段在读取、保存时应保留或给出不可迁移报告，不能静默丢失。

授权 `scopes[]` 对应阶段 A 可执行契约的 `kind`（Asset/Source/Text）、`project_path` 及源码 `start_line/end_line`；媒体或文本为完整逻辑对象，源码为明确的包含首尾行区间。`budget_ref` 必须解析到可信账本中的金额、币种、已占用额和预留关系；当前离线授权契约只验证 CNY 快照，不能据此宣称多币种或并发扣账已实现。相对逻辑路径不经 URL 解码，实际文件句柄、内容哈希及外发字节绑定在后续发送层校验。

## v1 `manifest.json` 迁移

旧清单见 `src/VideoProduction.Core/Models.cs`。`version: 1` 代表 CLI 章节清单，与新项目的 `schema_version` 互不混淆。导入必须保留旧文件原样并记录 SHA-256，输出迁移报告。

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

### 与旧渲染实现核对的时间不变量

- 有效速度 `r = scene.AudioRate ?? manifest.AudioRate`，章节时长以实测旁白 `audio.Duration / r` 为准；章节在项目中依次累加。
- 主持人的 `offset` 是该视频的源入点，不能直接作为时间线位置。按清单顺序累计有效源时长 `sourceCursor`，片段时间线起点为章节起点加 `sourceCursor / r`；空 `duration` 取视频剩余长度，末段按旁白剩余时间截短。
- 字幕起止时间先除以 `r` 再加章节起点。旧清单没有字幕时，旧渲染器按文本估算；迁移必须保留“估算”标记，不能包装成转写实测时码。
- 旧 `lip_synced=true` 是历史声明。新算当前音频的哈希不能证明旧生成任务绑定过同一音频；没有任务或原音频绑定证据时，新项目的同步实测状态仍为未知。
