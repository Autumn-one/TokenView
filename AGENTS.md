# AGENTS.md - TokenView 开发规范

本文件是 TokenView 的唯一入口规范，适用于本仓库内的全部代码、脚本与文档，人和 AI Agent 同样遵守。
更细的规则拆分为配套文档（索引见 `docs/README.md`），出现冲突时以本文件的约束优先级为准：

| 文档                    | 负责内容                                        |
| ----------------------- | ----------------------------------------------- |
| `docs/architecture.md`  | 分层、模块边界、依赖方向、架构决策登记          |
| `docs/ui-guidelines.md` | UI/UX 设计规范、组件与可视化规范                |
| `docs/data-metrics.md`  | 数据来源、统计口径、估算规则、隐私默认值        |
| `docs/data-model.md`    | 统一数据模型、Token 语义、去重键、SQLite 表结构 |

## 1. 项目目标与边界

TokenView 是一个**本地优先**的 Windows 桌面应用，对外显示名为 **TokenView**，用于统计本机
**Codex、Claude Code、OpenCode、DSH（deepseek-hermes）、Kimi Code、MiMoCode、Pi Desktop、Pi Agent、
WorkBuddy、GitHub Copilot、Gemini CLI、Grok CLI、MCode、Devin、Command Code、Cline、Qoder、CodeArts**
等 AI 编程客户端的使用情况（会话、请求、token、模型、时间分布等）。

必须坚持的三条底线：

1. **本地优先**：默认不联网、不上传、不做遥测。
2. **真实与估算分离**：任何指标都必须能回答"这是采集到的真实数据，还是推算出来的估算数据"（见 `docs/data-metrics.md`）。
3. **隐私默认保护**：只读取统计所需的最小元数据，默认不落盘、不打印、不外传会话正文。

当前仓库状态：**C# / .NET 10 / WPF 是唯一实现**，位于 `wpf/TokenView.Wpf`；`wpf/TokenView.Probe`
是无界面的采集/查询验证工具，`wpf/TokenView.Tests` 是 xUnit 测试项目。程序集、命名空间、数据目录
与库文件统一使用 TokenView；首次启动会把旧版自有数据迁移到新目录。原 Tauri + React + Rust 实现已从
工作区移除，完整快照保存在 `backup/webview-2026-10-19` 分支，仅用于历史对照，不再维护、不参与构建。

公告与关于页动态信息已接入：启动时及每 20 分钟按固定镜像列表拉取公开的 `mirrors.json`、`README.md`
与 `info.md`，用 Markdig 渲染为 WPF FlowDocument；正文只在内存，仅持久化 `lastReadAnnouncementHash`。

明确不做的事：云端账号体系、远程同步、修改或删除客户端的原始会话数据、绕过客户端自身配置读取数据、
采集与统计无关的正文内容。

## 2. 技术栈与工具链

| 层       | 技术                                                         |
| -------- | ------------------------------------------------------------ |
| 桌面     | C# / .NET 10 / WPF（`net10.0-windows`）                      |
| 数据     | SQLite（`Microsoft.Data.Sqlite`）                             |
| 目标平台 | Windows 10 / 11 x64                                           |
| 工具链   | .NET SDK 10；Windows 上使用 MSVC 运行时                       |

- 仓库不再使用 Node / pnpm / Rust / Tauri；不要引入 `package.json`、`Cargo.toml` 或前端构建链。
- 依赖版本以 `wpf/TokenView.Wpf/TokenView.Wpf.csproj` 为准；新增 NuGet 依赖前必须说明理由、
  维护状态与体积影响，并在 `docs/architecture.md` 登记。
- 不引入带遥测/统计上报的依赖。

常用命令：

```bash
dotnet build wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release
dotnet build wpf/TokenView.Probe/TokenView.Probe.csproj -c Release
dotnet test wpf/TokenView.Tests/TokenView.Tests.csproj -c Release
dotnet run --project wpf/TokenView.Wpf
dotnet publish wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release -r win-x64 --self-contained false
```

- Debug 构建可能被正在运行的 TokenView 实例锁定；无法覆盖 `bin/Debug` 时改用 `-c Release`，
  或先关闭应用。
- `dotnet run` 启动的是真实桌面应用，会在启动后按设置同步本地数据。

## 3. 目录职责

| 路径                              | 职责                                                                 |
| --------------------------------- | -------------------------------------------------------------------- |
| `wpf/TokenView.Wpf/`                | 唯一的桌面应用                                                       |
| `wpf/TokenView.Wpf/Data/`           | 数据模型、SQLite 存储、查询与聚合（不含 UI）                         |
| `wpf/TokenView.Wpf/Data/Collectors/`| 各客户端发现、解析、增量导入、同步调度                               |
| `wpf/TokenView.Wpf/Data/Usage/`     | Token 语义、归一化模型、写入与去重                                   |
| `wpf/TokenView.Wpf/Services/`       | 同步调度、托盘、自启、主题、窗口状态、存储迁移、更新检查、公告/关于信息与镜像获取 |
| `wpf/TokenView.Wpf/Controls/`       | 自绘图表、日期范围控件与 Markdown 渲染                               |
| `wpf/TokenView.Wpf/Settings/`       | `settings.json` 读写与设置模型                                       |
| `wpf/TokenView.Wpf/Assets/`         | 应用图标与图标源文件（`app-icon.svg`、`tokenview.ico`）              |
| `wpf/TokenView.Probe/`              | 无界面验证工具：复制库、同步、对账、自启开关、公告/关于拉取等        |
| `wpf/TokenView.Tests/`              | xUnit 测试：镜像获取、配置合并、未读持久化、一次性加载、旧存储迁移    |
| `docs/`                           | 规范与调研文档                                                       |
| `scripts/`                        | 只读对账脚本（`reconcile_overview.py`）                              |

约束：

- 依赖方向单向：窗口 / 控件 → `Services` / `Data`；采集器 → `Usage` / 存储；`Data` 不引用窗口、
  控件或 `System.Windows`。业务逻辑不得写在 XAML 后台代码里，除控件事件转发与视图状态。
- 新目录、持久化方案、测试框架、错误处理库等架构组件，先在 `docs/architecture.md` 登记再引入。
- 按变更原因拆分职责：Token 语义 / 归一化、客户端解析、文件发现 / 同步、存储 / 迁移 / 查询分开。
- 结构重构保留统计规则、DTO 与迁移 SQL；口径或行为修复另做提交并补验证，不与文件搬移混在一起。

## 4. 代码规范

- 遵循 .NET 默认命名与格式：PascalCase 类型 / 方法 / 属性，camelCase 局部变量与参数，
  私有字段 `_camelCase`；一个文件一个公开类型。
- `Nullable` 与 `ImplicitUsings` 保持开启；不关闭可空检查来省事。
- 异步 I/O 使用 `async` / `await`，不得阻塞 UI 线程；事件处理器外禁止 `async void`；
  长耗时扫描、同步与数据库操作放到后台线程。
- 可恢复错误返回 `Result` / 异常并给出用户可读信息；不要静默吞异常，不要在正常路径 `catch { }`。
- 不使用 `TODO` / `NotImplementedException` 进入主分支；未知输入按"忽略未知字段 + 记录计数"处理。
- 金额、时间、Token 展示统一走 `TokenFormatting` / 现有格式化方法，不在窗口里手写单位换算。
- XAML：使用 `Theme.xaml` 的语义画刷（`WindowBrush`、`CardBrush`、`TextBrush`、`AccentBrush` 等），
  不写死颜色；圆角、间距与字号沿用现有窗口；新增可视组件先在 `docs/ui-guidelines.md` 说明。
- 注释只写"为什么"、约束与数据来源，不复述代码；统计口径的说明放到 `docs/data-metrics.md`。
- 提交信息采用 `type(scope): description`，`type` 取 `feat` / `fix` / `chore` / `docs` / `refactor` /
  `test` / `build` / `ci`，描述用英文；一次提交只做一件事。

## 5. 安全要求

- **最小权限**：只读取统计所需的客户端元数据；不申请管理员权限，不写客户端目录。
- **网络**：默认零网络请求。已登记的联网入口只有两个：
  1. 固定 GitHub Releases `latest.json` 的更新检查与同仓库更新包下载，只能由用户手动触发或开启自动更新后按每天最多一次触发；
     更新包必须校验 `latest.json` 中的 SHA-256，替换必须由用户确认重启，不静默覆盖运行中的程序。
  2. 公告与关于页 Markdown：启动时一次 + 每 20 分钟，按固定镜像列表 GET 公开的 `mirrors.json`、`README.md` 与 `info.md`；
     不执行服务端下发的脚本/HTML，不加载远程图片，不上传任何本地数据。正文只在内存，仅持久化已读哈希。
- **不引入高风险能力**：不执行远程脚本（更新只接受校验过的 zip，安装脚本由本机生成）、不加载远程图片/HTML、
  不引入遥测 SDK；外部链接交给系统浏览器。
- **隐私**：不把会话正文、项目路径、会话标识、错误原文写入日志、设置或仓库；诊断摘要必须脱敏。
- **本地数据**：提供停用采集与清除统计的入口；清除不得影响客户端原始数据；迁移失败要保留原库。
- 不在代码、日志、错误信息或仓库中存放任何密钥、令牌、真实会话内容；`.key` 等文件不得入库。

## 6. 数据处理原则

完整口径见 `docs/data-metrics.md`，此处为强制约束：

1. **只读**：对客户端数据目录只做读取，绝不写入、移动或删除。
2. **最小采集**：只提取统计必需元数据（时间、模型、token 计数、会话标识等），不持久化消息正文。
3. **可溯源分级**：每个指标必须标注来源等级——`measured`（客户端真实记录）、`derived`（由真实字段直接换算）、
   `estimated`（推算，例如按字符数估算 token）。估算不得与实测混在同一数值里。
4. **估算可见**：UI 上估算数据必须有明确标注（图例、tooltip、样式区分），见 `docs/ui-guidelines.md`。
5. **本地无出口**：除第 5 节登记的两个入口外默认无任何网络请求；联网能力必须先登记。
6. **幂等**：重复扫描不得重复计数，唯一键与去重规则写在 `docs/data-metrics.md`。
7. **口径变更留痕**：修改统计口径必须更新 `docs/data-metrics.md` 并记录版本、日期与影响。
8. **可清除**：应用产生的本地数据必须提供一键清除方式，且不影响客户端原始数据。

## 7. 测试与验证

现状：`wpf/TokenView.Tests` 使用 xUnit（已登记于 `docs/architecture.md`），覆盖公告/关于的镜像获取、
配置合并、哈希、未读持久化、一次性加载、旧版存储迁移与更新包解析/下载校验；测试只使用假
`HttpMessageHandler` / `TimeProvider` 与临时目录，不访问真实网络、不依赖当前时间、不触碰真实 AppData。
新增可测试逻辑时补相应用例；测试数据只用合成内容，不要为了"看起来有测试"而创建无断言的占位测试。

口径类改动（Token 语义、去重键、累计值、迁移）必须验证：

- 使用 `wpf/TokenView.Probe` 在**数据库副本**上重跑同步与总量查询，对比改动前后的会话 / 消息 / Token 计数。
- 只使用合成数据或已脱敏副本；不得依赖真实客户端目录、网络或当前时间的自动化验证。
- 客户端格式变化导致解析失败时，保留"忽略未知字段 + 记录计数"的行为，并记录到提交说明。

UI 改动必须实际启动应用验证：窗口可启动、关键路径可完成、空 / 加载 / 错误三态正常，
深浅主题与 800×600 窄窗口不溢出。

## 8. 开发完成后的验收流程

1. `dotnet build wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release`，必须零错误。
2. 改动涉及服务层、公告/关于或配置逻辑时，运行 `dotnet test wpf/TokenView.Tests/TokenView.Tests.csproj -c Release`。
3. 改动涉及 Probe 或采集逻辑时，构建 `wpf/TokenView.Probe` 并在数据库副本上验证。
4. 涉及界面或交互时，运行 `dotnet run --project wpf/TokenView.Wpf` 手动验证；
   涉及托盘、关闭行为、自启时验证实际行为。
5. 涉及统计数据时，抽样与客户端自身统计对账，并在提交说明中写出口径、差异与原因。
6. 涉及新依赖、联网能力、目录结构或统计口径时，同步更新 `AGENTS.md` / `docs/` 对应文档。
7. 自检清单：
   - [ ] 没有引入未登记的依赖、UI 组件库或测试框架
   - [ ] 没有把会话正文、路径或会话标识写入日志、缓存或仓库
   - [ ] 估算数据在 UI 上有明确标注
   - [ ] 采集器仍然只读，重复扫描幂等
   - [ ] Release 构建与相关 `dotnet test` 通过，文档已同步

## 9. 规范维护

- 单一事实来源：构建命令以 `wpf/*/*.csproj` 与本文档为准，统计口径以 `docs/data-metrics.md` 为准，
  视觉规范以 `docs/ui-guidelines.md` 为准，结构与边界以 `docs/architecture.md` 为准。
- 修改任一规范文件后，检查其余规范文件是否出现表述冲突（术语、路径、命令、分级命名）。
- 规则取舍优先级：本地优先 > 最小权限 > 可验证 > 实现便利。
- 能被工具检查的规则优先写进 `.editorconfig` / `Directory.Build.props` / CI，文档只解释"为什么"。
