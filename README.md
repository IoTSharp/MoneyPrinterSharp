# 视频制作系列的步骤

这是一个可复用的 Codex 技能系列，用于把软件源码和脱敏页面整理成可验证的功能讲解视频。主入口是 `video-production-series`，按需路由到十个阶段：

`feature-audit → narrative-plan → screenplay → model-selection → presenter → narration → lip-sync → composition → quality-review → cost-delivery`

技能不携带生产账号、业务数据、供应商密钥或固定机器路径。技能安装器会在本地技能目录创建指向本仓库的符号链接；不要复制目录，以免相对引用和更新失效。

## 使用边界

- 功能主张必须能追到源码、路由/API、可复现页面或脱敏素材证据。
- 端口可达不等于视频可用；必须实际探测容器、解码帧、音轨和透明通道。
- 图片模型只产生静态图，不能冒充动作、时序或口型同步。
- Moark 后端按实际模型记录；Qwen、Vidu、Duix 不写成 GPT。
- 口型片段必须由同一段最终旁白驱动；备用片段明确标记不同步。
- Duix 单段不超过 60 秒，默认约 40 秒；五分钟以实测旁白时长为准，不用全片 1.4 倍速硬压。
- alpha 中间件使用经探测通过的 WebM 或其他透明格式；MP4 只作为背景合成后的最终交付。

## 目录

- `skills/video-production-series/`：总入口、共享契约和命令约定。
- `skills/video-01-feature-audit/` 至 `video-10-cost-delivery/`：阶段技能。
- `docs/`：持续维护的契约、安装与验收说明。
- `examples/`：不含密钥和生产数据的项目及请求样例。
- `src/VideoProduction/`：统一 C# CLI 源码。

## CLI

在 PowerShell 7 中，从已安装技能的物理目录向上两层定位仓库根，再确认 `src/VideoProduction/VideoProduction.csproj` 存在。完整命令和安全边界见总入口的 `references/cli-reference.md`。先运行 `--help`，只使用当前实现支持的参数：

```text
video-production init
video-production features
video-production validate
video-production costs
video-production credentials set|status
video-production moark submit|poll
video-production doctor|probe|split|key|render|verify
video-production install-skills
```

## C# 构建与离线验证

在 PowerShell 7 中执行：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\src\VideoProduction\VideoProduction.csproj --disable-build-servers -p:UseSharedCompilation=false
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tests\VideoProduction.Tests\VideoProduction.Tests.csproj --no-restore
& '.\src\VideoProduction\bin\Debug\net10.0\VideoProduction.exe' doctor
```

`doctor`、`probe`、`split`、`key`、`render` 和 `verify` 只处理本地素材。费用核算示例：

```powershell
& '.\src\VideoProduction\bin\Debug\net10.0\VideoProduction.exe' costs --input .\records --output .\delivery\costs.json --markdown .\delivery\costs.md
```

`costs` 按 `provider + task_id` 去重；已确认金额、未知价格和估算预留分别统计。超时任务保留为 `unknown`，不能据此再次提交付费请求。

## 示例

`examples/example-project.json` 按 `Models.cs` 的 snake_case 字段给出最小清单。`examples/requests/` 只含请求结构占位值，不会触发供应商调用。先完成审计、脚本和样片确认，再由用户明确授权外部提交。

## 验证

技能校验器只检查 frontmatter、命名和未完成脚手架占位符；它不代替实际媒体验收。完成技能修改后，应对每个技能运行 `quick_validate.py`，并对 CLI 做无网络、无凭据的帮助/验证检查。
