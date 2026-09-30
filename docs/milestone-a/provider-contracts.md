# 首批提供商公开契约核实

核查时间：2026-09-30（北京时间）。仅使用公开文档和未带凭据的只读 GET；未访问用户账号、未提交生成请求，也未保存供应商响应原文。`公开列出`、`账号可用`、`能力实测`是三个独立结论；后两者当前均为未知。以下数量和 HTTP 状态是核查时的快照，不保证长期不变。

## Moark（模力方舟）

公开文档入口：[模型广场 API](https://moark.com/docs/products/apis/)、[文本生成](https://moark.com/docs/products/apis/texts/text-generation)、[文生图](https://moark.com/docs/products/apis/images-vision/text2image)、[视频大模型](https://moark.com/docs/products/apis/videos/)、[异步任务](https://moark.com/docs/products/apis/async-task)。文档示例使用 `/v1` 路径，但未给出可核对的产品发布版本或文档修订号；不能把路径版本当成部署版本。

| 用途与公开来源 | 核实到的路径及字段 | 未知或不支持推断的边界 | 本仓库现状 |
| --- | --- | --- | --- |
| [模型目录](https://api.moark.com/v1/models) | 未认证 `GET https://api.moark.com/v1/models` 返回 HTTP 200；顶层为 `object: "list"`、`data` 数组。2026-09-30 快照含 223 条，条目字段合集为 `id`、`object`、`created`、`owned_by`；其中列出 `ViduQ2-Turbo`。 | 响应没有明确的能力、账号权限、可用区域、价格、限制、稳定模型版本或分页元数据；223 不是账号可调用模型数，也不能断定目录完整。 | 尚未接入动态目录。 |
| [文本生成](https://moark.com/docs/products/apis/texts/text-generation) | 示例为 Bearer 认证的 `POST https://api.moark.com/v1/chat/completions`，请求含 `model`、`messages`、`stream`。 | 示例模型名不能证明当前账号可调用或满足结构化输出能力；价格未知。 | 尚未接入。 |
| [图片生成](https://moark.com/docs/products/apis/images-vision/text2image) | 示例为 Bearer 认证的 `POST https://api.moark.com/v1/images/generations`，请求含 `model`、`prompt`、`size`、`response_format`。 | 示例使用 `Kolors`，不能据此推断现有 CLI 的图片模型权限、输出格式或价格。 | CLI 调用该路径，模型 ID 来自历史实现。 |
| [视频任务](https://moark.com/docs/products/apis/videos/) | 示例为 Bearer 认证的 `POST https://api.moark.com/v1/async/videos/generations`；提交后使用 `task_id` 查询，查询示例读取 `status` 与 `output.file_url`。 | 每个模型允许的 `duration`、`resolution`、费用及返回形状需分别核实；示例中的任务状态还需合同测试。 | CLI 固定使用 `ViduQ2-Turbo`。 |
| [异步状态与任务记录](https://moark.com/docs/products/apis/async-task) | 文档列出 `GET /v1/task/<task_id>/status`、`GET /v1/task/<task_id>`，同时将统一查询和取消写成 `/api/v1/task/<task_id>`、`POST /api/v1/task/<task_id>/cancel`；提交示例还使用返回的 `urls.get`。回调样例含 `event_id`、`task_id`、`status`、`output`、可为空的 `usage_info`；回调状态列为 `waiting`、`in_progress`、`success`、`failure`、`cancelled`。 | `/v1` 与 `/api/v1` 不能互换推断；视频页示例还判断 `failed`，与异步页的 `failure` 不一致。回调字段和查询响应是否完全相同未知。 | CLI 调用 `/v1/task/{task_id}` 和 `/get`。 |
| [异步并发配额](https://moark.com/docs/products/apis/async-task) | 文档给出 Bearer 认证的 `GET https://api.moark.com/v1/tasks/available-quota`，称用于查询账号异步任务并发配额。 | 响应字段、余额、单模型权限及计费关系未核实，不能将并发配额当成可用余额。 | 尚未接入。 |

配音、口型路径 `/v1/async/audio/speech`、`/v1/async/videos/audio-video-to-video` 仅见于[现有 CLI 源码](../../src/VideoProduction/ProviderRequest.cs)，本次未取得对应的当前公开接口合同。模型目录不提供这些能力的可靠分类，故这些路径、模型 ID 和字段仍需逐项核实；历史实现不构成当前账号可用证明。公开价格、账号用量账单和单价接口也未确认。

## sonnet.vip

[站点首页](https://sonnet.vip/) 返回 HTTP 200，标题为 `Sonnet.VIP - AI API Gateway`；其[公开前端脚本](https://sonnet.vip/assets/index-BiiV0DRN.js)含 `sub2api` 标识和 [Wei-Shaw/sub2api](https://github.com/Wei-Shaw/sub2api) 链接。脚本哈希只是本次访问的资源标识，不能从上游项目推断站点已启用的路由、模型、支付规则或后端构建。

| 公开只读来源 | 2026-09-30 核查结果 | 可得结论及边界 |
| --- | --- | --- |
| [`GET /api/v1/settings/public`](https://sonnet.vip/api/v1/settings/public) | 未认证返回 HTTP 200；顶层 `code`、`message`、`data`。`data.version` 为 `0.2.10`，`site_name` 为 `Sonnet.VIP`，`api_base_url` 和 `doc_url` 均为空字符串。 | `0.2.10` 是站点公开设置**自报版本**，没有公开提交号或后端构建证据；空白 API 基址/文档地址不构成具体推理 API 合同。 |
| [同一公开设置接口](https://sonnet.vip/api/v1/settings/public) | `model_plaza_enabled: false`、`model_plaza_require_auth: true`、`available_channels_enabled: false`、`payment_enabled: true`。 | 当前公开设置关闭模型广场与渠道列表；支付开关不提供单价、计费单位或账号余额。不能据此前端开关断定所有模型均不支持。 |
| [`GET /v1/models`](https://sonnet.vip/v1/models)、[`GET /api/v1/channels/available`](https://sonnet.vip/api/v1/channels/available) | 未认证请求均返回 HTTP 401。 | 公开匿名请求不能取得目录或账号可用模型；已认证响应形状与模型权限未知。 |
| [`GET /api/v1/model-plaza`](https://sonnet.vip/api/v1/model-plaza)、[`GET /api/v1/admin/system/version`](https://sonnet.vip/api/v1/admin/system/version) | 未认证请求分别返回 HTTP 404、401。 | 前者当前无公开可读目录，后者不可用于匿名复核部署版本；不能将 404 扩大解释为模型能力不存在。 |

站点的文本、视觉、图片、转写、配音、视频及口型 API 路径、请求 ID 映射、错误与限流格式、异步任务、账号余额和单价仍为未知。对这些事项需要站点自身发布的接口合同，或使用用户配置账号作只读查询；可能计费的能力实测另需当前任务授权与预算。

## 实施边界与未完成判据

1. 目录数据须带来源 URL、读取时间、站点自报版本（若有）和过期状态；Moark 目录的 `id` 不等于能力或账号权限，sonnet.vip 当前没有匿名可读模型目录。
2. 账号可调用状态须由该账号的只读接口或授权实测支持；401、403、余额不足和接口不存在分别记录。当前未访问任何账号，因此首批提供商的账号可用性判据均未完成。
3. 价格未确认时保持空值和币种未知；一次可能计费的探测必须先记录提供商、账号、模型、输入摘要、次数与预算。当前两站计费字段与价格判据均未完成。
4. 超时或 5xx 后优先查询已有任务；没有稳定任务号时标记人工核对，不能自动重新 POST。Moark 异步路径和状态差异、sonnet.vip 异步合同均需后续离线模拟及授权合同验证。
