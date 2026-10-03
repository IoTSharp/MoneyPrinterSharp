# 里程碑 C：能力、模型与提供商适配契约

本轮在本地离线范围内完成四项基础契约：能力分类、模型描述符、提供商适配接口和供应商模拟器。契约只保存可审计的白名单字段，不保存密钥、请求正文、供应商原始响应或有效签名下载地址。

## 能力分类与模型描述符

`src/VideoProduction.Core/ProviderCapabilities.cs` 固定七类能力：文本策划/脚本、视觉理解、图片生成、语音转写、语音合成、视频生成和口型同步。每项能力分别记录输入、输出、用途、来源、观察时间和证据状态；证据状态将公开列出、账号可调用和实测通过分开保存。

`src/VideoProduction.Core/ModelDescriptor.cs` 保存模型 ID、展示名、版本、模态、显式能力列表、上下文/时长/画幅/分辨率/格式限制、同步/异步方式、价格计量字段、来源和观察时间。缺失的价格与限制保留 `null`，未知模态、能力、执行方式和证据状态写成 `unknown`；描述符不会根据模型名称猜测能力。

## 提供商适配接口

`src/VideoProduction.Core/ProviderAdapterContracts.cs` 将目录、账号状态、能力探测、任务提交、状态/取消、产物下载、用量和错误映射拆成独立接口，并以 `IProviderAdapter` 聚合。目录页、稳定任务号、异步状态、币种/费用、分页游标和取消令牌均属于通用契约；下载接口要求调用方提供可写流，只返回本地写入统计，从接口层避免暴露签名 URL。

`ProviderAdapterValidation` 对页大小、游标、账号别名、模型 ID、脱敏输入指纹、任务号、费用和时间范围设置上限；含 URL、路径穿越、控制字符、未知能力或不可写目标流的请求会被拒绝。错误映射只接收 HTTP 状态、供应商错误码、超时/取消标志，不接收原始错误正文。

## 离线供应商模拟器

`src/VideoProduction.Core/OfflineProviderSimulator.cs` 提供内存中的分页目录、账号权限、401/403 分离、限流、总请求上限、稳定幂等任务号、异步成功/失败、费用、用量分页、响应格式标记和短期签名输出。模拟器使用可注入时钟和取消令牌；轮询同时受次数、时长和总请求边界约束，签名地址只作为一次性返回值存在，不进入状态、用量或日志。

## 验证

- `tests/VideoProduction.Tests/ProviderCapabilityTests.cs`：七类能力、输入/输出/用途分离、未知模型、价格/限制往返和 JSON 数字枚举/未知字段拒绝。
- `tests/VideoProduction.Tests/ProviderAdapterContractTests.cs`：接口分层、请求边界、秘密/路径拒绝、未知能力不推断和分页元数据。
- `tests/VideoProduction.Tests/OfflineProviderSimulatorTests.cs`：分页、响应格式、401/403、成功/失败异步任务、费用、幂等、签名地址脱敏、限流、总请求上限和取消。

本轮验证命令和结果见根目录 `CHANGELOG.md`；这些离线测试不访问网络，也不触发付费请求。Moark、sonnet.vip 的真实账号可用性、价格和生产接口仍需后续授权验证。
