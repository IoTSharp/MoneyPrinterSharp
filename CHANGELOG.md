# MoneyPrinter# 变更日志

本文件记录已经完成的实现与文档变更。未完成任务、预期能力及后续顺序放在 [ROADMAP.md](ROADMAP.md)。下列“已有实现基线”是截至 2026-09-28 对仓库现状的盘点，不表示本次新增或已完成真实素材的最终验收。

## 未发布

### 本轮新增（2026-10-04）

- 完成费用与任务恢复、技能运行时的离线契约：新增统一价格/限制元数据，区分未知、估算和已确认金额；新增按提供商与稳定任务号去重的项目预算账本，支持预留、确认、未知、释放和对账，严格限制币种并拒绝硬预算越界。
- 新增有界异步任务协调器与恢复快照：提交、查询与取消的单操作等待默认 30 秒、最高 5 分钟；轮询有次数上限与覆盖查询和延迟的总截止时间。取消令牌与等待时限同时生效，即使适配器忽略取消也会终止本次等待。提交前登记恢复记录，超时或用户中止后保留未知状态，重开时先查询旧任务，避免重复提交；快照只保存脱敏指纹、任务号和状态，不保存请求正文、凭据、原始响应或签名地址。
- 新增固定 11 个技能入口注册表（总入口加十阶段）与可恢复阶段状态/产物契约，记录阶段依赖、产物引用、阻断原因、状态迁移、版本和更新时间；严格拒绝缺失、重复、未知技能及未知 JSON 字段，并兼容旧十阶段状态机迁移。完整阶段执行所需的前置证据、允许工具、输出结构、质量门、回退阶段及技能文档一致性校验仍在路线图 #012。
- 新增价格/预算、异步恢复、技能注册表与阶段计划离线合同测试，接入统一测试入口；补充不返回或忽略取消的适配器、轮询查询/延迟总时限、用户中止后重开不重复提交的回归。验证不访问真实供应商、Windows Credential Manager 或付费服务。提交前以 `--no-restore` 构建 CLI/核心与测试（关闭构建服务器和共享编译）均为 0 警告、0 错误，`dotnet run --project tests/VideoProduction.Tests/VideoProduction.Tests.csproj --no-build --no-restore -p:UseSharedCompilation=false` 和 CLI `help` 均通过。
- 按路线图规则移除已验证的价格/限制、预算账本、异步协议与注册表基础工作，并保留尚未完成的阶段执行契约；里程碑 C 更新为 `003-011`，里程碑 D 更新为 `012-024`，后续阶段同步更新至 `106`。首批提供商真实账号、价格和生产调用仍保持未知；本轮开发与离线验证未发起真实供应商或付费请求。

### 本轮新增（2026-10-03）

- 完成里程碑 C 的账号与目录后续（原路线图 #003、#004、#005、#006、#010、#011）：新增 `ProviderAccountConfigurationSet` 多账号配置与当前选择，凭据只保存 Windows Credential Manager 目标引用；新增 API 连接边界，校验 HTTPS/TLS、精确目标主机、认证重定向、回环代理、响应大小和有限超时；新增公开模型目录缓存，支持有界分页、来源/版本/TTL，刷新失败保留旧缓存；新增账号可用性事实表，将账号/模型存在、权限、余额、配额和区域分开记录，401、403、余额不足和未知状态不互相误判；新增推荐与人工锁定解析，硬预算或能力证据不足时不自动选择，锁定失效时暂停交由用户选择。
- 新增账号配置、连接边界、目录缓存、账号可用性和模型路由的离线合同测试，并接入 `tests/VideoProduction.Tests` 统一入口。测试不访问网络、Windows Credential Manager 或付费服务；模型目录适配器只使用内存分页对象，连接响应仅使用本地 `StringContent`。为满足有界执行要求，目录分页、响应读取和素材哈希有页数/块数/大小/墙钟/取消边界，账号状态存储有条目上限和取消边界。
- 完成验证：`dotnet build src/VideoProduction/VideoProduction.csproj --disable-build-servers -p:UseSharedCompilation=false`、`dotnet build tests/VideoProduction.Tests/VideoProduction.Tests.csproj --disable-build-servers -p:UseSharedCompilation=false` 与 `dotnet run --project tests/VideoProduction.Tests/VideoProduction.Tests.csproj --no-build -p:UseSharedCompilation=false` 均通过（0 警告、0 错误；离线回归通过）。本次未发起真实账号请求、付费探测或远端推送；Moark、sonnet.vip 的账号权限、价格与生产接口仍保持未知。
- 完成里程碑 C 的能力与提供商基础契约（原路线图 #003、#004、#005、#021）：新增七类能力描述、输入/输出/用途与证据状态；新增不会根据模型名称猜测能力的模型描述符，显式保留未知模态、限制、执行方式和价格；新增目录、账号状态、能力探测、任务提交/状态/取消、产物下载、用量和错误映射的分层 `IProviderAdapter` 契约及有界参数校验。
- 新增完全离线的 `OfflineProviderSimulator`：覆盖分页目录、响应格式标记、401/403 权限分层、限流和总请求上限、稳定幂等任务号、异步成功/失败、费用/用量、取消和短期签名输出；签名地址只在内存返回值中出现，不进入状态、用量、日志或持久化文件。实现与验证说明见 [里程碑 C 契约记录](docs/milestone-c/provider-adapter-contracts.md)。
- 新增能力/模型、适配器和离线模拟器回归，测试只使用本地对象和可注入时钟，不访问网络或付费服务；同步接入 `tests/VideoProduction.Tests` 离线入口。完成验证：`dotnet build src/VideoProduction/VideoProduction.csproj --disable-build-servers -p:UseSharedCompilation=false`、`dotnet build tests/VideoProduction.Tests/VideoProduction.Tests.csproj --disable-build-servers -p:UseSharedCompilation=false` 与 `dotnet run --project tests/VideoProduction.Tests/VideoProduction.Tests.csproj --no-build -p:UseSharedCompilation=false` 均通过（0 警告、0 错误；离线回归通过）。
- 按路线图规则移除上述四项已验证工作并将后续未完成项连续重编号；里程碑 C 现为 `003-020`，阶段 D-J 的范围同步更新。Moark、sonnet.vip 的真实账号、价格和生产调用仍保持未知，未标为已接通。
- 完成 P1 里程碑 B（003-013）共享核心与项目持久化：新增旧版 `VideoManifest` 到多轨项目的显式迁移，保留章节源路径、证据、旁白文本、字幕及口型声明，并返回不可迁移字段报告。
- 新增有界素材索引：按项目相对路径、大小、SHA-256、容器/编解码、分辨率、时长、帧率和音频参数建立身份；同名不同内容不混淆，失联或内容变化只附加诊断并保留引用；FFprobe 仅接受显式已确认路径。
- 新增项目多会话与十阶段状态机：素材、轨道、阶段产物、预算只在共享状态保存一次；会话消息独立；状态支持未开始、进行中、待复核、通过、失败、失效，阶段变化按顺序使受影响下游失效。`mps.project.sessions` 快照支持 JSON 往返、预算恢复、稳定 ID 和共享引用重建。
- 新增统一编辑命令层（移动、裁切、分割、排序、字幕、音量）以及撤销/重做；命令只修改项目状态，不改原素材。新增原子项目存储、命名版本、数量/体积上限、最近完整快照恢复和项目写入租约，重复窗口不能覆盖项目。
- 新增证据记录和敏感 URL 拒绝、Unicode NFC/中英文换行及字体回退、带轨道/片段路径的项目诊断校验。项目素材引用保存索引元数据，项目目录继续拒绝未知字段、越界路径、非法时间/速度、重复 ID 和父轨道循环。
- 新增离线回归：素材索引、会话/阶段快照、并发锁、迁移、编辑撤销重做、原子版本、证据脱敏和详细诊断均纳入 `tests/VideoProduction.Tests`；未触发网络或付费请求。
- 验证：`dotnet build src/VideoProduction/VideoProduction.csproj --disable-build-servers -p:UseSharedCompilation=false` 和 `dotnet build tests/VideoProduction.Tests/VideoProduction.Tests.csproj --disable-build-servers -p:UseSharedCompilation=false` 均通过（0 警告、0 错误）；`dotnet run --project tests/VideoProduction.Tests/VideoProduction.Tests.csproj --no-build -p:UseSharedCompilation=false` 通过。阶段 A 的 Windows 10/11 实机证据仍未完成，不能据此宣称阶段出口 A 已通过。

### 本轮新增（2026-10-02）

- 完成里程碑 B 多轨项目模型（原 #3）：`MpsProjectDocument` 现在可保存画布配置、当前画布、屏幕/主持人/旁白/音乐/字幕/叠加轨道、父子层级、静音/隐藏状态及片段引用。片段使用 `{num, den}` 有理数时间、`speed_num/speed_den` 变速和受限的增益、布局、字幕、主持人抠像与口型属性；媒体片段外键由项目素材索引校验，字幕和主持人属性按轨道类型校验。
- 项目目录打开/保存接入多轨校验，拒绝重复或缺失 ID、错误素材外键、非法坐标/速度/帧率、父级循环、越界属性和嵌套未知字段；不同轨道的片段重叠保留给后续重叠策略任务。新增离线模型回归覆盖五类轨道 JSON 往返、1/3 秒精确值、父子轨道、项目保存重开及上述拒绝场景。
- 验证：`dotnet build src/VideoProduction/VideoProduction.csproj --disable-build-servers -p:UseSharedCompilation=false` 通过（0 警告、0 错误）；`dotnet run --project tests/VideoProduction.Tests/VideoProduction.Tests.csproj -p:UseSharedCompilation=false` 通过。阶段 A 的 Windows 10/11 实机证据仍未完成，不能据此宣称阶段出口 A 已通过。

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
