# MoneyPrinter# 变更日志

本文件记录已经完成的实现与文档变更。未完成任务、预期能力及后续顺序放在 [ROADMAP.md](ROADMAP.md)。下列“已有实现基线”是截至 2026-09-28 对仓库现状的盘点，不表示本次新增或已完成真实素材的最终验收。

## 未发布

### 本轮新增（2026-09-30）

- 建立 `src/VideoProduction.Core` 共享 .NET 10 类库，CLI 与桌面程序可直接调用清单校验、费用汇总、FFmpeg/FFprobe 媒体探测与 split/key/render/verify 流程、进程运行及模型。既有 `VideoProduction` 命令入口和旧清单保持兼容；迁移时清除了媒体渲染中的外部产品硬编码文案。CLI `help` 与演示清单 `validate --draft` 返回 0。
- 新增 `project.mps.json` 项目目录契约和 `assets/source`、`assets/generated`、`cache`、`records`、`sessions`、`versions`、`delivery` 分区。离线回归验证项目复制、移动后的相对路径、失联素材引用、越界路径与重解析点拒绝、并发新建不覆盖；根文件无凭据和原始响应字段，未知字段明确报错。完整时间线数据和自动版本快照仍在后续任务。
- 将项目素材外发规则落实为默认拒绝的精确预检，匹配提供商、账号别名、能力、用途、素材 ID、SHA-256 和到期时间；该文件预检尚未接入实际提交入口。Moark CLI 沿用 2026-09-29 已接入生产提交与轮询路径的 `ProviderResponsePolicy` 白名单提取。离线测试覆盖跨范围、内容变更、过期及密钥/签名地址/错误原文不入任务记录；发送时同句柄锁定、预算交集及全链路脱敏继续由后续任务验收。
- 建立源时间、项目时间、帧率、音频采样和片段变速的有理数坐标契约。离线测试覆盖 24/25/30/60 fps、半值舍入、负起始 PTS、12,000 个 VFR 时间戳、裁切逆映射与精确表示溢出；旧 CLI 清单渲染仍使用原时间格式，轨道序列化留给后续任务。
- 补充首批提供商公开只读契约快照：Moark 匿名模型目录当日返回 223 条有限元数据，sonnet.vip 公开设置自报 `0.2.10`、匿名目录返回 401；账号能力、价格及异步合同仍未知。记录 Windows 11 单机原型与 15 秒 1080p 合成媒体测量，明确其不能代替双系统真实预览和多轨导出性能门。
- 验证：本地提交 `8a9f725` 的 `dotnet build src/VideoProduction/VideoProduction.csproj --disable-build-servers -p:UseSharedCompilation=false` 为 0 警告、0 错误。合并后重新运行离线回归和 CLI `help` 均通过；安全契约包含 27 个拒绝、1 个允许、2 个取消及 7 个响应持久化场景，项目目录、素材哈希预检及时间坐标断言也通过。4 秒本地演示素材经 CLI probe/split/key/render/verify/doctor，MP4 报告完整解码通过、H.264/AAC、4.021333 秒，透明 WebM 报告 alpha 实测通过；成片仍标为未同步口型草稿，不作为真实商业素材验收。

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
