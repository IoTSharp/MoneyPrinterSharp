# Avalonia + AtomUI 桌面交互原型

在仓库根目录运行：

```powershell
dotnet run --project prototypes/AvaloniaWorkbench/AvaloniaWorkbench.csproj
```

场景下拉框可切换空项目、制作中、任务失败、预算耗尽、模型未知、导出完成和窄窗口。左侧可切换会话与技能；右侧可切换画幅、模拟播放，并选择、拖动、分割、裁短、移动和撤销时间线片段。窄于 1110 逻辑像素时，左侧菜单提供项目、会话、场景和技能入口。

离线验证和截图：

```powershell
dotnet run --project prototypes/AvaloniaWorkbench/AvaloniaWorkbench.csproj -- --smoke
dotnet run --project prototypes/AvaloniaWorkbench/AvaloniaWorkbench.csproj -- --capture-pilot
dotnet run --project prototypes/AvaloniaWorkbench/AvaloniaWorkbench.csproj -- --capture
```

先用 `--capture-pilot` 验证一个 1360×840 / 100% 组合，再执行完整截图批次。`--capture` 检查 1360×840、820×680、760×620 三种逻辑尺寸及 100%/150%/200% 离屏 DPI，共九张 PNG；校验关键控件可见且位于窗口内、输入/预览/播放/时间线区域不重叠、PNG 像素尺寸和非空白采样。截图与 `verification.json` 写入当前工作目录 `.runs/prototype-captures/<独占运行编号>/`，控制台给出准确路径，不覆盖旧证据。该目录为有意保留的本地诊断产物，不入 Git。

烟测覆盖七种状态、双画幅、片段分割/移动/裁短、完整状态撤销、会话共享时间线和窄窗口菜单。自动验证总时限 60 秒（烟测 15 秒、截图批次 45 秒），固定最多九组截图，可按 Ctrl+C 取消；启动/原生渲染若无响应，还需由进程宿主设置外部时限并回收其创建的进程树。

离屏 DPI 只证明相应像素密度下的布局与渲染，不代表真实显示器缩放、鼠标命中或跨屏切换验收。原型只用内存中的模拟状态和本地合成图片，不保存项目、不请求模型、不录制或导出真实视频。第一版生产桌面应用与共享项目核心仍需后续里程碑实现。

当前配色使用 App 资源中的中性色与单一蓝色强调，XAML 和动态轨道共享颜色。中文优先使用 Windows 的 Microsoft YaHei UI；截图验证会检查实际字形段及输入占位模板颜色/透明度，防止主题覆盖后再次出现字体或对比度退化。离线验证应从仓库根运行。
