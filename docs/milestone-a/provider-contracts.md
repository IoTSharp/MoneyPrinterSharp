# 首批提供商公开契约核实

核查时间：2026-09-29 11:31–11:33 及 2026-09-30（北京时间）。第一轮读取公开页面和仓库源码，逐来源证据见[公开来源复核记录](provider-public-evidence-2026-09-29.md)；第二轮增加未带凭据的公开只读 GET。两轮均未访问用户账号、未提交生成请求，也未保存供应商响应原文。`公开列出`、`账号可用`、`能力实测`是三个独立结论；后两者当前均为未知。数量和 HTTP 状态只代表访问时的快照。

## Moark（模力方舟）

公开文档入口：[模型广场 API](https://moark.com/docs/products/apis/)、[文本生成](https://moark.com/docs/products/apis/texts/text-generation)、[文生图](https://moark.com/docs/products/apis/images-vision/text2image)、[视频大模型](https://moark.com/docs/products/apis/videos/)、[异步任务](https://moark.com/docs/products/apis/async-task)。9 月 29 日所读静态页面的模型列表是动态加载占位；9 月 30 日另从匿名模型 API 取得目录快照。文档示例使用 `/v1` 路径，但未给出可核对的产品发布版本或文档修订号；不能把路径版本当成部署版本。

| 用途与公开来源 | 核实到的路径及字段 | 未知或不支持推断的边界 | 本仓库现状 |
| --- | --- | --- | --- |
| [模型目录](https://api.moark.com/v1/models) | 未认证 `GET https://api.moark.com/v1/models` 返回 HTTP 200；顶层为 `object: "list"`、`data` 数组。2026-09-30 快照含 223 条，条目字段合集为 `id`、`object`、`created`、`owned_by`；其中列出 `ViduQ2-Turbo`。 | 响应没有明确的能力、账号权限、可用区域、价格、限制、稳定模型版本或分页元数据；223 不是账号可调用模型数，也不能断定目录完整。 | 尚未接入动态目录。 |
| [文本生成](https://moark.com/docs/products/apis/texts/text-generation) | Bearer `POST https://api.moark.com/v1/chat/completions`；请求示例含 `model`、`messages[].role/content`、`stream`、`max_tokens`、`temperature`、`top_p`、`frequency_penalty`；响应示例含 `choices[].message`、`finish_reason`、`usage` 的 token 计数。 | 示例模型名、`guided_json` 等公开参数不能证明当前账号可调用或满足结构化输出能力；价格未知。 | 尚未接入。 |
| [图片生成](https://moark.com/docs/products/apis/images-vision/text2image) | 示例为 Bearer 认证的 `POST https://api.moark.com/v1/images/generations`，请求含 `model`、`prompt`、`size`、`response_format`。 | 示例使用 `Kolors`，不能据此推断现有 CLI 的图片模型权限、输出格式或价格。 | CLI 调用该路径，模型 ID 来自历史实现。 |
| [视频任务](https://moark.com/docs/products/apis/videos/) | Bearer `POST https://api.moark.com/v1/async/videos/generations`；示例包含 `model`、`num_frames`、`num_inferenece_steps`，提交后使用 `task_id` 查询，查询示例读取 `status` 与 `output.file_url`。 | 前文和示例代码的 Wan 模型 ID 不一致，参数拼写按原样记录，不能视为通用契约；单模型时长、分辨率、费用和返回形状仍需合同验证。 | CLI 固定使用 `ViduQ2-Turbo`。 |
| [异步查询](https://moark.com/docs/products/apis/async-task) | Bearer `GET /v1/task/{task_id}`、`GET /v1/task/{task_id}/status`；指南同时列出 `/api/v1/task/{task_id}` 及 `POST /api/v1/task/{task_id}/cancel`，提交示例使用 `urls.get`。查询示例读取 `status`、`output.file_url`、`started_at`、`completed_at`。 | `/v1` 与 `/api/v1` 的关系未知，不能互换推断；CLI 的 `/get` 后缀仅有历史实现证据。 | CLI 调用 `/v1/task/{task_id}` 和 `/get`。 |
| [异步状态与回调](https://moark.com/docs/products/apis/async-task) | 指南列出 `waiting`、`in_progress`、`success`、`failure`、`cancelled`，可选 `X-WebHook`；回调样例含 `event_id`、`task_id`、`status`、`output`、可为空的 `usage_info`。 | 视频页示例使用 `failed`，与指南的 `failure` 不一致；回调和查询是否同一字段集未知，`usage_info` 不是已扣金额。 | 尚无通用状态适配层；未启用回调。 |
| [异步并发配额](https://moark.com/docs/products/apis/async-task) | Bearer `GET https://api.moark.com/v1/tasks/available-quota`；FAQ 说明 `waiting` 和 `in_progress` 任务占用并发配额。 | 响应字段、余额、单模型权限及计费关系未核实，不能将并发配额当成可用余额。 | 尚未接入。 |

配音、口型路径 `/v1/async/audio/speech`、`/v1/async/videos/audio-video-to-video` 仅见于[现有 CLI 源码](../../src/VideoProduction/ProviderRequest.cs)，本次未取得对应的当前公开接口合同。模型目录不提供这些能力的可靠分类，故这些路径、模型 ID 和字段仍需逐项核实；历史实现不构成当前账号可用证明。公开价格、账号用量账单和单价接口也未确认。

公开概览说明模型调用可能按次数或 tokens 计费，并说明 `X-Failover-Enabled` 省略时，支持故障转移的算力模型可能默认转移并按最终成功模型扣费。后续适配器在项目锁定模型或算力路由时须显式关闭该机制并验证实际行为；未验证前不能承诺请求级锁定已生效。

## sonnet.vip

[站点首页](https://sonnet.vip/) 两轮均返回 HTTP 200，标题为 `Sonnet.VIP - AI API Gateway`。9 月 29 日首页配置的 `version`、`api_base_url`、`doc_url` 均为空；9 月 30 日公开设置接口另自报版本 `0.2.10`。先前读取的[公开前端脚本](https://sonnet.vip/assets/index-BiiV0DRN.js)含 `sub2api` 标识与 [Wei-Shaw/sub2api](https://github.com/Wei-Shaw/sub2api) 链接，9 月 29 日没有重新下载它。上游 README 的网关功能与最新版本均不能证明站点的部署构建、启用路由、计费配置或账号权限。

| 公开只读来源 | 2026-09-30 核查结果 | 可得结论及边界 |
| --- | --- | --- |
| [`GET /api/v1/settings/public`](https://sonnet.vip/api/v1/settings/public) | 未认证返回 HTTP 200；顶层 `code`、`message`、`data`。`data.version` 为 `0.2.10`，`site_name` 为 `Sonnet.VIP`，`api_base_url` 和 `doc_url` 均为空字符串。 | `0.2.10` 是站点公开设置**自报版本**，没有公开提交号或后端构建证据；空白 API 基址/文档地址不构成具体推理 API 合同。 |
| [同一公开设置接口](https://sonnet.vip/api/v1/settings/public) | `model_plaza_enabled: false`、`model_plaza_require_auth: true`、`available_channels_enabled: false`、`payment_enabled: true`。 | 当前公开设置关闭模型广场与渠道列表；支付开关不提供单价、计费单位或账号余额。不能据此前端开关断定所有模型均不支持。 |
| [`GET /v1/models`](https://sonnet.vip/v1/models)、[`GET /api/v1/channels/available`](https://sonnet.vip/api/v1/channels/available) | 未认证请求均返回 HTTP 401。 | 公开匿名请求不能取得目录或账号可用模型；已认证响应形状与模型权限未知。 |
| [`GET /api/v1/model-plaza`](https://sonnet.vip/api/v1/model-plaza)、[`GET /api/v1/admin/system/version`](https://sonnet.vip/api/v1/admin/system/version) | 未认证请求分别返回 HTTP 404、401。 | 前者当前无公开可读目录，后者不可用于匿名复核部署版本；不能将 404 扩大解释为模型能力不存在。 |

站点已自报版本，但实际后端构建、API 基址、文本/视觉/图片/转写/配音/视频/口型路径、展示 ID 与请求 ID 映射、错误与限流格式、异步任务、账号权限/分组、余额、用量和单价仍未知。以上未知表示公开证据不足，不表示“不支持”。后续需要站点自身的接口合同或用户配置账号的只读查询；可能计费的能力实测另需当前任务授权与预算。不记录账号响应原文或密钥，也不通过猜测路径补齐站点契约。

## 实施边界与未完成判据

1. 目录数据须带来源 URL、读取时间、站点自报版本（若有）和过期状态；Moark 目录的 `id` 不等于能力或账号权限，sonnet.vip 当前没有匿名可读模型目录。
2. 账号可调用状态须由该账号的只读接口或授权实测支持；401、403、余额不足和接口不存在分别记录。当前未访问任何账号，因此首批提供商的账号可用性判据均未完成。
3. 价格未确认时保持空值和币种未知；一次可能计费的探测必须先记录提供商、账号、模型、输入摘要、次数与预算。当前两站计费字段与价格判据均未完成。
4. 超时或 5xx 后优先查询已有任务；没有稳定任务号时标记人工核对，不能自动重新 POST。Moark 异步路径和状态差异、sonnet.vip 异步合同均需后续离线模拟及授权合同验证。
