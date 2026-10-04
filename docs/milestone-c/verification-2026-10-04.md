# 2026-10-04 本批离线验证

本批从 `main` / `origin/main` 的本地已推送引用 `7689a08ccb26ee575f82eeac08b3fe3bc4461305` 开始，工作区为独立 MoneyPrinterSharp 仓库。本记录描述尚未提交的后续改动；基线已有的价格/限制、预算账本、异步恢复和技能注册表不计作本批新实现。

## 验证环境与执行

PowerShell 使用固定 `C:\Program Files\PowerShell\7\pwsh.exe`，版本 `7.6.6`；SDK 为 `C:\Program Files\dotnet\dotnet.exe` / `.NET 10.0.401`。生产核心、CLI 与测试工程只串行构建，不安装或发现额外工具。每次外部命令有 30–180 秒墙钟上限、有限等待次数和取消入口；记录本次进程 PID、创建时间、命令行与父进程，并按这些身份信息检查和回收本任务进程树。测试本身使用 45 秒取消截止时间，假适配器没有供应商网络传输。

| 检查 | 结果 |
| --- | --- |
| `dotnet build src/VideoProduction/VideoProduction.csproj --no-restore --disable-build-servers -p:UseSharedCompilation=false` | 返回 0，核心/CLI 0 警告、0 错误 |
| `dotnet build tests/VideoProduction.Tests/VideoProduction.Tests.csproj --no-restore --disable-build-servers -p:UseSharedCompilation=false` | 最终返回 0，重新编译核心/CLI/测试，0 警告、0 错误 |
| `dotnet run --project tests/VideoProduction.Tests/VideoProduction.Tests.csproj --no-build --no-restore -p:UseSharedCompilation=false` | 最终返回 0，完整旧回归与六组新增入口全部通过 |
| `dotnet run --project src/VideoProduction/VideoProduction.csproj --no-build --no-restore -- help` | 返回 0，保留既有 CLI 入口 |
| `dotnet run --project src/VideoProduction/VideoProduction.csproj --no-build --no-restore -- validate --manifest examples/demo-mps/manifest.json --draft` | 返回 0；只证明兼容清单结构，不代替媒体验收 |
| `git diff --check` | 通过 |

验证期间曾发现图片复用在旧记录读取前被输出存在检查阻断，修复为同指纹、同路径、同 SHA-256 的安全复用；迟到响应释放断言改为实际读取已释放 JSON 缓冲。修复后的完整回归通过。没有发起真实供应商请求、Windows Credential Manager 读取或付费请求。

结束复核检查了本批 10 份进程记录中的 26 个进程身份，无仍存活的本任务进程；三个已记录编译器响应文件所在临时目录已由构建工具移除。离线样例目录由测试的归属校验/清理路径回收。本次专用执行脚本与进程记录在汇总本记录后清理，保留源码、测试、文档和正常构建输出。

## 完成证据

| 基线路线图事项 | 本批实现与可重复证据 | 进度判定 |
| --- | --- | --- |
| 原 #003 四级实测 | `MeasurementEvidence.cs`、`MeasurementEvidenceTests`：四等级、成本/来源/观察及到期、失效条件、快照、脱敏 | 核心完成；界面展示未验收，保留进行中 |
| 原 #004 授权小额探测 | `AuthorizedProbePlan.cs`、`AuthorizedProbeCoordinator.cs`、`AuthorizedProbePlanTests`：未授权/未知费用零提交、次数/单次/累计边界、提交前落盘、稳定任务对账、超时/取消/重开、超费用暂停、快照篡改拒绝 | 核心与离线协调完成；可复核用户操作及真实授权验收未完成，保留进行中 |
| 原 #005 Moark 旧能力 | `MoarkProviderAdapter.cs`、`MoarkAdapterCompatibilityTests`：四类 CLI 参数、精确指纹预像、上传句柄/文件名、版本 1 记录、跨记录去重、预算锁、输出复用/哈希错误拒绝、未知恢复及迟到响应释放 | 完成既有请求的离线适配与兼容判据，从路线图移除 |
| 原 #009 费用/脱敏 | `ProviderSafetyRegressionTests`、旧异步/安全回归：双协调器单次提交、十二请求争用预算、稳定任务绑定与重复用量、未知占用、超时/取消/重开、多币种、恶意费用/币种、签名来源、错误任务号、同号跨提供商恢复 | 离线判据通过，从路线图移除 |
| 原 #012 技能执行契约 | `SkillExecutionContract.cs`、`SkillExecutionContractValidator.cs`、`SkillExecutionContractTests`：固定 11 ID/版本、输入/证据/工具/输出/质量门/回退、严格 JSON 与固定 12 文档全文摘要 | 合同及文档一致性判据通过，从路线图移除；实际执行仍在后续事项 |

`ProbeProjectIntegrationTests` 将必填外发预检回调绑定到 `MpsProbeProjectPreflight.CheckAsync`，验证未授权素材、停用账号、失效模型锁、跨账号用途和内容哈希变化均在提交/意图/预算登记前拒绝；允许路径先保存两个意图再只提交一次，以稳定任务号确认费用。该预检检查本地素材，并未声称已完成实际传输时的同句柄外发契约。

并发提交意图现在由恢复仓库原子登记。有效任务号在费用校验前单独安全保存；非法价格/币种只能产生未知状态，不能先污染恢复字段；过期无号意图不能抹掉人工找回任务号。预算绑定在同一锁内迁移意图行，未知占用只能增额或按确认费用对账，不能未经确认释放。

## 保留的未知与外部条件

Moark 四个静态请求映射仅来自仓库已有契约；展示 ID/请求 ID 在该兼容映射中相同，版本为 `moark.legacy-offline.v1`。未知网关分组、其他模型、真实账号权限、实时价格和接口变动仍未实现或未验证。Moark 取消端点未经核实，适配器返回未知/不可用并且零请求；同步图片的本地投影 ID 不能发给供应商查询。

sonnet.vip 的站点部署、账号能力和生产调用仍未知。四级实测的离线证据不会提升任一供应商为真实“已实测可用”。本批不补 Windows 10/11 实机性能、录屏、预览或安装证据，不宣告阶段 A 或阶段 C 的完整出口通过。

路线图仅移除原 #005、#009、#012，余下连续 103 项；C 为 `003-009`，D 为 `010-021`，后续范围与首批冒烟/发布门交叉引用同步调整。开发结果留在本地工作区，没有提交或推送。
