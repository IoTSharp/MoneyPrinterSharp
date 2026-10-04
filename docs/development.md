# MoneyPrinter# 开发交接

展示名为 MoneyPrinter#，简称 MPS / MP#。GitHub 主仓库为 [IoTSharp/MoneyPrinterSharp](https://github.com/IoTSharp/MoneyPrinterSharp)；原 Gitee 仓库保留迁移前历史。后续开发在本独立仓库中继续。

## 会话交接铁律

每个会话结束前必须在 `docs/handoffs/` 创建一份带日期的 Markdown 交接文件，至少记录当前提交与工作区状态、已完成实现、验证命令及结果、未完成路线图事项、已知未知/阻断、下一会话首要动作和资源清理结果。交接文件必须随本次代码提交进入仓库。

每个新会话开始实现前必须先阅读最新交接文件，再阅读根目录 `AGENTS.md`、本文件、`CHANGELOG.md` 和 `ROADMAP.md`，核对 `HEAD` 与交接文件记录的提交一致；若工作区或提交不一致，先记录差异并暂停依赖旧状态的实现。新会话不得把交接文件中的“已验证”扩展成真实供应商、账号权限或生产能力主张。

## 当前结构

- `skills/video-production-series`：总入口；其余十个技能覆盖软件功能证据、介绍顺序、剧本分镜、模型选择、主持人、配音、口型、透明合成、验收与费用交付。
- `src/VideoProduction.Core`：桌面程序与 CLI 可直接引用的 .NET 10 共享核心，包含项目目录、媒体流程、清单校验、费用汇总、进程执行及安全预检。
- `src/VideoProduction`：C# CLI，负责命令参数、Moark 请求与恢复、凭据管理及技能安装；媒体流程由共享核心执行。
- `tests/VideoProduction.Tests`：无需付费服务的控制台回归测试。
- `prototypes/AvaloniaWorkbench`：Avalonia + AtomUI 交互原型、七场景烟测与九组合离屏布局验证。
- `prototypes/MediaBaseline`：有界本地合成媒体测量工具，记录横竖 1080p 探测、定位、编解码与峰值内存。

## 构建与验证

在仓库根目录使用 .NET 10 和 PowerShell 7：

```powershell
dotnet build src/VideoProduction/VideoProduction.csproj --disable-build-servers -p:UseSharedCompilation=false
dotnet run --project tests/VideoProduction.Tests/VideoProduction.Tests.csproj -p:UseSharedCompilation=false
dotnet run --project src/VideoProduction/VideoProduction.csproj --no-build -- help
```

FFmpeg 依赖可使用 `doctor` 探测。用 `install-skills` 将 11 个技能链接到本地技能目录；已经安装到旧仓库路径的链接应核对归属后迁移，不能覆盖其他技能内容。

## 验证范围与后续工作

已有构建、基本离线回归、外发授权契约与供应商记录脱敏测试、FFmpeg 能力探测、历史费用导入和技能格式检查。本轮阶段 A 补证见[工程审阅与验证记录](milestone-a/verification-2026-09-29.md)，单机数据不代表阶段 A 整体完成。历史已知费用约 ¥93.34，缺失价格仍需供应商账单确认；该数值不是产品定价。

完整真实素材的透明合成、口型一致性和最终成片还需要独立验收，不能用 `doctor` 或基础测试替代。后续优先补齐：

1. 在已有无敏感演示工程、公开契约和 [Moark 四类离线适配桥接](milestone-c/moark-legacy-adapter.md)基础上，补齐各模型准确请求样例和子命令帮助；sonnet.vip 站点部署事实保持未知。
2. 极短本地素材的 split / key / render / verify 集成验证，检查透明通道、字幕、音画时长及口型声明。
3. 将[四级实测与授权小额探测契约](milestone-c/measurement-probe-contracts.md)接入可复核的用户操作；核心和 Moark 旧记录已经完成离线兼容、预算绑定与未知恢复。项目侧可用 `MpsProbeProjectPreflight.CheckAsync` 对接账号启停、人工模型锁定和精确素材外发许可，提交前持久化回调保存计划与任务意图；这不替代实际发送时同句柄绑定或真实授权验收，禁止通过重复付费调用代替测试。
4. 按[技能阶段执行契约](../skills/video-production-series/references/skill-execution-contract.md)实现阶段执行器与代理工具门。固定 11 个入口保持兼容；修改 SKILL.md 时必须同步合同版本、内容与摘要，并通过 `SkillExecutionContractTests`。契约校验通过不代表各技能已完成生产执行。

2026-10-04 本批基于 `7689a08ccb26ee575f82eeac08b3fe3bc4461305` 的本地实现/验证见[验证记录](milestone-c/verification-2026-10-04.md)；没有自动提交或推送。

本次迁移保留 Gitee 两个原始提交和 GitHub 初始化提交及 MIT 许可证；不带原生产录像、任务账单或凭据进入公开仓库。
