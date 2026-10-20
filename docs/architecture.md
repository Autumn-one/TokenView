# 架构规范

本文约束 TokenView（对外显示名 **TokenView**）的模块划分、依赖方向与数据流。目标是让
"从哪个客户端的哪个文件读出什么数据、在哪一层变成指标、在哪个窗口显示"始终可定位，
避免统计口径散落到界面代码里。

## 1. 概述与三条底线

唯一实现是 C# / .NET 10 / WPF 桌面应用 `wpf/TokenView.Wpf`（`net10.0-windows`），
外加无界面验证工具 `wpf/TokenView.Probe` 与 xUnit 测试项目 `wpf/TokenView.Tests`。应用读取本机
AI 编程客户端的会话元数据，写入本地 SQLite 用量库，再聚合为界面数据；不上传本地数据、
不修改客户端原始数据，网络只用于第 1 条底线里登记的入口。

三条底线约束所有架构决策：

1. **本地优先**：默认零网络请求。已登记的联网入口只有两个：固定 GitHub Releases `latest.json`
   的更新检查，以及公告/关于页从固定公开镜像列表拉取 Markdown（启动一次 + 每 20 分钟，
   不加载远程图片/HTML、不上传本地数据，正文只存内存）。
2. **真实与估算分离**：每条记录携带 `source_kind`（`measured` / `derived` / `estimated`），
   会话总量另带 `total_basis`，明细完整度另带 `message_coverage`；换算关系不明的字段进入
   `unknown_relations`，不静默推断。
3. **隐私默认保护**：采集器只读统计必需的元数据字段，正文不进入候选、不落盘、不打印、不外传。

依赖方向与目录职责必须与 `AGENTS.md` 第 3 节一致；统计口径的唯一事实来源是
`docs/data-metrics.md`，数据模型与表结构是 `docs/data-model.md`，视觉规范是 `docs/ui-guidelines.md`。

## 2. 分层与依赖方向

箭头表示调用 / 引用方向（按代码实际 `using` 核对）：

```text
MainWindow / SettingsWindow / SessionLedgerWindow / SessionDetailWindow / CloseWindowDialog / AnnouncementWindow
        │（视图状态、格式化、事件转发）
        ├──→ Services：SyncService、SyncSchedule、TrayService、ThemeManager、WindowTheme、
        │              AutostartService、WindowStateStore、StorageRelocationService、UpdateService、UpdateInstaller、
        │              AnnouncementService、AboutInfoService、MirrorContentService
        └──→ Data：TokenViewDatabase（只读查询）、TokenFormatting
                    │
Controls：RequestLineChart ──→ Data（ChartPoint / ChartMetric / TokenFormatting）
          DailyStripPanel、DateTimeRangePicker ──→ 仅 WPF 基础类型，不引用 Data / Services
          MarkdownView ──→ Markdig（AST 渲染为 FlowDocument），不引用 Data / Services

Services：SyncService ──→ Data.Collectors.SyncEngine ──→ 各 *Collector ──→ Data.Usage.UsageStore
          StorageRelocationService ──→ Data.Collectors（来源路径校验）＋ Settings
          ThemeManager / AutostartService ──→ Settings；WindowStateStore / UpdateService / UpdateInstaller ──→ 无领域依赖
          AnnouncementService / AboutInfoService ──→ IContentFetcher ←── MirrorContentService
          MirrorContentService ──→ MirrorsConfig / MirrorUrl；两者只暴露内存状态与 Changed 事件

Data：UsageStore ──→ Usage（UsageModels / NormalizedUsage / TokenSemantics）
      *Collector ──→ RequestBuilder / CollectorSupport / SqliteSnapshot ──→ Usage
      TokenViewDatabase ──→ Usage（间接）＋ 根命名空间的展示记录（Models.cs）
```

- 公告与关于的网络访问只集中在 `MirrorContentService`（`IContentFetcher` 的唯一实现），
  更新检查、下载与校验集中在 `UpdateService`，文件替换由 `UpdateInstaller` 生成本机 PowerShell
  助手执行；两个内容状态服务只缓存内存结果并触发事件，窗口不直接调用 `HttpClient`，
  也不自行解析 Markdown 之外的远程内容。

硬性约束：

- `Data/` 不得引用 `System.Windows`、WPF 控件或窗口类型。已核对：`Data/**` 内没有任何
  `System.Windows` 依赖，唯一的外部引用是 `Data/TokenFormatting.cs` 使用 `Settings.TokenDisplay`
  （设置模型不是 UI，允许）。
- 采集器只读客户端文件、只写 TokenView 自有库；不执行界面逻辑、不发网络请求。
- `UsageStore` 只接受归一化后的记录，不解析任何客户端原始格式；`TokenViewDatabase` 只做只读查询。
- `Services` 是桌面生命周期与调度层，可以被窗口和 `StorageRelocationService` 使用，
  但 `Data` 不反向引用 `Services`。
- 当前**没有独立的 ViewModel 层**：窗口后台代码承担视图状态（选中的日期范围、图表指标、
  悬浮提示、骨架动画）与格式化，聚合与口径全部在 `Data`。新增统计规则必须落在 `Data`，
  不得写进窗口。

## 3. 数据流

```text
客户端文件 / 客户端 SQLite
   → 采集器发现候选（CollectorSupport.DiscoverFiles / DefaultRoot / DefaultDatabase）
   → 白名单解析（JSONL 流式按行 / 只读 SQL 投影）
   → 归一化（RawTokenCounts → UsageNormalizer.Normalize → NormalizedUsage）
   → UsageStore 事务写入 sessions / messages / source_files
   → request_hourly 预聚合（脏标记 + 批次结束后整体重建）
   → TokenViewDatabase 只读查询（OverviewSnapshot / LedgerSnapshot / SessionDetailSnapshot）
   → 窗口与控件渲染
```

关键机制：

- **只读源**：客户端文件只以 `FileShare.ReadWrite`（复制 SQLite 副本时另允许 `Delete`）打开读取，
  绝不写入、移动或删除。SQLite 源经 `SqliteSnapshot.Open` 把主库与 `-wal`（及 `-journal`）流式复制到
  私有临时文件后以 `PRAGMA query_only` 打开，从不直接打开客户端原库，也不生成客户端 `-shm`。
- **增量水位**：`source_files` 按 `(client, path)` 唯一，记录 `size_bytes` / `mtime_ms` /
  `content_offset` / `parser_version` / `last_scan_ms` / `unparsed_records`。
  `CollectorSupport.IsUnchanged` 判定"大小 + mtime + 解析版本"三者未变即跳过；`SyncOptions.ForceRescan`
  绕过该判定。
- **扫描期变化**：读后与提交前复核指纹（或 `SqliteSignature`），变化即抛
  `SyncException.ChangedDuringScan()`（唯一可重试错误），保留历史与水位，等待下次同步。
- **原子快照替换**：完整扫描成功后调用 `UsageStore.ReplaceClientSnapshot(client, files, sessions)`，
  在单事务内删除该客户端全部 `sessions` / `messages` / `source_files` 再整体写入，使源数据的
  修正、截断与删除能反映到统计中。部分文件失败时用 `MergeClientSnapshot`（仅 Copilot / DSH）
  只替换成功会话、保留失败文件水位。Codex 走逐文件增量：`ReplaceFileImport` 按来源文件覆盖重写。
- **幂等**：`sessions` 以 `(client, client_session_id)` 去重，`messages` 以
  `(client, session_id, dedup_key)` 去重；重复扫描只更新不翻倍。陈旧保护在
  `UsageStore.ShouldReplaceSession` / `UpsertMessage`：`client_cumulative` 总量不回退，
  新总量缺失不覆盖已有值，明细按原生时间取新。
- **预聚合**：任何写入事务把 `usage_rollup_state.request_dirty` 置 1；`SyncEngine.Sync` 在整批
  结束后按需 `RebuildRequestRollup()` 全量重建 `request_hourly` 与 `request_hourly_sessions`
  （只纳入 `measured` / `derived` 且时间已知的请求）。界面查询只读预聚合表与 `sessions`。

## 4. 模块清单

| 路径                                       | 内容与实际成员                                                                                                                                                                                                                                            | 职责边界                                                                                     |
| ------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| `wpf/TokenView.Wpf/`（根命名空间）           | `App`（单实例、主题、自启、窗口创建、公告调度、退出等待）、`MainWindow`、`SettingsWindow`、`SessionLedgerWindow`、`SessionDetailWindow`、`CloseWindowDialog`、`AnnouncementWindow`、`Models.cs`（展示记录）、`ClientIcon` / `ClientIconData`、`ModelIcon` / `ModelIconData`、`ShareBar`、`HexToBrushConverter`、`StoragePaths`、`Theme.xaml` | 视图与视图状态；不实现统计口径                                                               |
| `wpf/TokenView.Wpf/Data/`                    | `TokenViewDatabase`（区间构造、总览 / 总账 / 会话详情 / 客户端统计查询、今日托盘摘要）、`TokenFormatting`                                                                                                                                                    | 只读查询与展示格式化；不写库、不读客户端文件                                                 |
| `wpf/TokenView.Wpf/Data/Collectors/`         | `SyncEngine`、`CollectorContracts`（`IClientCollector` / `SyncReport` / `SyncException` / `SyncBatch` / `SyncProgress`）、`CollectorSupport`、`SqliteSnapshot`、`RequestBuilder`、18 个 `*Collector`                                                        | 发现、解析、归一化入口、同步协调；只写 `UsageStore` API                                      |
| `wpf/TokenView.Wpf/Data/Usage/`              | `UsageModels`（`SessionRecord` / `MessageRecord` / `SourceFile` / `UsageIds` / `UsageTime`）、`NormalizedUsage`（`RawTokenCounts` / `TokenBuckets` / `UsageNormalizer`）、`TokenSemantics`（`TokenSemanticsCatalog`）、`UsageStore`（迁移、写入、快照、预聚合） | 统一数据模型、Token 语义、SQLite 存储；纯领域逻辑，可用合成数据验证                          |
| `wpf/TokenView.Wpf/Services/`                | `SyncService`、`SyncSchedule`、`TrayService`、`ThemeManager`、`WindowTheme`、`AutostartService`、`WindowStateStore`、`LegacyStorageMigration`、`StorageRelocationService`、`UpdateService`、`UpdateInstaller`、`MirrorContentService`（唯一 HTTP 层）、`AnnouncementService`、`AboutInfoService`、`MirrorsConfig` / `MirrorUrl` / `SourceConfig` / `RepoConfig` | 桌面生命周期、调度、存储迁移、更新下载/替换与公开 Markdown 拉取                                  |
| `wpf/TokenView.Wpf/Controls/`                | `RequestLineChart`（自绘折线）、`DailyStripPanel`（等宽逐日条布局）、`DateTimeRangePicker`（锚定浮层 + 自绘双月日历）、`MarkdownView`（Markdig AST → FlowDocument）                                                                                          | 可视组件；图表只绘制传入的数据点，不自行聚合；Markdown 不执行 HTML/脚本                       |
| `wpf/TokenView.Wpf/Settings/`                | `AppSettings`（含 `AllClients`、`CloseBehavior`、`AppTheme`、`TokenDisplay`）、`SettingsStore`（`schemaVersion` 信封、原子保存、值归一化、顶层 `lastReadAnnouncementHash`）                                                                                    | `settings.json` 读写；与统计库分离                                                           |
| `wpf/TokenView.Wpf/Assets/`                  | `app-icon.svg`（图标源）、`tokenview.ico`（`ApplicationIcon`，随构建输出）                                                                                                                                                                                  | 仅图标资源                                                                                   |
| `wpf/TokenView.Probe/`                       | `Program.cs`：`copy` / `sync` / `totals` / `ledger` / `model` / `update` / `relocate` / `autostart` / `announcement` / `about-info`，引用 `TokenView.Wpf` 工程                                                                                                   | 无界面验证工具；口径回归与真实端点验证入口                                                   |
| `wpf/TokenView.Tests/`                       | xUnit 测试：镜像顺序/降级、HTML 错误页、禁用源、时间戳、原始字节哈希、配置回落/合并、未读持久化、关于页一次性加载、`MarkdownView` 渲染（表格 / 链接 / 裸 HTML 当文本）、旧存储迁移 | 只用假 HTTP handler / `TimeProvider` 与临时目录，不访问网络、不依赖当前时间、不触碰真实 AppData   |
| `scripts/reconcile_overview.py`            | 以只读 URI 打开生产库，按会话开始日聚合总量并与界面口径比对                                                                                                                                                                                               | 只读对账辅助，不参与构建                                                                     |

## 5. 采集器规范

每个客户端一个 `sealed class <Name>Collector : IClientCollector`，位于 `Data/Collectors/`，
成员为 `ClientKey`（与库内 `client` 字符串逐字一致）、`SourcePaths`（已配置位置，含不存在的，
供设置页诊断）、`Sync(UsageStore, SyncOptions) → SyncReport`。`SyncEngine` 通过反射发现全部
无参构造的实现并按 `ClientKey` 注册，`AllClients` 固定 18 个来源：
`codex`、`claude_code`、`opencode`、`dsh`、`kimi_code`、`mimocode`、`pi_desktop`、`pi_agent`、
`workbuddy`、`copilot`、`gemini_cli`、`grok`、`mcode`、`devin`、`command_code`、`cline`、
`qoder`、`codearts`。

规则（与 `wpf/COLLECTOR_PORTING.md` 的移植契约一致）：

1. 发现与解析分离；解析接收路径或内容并输出归一化记录，便于用合成数据验证。
2. 只读；根目录不存在是 `SyncReport.RootMissing` 正常报告，不是异常。
3. 前向兼容：未知字段忽略，未知记录类型按原实现计入 `IgnoredLines`；已知字段缺失保持 `null`，
   不用 0 代替（`RawTokenCounts` 全为 `long?`）。
4. 可降级：无法解析的完整行计入 `UnparsedLines` 并在设置 · 采集可见；无换行结尾的尾行视为
   源未写完，整文件放弃提交并保留历史。
5. 不落正文；`source_ref` 只保留定位所需的引用。
6. 大文件流式处理：JSONL 按 64 KiB 缓冲逐行读取；SQLite 源走临时副本，支持 >2 GiB 的库。
7. 字符串比较一律 `StringComparer.Ordinal`；采集器内部不触碰界面线程。

新增客户端检查清单：新增 `<Name>Collector.cs` → 在 `SyncEngine.AllClients`、
`AppSettings.AllClients`、`TokenViewDatabase.Clients`（标识、显示名、语义色）、
`ClientIconData`、`TokenSemanticsCatalog` 登记 → 用 Probe 在数据库副本上对账 →
更新 `docs/data-metrics.md` 与 `docs/data-model.md` → 确认未新增依赖、权限或联网能力。

## 6. 持久化与存储

- 依赖（`TokenView.Wpf.csproj`）：`Microsoft.Data.Sqlite` 10.0.13（本地用量库）与 `Markdig` 0.40.0
  （公告 / 关于页 Markdown 解析，纯托管、无遥测）。测试项目另引 `xunit` 2.9.3 与
  `Microsoft.NET.Test.Sdk` 18.0.0，只用于 `wpf/TokenView.Tests`。
- 连接配置：WAL、`synchronous=NORMAL`、`busy_timeout=5000`、`foreign_keys=ON`，
  写入连接另设 `cache_size=-8192` 与 `mmap_size=256 MiB`；查询连接 `ReadOnly` + `query_only`。
- 库与文件位置（`StoragePaths`）：定位文件 `%APPDATA%\com.tokenview.app\storage-location.json`
  （`schemaVersion` 1，唯一保留在系统目录的启动定位文件）；数据库
  `<当前存储目录>\tokenview-usage.sqlite3`；设置 `<当前存储目录>\settings.json`；
  窗口状态 `%APPDATA%\com.tokenview.app\window-state.json`（独立于设置，恢复默认偏好不改它）。
  已配置位置不可用或定位文件损坏时启动直接报错，不静默在系统盘新建空库。
- 旧版迁移（`LegacyStorageMigration`）：启动读取设置前执行一次；新库已存在则跳过。默认布局把设置、
  窗口状态、旧库及 `-wal/-shm/-journal` 复制到新目录并改名为 `tokenview-usage.sqlite3`；旧定位文件指向
  自定义目录时只把库文件原位改名并复制定位文件。只复制不删除旧文件，失败时保留旧布局并在下次启动重试。
- 表与迁移：`schema_migrations` 版本化，只追加不改已发布 SQL；当前 v1
  `initial_usage_schema`（`source_files` / `sessions` / `messages` 与索引）、v2 `request_rollup`
  （`request_hourly` / `request_hourly_sessions` / `usage_rollup_state`）、v3
  `refresh_request_rollup_once`（强制一次重建）。迁移在 `UsageStore.Open` 内逐版本单事务执行，
  并回写 `PRAGMA user_version`。表结构字段含义见 `docs/data-model.md`。
- 存储迁移（`StorageRelocationService.Relocate`）：只接受不含 `..` 的绝对目录；拒绝与任一采集器
  `SourcePaths` 重叠的目标；拒绝目标目录已存在 TokenView 自有文件；用 `VACUUM INTO` 生成含已提交
  WAL 的一致快照并对副本跑 `PRAGMA integrity_check`；成功后复制 `settings.json`、原子写定位文件，
  再清理旧目录中 TokenView 自有文件（清理失败只回警告）。任一步失败回滚目标文件并保留原位置与原数据。
- 清除统计：在单个 `BEGIN IMMEDIATE` 事务内删除 `messages` / `sessions` / `source_files` /
  `request_hourly` / `request_hourly_sessions` 并复位脏标记（领域侧对应 `UsageStore.ClearStatistics()`），
  完成后自动暂停采集；不影响客户端原始数据。
- 费用：仅保留客户端自报值（`cost_source` / `client_cost_usd`），不做价格换算，界面不展示金额。

## 7. 桌面生命周期

- **单实例**：命名互斥体 `Local\TokenView.Wpf.SingleInstance`；重复启动通过
  `Local\TokenView.Wpf.Activate` 唤醒已有实例（后台监听线程 → `MainWindow.RestoreFromTray`）后退出，
  避免两个进程同时写库。
- **托盘**：`TrayService`（WinForms `NotifyIcon`，故工程开启 `UseWindowsForms`）提供
  打开 TokenView / 立即同步 / 重启 TokenView / 退出，左键单击或双击托盘图标打开主窗口；采集中禁用"立即同步"并显示进行中；
  重启先拉起带 `--restart-wait <pid>` 的新实例，等待旧进程退出后接管，旧实例按正常退出流程等待同步完成；
  tooltip 为今日 Token 与请求数摘要（截断到 63 字符），只含聚合值。
- **关闭行为**：`CloseBehavior` = 询问 / 托盘 / 退出，默认询问；`CloseWindowDialog` 可记住选择。
  退出前等待进行中的采集任务（`App.ExitApplication` 最多 30 秒），关闭窗口时若仍在采集则标记
  待退出、同步完成后再关；`ShutdownMode` 为 `OnExplicitShutdown`，隐藏窗口不影响采集。
- **开机自启**：`AutostartService` 写 `HKCU\...\Run` 的 `TokenView` 值，指向当前可执行文件，
  `AutostartHidden` 时追加 `--hidden`（配合设置项隐藏启动到托盘）；写注册表失败不阻断启动。
- **主题**：`Theme.xaml` 定义语义画刷（`WindowBrush` / `CardBrush` / `BorderBrush` / `TextBrush` /
  `MutedBrush` / `AccentBrush` / … / `ChartGridBrush`）与共用样式；颜色一律经 `DynamicResource`
  引用（`StaticResource` 只用于与主题无关的排版样式），`ThemeManager` 持有浅色 / 深色两套调色板并在
  运行时替换画刷，`AppTheme.System` 读注册表 `AppsUseLightTheme` 并订阅
  `SystemEvents.UserPreferenceChanged`；`WindowTheme` 用 DWM 属性让原生标题栏跟随深浅色。
  界面不得写死颜色，客户端与模型品牌色是数据配色、不属于主题画刷。
- **窗口状态**：`WindowStateStore` 保存尺寸 / 位置 / 最大化，恢复时校验最小 800×600 与虚拟屏幕
  可见范围，损坏文件按默认窗口打开；`RememberWindow` 可关闭该行为。
  `AppSettings.SidebarCollapsed` 只为兼容已有 `settings.json` 而保留，布局不再读取或写入。
- **调度**：`MainWindow` 每 15 秒轮询一次，`SyncSchedule.Due` 按 `SyncIntervalMinutes`
  （仅 0 / 1 / 5 / 15 / 30，非法值归一化为 5）判定到期；`CollectionPaused` 时不调度、
  不手动触发；`SyncOnStart` 控制启动即采集。`SyncService` 用信号量保证同一时刻只有一个批次，
  在后台线程独立开库连接串行执行各采集器，进度与完成通过事件回到 UI 线程。
- **更新检查与自动更新**：`UpdateService` 只访问固定端点
  `https://github.com/Autumn-one/TokenView/releases/latest/download/latest.json`，15 秒超时，
  比较 `version` 与程序集版本。发现新版后，`UpdateInstaller` 只从同一仓库的
  `releases/download/` HTTPS 地址下载，按 `latest.json` 的 `sha256` 校验后解压到临时目录；
  用户点击“重启并更新”后，临时 PowerShell 助手等待进程退出、备份当前文件、覆盖并重启，
  失败时自动恢复旧文件。不静默覆盖运行中的程序，也不接受第三方下载地址。唯一入口是设置 · 更新
  （手动按钮每次都反馈结果）；默认关闭自动更新，开启后按 `settings.json` 的 `lastUpdateCheckMs`
  限制每天最多一次，命中时自动下载并用托盘气泡提示重启，检查或下载失败静默并在下个周期重试。
  发布流程见 `docs/releases.md`。
- **公告与关于信息**：`App` 启动时拉取一次公告并按 20 分钟 `DispatcherTimer` 刷新，退出时取消在途请求；
  `AnnouncementService` 与 `AboutInfoService` 只持有内存结果与 `Changed` 事件，实际请求集中在
  `MirrorContentService`（`IContentFetcher` 的唯一实现）。`MirrorsConfig` 内置固定的配置源与内容源
  优先级列表（Gitee、若干 GitHub 代理、GitHub raw / Release），远程 `mirrors.json` 只按字段合并、
  解析失败回落内置配置；Markdown 请求带 `Cache-Control: no-cache` 与时间戳查询参数（配置请求不追加），
  单次 12 秒超时、
  正文上限 512 KiB，返回 HTML 视为无效源继续降级。内容哈希取原始字节 SHA-256，
  `settings.json` 只持久化 `lastReadAnnouncementHash`（`AnnouncementWindow` 可见且停留 2 秒后写入），
  Markdown 正文不进磁盘、不进日志；`MarkdownView` 把 Markdig AST 渲染为 `FlowDocument`，
  HTML 只当文本、图片只渲染为链接、外部链接交给系统浏览器。

## 8. 查询与展示契约

- 查询入口集中在 `TokenViewDatabase`：`CreateRange` / `CreateCustomRange` 产出 `RangeSelection`
  （今天为本地小时桶，其余按日，日图最多 400 桶）；`Load` 产出 `OverviewSnapshot`
  （区间总量、图表点及其客户端 / 模型切片、每日条、客户端分布、模型与项目 Top 5、最近会话）；
  `LoadLedger` 产出 `LedgerSnapshot`（会话口径的总量、按会话开始日趋势、客户端分布与会话表）；
  `LoadSessionDetail` 产出 `SessionDetailSnapshot`（全会话分项、总量依据、完整度、未知关系与
  请求明细分页，默认 100 条、上限 500 条）；`LoadClientStats` 与 `LoadTodaySummary` 供
  设置 · 采集诊断与托盘摘要。
- 展示记录（`Models.cs`）是只读 `record`，携带已格式化的文本属性；Token 单位换算统一走
  `TokenFormatting`（`auto` / `chinese` / `k` / `m` / `b` / `exact`），窗口不手写单位逻辑。
- 会话累计与请求口径是两条独立查询路径：`request_hourly` 只统计有原生时间且非估算的请求，
  会话总量仍取 `sessions.total_tokens`；界面不得把两者混成同一个数字。
- 时间与单位：跨层与库内使用 Unix 毫秒（`UsageTime.NowMs` / `ParseIso8601UtcMs`），
  展示按本地时区；Token 分桶字段名表达单位，缺失显示 `—`。
- 诊断输出（状态浮层、脱敏诊断摘要）只含版本、客户端、状态、时间与计数，
  不含会话标识、项目路径或错误原文。

## 9. 错误处理与可观测性

- 采集错误集中在 `SyncException`：`Directory` / `FromSource` / `Database` / `Store` 为硬错误，
  `ChangedDuringScan` 为唯一可重试错误。`SyncBatch.HasBlockingErrors()` 决定整体是否标为"同步异常"，
  可重试错误只让对应客户端显示"源数据变化 · 待重试"，整体保持"已同步"。
- 单个采集器抛异常不中断其余客户端；逐客户端结果（`ClientSyncResult`）与计数器
  （`SyncReport` 的 `FilesSeen` / `FilesParsed` / `FilesSkippedUnchanged` / `UnparsedLines` /
  `IgnoredLines` / `FilesFailed` / `DirectoriesFailed` / `SessionsImported` / `Messages*`）保留，
  供状态浮层与 Probe 输出。
- 用户可见错误一句话说明发生了什么与可以怎么做；库缺失、目录不存在按"无数据"处理，
  用空状态呈现而非报错。当前没有日志框架，也不引入：诊断信息只经由界面与 Probe 输出，
  不得写入会话正文或完整主目录路径。
- 界面读取失败在窗口层提示，不静默保留看似最新的旧数字。

## 10. 性能约束

- 首次扫描与查询不得阻塞 UI：加载走 `Task.Run`，界面先显示骨架（140 ms 延迟出现，避免闪烁）。
- 增量优先：未变化文件按水位跳过；`DevinCollector` 另用进程内主库 + WAL 签名缓存避免每轮
  复制 2 GiB 级源库（重启失效，保证首扫必扫）。
- 聚合在 `Data` 侧完成，界面只绘制与格式化；返回给单个视图的数据量受分页与 Top N 约束。
- **采集内存**：客户端快照按惰性序列（`IEnumerable<SessionImport>`）在**单个事务**内逐会话写入，
  不要求把整个客户端历史同时留在托管堆；`source_files` 在会话流结束后按最新值回写。
  新采集器不要先构造全量 `List<SessionImport>`（跨文件合并同一会话的除外，需先按会话分组）。
- **大行**：JSONL 行缓冲必须池化（`ArrayPool`）并直接用 `ReadOnlySpan<byte>` 解码，禁止逐行
  `List<byte>` + `ToArray()`（本机实测 >85 KB 的行 2,431 条、合计 614.6 MB、最长 6.72 MB）。
- **SQLite**：`mmap_size` 不超过 32 MB（映射页计入工作集）、`journal_size_limit` 64 MB、同步结束
  执行一次 `wal_checkpoint(TRUNCATE)`；只读连接用 `Cache=Private`。
- **GC**：`runtimeconfig.template.json` 固定 `System.GC.ConserveMemory=5`、`RetainVM=false`，
  不开 Server GC；同步结束做一次 LOH 压缩。禁止用 `EmptyWorkingSet` 伪造工作集。
- 常驻内存基线（`wpf/README.md`）：首屏由 256 MB 私有 / 321 MB 工作集降到 **146–149 MB / 230–233 MB**
  （Release，采集完成后稳定 60 秒）。`TokenView.Probe sync` 的单客户端实测（`ConserveMemory` 0 → 5）：
  Codex 峰值 147 → 103 MB、私有 109 → 59 MB；DSH 峰值 340 → 179 MB、私有 153 → 62 MB；Devin 无变化
  （2.3 GiB 源库走流式副本，托管堆只有 14 MB）。同步耗时无回归。

## 11. 构建与验证

```bash
dotnet build wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release
dotnet build wpf/TokenView.Probe/TokenView.Probe.csproj -c Release
dotnet test wpf/TokenView.Tests/TokenView.Tests.csproj -c Release
dotnet run --project wpf/TokenView.Wpf
dotnet publish wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release -r win-x64 --self-contained false
```

- 工具链只有 .NET SDK 10；仓库不含 JavaScript / TypeScript 工具链、包管理清单或前端构建步骤，
  也不引入第二语言实现（见第 12 节）。
- 版本号只在 `TokenView.Wpf.csproj` 的 `Version` / `AssemblyVersion` / `FileVersion`（当前 `0.1.10`），
  应用内"关于"与更新检查读程序集版本；发布步骤见 `docs/releases.md`。
- CI（`.github/workflows/ci.yml`）在 `windows-latest` 用 `setup-dotnet` 10.0.x 构建 Wpf 与 Probe
  的 Release 并运行 `dotnet test`，只授予 `contents: read`，不读取签名、不发布制品。
- `wpf/TokenView.Tests` 覆盖公告/关于的镜像顺序与降级、HTTP 200 HTML 错误页、禁用源、时间戳、
  原始字节 SHA-256、远程配置回落/合并、未读哈希持久化与关于页一次性加载；只注入假
  `HttpMessageHandler` / `TimeProvider`，不访问网络、不依赖当前时间。
- 口径与采集改动的验证方式仍是：`Probe copy` 复制生产库 → `Probe sync <副本> [--force] [客户端...]`
  → `Probe totals <副本>` 与 `Probe ledger` 对比会话数 / 请求数 / Token 总量，必要时用
  `scripts/reconcile_overview.py` 独立复核；只使用合成数据或已脱敏副本。
- 界面改动必须实际启动应用验证：窗口可启动、关键路径可完成、空 / 加载 / 错误三态正常，
  深浅主题与 800×600 窄窗口不溢出。

## 12. 架构决策记录

只登记已决定的事项；新增架构组件（目录、持久化方案、测试框架、错误处理库、依赖、联网能力）
必须先在此登记一行，再落代码。

| 日期       | 决策                                                                                    | 状态   | 理由 / 影响                                                                                                              |
| ---------- | --------------------------------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------------------------------------ |
| 2026-10-20 | 更新改为「自动下载 + SHA-256 校验 + 用户确认重启安装」，发布走 GitHub Actions         | 已采纳 | 仅接受 `Autumn-one/TokenView` 的 HTTPS Release 资产；更新包必须带 `sha256`；独立 PowerShell 助手等待退出后备份、覆盖、重启，失败恢复旧文件；不静默覆盖运行中的程序 |
| 2026-10-20 | 产品标识、程序集、命名空间与本地存储统一为 TokenView                                      | 已采纳 | 程序集 `TokenView.Wpf`；数据目录 `com.tokenview.app`、库 `tokenview-usage.sqlite3`；首次启动迁移旧版自有文件，失败回退旧布局；GitHub 仓库已迁移到 `Autumn-one/TokenView` |
| 2026-10-20 | WPF 成为唯一实现                                                                          | 已采纳 | 常驻内存与工作集显著下降、单一技术栈；当时更新只提示并打开下载页，后续由同日自动更新决策取代，发布仅保留 Windows x64 |
| 2026-10-20 | 更新检查保持固定 GitHub `latest.json` 端点，用户触发或每日一次，不自动下载安装           | 已被取代 | 由同日「自动下载 + SHA-256 校验 + 用户确认重启安装」决策取代；固定端点、每日限频与本地优先原则保留 |
| 2026-10-20 | 公告与关于页从固定公开镜像列表拉取 Markdown，Markdig 渲染，正文只存内存                  | 已采纳 | 启动一次 + 每 20 分钟；12 秒/请求、512 KiB 上限、原始字节 SHA-256、HTML 错误页拒绝；只持久化已读哈希；新增 Markdig 0.40.0 与 xUnit 测试项目，无 schema / 统计口径变化 |
| 2026-10-20 | 采集写库改「单事务 + 惰性会话序列」，GC 固定 `ConserveMemory=5`，`mmap_size` 降到 32 MB   | 已采纳 | 峰值与常驻从「整客户端物化 + 高水位」改为流式；`Probe` 实测 Codex 峰值 147→103 MB、DSH 340→179 MB，耗时无回归；无 schema 与统计口径变化 |
| 2026-10-19 | 自定义时间范围使用锚定浮层 + 自绘双月日历（`DateTimeRangePicker`）                       | 已采纳 | 无第三方日期控件依赖；确认后区间固定，编辑草稿不随时钟刷新；无 schema 与口径变化                                          |
| 2026-10-19 | 同步状态区分可重试错误：`ChangedDuringScan` 不把整体同步标红                             | 已采纳 | 该错误由客户端并发写入引起，保留历史并等下次同步；只有硬错误设置整体同步错误                                              |
| 2026-10-19 | 请求统计使用小时预聚合 + 脏标记 + 批次结束重建                                           | 已采纳 | 避免每次全表扫描明细；迁移 v3 强制一次重建，清除与导入后复位脏标记，口径不变                                              |
| 2026-10-13 | 本地库与设置支持用户指定目录，系统应用目录只保留版本化定位文件                            | 已采纳 | 可跨磁盘迁移；`VACUUM INTO` 一致快照 + 完整性校验 + 失败回滚，来源只读与重启定位不变；位置独立于普通偏好                   |
| 2026-10-13 | 大库 SQLite 源用有界流式临时副本读取，不整库反序列化                                     | 已采纳 | 2 GiB 级源库整库载入内存会产生约两倍峰值且易失败；副本前后复核签名，成功与失败路径都清理临时文件，源库与 `-shm` 零写入      |
| 2026-10-13 | 不把客户端的"隐藏"标志当过滤或删除标记，缺乏证据时子代理归属保持 false                    | 已采纳 | 本机样本显示隐藏会话携带真实用量，过滤会丢数据；无正样本的包含关系不推断                                                  |
| 2026-10-11 | `sessions` 是会话总量唯一事实来源，`messages` 只做请求明细                                | 已采纳 | 避免累计值与明细求和重复计算；两条查询路径在界面分别标注                                                                   |
| 2026-10-11 | 迁移用 `schema_migrations` 版本化，已发布 SQL 只追加不修改                                | 已采纳 | 支持历史导入与升级可追溯；升级失败保留原库                                                                                  |
| 2026-10-11 | 本地库放在应用数据目录（`%APPDATA%\com.tokenview.app`）                                     | 已采纳 | 不写项目目录，客户端数据只读；目录与库统一 TokenView 命名，旧数据由一次性迁移承接                                            |
| 2026-10-11 | 界面不展示费用金额，只保留客户端自报值                                                     | 已采纳 | 本机没有可靠成本来源，不用 Token 反推金额                                                                                   |
| 2026-10-11 | Token 语义按客户端版本化登记，字段关系与证据随口径常量保存                                 | 已采纳 | `TokenSemanticsCatalog` 每条口径带 `basis`；`UsageNormalizer` 只做确定性去重叠换算，关系不明写入 `unknown_relations`        |
| 2026-10-11 | 客户端与模型品牌图标本地打包，按 24×24 视口自绘                                            | 已采纳 | `ClientIconData` / `ModelIconData` 内嵌路径，无 CDN、无远程图片、无遥测；未知模型回退通用图标且保留原始名称                  |
| 2026-10-11 | 每个客户端一个独立采集器模块，采集与统计与 UI 三层分离                                     | 已采纳 | 口径集中在 `Data`；采集器只做发现 / 解析 / 增量调度，界面只消费查询结果                                                      |
| 2026-10-12 | 主题与 Token 展示偏好走原生设置存储，运行时替换共享画刷                                     | 已采纳 | 无额外状态库；`ThemeManager` 改画刷即刷新，`TokenFormatting` 只改显示不改原始计数                                           |
| 2026-10-12 | 托盘、自启、单实例、窗口状态由本地服务实现，不采集正文                                     | 已采纳 | 复用系统托盘与注册表；隐藏窗口继续采集；窗口状态独立于设置文件                                                                |

**已随原实现移除的历史决策**（保留说明，不再适用）：跨端调用边界与 DTO 序列化约定、前端状态管理与
组件库选型、图表库与热力图组件、右侧抽屉式设置与单页收束、统计报告导出（已于 2026-10-16 移除）、
多平台安装包与自动化签名发布、领域层独立成包的重构。这些条目不再约束当前实现；相关口径类结论
（数据模型、Token 语义、去重键、预聚合、存储迁移、主题与更新端点）已在上表按当前代码重新登记。

待登记（引入前补一行）：错误类型库（当前为 `SyncException` 加可读消息）、
应用内更新的签名校验方案、价格表来源。
