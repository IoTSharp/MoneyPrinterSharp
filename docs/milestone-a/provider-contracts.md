# 首批提供商公开契约核实

核查时间：2026-09-29（北京时间）。本记录只使用公开页面与仓库现有源码，不访问用户账号，不提交生成请求。`公开列出`、`账号可用`、`能力实测`是三个独立结论；后两者当前均为未知。

## Moark（模力方舟）

公开文档入口：[模型广场 API](https://moark.com/docs/products/apis/)、[文本生成](https://moark.com/docs/products/apis/texts/text-generation)、[视频大模型](https://moark.com/docs/products/apis/videos/)、[异步任务](https://moark.com/docs/products/apis/async-task)。这些页面本次读取成功，但页面未提供可核对的固定 API 版本号或文档修订号，故版本记为未知，按访问日期保存证据。

| 用途 | 已确认公开契约 | 字段与状态边界 | 本仓库现状 |
| --- | --- | --- | --- |
| 文本生成 | `POST https://api.moark.com/v1/chat/completions`，Bearer 认证 | 请求示例含 `model`、`messages`；账号模型权限、价格及实际结构化输出能力未知 | 尚未接入 |
| 视频任务 | `POST https://api.moark.com/v1/async/videos/generations` | 示例包含 `model`，返回稳定 `task_id` 后需查询；模型时长、分辨率与价格应逐模型确认 | CLI 固定使用 `ViduQ2-Turbo` |
| 异步查询 | 文档出现 `GET /v1/task/{task_id}`、`GET /v1/task/{task_id}/status` 及输出查询说明 | 文档还出现旧的 `/api/v1/task/` 路径；两种前缀不可互换推断，需以当前接口合同测试确认 | CLI 调用 `/v1/task/{task_id}` 和 `/get` |
| 可用额度 | 文档出现 `/v1/tasks/available-quota` | 查询方式、响应字段、与余额/模型权限的关系未完成核实 | 尚未接入 |
| 图片、配音、口型 | 现有 CLI 分别调用 `/v1/images/generations`、`/v1/async/audio/speech`、`/v1/async/videos/audio-video-to-video` | 当前代码的模型 ID 与字段是历史实现证据，不能当作当前账号可用证明 | 固定四种模型、请求指纹与任务记录 |

模型目录、账号权限、用量账单和单价的公开只读接口尚未确认。任何未核实字段保持 `unknown`，禁止把页面列出的模型直接标为当前账号可用。

## sonnet.vip

[站点首页](https://sonnet.vip/) 本次返回 200，标题为 `Sonnet.VIP - AI API Gateway`；公开前端资源包含 `sub2api` 标识。参考项目为 [Wei-Shaw/sub2api](https://github.com/Wei-Shaw/sub2api)，但不能据此认定站点部署版本、启用的路由、计费配置或管理端权限。

| 待核实内容 | 当前结论 | 验证门槛 |
| --- | --- | --- |
| 站点软件版本与 API 基址 | 未知 | 站点公开版本信息或管理员提供的版本证据 |
| 模型目录与请求 ID 映射 | 未知 | 公开文档加当前账号只读查询，分别记录展示 ID 与调用 ID |
| Key 权限、分组、余额及用量 | 未知 | 使用用户配置的测试账号进行只读查询，不记录响应原文或密钥 |
| 文本/视觉/图片/音频/视频能力 | 未知 | 逐能力核实公开契约；计费实测须另有授权预算 |
| 错误、限流与异步任务 | 未知 | 站点实际版本的合同样例和离线模拟 |

## 实施边界

1. 目录数据须带来源 URL、读取时间、站点版本（未知可为空）和过期状态。
2. 账号可调用状态须由该账号的只读接口或授权实测支持；401、403、余额不足和接口不存在分别记录。
3. 价格未确认时保持空值和币种未知；一次可能计费的探测必须先记录提供商、账号、模型、输入摘要、次数与预算。
4. 超时或 5xx 后优先查询已有任务；没有稳定任务号时标记人工核对，不能自动重新 POST。
