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

## 账号、连接边界与目录事实

`src/VideoProduction.Core/ProviderAccountConfiguration.cs` 只保存提供商、账号别名、启用状态和 Windows Credential Manager 目标名；`ProviderAccountConfigurationSet` 限制账号数量、拒绝重复别名/凭据目标并维护当前选择，脱敏快照拒绝未知字段。凭据值、请求头和原始响应不会进入配置或项目。

`src/VideoProduction.Core/ProviderConnectionBoundary.cs` 将 API 根地址限制为无凭据 HTTPS，并要求精确主机白名单、TLS 1.2/1.3、有限响应（最多 64 MiB）和有限请求超时；代理只能是无凭据的本机回环地址。客户端关闭自动重定向、Cookie 和环境代理继承，目标 URI 复用主机/端口校验，认证重定向被拒绝。

`src/VideoProduction.Core/ModelCatalogCache.cs` 通过 `IProviderCatalogAdapter` 读取有界分页目录，保留来源、供应商版本、观察时间和 TTL；游标重复、页数/模型数超限或读取失败时保留上一份缓存，不根据模型名称推断能力。`ProviderAccountAvailability.cs` 将账号存在、模型存在、权限、余额、配额和区域分别记录为三态事实；401、403、余额不足和配额耗尽不会被压成模型不存在，查询不到的字段保持未知。

`src/VideoProduction.Core/ProviderModelRouting.cs` 提供只读的自动推荐和人工锁定解析：推荐只接受账号可用且能力证据为账号可调用/实测的候选，硬预算下未知价格不可入选；锁定模型缺失或不可用时返回暂停状态，要求用户选择，不自动跨提供商切换。

## 验证

- `tests/VideoProduction.Tests/ProviderCapabilityTests.cs`：七类能力、输入/输出/用途分离、未知模型、价格/限制往返和 JSON 数字枚举/未知字段拒绝。
- `tests/VideoProduction.Tests/ProviderAdapterContractTests.cs`：接口分层、请求边界、秘密/路径拒绝、未知能力不推断和分页元数据。
- `tests/VideoProduction.Tests/ProviderAccountConfigurationTests.cs`：多账号选择、凭据目标引用、重复/控制字符拒绝、脱敏快照和取消边界。
- `tests/VideoProduction.Tests/ProviderConnectionBoundaryTests.cs`：HTTPS/TLS、精确主机、认证重定向、回环代理、响应大小和有限超时。
- `tests/VideoProduction.Tests/ModelCatalogCacheTests.cs`：分页、来源/版本/观察时间、TTL、失败保留旧快照和分页上限。
- `tests/VideoProduction.Tests/ProviderAccountAvailabilityTests.cs`：401/403、余额/配额分离、模型存在性独立更新和有界状态表。
- `tests/VideoProduction.Tests/ProviderModelRoutingTests.cs`：硬预算、未知费用/能力、确定性推荐和锁定暂停。
- `tests/VideoProduction.Tests/OfflineProviderSimulatorTests.cs`：分页、响应格式、401/403、成功/失败异步任务、费用、幂等、签名地址脱敏、限流、总请求上限和取消。

本轮验证命令和结果见根目录 `CHANGELOG.md`；这些离线测试不访问网络，也不触发付费请求。Moark、sonnet.vip 的真实账号可用性、价格和生产接口仍需后续授权验证。
