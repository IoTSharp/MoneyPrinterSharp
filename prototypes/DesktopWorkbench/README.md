# 桌面交互原型

运行：`dotnet run --project prototypes/DesktopWorkbench/DesktopWorkbench.csproj`。场景下拉框用于走查七种状态；左侧切换项目、会话与技能，中间发送模拟指令，右侧切换画幅并拖动或编辑时间线片段。窄于 1100 逻辑像素时使用左上角菜单访问导航。

离线验证：`dotnet run --project prototypes/DesktopWorkbench/DesktopWorkbench.csproj -- --smoke`。图像检查：同命令改为 `--capture`，在系统临时目录生成宽、窄、最小窗口 PNG。原型不保存项目、不发模型请求、不渲染真实成片；所有生成和预算操作均为交互占位。
