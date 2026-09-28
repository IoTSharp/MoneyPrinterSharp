# MoneyPrinter# 开发交接

展示名为 MoneyPrinter#，简称 MPS / MP#。GitHub 主仓库为 [IoTSharp/MoneyPrinterSharp](https://github.com/IoTSharp/MoneyPrinterSharp)；原 Gitee 仓库保留迁移前历史。后续开发在本独立仓库中继续。

## 当前结构

- `skills/video-production-series`：总入口；其余十个技能覆盖软件功能证据、介绍顺序、剧本分镜、模型选择、主持人、配音、口型、透明合成、验收与费用交付。
- `src/VideoProduction`：C# CLI，包含本地 FFmpeg 管线、Moark 请求与恢复、凭据管理、成本去重及技能安装。
- `tests/VideoProduction.Tests`：无需付费服务的控制台回归测试。

## 构建与验证

在仓库根目录使用 .NET 10 和 PowerShell 7：

```powershell
dotnet build src/VideoProduction/VideoProduction.csproj --disable-build-servers -p:UseSharedCompilation=false
dotnet run --project tests/VideoProduction.Tests/VideoProduction.Tests.csproj -p:UseSharedCompilation=false
dotnet run --project src/VideoProduction/VideoProduction.csproj --no-build -- help
```

FFmpeg 依赖可使用 `doctor` 探测。用 `install-skills` 将 11 个技能链接到本地技能目录；已经安装到旧仓库路径的链接应核对归属后迁移，不能覆盖其他技能内容。

## 验证范围与后续工作

已有构建、基本离线回归、FFmpeg 能力探测、历史费用导入和技能格式检查。历史已知费用约 ¥93.34，缺失价格仍需供应商账单确认；该数值不是产品定价。

完整真实素材的透明合成、口型一致性和最终成片还需要独立验收，不能用 `doctor` 或基础测试替代。后续优先补齐：

1. 可复制的无敏感信息项目清单、各模型请求样例和准确的子命令帮助。
2. 极短本地素材的 split / key / render / verify 集成验证，检查透明通道、字幕、音画时长及口型声明。
3. 提交、轮询、预算锁和超时恢复的离线模拟测试；禁止通过重复付费调用代替测试。

本次迁移保留 Gitee 两个原始提交和 GitHub 初始化提交及 MIT 许可证；不带原生产录像、任务账单或凭据进入公开仓库。
