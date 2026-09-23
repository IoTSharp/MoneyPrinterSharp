# 安装与定位

技能目录应指向仓库内对应技能目录，而不是复制文件。安装器使用 Windows 符号链接；安装后，技能可以从自身物理路径向上两层找到仓库根；之后以 `src/VideoProduction/VideoProduction.csproj` 文件存在作为成功条件。

## 检查

在 PowerShell 7 中：

```powershell
$PSVersionTable.PSVersion
$skill = Get-Item -LiteralPath '<本地技能目录>'
$physical = if ($skill.LinkType -in @('Junction','SymbolicLink') -and $skill.Target) { [string]($skill.Target | Select-Object -First 1) } else { $skill.FullName }
$repo = (Resolve-Path (Join-Path $physical '..\..')).Path
Test-Path (Join-Path $repo 'src\VideoProduction\VideoProduction.csproj')
```

不要从盘符根目录递归搜索编译器、项目或技能。工具发现使用 `Get-Command`、明确配置和固定已知路径。

安装器创建链接后应在本地技能目录运行一次无网络的帮助或验证命令；不要在安装阶段提交 Moark 任务。
