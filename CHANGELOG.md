# MoneyPrinter# 变更日志

本文件记录已经完成的实现与文档变更。未完成任务、预期能力及后续顺序放在 [ROADMAP.md](ROADMAP.md)。下列“已有实现基线”是截至 2026-09-28 对仓库现状的盘点，不表示本次新增或已完成真实素材的最终验收。

## 未发布

### 里程碑 A：契约与验证补证（2026-09-29）

- 按 `impeccable` 的桌面操作界面原则改善 Avalonia 原型：浅灰导航、白色内容区、石墨预览和单一蓝色交互强调，统一颜色资源并收敛轨道色、圆角和消息边框。中文字体固定 Microsoft YaHei UI；修正 Fluent 模板占位文字的 50% 透明度覆盖，实际颜色 `#5D6672`。构建、原型烟测和九组离屏布局通过，实际字形段与占位模板增加回归断言。
- ✅️ 完成公开契约梳理（原 A001）：[六个公开来源复核](docs/milestone-a/provider-public-evidence-2026-09-29.md)补足路径、字段与文档差异；Moark 可用配额明确为剩余异步并发量，sonnet.vip 部署事实仍标未知。账号权限与计费实测不属于本次结论。
- ✅️ 完成安全设计与离线验证（原 A003）：项目外发授权按各维度求交，Moark 实际提交/查询共用白名单响应提取；27 个拒绝、1 个允许、2 个取消及 7 个响应持久化场景通过，原离线回归保持通过。规则与实际字节绑定、全适配器外发控制仍需后续集成。
- 原型烟测增加裁短、完整状态撤销及会话共享验证；新增三种尺寸×三种离屏 DPI 的九组布局、PNG 尺寸与非空白检查，独占目录保存证据，支持取消和限时。
- 新增独立 C# [媒体基线工具](prototypes/MediaBaseline/README.md)，本机横竖 1080p、15 秒 H.264/AAC 样本均完整解码 450 帧，记录定位、转码、完整解码与峰值内存。[单机测量](docs/milestone-a/media-baseline-2026-09-29.md)只形成候选阈值，第二台机器及真实 UI/录屏仍待验收。
- 完成[需求/架构/项目格式工程审阅](docs/milestone-a/verification-2026-09-29.md)，同步授权字段、旧清单迁移时间语义及验收矩阵；旧渲染任务栏品牌改为 MoneyPrinter#。路线图移除两个已完成分项并更新连续编号，里程碑 A 整体保持未完成。

### 里程碑 A：本轮新增（2026-09-29）

- 桌面技术方案改为 Avalonia 12.1.2 + AtomUI 6.2.1，建立 [.NET 10 交互原型](prototypes/AvaloniaWorkbench/README.md)。已在 Windows 11 构建零警告，`--smoke --capture` 退出码 0；七种场景、会话切换、窄窗口项目/会话/技能菜单、五轨片段分割/移动/撤销通过离线烟测，1360×840、820×680、760×620 离屏图已人工走查。模拟预览和导出不代表真实媒体能力。
- 更新[桌面宿主决策](docs/milestone-a/desktop-adr.md)：WPF 原型转为历史对照，记录 AtomUI LGPL-3.0 分发义务及跨平台 UI 与第一版 Windows 媒体支持的边界；Windows 10、DPI、录屏、真实预览及安装仍待验证。

### 里程碑 A：此前阶段基线（2026-09-29）

- 完成 V1 [需求验收矩阵](docs/milestone-a/acceptance-matrix.md)：26 项必需能力均对应 UI 入口、领域责任、验证方法与交付产物；后续里程碑的测试尚未执行。
- 建立可点击的 [.NET 10 WPF 交互原型](prototypes/DesktopWorkbench/README.md)：覆盖空项目、制作中、失败、预算耗尽、模型未知、导出完成和窄窗口；`--smoke` 遍历状态恢复入口、会话切换、窄窗菜单及片段分割/移动/撤销，退出码 0。`--capture` 对 1360×840、820×680、760×620 生成非黑图像并人工检查；原型只用模拟数据。
- 完成[项目格式与兼容策略](docs/milestone-a/project-format.md)的阶段 A 设计：新多轨项目以 `project.mps.json` 为唯一编辑事实来源，旧版 `manifest.json` 保持 CLI 输入并以显式迁移报告承接。新建 `examples/demo-mps/manifest.json` 由现有 CLI `validate` 校验通过；没有修改原 CLI 或技能 ID。
- 建立[无敏感演示工程](examples/demo-mps/README.md)及 C# 生成器：中英文合成界面、录屏、音调、字幕和未同步口型的几何主持人替身均为本地生成、MIT 许可。FFprobe 检查录屏为 1280×720 H.264/AAC、4.000 秒，主持人片段为 640×720 H.264、4.000 秒；FFmpeg 对两段视频完整解码通过。这些占位素材不构成最终成片或真实功能证据。
- 新增[供应商公开契约记录](docs/milestone-a/provider-contracts.md)、[安全与数据流](docs/milestone-a/security-data-flow.md)、[非功能基线草案](docs/milestone-a/nonfunctional-baseline.md)和桌面宿主决策初稿；其中账号能力、脱敏实现测试及性能阈值尚未完成。桌面宿主决策已在本轮更新。

### 文档

- 完成 Windows 桌面视频制作工具的第一版需求分析、竞品与相关技能调研，并建立根目录待办路线图。
- 将跨任务生效的产品、模型、费用、安全和进度约束集中到 `AGENTS.md`，调整文档导航。

## 已有实现基线（截至 2026-09-28）

- `src/VideoProduction` 提供 .NET 10 CLI 入口；已有项目初始化、功能证据、清单校验、费用核算、凭据状态与安装技能命令。
- 已有 FFmpeg/FFprobe 的 `doctor`、`probe`、`split`、`key`、`render`、`verify` 管线及有界外部进程执行。此处仅陈述代码能力，真实成片仍需独立验收。
- 已有 Moark 图片、配音、动作视频和口型同步四类固定模型请求，包含任务记录、预算预留、轮询与超时恢复逻辑；尚无动态模型目录或通用提供商适配层。
- 已有 Windows Credential Manager 凭据存储、按 `provider + task_id` 去重的费用统计，以及无需付费请求的基础离线回归测试。
- `skills/` 已有 `video-production-series` 总入口和十个阶段技能；这些技能当前是供代理读取的说明，桌面应用的独立 C# 代理运行时尚未实现。
