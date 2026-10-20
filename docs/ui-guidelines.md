# UI/UX 设计规范（TokenView · WPF 桌面版）

本文件是 TokenView 唯一实现 `wpf/TokenView.Wpf`（C# / .NET 10 / WPF）的视觉与交互规范。所有取值均来自现有代码：画刷在 `Theme.xaml`，主题切换在
`Services/ThemeManager.cs`，窗口与控件在 `MainWindow.xaml`、`SettingsWindow.xaml`、`SessionLedgerWindow.xaml`、
`SessionDetailWindow.xaml`、`AnnouncementWindow.xaml` 与 `Controls/`。新增可视组件或新语义画刷先在本文件登记，再进代码。

TokenView 是长时间驻留的桌面工具型应用：界面安静、克制、信息密度优先，让数据本身成为视觉焦点。基调参考 Linear / Raycast 的中性工具界面，以浅色底 + 品牌翡翠强调色为主。

## 桌面设置约定（2026-10-11，WPF 现状 2026-10-20）

设置入口是主工具栏齿轮按钮，打开独立窗口 `SettingsWindow`（不是抽屉）：左侧固定分类侧栏（`SidebarBrush`，宽 210），右侧内容面板；分类为通用 / 外观与统计 / 采集 / 更新 /
数据与隐私 / 关于。设置行由标题（`StatLabel`）、说明（`MutedText`）与控件组成，空间不足时换行；只用原生 `Button`、`CheckBox`、`ComboBox` 与 `Theme.xaml`
样式，设置窗口内不显示统计日期筛选器。设置即时保存且失败保持实际值；主题切换调用 `ThemeManager.Apply` 后立即生效。首次关闭询问退出或缩小到托盘（`CloseWindowDialog`：440 宽、
`SizeToContent=Height`、不可缩放、不占任务栏），可记住选择、可取消；清除统计与恢复默认设置必须确认。

托盘（`TrayService`）菜单提供打开 TokenView、立即同步、退出；同步进行中禁用立即同步并显示“正在同步…”。
托盘 tooltip 是“今日、本地、按请求时间”的聚合摘要（`TokenView · 今日 X Token · N 请求`），受系统 63 字符限制截断；
只含聚合数据，不含项目路径、会话标识或正文；读取失败退回纯 `TokenView`，不保留一个看似最新的旧数字。
更新检查入口只在设置 · 更新（手动“检查更新”按钮单次触发）；开启自动检查后按“距上次检查 ≥ 24 小时”触发，
命中新版本时用托盘气泡提示版本，点击气泡打开官方下载页，不打扰当前操作、不自动下载或安装。

## 1. 设计原则

1. **内容优先**：先结构（标题、筛选、数据）再装饰；任何视觉效果都不能影响读数的准确性。
2. **克制**：一个页面只保留一个视觉重点；统一卡片外观，避免多余装饰、阴影和动效。
3. **一致**：颜色、间距、圆角、字号、图表配色来自 `Theme.xaml` 的语义画刷与既有尺度；同类信息全局同一种呈现。
4. **可信**：实测 / 换算 / 估算与未知在视觉上必须可区分，单位与时间范围必须可见（见第 6、7 节与 `data-metrics.md`）。
5. **桌面优先**：只做 Windows 10 / 11 桌面，不为移动端适配；小窗口下主内容不得溢出或错位（见第 4 节）。

### 界面文案（2026-10-12）

- 标题、控件与数值能表达的信息，不重复写成副标题、段落或页脚；常驻说明只保留范围、单位、当前口径及条件性异常，详细公式与目录配置集中在来源页与 `docs/data-metrics.md`；
  隐私说明集中在数据与隐私分类及来源页，不逐页重复。
- 未知、已知子集、不完整与估算标记直接可见；空状态用一句短操作提示；清除、恢复默认与联网检查保留必要后果提示。

### 数据源诊断（2026-10-13；分级 2026-10-19）

设置 · 采集逐客户端显示启用状态、最近检查结果（`已检查 · N 个文件已解析` / `未发现数据` / `读取失败` / `源数据变化 · 待重试` / `已停用` / `尚未检查`）与“重扫此客户端”入口；
客户端未启用、采集已暂停或正在同步时禁用重扫。脱敏诊断摘要只在用户点击复制时写入剪贴板，含应用版本、状态、时间与计数，不含路径、项目 / 会话标识或错误原文。源数据在扫描期间变化属于可重试错误：工具栏保持“已同步”，
来源行显示“源数据变化 · 待重试”并保留历史，只有硬错误才标红为“同步异常”。

## 2. 颜色与主题

- 窗口、控件与图表只使用 `Theme.xaml` 定义的语义画刷，通过 `{DynamicResource ...}` 引用，使 `ThemeManager` 换色时立即刷新：`WindowBrush`、
  `CardBrush`、`BorderBrush`、`TextBrush`、`MutedBrush`、`AccentBrush`、`AccentDarkBrush`、`AccentSoftBrush`、
  `HoverBrush`、`TrackBrush`、`ChipBrush`、`RowHoverBrush`、`SidebarBrush`、`DangerBrush`、`ChartGridBrush`。
- **禁止**在 XAML 或代码里写死颜色（`#RRGGBB`、`Brushes.X`、内联 `SolidColorBrush`）；例外只有 `Theme.xaml` 与 `ThemeManager` 的调色板定义、
  `TokenViewDatabase.Clients` 的客户端语义色、以及 `app-icon.svg` 里的品牌字标渐变（见第 12 节）。
- 两套调色板由 `ThemeManager.Apply` 成对写入同一批画刷键，浅深必须逐键对应；新增画刷必须同时给出两种取值。原生标题栏由 `Services/WindowTheme.cs` 通过 DWM 属性跟随主题，
  组件内不得写“深色分支”逻辑。
- 强调色（`AccentBrush` / `AccentDarkBrush` / `AccentSoftBrush`）只用于主操作、选中态与状态正常点，保持稀缺；大面积区域使用 `WindowBrush` /
  `CardBrush` / `ChipBrush` 等中性底色。危险操作用 `DangerBrush` 前景（如“清除全部统计数据”、公告错误条）；错误状态点当前复用 `Brushes.IndianRed`，
  新增错误样式时应改用 `DangerBrush`。

| 画刷键 | 浅色 | 深色 | 主要用途 |
| ------ | ---- | ---- | -------- |
| `WindowBrush` | `#F6F8FA` | `#0F1318` | 窗口底 |
| `CardBrush` | `#FFFFFF` | `#161C23` | 卡片、浮层、tooltip |
| `BorderBrush` | `#DEE5ED` | `#2A333E` | 1px 分隔与描边 |
| `TextBrush` | `#202936` | `#E6EDF3` | 正文与数值 |
| `MutedBrush` | `#687589` | `#93A1B3` | 辅助文字、坐标轴标签 |
| `AccentBrush` | `#099F83` | `#2BB39A` | 主操作、趋势线、状态正常 |
| `AccentDarkBrush` | `#087961` | `#7FE6CD` | 选中态文字 |
| `AccentSoftBrush` | `#E6F5EF` | `#183B34` | 选中底色、表格选中行 |
| `HoverBrush` | `#EEF2F5` | `#1D252E` | 悬停 |
| `TrackBrush` | `#EDF1F5` | `#232C36` | 进度 / 条形轨道 |
| `ChipBrush` | `#EEF2F5` | `#202832` | 徽标底、骨架块、只读路径框 |
| `RowHoverBrush` | `#E3EAF2` | `#28323D` | 行悬停 |
| `SidebarBrush` | `#F8FAFB` | `#12171D` | 设置侧栏 |
| `DangerBrush` | `#C94343` | `#F07178` | 危险操作、校验错误 |
| `ChartGridBrush` | `#DEE5ED` | `#2A333E` | 图表网格与轴线 |

客户端语义色集中在 `Data/TokenViewDatabase.cs` 的 `Clients` 表（`ClientInfo.ColorHex`），Codex `#099f83`、Claude Code `#d97706`、
OpenCode `#6366f1`、Kimi Code `#4f8ff7`、Qoder `#c026d3` 等；未登记客户端统一回退中性 `#6b7280`。这些值只在该表定义一次，界面经
`HexToBrushConverter` 消费，不得在窗口里另调一套客户端颜色，也不得把客户端色当品牌色使用。

## 3. 层次、间距、圆角、字体

| 维度 | 规则 |
| ---- | ---- |
| 层次 | 1px `BorderBrush` + 背景色差 + 间距分层；只有浮层（日期范围 `Popup`）允许 `DropShadowEffect`（`BlurRadius 24 / ShadowDepth 4 / Opacity 0.18`）；卡片不加阴影 |
| 间距 | 页面外边距 24，卡片内边距 20（`CardStyle`）；卡片间 16，区块间 20；卡内标题到内容 12 / 14 / 18；行内 4 / 6 / 8 / 10 / 12；不出现 `13px` 之类散值 |
| 圆角 | 卡片 12（`CardStyle`）、浮层 14、tooltip 与图标按钮 8、次级面板与色块 8、日期范围按钮 7、徽标 5–6、骨架块 4–8、进度条与 `ShareBar` 全圆角（半径 = 高度 / 2） |
| 字号 | 全局 `FontFamily="Microsoft YaHei UI"`；正文 12–13；辅助信息 11–12 + `MutedText`；图表标签 10.5；区块标题 14（`SectionTitle`）；卡片标题 15（`CardTitle`）；窗口标题 18；核心指标 24（`StatValue`）；不使用 24 以上做装饰 |
| 字重 | 标题与数值 `SemiBold`，正文常规；不靠加粗做层级之外的强调 |
| 数字 | 计数用 `N0` 千分位；表格与指标靠固定列宽 + 右对齐 / `TextAlignment="Right"` 对齐（占比列 `MinWidth="46"`），缺失值一律显示 `—`；等宽数字用 `Typography.NumeralAlignment="Tabular"`，不要临时换字体；代码 / 行内等宽片段用 `Cascadia Mono, Consolas, Microsoft YaHei UI` 栈（同 `MarkdownView`） |
| 动效 | 只用于状态反馈，时长 ≤ 200ms；无装饰性动画。唯一常驻动效是骨架透明度呼吸（0.35↔0.9，0.85s 往返），且延迟 140ms 才出现以避免闪烁；呼吸是纯透明度变化、无位移。新增长动画必须尊重系统“关闭动画”偏好（`SystemParameters.ClientAreaAnimation` / `MinAnimate`），当前骨架呼吸尚未接该判断，属待补项 |

## 4. 布局与窗口

- **主窗口尺寸**：默认 1200×860，最小 1000×640（`MainWindow.xaml`）；800×600 是窗口状态恢复接受的下限（`WindowStateStore`）与窄窗口验收尺寸。
  尺寸与位置由“记住窗口尺寸与位置”开关持久化，恢复时按工作区与虚拟屏幕夹取，越界或损坏状态文件回退默认。
- **主框架**：顶部 54px 工具栏 + 下方 `ScrollViewer` 内容区，内容区单列铺满可用宽度，左右边距 24。工具栏两列：左侧日期范围预设（左对齐）/
  右对齐的同步状态与图标按钮；不重复展示页面副标题。2026-10-20 起工具栏不再渲染品牌字标与 `TokenView` 文字，应用身份只保留在窗口标题栏、
  exe 图标与托盘图标（见第 12 节）。
- 日期范围预设（今天 / 7 天 / 本周 / 30 天 / 本月 / 90 天 / 全部）用 `RangeRadio` 单选样式，点击即时切换；自定义用带 `AutomationProperties.Name`
  的日历按钮，在 `Popup` 中编辑并确认。卡片标题右侧只保留“共 N 天”，精确日期区间、时区与口径说明由“请求用量”标题的 `ToolTip` 提供；窄窗口允许按钮换行；打开设置窗口不改变已选范围。
- 同步状态：8px 状态点 + 固定宽 140 的短文案（同步中 / 已同步 / 同步异常 / 已暂停 / 读取中… / 读取失败），避免文案长短推动布局；点击状态文字打开只读 `ContextMenu`，
  展示上次完成时间、扫描 / 解析 / 跳过 / 导入 / 失败计数与逐客户端错误，并提供“打开设置”。工具栏图标按钮固定为刷新、公告、关于、设置四个 34×34
  `IconButton`，“关于”直接打开设置 · 关于分类（复用 `SettingsWindow` 的 `section` 参数，不新建独立窗口），
  无“更多操作”菜单（重扫走设置 · 采集的单客户端入口）。
- 不提供左侧导航：2026-10-16 单页收束后仅剩总览，未来恢复多页时再重新登记导航规范。
- **总览结构**：请求用量卡片（标题 + “共 N 天” + 标题 `ToolTip` 承载精确区间与口径 + 指标切换 + 三项核心指标 + 趋势 + 每日条带）、客户端用量分布卡片、会话视角区块（模型排行 / 项目排行双卡 + 最近会话表 + 口径脚注）。统一
  `CardStyle` 外观，**禁止卡片嵌套卡片**；展开的模型明细用 `ChipBrush` 内联面板，不是第二层卡片。列表与表格（`DataGrid`）是明细的主要呈现方式，避免用小卡片堆叠替代表格。
- **三态**：加载用骨架（`LoadingOverlay` + `ChipBrush` 块，整页数据到位后原位收起）；空数据显示“暂无数据”并说明如何产生数据；错误显示 `DangerBrush` / 状态点异常 +
  可重试操作。当前硬错误走原生 `MessageBox`，新增界面优先做内联提示（如公告窗的错误条 + 重试按钮），不要把整页替换成错误页。后台刷新保留上一份已成功数据，完成后原位替换；不得因刷新让整页闪成骨架或跳回顶部。
- 次级窗口：设置 940×720、会话总账 1100×740、会话详情 1280×760、公告 760×660（最小 560×420），均 `WindowStartupLocation=CenterOwner`、
  `Background=WindowBrush`、`FontFamily=Microsoft YaHei UI`、`UseLayoutRounding=True`。详情表列宽固定并允许表内横向滚动，
  浏览长列表时表头始终可见。

## 5. 组件

- 只用原生 WPF 控件（`Button`、`CheckBox`、`ComboBox`、`RadioButton`、`ListBox`、`DataGrid`、`TextBox`、`ScrollViewer`、
  `Popup`、`ContextMenu`、`ToolTip`、`FlowDocumentScrollViewer`、`NotifyIcon`）加 `Theme.xaml` 样式与仓库自绘控件；
  **不引入第三方 UI 组件库、图标库或主题库**。唯一已登记的第三方渲染依赖是 `Markdig` 0.40.0（只在
  `MarkdownView` 内解析 Markdown，见 `docs/architecture.md`），不得用它承载业务 UI。
- 自绘控件清单（新增须在此登记）：`Controls/RequestLineChart`（趋势折线）、`Controls/DailyStripPanel`（等距每日条带面板）、
  `Controls/DateTimeRangePicker`（双月日历 + 时分选择）、`Controls/MarkdownView`（Markdown → FlowDocument）、`ShareBar`（占比条）、
  `ClientIcon` / `ModelIcon`（本地图标几何）、`ModelBadge`（图标 + 模型名）。
- 样式约定：图标按钮 `IconButton`（34×34，圆角 8，`HoverBrush` / `ChipBrush` 反馈）；次级按钮 `LinkButton`（1px 边框 + 圆角 8 +
  `HoverBrush`）；徽标用 `ChipBrush` 底 + 圆角 5–6；选中行 `AccentSoftBrush`，悬停行 `HoverBrush` / `RowHoverBrush`。
  主操作按钮目前只有日期浮层的 `ConfirmButton`（`DateTimeRangePicker.xaml` 局部样式，`AccentBrush` 底 + 白字，`IsDefault=True`）；
  新增主操作按钮时应把它上收到 `Theme.xaml` 并登记，而不是再复制一份局部样式。
- 图标一律本地绘制，**不加载远程图片或字体图标**；`ClientIcon` / `ModelIcon` 按 24×24 视口缩放，常规 15–16px、紧凑 12–13px，色值取客户端语义色或
  `MutedBrush`。图标是装饰信息，旁边始终保留文字标签，不得用图标推断未记录的客户端或 provider。
- 所有可交互元素提供 hover / pressed / disabled 状态，键盘可达：Tab 顺序符合阅读顺序，表格 `PreviewKeyDown` + `Enter` 打开详情，日期浮层 `Escape` 取消、
  `IsDefault` 按钮确认。图标按钮与自绘控件必须有 `ToolTip` 或 `AutomationProperties.Name`（日历格、月份翻页、时分下拉、公告按钮已带），新增同类控件必须补齐。
- 浮层（`Popup` / tooltip）从触发元素下方展开并保留 8px 间隙（`Placement=Bottom`、`VerticalOffset=8`），日期浮层按 `HorizontalOffset`
  右缘对齐触发按钮；`AllowsTransparency` + 圆角 14 + 阴影，窗口移动或状态变化时收起。不使用 toast 框架：一次性反馈用 `MessageBox` 或状态区文字。
- **公告与关于页 Markdown**（2026-10-20）：工具栏铃铛 `IconButton` + 右上角 7px `AccentBrush` 未读点（`IsHitTestVisible=False`，
  `ToolTip` 区分“公告 / 公告（有未读）”），相邻的“关于”`IconButton` 以本地绘制的圆圈 + 竖笔图标打开设置 · 关于；`AnnouncementWindow` 顶部标题 + `AccentSoftBrush` 未读徽标 + 更新时间 + 刷新按钮，正文是单张
  `CardStyle` 内的 `MarkdownView`，底部 `DangerBrush` 错误条 + 重试按钮；获取中 / 空 / 错误三态用居中状态块。`MarkdownView` 把 HTML 当纯文本、
  图片只显示为链接、不下载远程资源，外部链接交给系统浏览器；正文只在内存，仅持久化已读哈希。设置 · 关于同样内嵌 `MarkdownView`，只保留版本号、动态信息标题与更新时间，不再常驻来源或隐私说明（隐私说明集中在数据与隐私分类与来源页）。

## 6. 数据可视化

- 图表必须包含指标名称、单位与时间范围：趋势卡片标题给出指标，单位由指标切换（Token / 请求数）、指标标签与浮框文案表达，不再单列“单位：…”文字；选中区间由工具栏预设 / 自定义按钮表达，标题右侧 `RangeDaysText` 显示“共 N 天”，精确区间与时区由标题与 `RangeDaysText` 共享的 `ToolTip` 给出；无数据时显示“暂无数据”，不画空坐标轴。
- 趋势线用 `AccentBrush`，网格与轴用 `ChartGridBrush`，刻度文字用 `MutedBrush`（由窗口把画刷作为 `Accent` / `GridBrush` / `MutedBrush`
  依赖属性传入 `RequestLineChart`，控件内部不得自带主题色）。纵轴固定 4 档、取“可读”上界（`NiceMax`），横轴刻度最多 12 个；客户端 / 模型分项沿用第 2 节的稳定语义色。
- **未知不伪造**：值为 `null` 的点断开折线，绝不补 0 或插值；真实 0 必须画成 0 并可辨识。单日只有一个点时画点而非线段；今天预设按小时分桶，浮框总量标注“所选时段 Token 用量”并说明小时未结束，
  未来小时不绘制零。
- **估算 / 换算 / 未知分级可见**（与 `data-metrics.md` 的 `measured` / `derived` / `estimated` 一致）：明细表“来源”列显示“实测 / 换算 / 估算”，
  估算记录不得与实测混入同一个数值，需要合并展示时同时给出拆分；无法确认的总量显示 `—`，不显示 0；混合数据与明细不完整（`partial`）的计数收在“请求用量”标题 `ToolTip` 中标注“已知子集 · N 条请求用量未知 / N 个会话明细不完整”，不占用卡片正文，
  来源关系未知标注“未知关系：…”。
- tooltip 必须给出该数值的口径与单位；趋势浮框（`CardBrush` 底 + `BorderBrush` 描边，`MinWidth 210 / MaxWidth 320`）按日期、当日已知 Token、请求数、
  会话数分行，异常提示用 `MutedBrush` 单独一行，并可在“客户端 / 模型”两个维度间切换；分项按值降序，未知排最后，同值保持稳定顺序，数值直接用后端总量。
  浮框标题只给日期（小时按 `yyyy-MM-dd HH:00`），不追加“· 本地日期”；总量行的口径与单位由“（所选时段）Token 用量”标签表达，数值后不重复单位，客户端 / 模型分项数值仍带 `Token` 后缀。浮框定位在图表覆盖层内完成，靠右 /
  靠下边缘时自动翻转到另一侧并夹取到 0，不得被卡片、滚动容器或相邻卡片遮挡；鼠标移入浮框可停留（160ms 延迟收起），移出即收起。
- 每日条带（`DailyStripPanel`）等距铺满视口宽度，列数过多时按最小间距 84px 展开并允许横向滚动，单日数值居中显示，标签用 `MutedText`。
  条带内的滚轮一律滚动页面（水平滚动条会吞掉滚轮，窗口用 `PreviewMouseWheel` 转交），横向移动靠条带自身的水平滚动条。占比条（`ShareBar`）高 6px、轨道
  `TrackBrush`、填充用客户端语义色或 `AccentBrush`；条形只表示相对榜首 / 后端 `Share` 的大小，不宣称全局占比，未知不画零长条。
- 不使用 3D、装饰性动画或第二套图表库；不引入渐变面积填充。图表含义必须同时有文字，不单靠颜色传达。

## 7. 文案与数字格式

- 界面文案使用中文；术语与 `data-metrics.md` 保持一致（会话、请求、实测、换算、估算、缓存 token 等）。
- Token 展示统一走 `Data/TokenFormatting.cs`（受设置“Token 显示”控制：自动 k/M/B、中文万/亿、固定 K/M/B、精确值）；指标、坐标轴、浮框与列表遵循同一设置，
  浮框保留精确总量。固定单位下非零小值显示 `<0.01`，不舍入成实测零；`N0` 千分位用于请求数与文件数。
- 时间范围必须显式标注并按本地时区聚合：工具栏预设 / 自定义按钮表达当前选择，标题右侧固定显示“共 N 天”，精确区间与时区由“请求用量”标题 `ToolTip` 给出；日期时间显示 `yyyy-MM-dd HH:mm(:ss)`，跨时区场景注明时区，自定义区间标注“本地时区 · 包含结束分钟”。空状态与错误文案说明“发生了什么 + 下一步怎么做”。
- **不展示费用金额**：没有可靠成本来源前，界面不出现任何金额或“预计花费”。
- 已移除的历史视图不再提供：活动日历 / 热力图、统计报告导出、用量分析页与左侧导航（2026-10-16 单页收束）；相关口径仍留在 `data-metrics.md`。

## 8. 无障碍

- 实测对比度（WCAG 相对亮度）：浅色正文 `Text` / `Card` 14.7:1、`Text` / `Window` 13.8:1；辅助文字 `Muted` / `Card` 4.7:1（`Muted` /
  `Window` 4.4:1）。深色正文 14.5:1、辅助文字 6.5:1。新增文字不得低于正文 4.5:1、辅助 3:1；`Accent` 作文字色时改用 `AccentDark`（浅 4.8:1 / 深
  8.2:1）。
- 客户端语义色用于图标与条形，同旁必须有文字标签；`command_code`、`cline`、`codearts` 等深色值在深色卡片上对比不足，只用于浅色底或加底块（`ChipBrush` / 12% 透明色块），
  不单独承担状态含义。
- 表格用 `DataGrid` 列头表达列含义，列头样式 `GridHeader`（`MutedBrush` 11px）；排序 / 筛选状态要能被读屏识别，自绘控件补充
  `AutomationProperties.Name`。不使用“仅颜色”传达状态：错误 / 估算 / 不完整 / 未读同时给文字或图标（未读点旁 `ToolTip` 说明“有未读”）。
- 键盘路径必须覆盖：范围预设与自定义浮层、刷新 / 公告 / 关于 / 更新 / 设置按钮、最近会话表（方向键 + `Enter`）、会话详情翻页、设置分类侧栏与来源列表。

## 9. 反模式（禁止）

- 强烈玻璃拟态、霓虹渐变、大面积彩色背景、随机阴影、装饰性发光。
- 卡片套卡片、无关联指标过度拆分、卡片内表格再套边框。
- 硬编码颜色 / 间距魔数、复制粘贴的“近似样式”、只改浅色不改深色的画刷。
- 引入第三方 UI / 图标 / 图表库，或第二套图表实现。
- 用动画掩盖加载延迟，或用占位内容造成布局跳动；同步期间改变工具栏高度或推动日期范围。
- 把未知或估算画成实测零、伪造坐标轴、伪造同比涨跌、用分项之和补造未知总量。
- 加载远程图片 / 字体 / HTML，执行服务端下发的脚本，或把会话正文、项目路径、会话标识显示到界面与日志里。

## 10. 提交前检查

- [ ] `dotnet build wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release` 零错误；改动涉及服务层 / 公告时
      `dotnet test wpf/TokenView.Tests/TokenView.Tests.csproj -c Release` 通过
- [ ] 颜色全部来自 `Theme.xaml` 语义画刷，新增画刷已在浅 / 深两套调色板成对登记
- [ ] 间距 / 圆角 / 字号取自现有尺度，未出现散值
- [ ] 加载、空、错误三态齐备；同步进行中不阻塞已有数据浏览
- [ ] 图表有指标名、单位与时间范围；估算 / 换算 / 未知 / 不完整已标注，未知显示 `—`
- [ ] 图标按钮与自绘控件有 `ToolTip` / `AutomationProperties.Name`，键盘可达
- [ ] 实际启动 `dotnet run --project wpf/TokenView.Wpf`：浅色 / 深色均检查，800×600 窄窗口无溢出
- [ ] 未引入未登记的控件库、图标库或远程资源；未把正文 / 路径 / 会话标识写入界面、日志或仓库
- [ ] 新增可视组件或布局约定已回写本文件，口径变更同步 `docs/data-metrics.md`

## 11. 统计界面约定

- **打开即看数据**：不做欢迎页、营销文案或账户中心；首屏直接是总览统计。
- **区分真实 / 未知 / 不完整**：缺失值 `—` 且不参与合计；`partial` 与口径未知项（如缓存写入与输入的关系）必须标注；未接入的客户端在设置 · 采集置灰显示“尚未检查 / 未发现数据”，不显示 0
  或模拟用量。
- **口径可见**：每个卡片或图表旁有口径说明（脚注或浮框），措辞与 `docs/data-model.md` 一致；脚注只说明当前区块，不把全历史覆盖计数描述成当前范围完整率。
- **请求时间为主视图**（口径 2.3，2026-10-11）：总览按请求发生日期展示客户端比较与趋势；会话累计放在“会话视角”次级区块与“查看会话总账”窗口（`SessionLedgerWindow`），
  两处都注明按会话开始日筛选、展示整段累计、与请求时间总量不可直接对账。今天预设的趋势按本地小时展示；明确统计截止分钟与未结束小时，未来小时不绘制零，重复小时的浮框包含时区偏移，不把缺少记录画成实际下降；
  无法确认昨日同期覆盖时不展示同比。每日会话数是当日有请求的去重会话数，浮框说明跨日会话可重复，不能相加得到区间去重会话数。统计分布、趋势图例与每日条带只展示当前范围内有记录的客户端，依据会话数 / 请求数判断，不用
  Token 是否大于零判断；真实零与未知用量必须保留。设置 · 采集仍列出全部客户端。
- **会话详情**（`SessionDetailWindow`）：注明不受全局日期范围影响，显示整段会话摘要、来源口径与全会话请求覆盖（已记录时间 / 缺失时间 / 有用量 / 无用量 / 估算），请求表按 100
  条分页并显示“全会话 N 条”。列含序号、起止时间、模型、代理、输入 / 输出 / 推理 / 缓存读取 / 缓存写入 / 总量、来源等级、来源引用与状态；缺失时间排在最后并显示 `—`，NULL Token 与真实 0
  区分；无原生序号的日志显示 `—`，不把文件行号当客户端序号。详情不展示 prompt、回答与工具正文，来源引用只作元数据溯源。
- **日期时间筛选**（2026-10-11）：预设含本周（周一起始）与本月；自定义浮层为双月日历 + 时分下拉，显示起始 / 结束并明确“本地时区 · 包含结束分钟”，确认后才应用，关闭或 `Escape` 丢弃草稿；
  非法、缺失或倒序输入用 `DangerBrush` 给可读校验。所有日期标签按本地时区呈现。超长区间的图表截断（超过 400 天只画最近 400 天并标明截断）是 WebView 版遗留约定，**WPF 版尚未实现**：
  当前趋势按完整区间逐日绘制，实现前不得在文档或界面宣称已截断。
- **存储位置**（2026-10-13）：数据与隐私页显示只读 `ChipBrush` 路径框（可复制）、打开数据目录、迁移存储目录、恢复默认设置与清除全部统计数据；迁移与清除必须确认，失败保留可读错误，
  清除不得影响客户端原始数据。**刷新闪烁**（2026-10-16 / 2026-10-17 教训）：状态文案与状态点必须占用固定宽度，同步前后工具栏、日期范围与首个卡片的元素位置位移为 0；骨架呼吸只在超过
  140ms 的加载后出现，系统关闭动画时不得变成高频循环。

## 12. 品牌标识（2026-10-20）

应用标识由「深翡翠渐变圆角方底 + 薄荷渐变 T 字标」组成：T 对应 TokenView 首字母，由一条圆角横杠与一条圆角竖笔拼成单一剪影；深色底在浅色与深色任务栏上都立得住，薄荷字标保证 16px
仍清楚。不叠加文字、投影、描边装饰或第二层图形，也不再提供 `currentColor` 单色变体。

- **唯一源文件**：`wpf/TokenView.Wpf/Assets/app-icon.svg`（1024×1024，`rx=232` 圆角方底）。改标识只改这一份，再重新导出 ICO，不单独改某一处位图。
- **调色**：底 `#123B33 → #07130F`（左上到右下线性渐变），字标 `#EAFFF9 → #8BEBD2`，白色 12% 内描边（`rx=226`，10px），`#45c7a4` 16% 中心光晕，
  顶部 12% 白色高光渐隐。
- **界面内标记**：2026-10-20 起主窗口工具栏不再绘制字标（品牌块与 `TokenView` 文字已移除，日期范围预设顶到左侧），应用身份只由窗口标题栏文字、
  exe 图标与托盘图标承担；界面内需要品牌标记时统一引用 `tokenview.ico`，不在 XAML 里另画一份几何。
- **Windows 图标**：`wpf/TokenView.Wpf/Assets/tokenview.ico` 保留 16 / 20 / 24 / 32 / 40 / 48 / 64 / 128 / 256 九档（全部
  PNG 编码），由 `<ApplicationIcon>` 用于 exe，托盘 `Icon.ExtractAssociatedIcon` 共用同一份。
- **再生成配方**（本机 ImageMagick + Pillow，已验证）：

  ```bash
  # 1) SVG 光栅化为 1024 方形 PNG（透明底）
  magick -background none wpf/TokenView.Wpf/Assets/app-icon.svg -resize 1024x1024 tv.png
  # 2) 由该 PNG 生成九档 ICO（Pillow 逐档写入 PNG 编码条目）
  python -c "from PIL import Image; Image.open('tv.png').convert('RGBA').save(
      'wpf/TokenView.Wpf/Assets/tokenview.ico', format='ICO',
      sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])"
  ```

  不要直接用 `magick ... tokenview.ico`：它只把 256 档写成 PNG，其余档写成 BMP 条目，与现有文件结构不一致。生成后核对条目数与尺寸，再提交；`bin/`、`obj/`
  下的产物不入库。
- **禁止**：改变字标形状或单独换色、在标识里加文字、用客户端语义色当品牌色；最小展示尺寸 16px。
