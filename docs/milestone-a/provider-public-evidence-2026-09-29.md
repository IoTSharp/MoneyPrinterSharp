# 首批提供商公开来源复核记录

核查日期：2026-09-29（北京时间）。本记录保存公开网页的结论与必要字段，不保存网页全文、供应商 API 原始响应、账号信息或下载签名地址。公开文档可变，读取时间是证据时点，不是部署版本。

## 来源与读取结果

只对以下六个明确 URL 发起普通匿名 GET。每请求超时 20 秒、失败至多通过本地代理重试一次；未发起账号查询、模型推理、目录探测或端点扫描。

| ID | 来源 | 本次读取时间 | 结果 | 可证明范围 |
| --- | --- | --- | --- | --- |
| M1 | [Moark 模型广场 API 概览](https://moark.com/docs/products/apis/) | 11:32:26 +08:00 | 直连 200 | 资源包与令牌关系、按次数/tokens 计费说明、故障转移请求头及其默认行为；模型列表是动态加载占位 |
| M2 | [Moark 文本生成](https://moark.com/docs/products/apis/texts/text-generation) | 11:32:26 +08:00 | 直连 200 | Chat Completions 路径、Bearer/JSON、请求和响应示例字段、结构化输出参数说明 |
| M3 | [Moark 视频大模型](https://moark.com/docs/products/apis/videos/) | 11:32:27 +08:00 | 直连 200 | 异步视频提交路径、任务查询示例与输出字段；模型和参数示例存在不一致 |
| M4 | [Moark 异步任务指南](https://moark.com/docs/products/apis/async-task) | 11:31:29 +08:00 | 直连 200 | 任务/状态路径、并发配额 GET、状态枚举、回调与用量字段；未给统一响应 schema 或金额字段 |
| S1 | [sonnet.vip 首页](https://sonnet.vip/) | 11:33:01 +08:00 | 直连 200 | 页面标题及公开配置中的三个空字段，不能证明 API 基址或部署版本 |
| S2 | [sub2api 上游仓库首页](https://github.com/Wei-Shaw/sub2api) | 11:33:23 +08:00 | 直连 20 秒超时，代理重试 200 | 上游 README 的网关功能说明；不能代表 sonnet.vip 站点契约 |

## 已确认正文与差异

1. M4 的 cURL 示例以 Bearer 请求 `https://api.moark.com/v1/tasks/available-quota`，未指定其他方法，因此为 GET；FAQ 明确“并发配额限制”包含 `waiting` 和 `in_progress` 任务总数。原记录中的“可用额度”应明确为异步并发配额，余额与价格仍未知。
2. M4 的回调状态列出 `waiting`、`in_progress`、`success`、`failure`、`cancelled`，M3 查询示例却使用 `failed`。M4 同时出现 `/api/v1/task/<task_id>` 与 `/v1/task/<task_id>`，未说明版本关系。不能把任何一方标为已废弃或直接统一替换。
3. M4 提交示例读取 `task_id` 和 `urls.get`，查询结果读取 `status` 与 `output.file_url`；回调示例包含 `event_id`、`usage_info.unit/quantity/prompt_tokens/completion_tokens/resolution`，并说明用量可为空。公开用量字段不是已扣费用，也不能充当实际模型权限证据。
4. M3 前文称示例模型为 `Wan2.1-T2V-14B`，代码中却使用 `Wan2.1-T2V-1.3B`，参数拼写为 `num_inferenece_steps`。本次按原样记录差异，不将疑似笔误改写成已验证请求参数。
5. M1 描述 `X-Failover-Enabled=true/false`；省略时，对支持该机制的算力模型默认启用，并按最后成功的算力模型扣费。后续模型锁定合同测试须覆盖显式关闭与实际路由结果。
6. M2 公开响应示例给出 `choices[].message.content`、`finish_reason` 和 token 用量；`guided_json` 等参数仅能证明公开说明存在，不能证明某个当前账号或模型已实测可用。
7. S1 首页标题为 `Sonnet.VIP - AI API Gateway`，公开配置的 `version`、`api_base_url`、`doc_url` 值均为 `""`。页面未提供可核对的部署版本、API 基址或文档入口。S2 的通用网关功能、最新源码或版本均不能填补这些站点事实。

## 剩余证据条件

- Moark：仍需当前版本的统一字段说明或后续经授权的合同结果，确定异步路径、状态差异、目录 schema、账号权限和金额字段；动态模型名称与单价不由静态占位列表推断。
- sonnet.vip：需要站点公开接口文档、明确的部署版本/基址证据，或后续在用户配置账号范围内的只读契约验证。没有公开证据时保留未知，不推定不存在或可用。
- 两家提供商：本轮没有任何账号可调用或能力实测结论，也没有发生费用。阶段 A 的公开文档审查与后续 C 阶段账号/能力验收须分别记录。

执行环境为 PowerShell 7.6.6。请求通过进程内 HTTP 客户端完成，命令记录包含宿主 PID、创建时间、命令行与父进程；未启动附属进程、未创建临时文件或下载文件。所有命令均正常结束。
