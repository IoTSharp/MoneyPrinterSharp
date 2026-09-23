# 统一命令行约定

统一工具位于仓库 `src/VideoProduction/VideoProduction.csproj`。不要硬编码仓库盘符；从当前技能的物理目录向上两层定位仓库根，并用项目文件存在性确认结果。

## 定位与调用

在 PowerShell 7 中，把 `<当前技能目录>` 替换为本次已加载技能所在目录：

```powershell
$vpSkillItem = Get-Item -LiteralPath '<当前技能目录>'
$vpPhysicalSkill = if ($vpSkillItem.LinkType -in @('Junction','SymbolicLink') -and $vpSkillItem.Target) {
    [string]($vpSkillItem.Target | Select-Object -First 1)
} else {
    $vpSkillItem.FullName
}
$vpRepo = (Resolve-Path -LiteralPath (Join-Path $vpPhysicalSkill '..\..')).Path
$vpProject = Join-Path $vpRepo 'src\VideoProduction\VideoProduction.csproj'
if (-not (Test-Path -LiteralPath $vpProject -PathType Leaf)) {
    throw "未能从技能目录定位 VideoProduction.csproj"
}
dotnet run --project $vpProject -- --help
```

首次使用某个子命令先读取它的 `--help`，只传当前版本明确支持的选项。不要通过递归扫描寻找项目或媒体工具。

## 命令族

```text
video-production init
video-production features
video-production validate
video-production costs
video-production credentials set
video-production credentials status
video-production moark submit --kind image|voice|motion|lipsync --request <json> --record <json> --budget-cny <金额> --estimate-cny <金额>
video-production moark poll --record <json> --download <路径> --max-attempts <次数> --timeout-seconds <秒>
video-production doctor
video-production probe
video-production split
video-production key
video-production render
video-production verify
video-production install-skills
```

根命令直接使用 `doctor/probe/split/key/render/verify`，不增加 `media` 前缀。`moark submit` 的 `--request` 是供应商请求正文，`--record` 是本地任务记录；两者不可混用。

## 有界执行

- `submit` 每个记录文件只执行一次；失败或超时后先对同一记录执行 `poll` 或检查状态。
- `poll` 必须同时给出 `--max-attempts` 和 `--timeout-seconds`，超时返回“未知/仍可能执行”，不是“未扣费”。
- 长时间媒体命令应由调用方记录进程归属，并设置外层墙钟超时。只终止核实属于本任务的完整子进程树。
- 安装使用 `install-skills` 在本地技能目录创建指向仓库技能目录的符号链接。不要复制技能，否则跨技能相对引用和后续更新会失效。
