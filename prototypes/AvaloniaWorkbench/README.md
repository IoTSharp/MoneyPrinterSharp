# Avalonia + AtomUI 桌面交互原型

在仓库根目录运行：

```powershell
dotnet run --project prototypes/AvaloniaWorkbench/AvaloniaWorkbench.csproj
```

场景下拉框可切换空项目、制作中、任务失败、预算耗尽、模型未知、导出完成和窄窗口。左侧可切换会话与技能；右侧可切换画幅、模拟播放，并选择、拖动、分割、裁短、移动和撤销时间线片段。窄于 1110 逻辑像素时，左侧菜单提供项目、会话、场景和技能入口。

离线验证和截图：

```powershell
dotnet run --project prototypes/AvaloniaWorkbench/AvaloniaWorkbench.csproj -- --smoke
dotnet run --project prototypes/AvaloniaWorkbench/AvaloniaWorkbench.csproj -- --capture
```

截图写入系统临时目录，文件名为 `mps-atomui-wide.png`、`mps-atomui-narrow.png`、`mps-atomui-minimum.png`。原型只用内存中的模拟状态和本地合成图片，不保存项目、不请求模型、不录制或导出真实视频。第一版生产桌面应用与共享项目核心仍需后续里程碑实现。
