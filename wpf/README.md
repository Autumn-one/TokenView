# TokenView

`wpf/` 是 TokenView 的唯一实现：C# / .NET 10 / WPF 桌面应用，对外显示名为 **TokenView**。
程序集、命名空间、数据目录与库文件已统一为 TokenView；旧版 TokLens 数据会在首次启动时自动迁移，
GitHub 仓库地址仍沿用 `TokLens`。

## 历史说明

重写前的 WebView2（Tauri + React + Rust）实现已从工作区移除，完整快照保存在
`backup/webview-2026-10-19` 分支（提交 `91efb73`），仅作历史对照，不再维护、不参与构建。

## 数据兼容

- 设置：`%APPDATA%\com.tokenview.app\settings.json`（schema v1，字段与旧实现兼容）。
- 数据库：`tokenview-usage.sqlite3`（SQLite，表结构与去重键见 `docs/data-model.md`）。
- 存储定位：`storage-location.json` 记录迁移后的目录，数据库与设置可整体搬到用户目录。

## 已实现

- 总览：标题栏、日期范围（预设 + 双月日历自定义区间浮层）、同步状态（可点击查看扫描/导入与错误详情）、
  加载骨架动画、刷新与设置入口。
- 请求用量卡片：已知 Token、请求数、会话数、未知用量提示。
- 自绘折线图：网格、坐标轴、Token/请求数切换、空值断点、悬浮 tooltip（精确值、会话数，客户端 / 模型切换分项）。
- 每日条、客户端分布（品牌图标，可展开查看该客户端的请求级模型统计）、模型/项目 Top 5（模型品牌图标）、最近会话表。
- 会话总账：今日/区间 Token、会话数与请求明细/缓存命中率、按会话开始日聚合、趋势图、客户端分布、会话表。
- 会话详情：会话 Token 分项与总量依据、完整度、请求覆盖统计、请求明细分页（100 条/页）、
  序号/时间/模型/代理/分项 Token/来源等级/引用/未知关系；双击或 Enter 打开。
- 设置：通用、外观与统计、采集、更新、数据与隐私、关于；即时应用主题与 Token 展示口径。
- 采集：18 个客户端采集器（Codex / Claude Code / OpenCode / DSH / Kimi Code / MiMoCode /
  Pi Desktop / Pi Agent / WorkBuddy / GitHub Copilot / Gemini CLI / Grok CLI / MCode /
  Devin / Command Code / Cline / Qoder / CodeArts），独立后台连接、增量水位、快照原子替换、
  请求口径预聚合重建。CodeArts 读取 `~/.codearts/kernel/sessions` 下的 `meta.json` 与
  `messages.index.jsonl`，不读取 `history.jsonl` 正文。
- 调度：启动采集、可配置 0/1/5/15/30 分钟定时、暂停。
- 桌面：单实例（重复启动会唤醒已有窗口/托盘）、系统托盘（打开 / 立即同步 / 退出）、
  关闭行为询问与记住选择、开机自启、窗口尺寸与位置记忆。
- 更新：入口只在设置 · 更新，固定 GitHub Releases `latest.json` 端点的手动与每日自动检查；
  自动检查命中新版本时用托盘气泡提示，点击气泡打开官方下载页，不弹窗打断操作。
- 公告：启动时与每 20 分钟从固定公开镜像列表获取 Markdown，工具栏铃铛入口带未读圆点；
  公告窗口 2 秒可见停留后清除未读，支持手动刷新与失败重试，刷新失败保留上一次内容。
- 关于：工具栏“关于”按钮直接打开设置 · 关于分类（不新建独立窗口）；该分类中的动态信息首次进入
  自动获取一次，失败可手动重试；正文只在内存中复用。
- Markdown：Markdig 渲染标题、段落、列表、粗体、引用、代码与表格；HTML 按文本显示，
  图片只显示为链接，只有绝对 `http` / `https` / `mailto` 链接会交给系统浏览器。
- 网络：公告/关于的镜像列表、12 秒单请求超时、512 KiB 上限、原始字节 SHA-256 与 HTML 错误页降级
  登记在 `docs/architecture.md`；只持久化 `lastReadAnnouncementHash`，不保存正文。
- 数据与隐私：存储目录展示、一键清除统计（随后暂停采集）、恢复默认设置、迁移数据库与设置到
  用户目录（`storage-location.json`）。
- 深色主题（含标题栏与图表）、品牌图标、自定义滚动条。

## 应用图标

- 源图：`wpf/TokenView.Wpf/Assets/app-icon.svg`（1024×1024 矢量）。
- Windows 图标：`wpf/TokenView.Wpf/Assets/tokenview.ico`，含 16 / 20 / 24 / 32 / 40 / 48 / 64 / 128 / 256 px 九档。
- 通过 `TokenView.Wpf.csproj` 的 `<ApplicationIcon>Assets\tokenview.ico</ApplicationIcon>` 嵌入可执行文件；
  系统托盘图标由 `Services/TrayService.cs` 从可执行文件提取，窗口未单独设置 `Icon`，
  标题栏与任务栏沿用同一嵌入图标。

## 采集器对账：重写期间与已移除的 WebView2 版本对账（历史记录）

下表为重写期间用本机真实数据同步到生产库副本、与已移除的 WebView2 版逐客户端对账的结果，
保留作为口径一致性的历史证据；该版本已不存在，无法复跑。

| 客户端                             | 会话 |   消息 | 说明                                       |
| ---------------------------------- | ---: | -----: | ------------------------------------------ |
| Codex                              |  191 | 11,314 | 增量/强制重扫均与旧版行级一致              |
| Kimi Code                          |  188 | 61,198 | 仅活动会话有新增，其余 187 个逐列一致      |
| DSH                                |  156 | 23,420 | 含 zstd 解码；unparsed 49,961 与旧版相同   |
| Pi Agent                           |  117 |  9,943 | 全部列一致                                 |
| Devin                              |  356 | 24,435 | 2.3 GB 源库；消息 24,435/24,435 逐列一致   |
| WorkBuddy                          |   39 |  2,166 | 全部列一致                                 |
| OpenCode                           |   49 |  2,080 | 全部列一致（1 个费用值 1 ULP 差异）        |
| Grok CLI                           |   20 |     81 | 全部列一致                                 |
| Command Code                       |   26 |  2,453 | 全部列一致                                 |
| Cline                              |    5 |      5 | 全部列一致                                 |
| Qoder                              |    3 |    166 | 全部列一致                                 |
| MiMoCode                           |    2 |     46 | 全部列一致                                 |
| Pi Desktop                         |    1 |     86 | 全部列一致                                 |
| CodeArts                           |    3 |     61 | 会话/消息总量与 meta、index 完全一致       |
| MCode                              |    0 |      0 | 本机源库为空                               |
| Copilot / Gemini CLI / Claude Code |    0 |      0 | 本机无源数据，使用合成 fixture 验证        |

完整同步（18 个客户端、强制重扫）在本机约 1–1.5 分钟，结果与生产库逐客户端一致：
会话数、会话 Token 总量与消息行完全一致；仅正在追加的活动会话会出现预期的少量新增。

## 尚未完成 / 已知差异

- 更新不做应用内安装：未实现 minisign 校验，发现新版本后只打开官方 GitHub Releases 页面。
- 标题栏沿用系统配色，内容区、图表和滚动条跟随应用主题。
- 未做单文件发布 / trimming；Release 使用框架依赖构建。
- 会话详情按 100 条/页分页，不截断总会话明细。

## 开发与验证

需要 .NET 10 SDK。验证走 Release 构建 + `dotnet test` + `wpf/TokenView.Probe`（见 `AGENTS.md` 第 7 节）。

```bash
dotnet build wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release
dotnet build wpf/TokenView.Probe/TokenView.Probe.csproj -c Release
dotnet test wpf/TokenView.Tests/TokenView.Tests.csproj -c Release
dotnet run --project wpf/TokenView.Wpf
```

`wpf/TokenView.Probe` 是无界面的采集/查询验证工具（copy / sync / totals / autostart / announcement / about-info），
数据库验证只能在副本上进行，不得对生产库重跑同步：

```bash
P=wpf/TokenView.Probe/bin/Release/net10.0-windows/TokenView.Probe.dll
dotnet $P copy "$APPDATA/com.tokenview.app/tokenview-usage.sqlite3" /tmp/db.sqlite3
dotnet $P sync /tmp/db.sqlite3 --force
dotnet $P totals /tmp/db.sqlite3
dotnet $P announcement   # 真实拉取公告，打印 source / hash / bytes / title
dotnet $P about-info     # 真实拉取关于信息
```

发布：

```bash
dotnet publish wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release -r win-x64 --self-contained false
```

## 内存基线（本机，首屏总览；历史对照）

下表为重写期间的实测对照，WebView2 版已移除，仅作历史参考：

| 版本                                          |  私有内存 |    工作集 |
| --------------------------------------------- | --------: | --------: |
| WebView2 生产版（含浏览器子进程，历史）       | 约 332 MB | 约 539 MB |
| WPF Release（含 18 个采集器与完整设置）       | 约 256 MB | 约 321 MB |

WPF 首次同步 Devin 的 2.3 GB 源库时会有约 300 MB 的瞬时峰值，完成后 GC 回落；上表为同步完成后的稳定值。

### 采集峰值（2026-10-20，`TokenView.Probe sync <副本库> --force <client>` 单进程实测）

`runtimeconfig.template.json` 固定 `System.GC.ConserveMemory=5`，`mmap_size` 32 MB；下表的
"改动前/后"指是否启用该 GC 配置，同步耗时无回归：

| 客户端 | 峰值工作集 | 私有内存 | 托管堆 | 说明 |
| --- | --- | --- | --- | --- |
| Codex | 147 → 103 MB | 109 → 59 MB | 41 → 26 MB | 49 个 rollout 文件全量重扫 |
| DSH | 340 → 179 MB | 153 → 62 MB | 118 → 28 MB | 含 75 MB zstd 文件；本轮仍在写入，按解析峰值计 |
| Devin | 91 → 91 MB | 61 → 61 MB | 14 MB | 2.3 GB 源库走流式临时副本，托管堆不随库大小增长 |

GUI 首屏实测（Release，启动采集完成后稳定 60 秒）：

| 指标 | 改动前 | 改动后 |
| --- | ---: | ---: |
| 私有内存 | 256 MB | **146–149 MB** |
| 工作集 | 321 MB | **230–233 MB** |

启动后 18 秒内是 177 MB / 280 MB（采集与首屏叠加），同步结束触发 LOH 压缩与
`ConserveMemory` 后退到上表；采样期间无网络请求、无对话框交互。

