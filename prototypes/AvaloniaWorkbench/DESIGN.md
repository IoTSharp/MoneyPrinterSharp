# Avalonia 工作台设计规则

适用范围：`prototypes/AvaloniaWorkbench` 的 Windows 桌面交互原型。记录日期：2026-09-29。采用 Operate 模式：以持续制作、预览和编辑任务为中心，使用浅色系统界面、清晰的状态与克制的颜色。本文件记录当前实现，不代表生产桌面功能已验收。

## 结构与视觉层级

- 保持左侧项目/会话/技能、中间制作对话、右侧上方预览和下方五轨时间线的三栏结构。
- 普通内容面为白色，导航和工具区域使用浅灰中性层。对话条目以间距和分隔线组织，避免重复彩色卡片。
- 深色区域只承担媒体预览画布和媒体边界。预览内合成演示素材保留原色，不随工作台主题改色，也不冒充真实软件界面。
- 蓝色用于主要操作、当前选择、焦点和播放控制；普通导航、工具按钮与信息状态使用中性色。项目状态同时提供文字，不只靠颜色表达。
- 窄窗折叠左侧导航为图标和菜单入口，保留制作对话、预览、时间线及输入操作；时间线使用横向滚动，不强行压缩片段。

## 配色令牌

令牌在 `App.axaml` 集中定义，由 XAML 和动态创建的原型控件复用。

| 用途 | 资源 | 当前值 |
| --- | --- | --- |
| 内容面 | `MpsSurfaceBrush` | `#FFFFFF` |
| 工作画布 | `MpsCanvasBrush` | `#F8F9FA` |
| 导航、面板 | `MpsPanelBrush` | `#F1F2F4` |
| 标准、次级边界 | `MpsBorderBrush` / `MpsSubtleBorderBrush` | `#D7DBE0` / `#E4E6EA` |
| 正文、次要文字 | `MpsTextBrush` / `MpsSecondaryTextBrush` | `#252A31` / `#5D6672` |
| 禁用文字 | `MpsDisabledTextBrush` | `#8B929C`，仅用于禁用状态 |
| 普通悬停、按下 | `MpsHoverBrush` / `MpsPressedBrush` | `#E8EBEF` / `#DCE1E7` |
| 主色、悬停、按下 | `MpsAccentBrush` / `MpsAccentHoverBrush` / `MpsAccentPressedBrush` | `#285FA6` / `#214F8A` / `#1B4274` |
| 选择、选择悬停 | `MpsSelectionBrush` / `MpsSelectionHoverBrush` | `#E3EBF6` / `#D5E2F3` |
| 主色上的文字 | `MpsOnAccentBrush` | `#FFFFFF` |
| 预览外框、视频面 | `MpsPreviewBrush` / `MpsVideoBrush` | `#25272B` / `#191B1F` |
| 预览消息背景 | `MpsPreviewOverlayBrush` | `#E625272B`，ARGB |

五轨使用少量低饱和类别色，保留文字标签与所在轨道，颜色不承担唯一识别责任。

| 轨道 | 资源 | 当前值 |
| --- | --- | --- |
| 屏幕画面 | `MpsScreenTrackBrush` | `#D1D9E3` |
| 主持人/贴图 | `MpsOverlayTrackBrush` | `#DDDDE0` |
| 旁白 | `MpsVoiceTrackBrush` | `#CEDBD5` |
| 音乐 | `MpsMusicTrackBrush` | `#DCE4DF` |
| 字幕 | `MpsCaptionTrackBrush` | `#E2DFD8` |
| 片段普通边界 | `MpsTrackBorderBrush` | `#A6AFB9` |

## 字体与控件

- 系统字体令牌 `MpsUiFont` 为 `Microsoft YaHei UI, Segoe UI`，以中文界面字体优先；Window、TextBlock、TextBox、ComboBox 和按钮显式复用。标题使用 SemiBold，普通控件使用 Normal，不引入展示字体。
- 默认逻辑字号为 13；标题主要使用 16–17，轨道和元信息按当前密度使用 10–11。DPI 缩放遵循原型布局，不通过整体缩小字体挤入窄窗。
- 普通按钮、输入框、下拉框主要为 4 逻辑像素圆角、1 像素细边界。按钮默认最低高度 32，图标工具按钮为 32×32；侧栏行最低高度 37，折叠导航按钮为 40×40。画幅切换为紧凑控件。
- 图标沿用 Lucide 的统一线性风格。工具按钮默认透明，悬停和按下时使用中性背景，主要按钮使用蓝底白字。
- 选择、悬停、按下、键盘焦点及禁用状态分别定义。Fluent 模板内部的按钮 ContentPresenter 同步使用这些状态画刷，避免外层样式与实际绘制不一致。
- 占位文字使用 `MpsSecondaryTextBrush`。Avalonia 12.1.2 Fluent 的 `TextControlPlaceholderOpacity` 资源设为 `1`，实际 `PART_Placeholder` 同步使用不透明文字；只设置 TextBox.Foreground 或模板样式不足以抵消原有 0.5 透明度。

## 验证记录与限制

本轮保留七种原型场景、双画幅、片段分割/移动/裁短/完整撤销、共享会话与窄窗菜单。自动布局验证覆盖 1360×840、820×680、760×620 逻辑尺寸与 100%、150%、200% 离屏 DPI 的九种组合。代表性截图独立走查覆盖宽窗 100%、窄窗 150% 和最小窗 200%。

最终运行记录为本地 `.runs/milestone-a-validation-20260929/rendered-text-verification.stdout.log`，最终图像目录为 `.runs/prototype-captures/82a1568bb0e844a4a9063352ef406535`。它们是本轮本机证据，不是项目分发依赖。

- 九组运行时字形断言确认正文、轨道和 `PART_Placeholder` 的实际字体为 Microsoft YaHei UI；先前仅凭截图观感判断字体仍有回退的结论据此更正。
- 最小窗 200% 最终截图的占位文字主体像素为 `#5D6672`，白底对比约 5.82:1；运行时占位透明度为 1。先前 `#AEB2B8`、约 2.13:1 的透明度缺陷已解决。
- 最终确认未发现明显布局退化。这里只确认本轮原型配色、字体与占位文字修复，不宣称完成全量可访问性验收、Windows 实机缩放切换、屏幕阅读器、Windows 10 安装、真实视频预览、捕获性能或生产工作流。

后续修改继续复用现有令牌与控件状态，按变化范围进行有界验证；不以截图、构建成功或设计检测器空结果替代真实媒体和目标系统验收。
