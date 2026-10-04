# MoneyPrinter# 会话交接：2026-10-04 本批完成后

## 启动铁律

新会话开始任何实现前，必须先阅读本文件，然后阅读 `AGENTS.md`、`docs/development.md`、`CHANGELOG.md` 和 `ROADMAP.md`；核对当前 `HEAD` 与本文件的“提交基线”一致。若不一致，先记录实际差异并重新建立交接，不得依据旧状态继续实现。

每个会话结束前必须创建新的 `docs/handoffs/YYYY-MM-DD-*.md` 交接文件，并随代码提交。该文件必须包含提交、验证、剩余事项、未知/阻断和下一会话首要动作。

## 提交基线

- 起始基线：`7689a08ccb26ee575f82eeac08b3fe3bc4461305`。
- 本批实现提交：`2ab6542`（已推送到 `origin/main`）。
- 最终交接提交与新会话启动基线：`9a5686a50622ee42fe95c6820d9ddb9135dcd6d3`（已推送到 `origin/main`）。
- 工作区：`D:\Uixe\MoneyPrinterSharp`；产品仓库为 `MoneyPrinterSharp`，产品名为 `MoneyPrinter#`。
- 本会话未访问真实供应商、Windows Credential Manager、账号权限或付费接口；未提交生产素材、原始响应或签名下载地址。

## 已完成实现

- 四级实测证据：只读元数据、免费最小调用、可能计费最小调用、真实素材样片分别记录来源、时间、成本、限制和失效条件；快照拒绝秘密、签名地址和原始响应。
- 授权小额探测：授权期限、提供商/账号/模型/能力锁定、请求摘要、单次/累计金额、币种、次数、项目硬预算和精确素材外发预检；提交前登记恢复意图并持久化；稳定任务号绑定预算；超时、取消、未知费用和超授权费用保留 Unknown，恢复时先查旧任务，不自动重提。
- Moark 旧四类能力适配：图片、配音、动作、口型通过统一适配桥接，保留 `VideoProduction` CLI、请求指纹、版本 1 记录、目录预算锁和下载恢复；图片复用必须匹配原路径和 SHA-256，未知任务与未经核实的取消端点不会伪造成功。
- 11 个技能阶段执行契约：版本、输入、前置证据、允许的 C# 工具、输出结构、阻断质量门、回退阶段及 11 份技能文档和共享说明摘要校验；现有技能 ID 与 `VideoProduction` 入口兼容。
- #009 回归增强：跨协调器原子提交意图、并发硬预算、意图到稳定任务号绑定、重启恢复、取消/超时、多币种、错误任务号、敏感字段脱敏和迟到响应释放。
- 项目预检集成：账号停用、模型锁定失效、用途/账号不匹配、素材哈希变化和未授权外发均在提交意图及预算登记前阻断。

## 验证证据

- PowerShell `7.6.6`，.NET SDK `10.0.401`。
- `dotnet build src/VideoProduction/VideoProduction.csproj --no-restore --disable-build-servers -p:UseSharedCompilation=false`：0 警告、0 错误。
- `dotnet build tests/VideoProduction.Tests/VideoProduction.Tests.csproj --no-restore --disable-build-servers -p:UseSharedCompilation=false`：0 警告、0 错误。
- `dotnet run --project tests/VideoProduction.Tests/VideoProduction.Tests.csproj --no-build --no-restore -p:UseSharedCompilation=false`：完整离线回归通过。
- CLI `help` 与 `validate --manifest examples/demo-mps/manifest.json --draft` 均返回 0。
- `git diff --check` 通过；任务创建的外部进程已按 PID、创建时间、命令行和父进程核对并回收，临时编译目录已清理。
- 详细证据：[docs/milestone-c/verification-2026-10-04.md](../milestone-c/verification-2026-10-04.md)。

## 路线图状态

- 原 #005（Moark 统一适配）、#009（费用/脱敏回归）、#012（技能阶段执行契约）已按离线完成判据移除并记入 `CHANGELOG.md`。
- #003 实测等级展示：🟡，核心证据契约完成，界面展示和真实供应商证据未完成。
- #004 授权小额探测：🟡，核心计划/恢复/预检完成，用户可复核界面操作和真实授权预算验收未完成。
- 其余路线图事项仍是未完成工作；Windows 10/11 第二套实机和阶段 A 出口仍缺外部证据。
- 真实 Moark/sonnet.vip 账号能力、实时价格、生产接口、取消端点和完整成片验收均保持未知。

## 下一会话首要动作

1. 先核对本文件记录的提交号、`git status --short --branch`、`git rev-parse HEAD` 与 `git rev-parse origin/main`。
2. 阅读 `AGENTS.md`、`docs/development.md`、`CHANGELOG.md`、`ROADMAP.md` 及本批验证记录。
3. 优先继续 #003/#004 的可复核展示/用户授权流程，保持无真实供应商网络请求、无凭据访问、无付费调用；必要时先补离线合同测试。
4. 在实现前检查是否需要更新提供商目录/账号事实与模型锁定契约；未知事实只能显示未知，不能自动切换或宣称已接通。
5. 会话结束再次创建新的 `docs/handoffs/` 交接文件并提交。

## 资源与边界

- 所有 PowerShell 必须通过 `C:\Program Files\PowerShell\7\pwsh.exe`，循环/轮询/搜索/批处理必须有最大次数、墙钟超时、取消和退避。
- 不使用 Graphify；不广域扫描磁盘，不安装缺失工具，不引入 Python/JavaScript 业务脚本。
- 外部进程必须记录 PID、创建时间、完整命令行和父进程链；仅清理本任务创建且已确认归属的进程树和临时文件。
- 不把凭据、原始供应商响应、签名 URL、生产素材或付费请求写入仓库、日志或任务记录。
