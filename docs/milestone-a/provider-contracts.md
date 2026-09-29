# 首批提供商公开契约核实

核查时间：2026-09-29 11:31–11:33（北京时间）。本记录只使用公开页面与仓库现有源码，不访问用户账号，不提交生成请求。`公开列出`、`账号可用`、`能力实测`是三个独立结论；后两者当前均为未知。逐来源的读取结果、正文证据及限制见[公开来源复核记录](provider-public-evidence-2026-09-29.md)。

## Moark（模力方舟）

公开文档入口：[模型广场 API](https://moark.com/docs/products/apis/)、[文本生成](https://moark.com/docs/products/apis/texts/text-generation)、[视频大模型](https://moark.com/docs/products/apis/videos/)、[异步任务](https://moark.com/docs/products/apis/async-task)。这些页面本次读取成功。接口路径可确认包含 `v1`，但页面未提供可核对的部署版本或文档修订号，故这两项记为未知，按访问日期保存证据。

| 用途 | 已确认公开契约 | 字段与状态边界 | 本仓库现状 |
| --- | --- | --- | --- |
| 文本生成 | `POST https://api.moark.com/v1/chat/completions`，Bearer 认证，JSON | 请求示例含 `model`、`messages[].role/content`、`stream`、`max_tokens`、`temperature`、`top_p`、`frequency_penalty`；响应示例含 `choices[].message`、`finish_reason`、`usage.prompt_tokens/completion_tokens/total_tokens`；单模型权限、价格与结构化输出实效未知 | 尚未接入 |
| 视频任务 | `POST https://api.moark.com/v1/async/videos/generations`，Bearer 认证，JSON | 示例包含 `model`、`num_frames` 及拼写为 `num_inferenece_steps` 的参数，提交后提取 `task_id`；示例前文与实际代码的 Wan 模型 ID 不一致，字段拼写、单模型限制和价格仍需合同验证，不能直接照抄为通用契约 | CLI 固定使用 `ViduQ2-Turbo` |
| 异步查询 | 公开示例使用 Bearer `GET /v1/task/{task_id}`、`GET /v1/task/{task_id}/status`；音乐示例使用提交结果的 `urls.get` | 查询示例读取 `status`、`output.file_url`、`started_at`、`completed_at`；指南同时列出 `/api/v1/task/{task_id}`，没有说明两种前缀的版本关系，不能认定其中一个是旧版或可互换；`/get` 后缀仅为历史实现证据 | CLI 调用 `/v1/task/{task_id}` 和 `/get` |
| 异步可用并发配额 | Bearer `GET https://api.moark.com/v1/tasks/available-quota` | FAQ 明确统计 `waiting` 与 `in_progress` 状态任务占用的并发配额；不是账户余额，也不能证明模型权限；响应 JSON 字段未在所读页面列出 | 尚未接入 |
| 异步状态与回调 | 指南列出 `waiting`、`in_progress`、`success`、`failure`、`cancelled`；可选 `X-WebHook` 请求头 | 回调示例包含 `event_id`、`task_id`、`status`、`output`、`usage_info`；`usage_info` 可为空，仅表示用量，未给已扣金额。视频页示例使用 `failed`，与指南的 `failure` 存在差异，适配器须保存未知状态并以合同测试确定映射 | 尚无通用状态适配层；未启用回调 |
| 图片、配音、口型 | 现有 CLI 分别调用 `/v1/images/generations`、`/v1/async/audio/speech`、`/v1/async/videos/audio-video-to-video` | 当前代码的模型 ID 与字段是历史实现证据，不能当作当前账号可用证明 | 固定四种模型、请求指纹与任务记录 |

模型目录、账号权限、用量账单和单价的公开只读接口尚未确认。模型广场与视频页的静态正文仅显示“加载 Serverless API 服务列表...”，没有返回可据此固化的目录字段。公开概览说明支持按调用次数或 tokens 计费，但没有给出本项目模型的单价；异步配额不可代替金额预算。任何未核实字段保持 `unknown`，禁止把页面列出的模型直接标为当前账号可用。

公开概览还说明 `X-Failover-Enabled` 省略时，支持故障转移的算力模型可能默认转移，并按最终成功调用的算力模型扣费。后续适配器在项目锁定模型或算力路由时须显式关闭该机制，并验证供应商实际行为；未验证前不能承诺请求级锁定已生效。本次只记录设计边界，未改变或调用 CLI。

## sonnet.vip

[站点首页](https://sonnet.vip/) 本次返回 200，标题为 `Sonnet.VIP - AI API Gateway`；公开首页配置的 `version`、`api_base_url`、`doc_url` 均为空。此前记录的前端 `sub2api` 标识仅是技术线索，本次没有重复下载前端资源来验证它。参考项目 [Wei-Shaw/sub2api](https://github.com/Wei-Shaw/sub2api) 的公开 README 说明其负责认证、计费、负载均衡、请求转发和模型路由；这些上游能力不能证明 sonnet.vip 的部署版本、启用路由、计费配置或账号权限。不能用上游最新版本号替代站点版本。

| 待核实内容 | 当前结论 | 验证门槛 |
| --- | --- | --- |
| 站点软件版本与 API 基址 | 未知；首页 `version` 与 `api_base_url` 为空 | 站点公开版本信息、公开接口文档或管理员提供的版本与基址证据；不猜路径扫描 |
| 模型目录与请求 ID 映射 | 未知 | 公开文档加当前账号只读查询，分别记录展示 ID 与调用 ID |
| Key 权限、分组、余额及用量 | 未知 | 使用用户配置的测试账号进行只读查询，不记录响应原文或密钥 |
| 文本/视觉/图片/音频/视频能力 | 未知 | 逐能力核实公开契约；计费实测须另有授权预算 |
| 错误、限流与异步任务 | 未知 | 站点实际版本的合同样例和离线模拟 |

以上未知表示本轮公开证据不足，不表示“不支持”。公共契约文档已覆盖本轮可确认的来源、路径、字段与差异；sonnet.vip 的站点契约闭环仍需要明确的站点文档或部署证据，账号可用性则需后续独立的只读查询，不能从首页 HTTP 200 推导。

## 实施边界

1. 目录数据须带来源 URL、读取时间、站点版本（未知可为空）和过期状态。
2. 账号可调用状态须由该账号的只读接口或授权实测支持；401、403、余额不足和接口不存在分别记录。
3. 价格未确认时保持空值和币种未知；一次可能计费的探测必须先记录提供商、账号、模型、输入摘要、次数与预算。
4. 超时或 5xx 后优先查询已有任务；没有稳定任务号时标记人工核对，不能自动重新 POST。
