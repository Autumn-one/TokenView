# TokenView（WPF）内存占用诊断报告

- 日期：2026-10-20
- 分支：`experiment/wpf-rewrite`
- 被测程序：`wpf/TokenView.Wpf`（对外名 TokenView，`net10.0-windows`，WPF + WinForms）
- 方式：**只读探索**。未运行应用、未改动代码、未改动数据；仅对用量库做过只读 `COUNT(*)`/`MAX()` 查询，对客户端目录做过 `du`/`find` 统计。
- 参与智能体：Kimi 主智能体（本文件作者）、`trae/qwen3.8-flash`、`primary`（deepseek-v4.1-flash）。
  > 原计划要求 `glm-5.3`，但该模型不在本会话可用模型列表中（调用返回 `Invalid model "glm-5.3"`，可选仅 `trae/qwen3.8-flash`、`primary`），因此用 `primary` 作为第二个独立视角。

> 注：项目标识统一为 TokenView 后，文中数据库路径已同步为新名称；测量数据来自改名前的同一份库。

---

## 0. 结论摘要

**没有找到真正的无界泄漏。** 全部跨同步存活的结构都有硬上限：

| 结构 | 上限 | 证据 |
| --- | --- | --- |
| `SyncService.LastResults` | 18（客户端数） | `Services/SyncService.cs:23,41-44` |
| `SyncEngine._collectors` | 18 个无状态实例 | `Data/Collectors/SyncEngine.cs:15-28` |
| `DevinCollector.SignatureCache` | 每个源库路径 1 条 `(size, mtime)` | `Data/Collectors/DevinCollector.cs:20-21` |
| `OpenCodeCollector.Signatures` | 同上 | `Data/Collectors/OpenCodeCollector.cs:19` |
| 事件订阅 | 成对退订 | `MainWindow.xaml.cs:76-83`、`SettingsWindow.xaml.cs:27-28` |
| 定时器 | `Closed` 全部停止 | `MainWindow.xaml.cs:78-79,682` |

内存高、且"用久了不回落到初值"，由 **一条峰值链 + 一个高水位** 解释：

1. **峰值链**：采集器先把**整个客户端的历史**构造成托管对象（+ JSONL 大行进 LOH），再一次性写库；任一源文件变化就整客户端重解析，每 5 分钟一次。
2. **高水位**：峰值过后，.NET 工作站 GC 把 gen2/LOH 段保留在已提交状态，加上 SQLite `mmap_size=256MB`（只影响工作集）与 WPF/WinForms 基线，于是任务管理器里的数字停在峰值附近。

因此优化不是"找泄漏点"，而是三件事：**把峰值变成流式、把大行移出 LOH、把映射与运行时常驻压下去**。

---

## 1. 本机实测基线

### 1.1 应用数据库（只读）

| 项 | 值 |
| --- | --- |
| 路径 | `%APPDATA%\com.tokenview.app\tokenview-usage.sqlite3` |
| 大小 | 92,942,336 B（88.6 MiB），`page_size=4096`，`freelist_count=1` |
| `messages` / `sessions` / `source_files` | 142,324 / 1,220 / 1,846 |
| `request_hourly` / `request_hourly_sessions` | **2,408 / 3,235** |
| 消息数最多的客户端 | `kimi_code` 63,496；`devin` 24,435；`dsh` 24,050；`codex` 11,655 |
| WAL | 0 B（已 checkpoint） |

结论：**查询层物化量很小**。即使把 `request_hourly` 与 `request_hourly_sessions` 全读进内存，也只是几 MB 量级，并且图表有 400 天硬上限（`Data/TokenViewDatabase.cs:469-473`）。

### 1.2 客户端源数据（本项目只读）

| 目录 | 大小 |
| --- | --- |
| `~/.kimi-code/sessions` | ≈ 2,338 MB |
| Devin `sessions.db`（`%APPDATA%\Devin\cli`） | ≈ 2,309 MB |
| `~/.grok/sessions` | ≈ 458 MB |
| `~/.codex/sessions` | ≈ 409 MB（49 个 `.jsonl`） |
| `~/.dsh/sessions` | ≈ 209 MB，最大单文件 75.5 MB 的 `session.v4.jsonl.zstd` |
| `~/.local/share/opencode` | ≈ 283 MB |

### 1.3 已知基线（仓库文档记录）

`wpf/README.md`：

| 版本 | 私有内存 | 工作集 |
| --- | ---: | ---: |
| WPF Release（18 个采集器 + 完整设置） | 约 256 MB | 约 321 MB |

并注明：首次同步 Devin 的 2.3 GB 源库时有约 300 MB 瞬时峰值，完成后 GC 回落。

---

## 2. 三个智能体的探索记录

### 2.1 主智能体（Kimi）：A/B/C 三层模型

**A. 常驻基线**

| # | 证据 | 说明 |
| --- | --- | --- |
| A1 | `Data/Usage/UsageStore.cs:55-56` `PRAGMA cache_size = -8192; PRAGMA mmap_size = 268435456;` | 库 88.6 MiB，写连接把整库映射进地址空间；全表重写会把映射页全部拉进工作集。`Pooling = false`（`:27`）。 |
| A2 | `TokenView.Wpf.csproj:1-31` | 只有 `net10.0-windows` + `UseWPF` + `UseWindowsForms`，**没有任何 GC 配置**（无 server/conserve/RetainVM），也没有 `runtimeconfig.template.json`。 |
| A3 | `Data/Collectors/CollectorSupport.cs:80-109` | `ReadJsonLines` 每行 `List<byte>` 累积 → `line.ToArray()` → `GetString`：每行至少两次分配，>85 KB 进 LOH。 |
| A4 | `TokenView.Wpf.csproj:9` + `Services/TrayService.cs:31-35,46` | 仅为托盘 `NotifyIcon`/`Icon.ExtractAssociatedIcon` 引入整个 `System.Windows.Forms` + `System.Drawing`。 |
| A5 | `MainWindow.xaml:620`、`Controls/RequestLineChart.cs:91-181,198-202` | 弹层 `DropShadowEffect`（软件渲染）；折线图每次重绘新建数组/3 个 Pen/StreamGeometry，hover 跨点即 `InvalidateVisual`。 |

**B. 采集期峰值**

| # | 证据 | 说明 |
| --- | --- | --- |
| B1 | `ClaudeCodeCollector.cs:69-112`、`:211-280` | 全部文件的全部记录先进 `sessions` 字典（`Rollup._records`），`Finish()` 再为每个会话生成完整 `List<MessageRecord>`，最后一次性 `ReplaceClientSnapshot`。峰值 = O(该客户端全部消息)。 |
| B2 | 19 个调用点（见 §4.3） | 同一模式覆盖 17 个客户端。 |
| B3 | `ClaudeCodeCollector.cs:52-67` | `unchanged` 是**全局与运算**：任何一个文件变了就整客户端重解析；`NewSourceFile(..., size, ...)` 把 `content_offset` 直接写成 `size`（`:59`），等于没有行级增量。 |
| B4 | `DshCollector.cs:210-221,226,830,951` | `File.ReadAllBytes` 整文件；zstd 窗口按帧声明大小分配，上限 `128 << 20`；`ParseState._events` 保留整会话事件。 |
| B5 | `NormalizedUsage.cs:71-76,81-82,193-200` | 每条消息固定分配 `List<string>` + `Sort` + `Distinct().ToList()`；`NormalizedUsage` 是 record class，每条一个堆对象。 |
| B6 | `SqliteSnapshot.cs:49-93,104-113` | Devin/Copilot 等把源库复制到 `%TEMP%` 再打开，`CopyTo` 流式（85 KB 缓冲），托管堆影响小但会推高系统文件缓存与 WS。 |

**C. UI 交互抖动**

| # | 证据 | 说明 |
| --- | --- | --- |
| C1 | `MainWindow.xaml.cs:703-724,741-782,805` | 每个 `MouseMove` 都 `ShowTooltip` → `RenderTooltipBreakdown` → `ToList()` + 重新赋 `ItemsSource`，索引没变也照做。 |
| C2 | `ClientIcon.cs:56-70`、`ModelIcon.cs:58-71` | 每次 `OnRender` 重新 `Geometry.Parse(data)`，路径字符串是静态的。 |
| C3 | `TokenViewDatabase.cs:259-391`、`SessionLedgerWindow.xaml.cs:17-31` | `LoadLedger` 把区间内全部会话物化（含每行 `"yyyy-MM-dd HH:mm"` 字符串），内存里 `GroupBy` 逐日补齐；DataGrid 行虚拟化本身正常。 |

主智能体明确排除的项：无静态无界缓存；事件成对退订；`RequestLineChart` 每帧对象数固定且已 Freeze；主窗口列表规模很小（最近会话 8 行、客户端 18 行、Top5）；主题切换只覆盖 15 个冻结画刷（`ThemeManager.cs:51-77`）；窗口视觉树属正常量级。

---

### 2.2 智能体 `trae/qwen3.8-flash`：R1–R10 与 LOH 实测

> 该智能体报告了独立结论，并新增了两个关键实测：**超 LOH 阈值的行统计** 与 **`content_offset` 未被使用**。

**R1（它判为第一位）— `ReadJsonLines` 每行两次堆分配，大行进 LOH**

`Data/Collectors/CollectorSupport.cs:84,94`：

```csharp
var line = new List<byte>(4096);
...
var text = System.Text.Encoding.UTF8.GetString(line.Count == 0 ? [] : line.ToArray(), 0, length);
```

实测（kimi + codex + grok 的 JSONL）：**单行 >85 KB（LOH 阈值）的共 2,431 条，合计 614.6 MB，最长单行 6.72 MB**。一次全量同步就会产生同等量级的 LOH 分配；LOH 只在 gen2/压缩时回收，而工作站 GC 下 gen2 很少触发，于是私有字节单调爬升、迟迟不还。走这条路径的采集器至少 8 个：Claude、Codex、Kimi、WorkBuddy、PiAgent、PiDesktop、CommandCode、Cline。

**R2 — 整客户端快照**：`SessionImport` 同时持有 `List<MessageRecord>` 与 `Dictionary<string,string>`（`UsageModels.cs:112`）；Devin 甚至 `new List<SessionImport>(sessionRows.Count)` 全量累积（`DevinCollector.cs:229`），本机 24,435 条消息全部驻留。

**R3 — `content_offset` 形同虚设**：schema 有该列（`UsageStore.cs:573`），但除 Codex 外所有采集器写入时直接等于 `size`（如 `ClaudeCodeCollector.cs:59`），说明**行级增量从未实现**，`unchanged` 只能退化为整客户端判断。同模式至少 8 处：`DshCollector.cs:72,80`、`GrokCollector.cs:65,69`、`WorkBuddyCollector.cs:61,65`、`KimiCodeCollector.cs:57,64`、`ClineCollector.cs:114,118`、`CodeArtsCollector.cs:53,57`、`CommandCodeCollector.cs:53,57`、`GeminiCliCollector.cs:68,73`。

**R4 — DSH 整文件 + 128 MB 窗口**：`DshCollector.cs:212` `File.ReadAllBytes`；`:830` `MaximumWindow = 128 << 20`；`:951` `_window = new byte[windowSize]`；`:778` 的 `DshLineConsumer._line` 是 `MemoryStream` 且用 `GetBuffer()`（`:803`），跨行拼接翻倍扩容。

**R5 — mmap 只影响工作集**：`UsageStore.cs:56` 只作用于写连接；UI 的 `OpenReadOnly()`（`TokenViewDatabase.cs:393-408`）根本没设 mmap，只设 `query_only` + `busy_timeout`。88.6 MiB 库 → 工作集 +~90 MB，不计私有字节，`Dispose` 即 unmap。

**R6 — `LoadLedger` 全量物化 + `LoadAsync` 无重入**：`TokenViewDatabase.cs:286,270-272`；`MainWindow.xaml.cs:670`。触发点有 5 个（范围切换/自定义范围/刷新/设置关闭/同步完成）+ 15 秒定时器，并发时多份快照同时存活，且每份都重跑 `ReadSessionBuckets`（`:492` 用 `Dictionary<long, HashSet<string>>` 装全部 session_id 字符串）。

**R7 — UI 抖动**：tooltip 每次鼠标移动重建（`MainWindow.xaml.cs:805`）；图标每次 `OnRender` 重 Parse（`ModelIcon.cs:60,67`、`ClientIcon.cs:58,66`）；`DailyStrip` 无虚拟化但被 400 天钳住；`RenderClientUsage` 每次 `Select(... with { IsExpanded })` 重建整个列表并换 `ItemsSource`。

**R8 — 运行时配置**：无 GC/发布调优；并提醒**不要开 ServerGC**（会显著抬高常驻），`UseWindowsForms` 是固定基线成本。

**R9 — SqliteSnapshot 整库复制**：`SqliteSnapshot.cs:104-110`，Devin 2.3 GB 每次全量同步复制一份到 `%TEMP%`；托管堆影响小，但推高系统缓存/WS，且属于"已修复但代价仍在"。

**R10 — 已排除项**（它独立核对，与主智能体一致）：静态集合上限（18 / 路径数 / 枚举 6 项）、事件成对退订、`using` 覆盖所有连接与 reader、`Pooling=false`、无热循环 `new Regex`、图表 400 天上限、会话详情分页、无 `RenderTargetBitmap`/`BitmapCache`/`WriteableBitmap`、唯一 `DropShadowEffect` 在 Popup 内、`TrayService.Dispose` 被调用。

**它给 9 条候选结论的表态**（详见 §3）：

1. 部分同意（"无泄漏"只能证到静态层面；`LoadAsync` 并发是可观测的爬升）。
2. 同意。
3. 同意。
4. 部分同意（10–20 MB 无法静态验证；剥离等于重写托盘，成本高于"可选"字面）。
5. 同意。
6. 部分同意（有界非泄漏正确，但列为 P0 是错配）。
7. 同意。
8. 部分同意（"先量化"对；内部顺序应把 `ReadJsonLines`/LOH 与采集模型并列 P0）。
9. 部分同意（P0 −60~100 MB 偏乐观；mmap 不降私有字节）。

**它的 top-3**：① `ReadJsonLines` 去 LOH；② 采集流式 + 真正行级增量；③ mmap/cache 配置 + WinForms 剥离。

---

### 2.3 智能体 `primary`（deepseek-v4.1-flash）：1–10 条与最小改造方案

**根因排序（它自己的编号）**

1. **整客户端快照 + 全局 `unchanged`**（影响大、置信度高）。它把"一个文件变化 → 重读全部历史 → 全部会话常驻 → 整库删后重插"作为第一根因，并引用 `UsageStore.cs:198-202`（整客户端 `DELETE`）与 `:224-235`（逐条重插）。本机数据：Kimi 63,496 / Devin 24,435 / DSH 24,050 / Codex 11,655。按每条 400–600 B 活对象估算，单客户端瞬时 30–80 MB，整轮 100–300 MB 量级。
2. **GC 高水位 + 无释放路径**（中–大，置信度中高）：每次同步制造几十到几百 MB 短命对象（含 LOH），段被保留在高水位，"同步完为什么不掉"主要由它解释。同链上还指出 DSH 的 `128 << 20` 解码窗口与 `line.ToArray()`。
3. **Copilot 完全没有"未变化"短路**（影响大，仅限 Copilot 用户）：`CopilotCollector.cs:87-95` 的签名只用于"扫描期间变化"校验，不用于跳过；每次都复制源库 → 读全部会话/消息/events/host DB → 全量 Replace（`:209-212`）。
4. **Codex 把"所有已变更文件的全部消息"同时留在内存**：`CodexCollector.cs:72-98` 的 `parsedFiles` 持有全部已解析文件（`:663` 每文件一个 `Messages` 列表），`:100-121` 再聚合一次引用，直到全部写库结束。增量场景小，`--force`/首次时大。**注意：Codex 仍是最接近正确形态的实现**（`ReplaceFileImport` 逐文件写）。
5. **每条消息 `NormalizedUsage.Normalize` 固定两次 List 分配**（中，放大第 1 条）。
6. **mmap 256 MB**（中，仅工作集，有界非增长）。
7. **查询层全量物化**（本机小；设计上无上限）：`ReadHourRows` 全读（`:413-441`）、`GroupBy(...).ToDictionary(g => g.ToList())` 复制引用列表（`:474-476`）、`ReadSessionBuckets` 每个 (小时,会话) 一个 string（`:489-514`）。并明确标注"本机 `request_hourly` 2,408 行，所以查询层导致几百 MB 在这台机器上不成立"。
8. **自绘 UI 每帧分配**（小–中，有界非泄漏）。
9. **DailyStrip 不虚拟化**（有界，≤400 单元格）。
10. **`LoadAsync` 无重入保护**（小–中）。

**它的已排除表**：静态集合无增长、事件无泄漏、定时器全停、同步用 `SemaphoreSlim(1,1)` 串行（`SyncService.cs:12,27`）、无 `FileSystemWatcher`、无 `RenderTargetBitmap`/`BitmapCache`/`BlurEffect`、未用 `ObservableCollection`、DataGrid 虚拟化正常、会话详情分页、主题只替换 15 个冻结画刷、`SettingsWindow.BuildClientList` 先 `Children.Clear()`、存储迁移用 `VACUUM INTO` 流式。

**它给 9 条的表态**：1 同意；2 部分同意（**调用点应为 17 处，且 OpenCode 有短路、Copilot 完全没有，不应混为一谈**）；3 同意；4 部分同意；5 同意；6 部分同意（应把"重建 ItemsSource 未去重"这一点写得更严重，但归 P0 不成立）；7 同意；8 **不同意**（把采集模型放最后是成本/收益倒挂）；9 **不同意**（数字自相矛盾：mmap 不计私有字节，P0 就不可能有 60–100 MB 私有收益）。

**它提出的最小改造（本文件采纳）**：`ReplaceClientSnapshot` 的原子性本来就由"单事务"提供（`UsageStore.cs:194-238`，未 Commit 即 Dispose 会回滚，`source_files` 水位也在同一事务），不需要靠"一次性把全部数据放内存"来换取。所以只需把入参从 `IReadOnlyList<SessionImport>` 改成 `IEnumerable<SessionImport>`，采集器把 `ToList()` 改成 `yield return`，逐条校验替代全量预校验——**语义零变化、无 schema 迁移、无查询层改动**。详见 §5。

**它回答的 GC 问题**：`System.GC.ConserveMemory`（0–9）在 Windows 上对应 `VirtualFree(MEM_DECOMMIT)`，**确实会反映在私有字节**；但它只削"同步后的平台期"，不削峰值，且 .NET 7+ 区域化 GC 的可回收余量通常小于历史经验。A/B 方法：三次独立启动 `DOTNET_GCConserveMemory=0/5/9`，T2（同步后再空闲 10 分钟）私有下降 **≥15%** 且离散度 <10% 才保留；同步峰值 WS 不得上升 >5%；`time-in-gc` <5%；否则回退。**不要** `RetainVM=true`（效果相反）；**不要** `EmptyWorkingSet`；`HeapHardLimitPercent` 只能在削峰之后上，否则大同步直接 OOM。

---

## 3. 交叉验证与裁决

### 3.1 三方一致

1. 没有无界泄漏；静态集合、事件、定时器全部有界。
2. 峰值主因是"整客户端快照 + 全局 `unchanged`"，Kimi/DSH/Devin/Codex 都被点名。
3. `ReadJsonLines` 的逐行 `ToArray()` 是 LOH 主来源（qwen 给出了 2,431 条 / 614.6 MB / 6.72 MB 的实测）。
4. mmap 只影响工作集、不计私有字节、Dispose 即 unmap；**不能用它解释私有内存**。
5. 查询层在本机不是主因（2,408 / 3,235 行 + 400 天上限）；`LoadLedger` 全量物化与 `LoadAsync` 无重入是设计隐患。
6. UI 抖动（tooltip 每事件重建、icon 每帧 Parse）有界、非泄漏，**不是内存元凶**。
7. 每条消息的 `NormalizedUsage` 两次 List 分配是"倍数放大器"，不是独立根因。
8. 不能用 `EmptyWorkingSet` 之类手段伪造指标。
9. 正确顺序应以"先量化"开头，并以"削峰"为主体。

### 3.2 分歧与裁决

| 分歧 | 各方 | 裁决 |
| --- | --- | --- |
| 整客户端快照的调用点数量 | qwen 说 18 个/17 文件；primary 说 17 处 | 实数为 **17 个客户端采集器、19 个调用点**（`DshCollector` 与 `CopilotCollector` 各有 Replace+Merge 两条分支），另 1 个客户端 Codex 走 `ReplaceFileImport`。合计 18 个客户端。 |
| P0 的收益 | 主智能体估 −60~100 MB 私有 | **改判**：mmap 只降 WS、不降私有；P0（LOH + GC + mmap）合理预期 **私有 −20~40 MB / 工作集 −80~120 MB**；真正的私有内存下降来自 P1 的流式化与去 LOH，且应以逐客户端实测为准，不预设绝对值。 |
| 优化顺序 | 主智能体把 UI 放 P0 | **改判**：量化 → 削峰（`ReadJsonLines` + 流式快照，并列 P0）→ 配置（mmap/cache/GC）→ UI 微优化（P2）。理由：UI 抖动不改变用户抱怨的常驻量，先做小项会测不出曲线变化。 |
| 是否值得剥离 WinForms | 主智能体列 P2 可选；两方都提醒成本 | **保留为 P2，并明确"收益未测"**：`TrayService` 依赖 `Forms.NotifyIcon` 与 `System.Drawing.Icon`，剥离等于重写托盘 + 按 `AGENTS.md` 登记。 |
| GC 旋钮的价值 | 主智能体曾是 P0；两方质疑 | **降级为 P1 且必须 A/B**：只削平台、不削峰；≥15% 才保留。 |

---

## 4. 根因模型

### 4.1 峰值怎么形成（一次同步）

```
发现文件 → 指纹/水位 → 任一文件变化 ⇒ unchanged=false
        → 逐文件解析（每行 ToArray→string，>85KB 进 LOH）
        → 全部会话/消息留在字典里（MessageRecord + NormalizedUsage + 匿名 List）
        → 二次指纹校验 → 单事务：DELETE 该客户端全部 → 逐条 UPSERT
        → RebuildRequestRollup（全表 GROUP BY，扫 88.6 MB 库）
```

- 峰值 ≈ 该客户端全部消息的对象图 + 大行文本 + 映射页。
- 本机最重的三个客户端：Kimi 63,496 条（源 2.3 GB）、DSH 24,050 条（含 75.5 MB zstd）、Devin 24,435 条（2.3 GB 库复制）。
- 每 5 分钟一次（`settings.json` 的 `syncIntervalMinutes: 5`）；活动会话持续追加 ⇒ 经常命中。

### 4.2 为什么峰值过后不回落

1. LOH 段被保留（`GCHeapHardLimit` 未设、无 ConserveMemory）。
2. 写连接的 256 MB mmap 在同步期间被摸遍 → 工作集高位；连接 Dispose 后 unmap，但 WS 的减少依赖系统裁剪。
3. WPF 渲染/字体缓存 + WinForms/System.Drawing 基线不可回收。
4. `LoadAsync` 无重入时，多份 `OverviewSnapshot` 与查询中间结构可能同时存活。

### 4.3 逐项清单（合并三方，按影响排序）

| 级别 | 项 | file:line | 备注 |
| --- | --- | --- | --- |
| 大 | JSONL 大行进 LOH | `CollectorSupport.cs:84,94` | 实测 2,431 行 / 614.6 MB |
| 大 | 整客户端快照（19 调用点） | `ClaudeCodeCollector.cs:69-112`、`UsageStore.cs:172-239` 等 | 详见附录 A |
| 大 | `unchanged` 全局与运算 + 无行级增量 | `ClaudeCodeCollector.cs:57,59,61` 等 8 处 | `content_offset` 未被使用 |
| 大 | DSH 整文件 + 128 MB 窗口 | `DshCollector.cs:212,830,951` | 单文件最大 75.5 MB |
| 大（仅 Copilot 用户） | 无未变化短路 | `CopilotCollector.cs:87-95,209-212` | 每 5 分钟全量复制+重读 |
| 中 | 每条消息三重分配 | `NormalizedUsage.cs:81-82,193-200` | 142k 消息被放大 |
| 中 | mmap 256 MB | `UsageStore.cs:56` | 仅 WS |
| 中 | 无 GC/发布配置 | `TokenView.Wpf.csproj:1-31` | 无 runtimeconfig.template.json |
| 中 | `LoadAsync` 无重入 | `MainWindow.xaml.cs:631-644` | 5 个触发点 + 15 秒定时器 |
| 中 | `LoadLedger` 全量物化 | `TokenViewDatabase.cs:259-391` | "全部"区间无上限 |
| 小 | tooltip 每事件重建 | `MainWindow.xaml.cs:703-724,779` | CPU/分配抖动 |
| 小 | icon 每帧 Parse | `ClientIcon.cs:58,66`、`ModelIcon.cs:60,67` | 可缓存 |
| 小 | DailyStrip 不虚拟化 | `MainWindow.xaml:314-340` | 被 400 天钳住 |
| 磁盘（非 RAM） | `%TEMP%` 残留 18 个 `tokenview-collectors-*` 目录 + 58 MB 备份 | — | 建议一次性清理并在启动时兜底 |

---

## 5. 终极方案

### 第 0 步：量化（半天，不改产品代码）

```powershell
# 私有 vs 工作集（每 30 秒一次，观察是否单向上涨）
1..12 | % {
  $p = Get-Process TokenView.Wpf
  "{0:HH:mm:ss} priv={1:N0}MB ws={2:N0}MB delta={3:N0}MB" -f (Get-Date),
    ($p.PrivateMemorySize64/1MB), ($p.WorkingSet64/1MB),
    (($p.WorkingSet64-$p.PrivateMemorySize64)/1MB)
  Start-Sleep 30
}
```

```bash
dotnet tool install -g dotnet-counters
dotnet-counters monitor -n TokenView.Wpf --refresh-interval 2 \
  --counters System.Runtime[gc-heap-size,loh-size,gc-committed,working-set,time-in-gc]

dotnet tool install -g dotnet-gcdump
dotnet-gcdump collect -p <PID> -o idle.gcdump
dotnet-gcdump report idle.gcdump > idle.txt   # 搜 MessageRecord / SessionImport / ParsedFile
```

```bash
dotnet build wpf/TokenView.Probe/TokenView.Probe.csproj -c Release
P=wpf/TokenView.Probe/bin/Release/net10.0-windows/TokenView.Probe.dll
dotnet $P copy "$APPDATA/com.tokenview.app/tokenview-usage.sqlite3" /tmp/db.sqlite3
dotnet $P sync /tmp/db.sqlite3 --force kimi_code     # 逐客户端隔离峰值
dotnet $P totals /tmp/db.sqlite3                     # 对账基线，优化前后必须逐客户端一致
```

判定规则：

| 观察 | 结论 |
| --- | --- |
| `ws − priv ≈ 90 MB` | mmap（R5） |
| 同步后 `gc-heap-size` 回落、`gc-committed` 不降 | 段保留（A2/R2） |
| 空闲时 `gcdump` 里 `MessageRecord` 上万且随同步次数增长 | 真泄漏（当前预期为 0） |
| 单个客户端 `--force` 峰值高、增量同步接近 0 | 验证 R2/R3 |
| 切到"全部"区间后 `gc-heap-size` 只涨几 MB | 查询层不是主因 |

### P0 — 削峰（收益真正来源）

1. **`ReadJsonLines` 去 LOH**（`CollectorSupport.cs:80-109`）
   - 用 `ArrayPool<byte>.Shared` 复用行缓冲；用 `Encoding.UTF8.GetString(ReadOnlySpan<byte>)` 直接解码，删掉 `line.ToArray()`。
   - 单行上限 8 MB：超过则计 `UnparsedLines` 并跳过该行，不抛异常（对齐 `AGENTS.md`「未知输入忽略并计数，不 panic」）。
     > 实施时未加该上限（见 §7）：本机存在 6.72 MB 行，加限会静默丢数据，且回调签名没有计数出口；缓冲池化已解决主要分配问题。
   - 覆盖 Claude/Codex/Kimi/WorkBuddy/PiAgent/PiDesktop/CommandCode/Cline 等 8 个采集器。
   - 预期：消除单次全量同步约 615 MB 的 LOH 分配量。

2. **快照流式化（语义零变化）**（`UsageStore.cs:165-239`）
   ```csharp
   // 入参从 IReadOnlyList<SessionImport> 改为 IEnumerable<SessionImport>；files 仍是 IReadOnlyList
   public ImportSummary ReplaceClientSnapshot(string client, IReadOnlyList<SourceFile> files,
       IEnumerable<SessionImport> sessions) => WriteClientSnapshot(client, files, sessions, true);
   ```
   - 仍用**一个事务**（`using var transaction`，唯一 `Commit()` 在末尾）：失败自动回滚，旧 `sessions`/`messages`/`source_files` 全部还原，满足 `docs/data-model.md:268-269` 的"失败保留旧水位"。
   - 把 `:181-192` 的全量预校验改成逐条校验（报错行为不变：`ArgumentException` + 库无变化）。
   - 采集器侧：`var imports = sessions.Select(...).ToList()` 改为迭代器，`Finish()` 后立刻从累积字典 `Remove`，活集从"全历史"降到"一个会话"：
   ```csharp
   IEnumerable<SessionImport> Drain(Dictionary<string, Rollup> sessions)
   {
       foreach (var id in sessions.Keys.ToList())
       {
           var rollup = sessions[id];
           sessions.Remove(id);
           yield return rollup.Finish(id);
       }
   }
   ```
   - 对"1 会话 ↔ 1 文件组"的客户端（Kimi/CodeArts/PiDesktop/Devin 等），直接在解析循环里 `yield return`，`imports` 列表彻底消失。
     > 落地情况见 §7：Claude（惰性 `Finish`）与 Kimi（解析进流）已完成；其余需要先解决"跨文件合并同一会话"或"源/库错误分类"，见 §7.3。
   - **禁止按消息行分块提交**：`client_cumulative` 口径的会话总量来自全量消息求和（`RequestBuilder.SumCompleteCounts`），部分写入会改变统计口径。
   - **只有在 P0 实测出 WAL 膨胀或写锁时长不可接受时**，才升级到"版本令牌"方案（`snapshot_token` + `client_snapshot_state` + 全部读路径过滤 + 后台清理），代价是 schema + 6 处以上读路径 + 库临时膨胀近一倍。

3. **补行级增量**（按客户端逐个做，改口径要留痕）
   - Claude：未变文件跳过，只解析变更文件；`content_offset` 改为记录实际消费到的字节偏移（当前 `:59` 直接写 `size`）。
   - DSH：确认 `seed/fold` 语义允许流式后，去掉 `File.ReadAllBytes`（`DshCollector.cs:247-273` 是前置条件）。
   - 每接一个客户端，就在 `docs/data-model.md` 对应小节写清"水位语义变化 + 对账结果"。

4. **写库往返**（`UsageStore.cs:312-337,440-523`）
   - `UpsertMessage` 目前每行先 `SELECT ... LIMIT 1` 再 `INSERT`；`UpsertSourceFile` 先 `INSERT` 再 `SELECT id`。142k 条消息 ≈ 28 万次额外往返。
   - 改为集合式 `INSERT ... ON CONFLICT DO UPDATE` + `RETURNING id`，或先建临时表再批量合并。
   - 补 `PRAGMA journal_size_limit = 67108864` 与同步末尾 `PRAGMA wal_checkpoint(TRUNCATE)`（当前 `UsageStore.cs:50-57` 缺失）。单事务重写 6.3 万条消息会产生几十 MB WAL。

### P1 — 配置与常驻

5. `mmap_size` 256 MB → 0~32 MB；只读连接 `Cache=SqliteCacheMode.Shared` → `Private`（`Pooling=false` 下共享无收益）；`cache_size` 视实测保留 8 MB 或降到 4 MB。
6. `runtimeconfig.template.json`：
   ```json
   { "configProperties": { "System.GC.ConserveMemory": 5, "System.GC.RetainVM": false } }
   ```
   A/B 用 `DOTNET_GCConserveMemory=0/5/9`，按 §6 的阈值决定保留或回退。**不要开 ServerGC。**
7. 同步结束（`SyncService.cs:53-59` 的 `finally`）在后台线程执行一次：
   ```csharp
   GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
   GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
   ```
   仅在同步末尾，禁止放进渲染/定时器路径。
8. `LoadAsync` 加 `_loading` 门闩（与 `CheckUpdatesAsync` 的 `_updateChecking` 同款）；`LoadLedger` 加 `LIMIT/OFFSET` + 独立 `COUNT(*)`，"全部"默认只取最近 N 条并提示；`SessionRow.Started` 改为按需格式化。

### P2 — UI 与打包

9. tooltip 仅在索引变化时重建 `ItemsSource`；`RenderTooltipBreakdown` 结果按 `(point, metric, dimension)` 缓存。
10. `ClientIcon`/`ModelIcon` 缓存已 `Parse` + `Freeze` 的 `Geometry`（静态 `ConcurrentDictionary`），`OnRender` 只做 transform + Draw。
11. 剥离 `UseWindowsForms`：用 `Shell_NotifyIcon` P/Invoke 重写托盘（预计 −10~20 MB，**未实测**；需按 `AGENTS.md` 登记）。
12. 打包：`PublishReadyToRun=true` 只为降低 JIT 内存（几 MB）；**不要**为省内存上 trimming（WPF 不支持完整裁剪）。

### 明确不做

- 不用 `EmptyWorkingSet` / `SetProcessWorkingSetSize(-1,-1)` 伪造指标。
- 不在削峰之前上 `GCHeapHardLimitPercent`（会 OOM）。
- 不改统计口径、不改 schema（P0 的流式化不需要迁移）。
- 不写入、不移动、不删除客户端源数据；不引入新依赖。

---

## 6. 验收

| 指标 | 基线（README） | 目标 |
| --- | --- | --- |
| 冷启动空闲私有 | 256 MB | ≤ 200 MB（阶段目标），最终 ≤ 150 MB |
| 冷启动空闲工作集 | 321 MB | ≤ 240 MB |
| 单客户端 `--force` 峰值 | Kimi/DSH/Devin 主导，整轮 100–300 MB 量级 | 各自比基线低 ≥ 60% |
| 同步耗时 | 全量 1–1.5 分钟 | ≤ 基线 × 1.15 |
| 数据一致性 | — | `probe totals` 逐客户端完全一致 |
| 测试 | — | `dotnet test wpf/TokenView.Tests` 全绿 |

GC 旋钮的保留阈值：T2（同步后空闲 10 分钟）私有下降 ≥15% 且三次中位数离散度 <10%；同步峰值 WS 上升 ≤5%；`time-in-gc` <5%。任一条不满足即回退。

---

## 附录 A：19 个整客户端快照调用点

| 客户端 | 文件:行 |
| --- | --- |
| codearts | `CodeArtsCollector.cs:103` |
| claude_code | `ClaudeCodeCollector.cs:112` |
| cline | `ClineCollector.cs:157` |
| command_code | `CommandCodeCollector.cs:103` |
| copilot | `CopilotCollector.cs:211`（Merge）、`:212`（Replace） |
| devin | `DevinCollector.cs:135` |
| dsh | `DshCollector.cs:138`（Replace）、`:139`（Merge） |
| gemini_cli | `GeminiCliCollector.cs:150` |
| grok | `GrokCollector.cs:142` |
| kimi_code | `KimiCodeCollector.cs:143` |
| mcode | `MCodeCollector.cs:125` |
| mimocode | `MiMoCodeCollector.cs:107` |
| opencode | `OpenCodeCollector.cs:86` |
| pi_agent | `PiAgentCollector.cs:123` |
| pi_desktop | `PiDesktopCollector.cs:144` |
| qoder | `QoderCollector.cs:225` |
| workbuddy | `WorkBuddyCollector.cs:117` |
| **codex（例外）** | `CodexCollector.cs:141` 走 `ReplaceFileImport`，逐文件写入 |

## 附录 B：已排除清单（合并三方）

| 项 | 结论 |
| --- | --- |
| 静态集合/缓存 | 全部有硬上限（18 / 路径数 / 枚举 6 项），值只有几十字节 |
| 事件订阅 | `MainWindow`、`SettingsWindow` 成对 `-=`；`SystemEvents` 全局一次 |
| 定时器 | `_timer`、`_tooltipHideTimer`、`_skeletonTimer` 全部停止；骨架动画在 finally 移除 |
| 同步并发 | `SemaphoreSlim(1,1)` + `Wait(0)` 拒绝重入；每次 `using var store` |
| SQLite 连接/reader | 全部 `using`；`Pooling=false`，无池驻留 |
| 文件监听 | 无 `FileSystemWatcher`；仅一个单实例监听后台线程 |
| 位图/特效 | 无 `RenderTargetBitmap`/`BitmapCache`/`WriteableBitmap`/`BlurEffect`；唯一 `DropShadowEffect` 在 Popup |
| 集合通知 | 全项目未使用 `ObservableCollection` |
| DataGrid 虚拟化 | 主窗口 8 行、总账窗口在 `*` 行内，行虚拟化有效 |
| 会话详情 | 分页 100、`LIMIT/OFFSET`、上限 500 |
| 主题 | 只覆盖 15 个已 Freeze 的 brush，不 merge 新字典 |
| 迁移/清理 | `VACUUM INTO` 流式；`ClearStatistics` 单事务删除 |
| 图表点数 | 日粒度 400 天硬上限，小时粒度 ≤48 点，每点 slices ≤18 |

## 附录 C：环境与遗留问题

- `%TEMP%` 有 18 个 `tokenview-collectors-<pid>-*` 目录（2026-10-14 遗留）与 `tokenview-backup-before-v2.sqlite3`（58 MB）。属磁盘卫生问题，不影响 RAM；建议清理，并在启动时对 `tokenview-*.sqlite` 临时快照做一次过期清理（进程被杀时 `SqliteSnapshot.Dispose` 不会执行）。
- `%APPDATA%\com.tokenview.app\tokenview.log` 为旧实现遗留，当前 WPF 版没有日志写入。
- 本次探索未运行应用，因此 `256 MB 私有` 的具体拆分（托管堆 / native / 映射）仍需按 §5 第 0 步实测确认；本报告给出的所有百分比都是待验证的预期，不是既成结论。

---

## 7. 实施记录（2026-10-20，P0）

### 7.1 已落地

`dotnet build wpf/TokenView.Tests/TokenView.Tests.csproj`：0 警告 0 错误；`dotnet test`：34 通过、0 失败。

| # | 文件 | 改动 |
| --- | --- | --- |
| 1 | `Data/Collectors/CollectorSupport.cs` | `ReadJsonLines` 的 64 KB 读取缓冲与行缓冲都从 `ArrayPool<byte>.Shared` 租用；行内用 `Encoding.UTF8.GetString(ReadOnlySpan<byte>)` 解码，删除每行 `List<byte>` 增长与 `ToArray()`。CRLF、空行、无尾换行语义保持不变。 |
| 2 | `Data/Usage/UsageStore.cs` | `ReplaceClientSnapshot` / `MergeClientSnapshot` 入参改 `IEnumerable<SessionImport>`；仍是**单事务**；校验从"全量预校验"改为"逐条校验"（重复会话 id、路径归属、消息归属），失败整体回滚；Merge 的"逐会话删旧"并入同一循环。 |
| 3 | `Data/Collectors/ClaudeCodeCollector.cs` | `Stream()` 逐会话 `Finish` 并立即从累积字典移除，活集少一整份 `MessageRecord` 副本。 |
| 4 | `Data/Usage/UsageStore.cs` | `mmap_size` 256 MB → **32 MB**；新增 `journal_size_limit = 67108864`；新增 `Checkpoint()`（`wal_checkpoint(TRUNCATE)`，被并发读者占用时忽略）。 |
| 5 | `Data/Collectors/SyncEngine.cs` | 同步末（rollup 重建之后）调用 `store.Checkpoint()`。 |
| 6 | `Data/TokenViewDatabase.cs` | 只读连接 `Cache` 由 `Shared` 改为 `Private`（`Pooling=false` 下共享无收益）。 |
| 7 | `Services/SyncService.cs` | 每轮结束（`Completed` 之后）执行一次 `LargeObjectHeapCompactionMode=CompactOnce` + `GC.Collect(2, Aggressive, blocking, compacting)`。 |
| 8 | `MainWindow.xaml.cs` | `LoadAsync` 加重入门闩；加载期间的新请求合并为一次重跑（原方法体移到 `LoadOnceAsync`），避免并发多份 `OverviewSnapshot`。 |
| 9 | `Data/Usage/UsageStore.cs` | 会话流结束后按最新值**再写一次 `source_files`**：采集器可以把解析移进流内并更新 `UnparsedRecords`，水印不会丢更新（消息 id 用首次写入的结果）。 |
| 10 | `Data/Collectors/KimiCodeCollector.cs` | 解析移入 `Stream()`：逐会话读 `state.json` + `wire.jsonl` 并产出；文件集合校验提前到写库之前，**每个 wire 解析后立即复核指纹**；活集从"全部会话消息"降到"当前会话消息"。 |
| 11 | `Data/Usage/UsageStore.cs` | 路径比对统一用 `Path.GetFullPath`（目录枚举保留根路径的 `/`，而 `Path.Combine` 会规范成 `\`，此前用 `Ordinal` 比对会误判）；`source_file_id` 回填同样按规范化路径缓存查找，找不到时写 `NULL` 而不是 `0`（避免外键失败）。 |
| 12 | `wpf/TokenView.Tests/` | 新增 `CollectorSupportTests`（跨 64 KB 边界的 200 KB 行、空文件、尾换行）与 `UsageStoreSnapshotTests`（流式写入、迭代器中途失败整轮回滚、重复会话不回写、Merge 保留流外会话、混合分隔符路径可匹配）。 |
| 13 | `wpf/TokenView.Wpf/runtimeconfig.template.json` | 固定 `System.GC.ConserveMemory = 5`、`System.GC.RetainVM = false`；生成的 `TokenView.Wpf.runtimeconfig.json` 已验证包含这两个键。 |
| 14 | `ClientIcon.cs` / `ModelIcon.cs` | 按客户端键 / 模型 kind 缓存已解析并冻结的 `Geometry`，`OnRender` 只做变换与绘制，不再每次重绘 `Geometry.Parse`。 |
| 15 | `MainWindow.xaml.cs` | 同一数据点只重建一次 tooltip（`ReferenceEquals` 判等），鼠标段内移动只改浮层位置；离开图表时清空悬停点以便重新构建。 |
| 16 | `wpf/TokenView.Probe/Program.cs` | `sync` 结束打印 `peak_ws / ws / private / gc_heap / committed`，作为后续内存回归的固定钩子。 |

### 7.2 验证（只对副本库与脱敏 fixture 操作，未触碰生产库与客户端数据）

| 验证 | 结果 |
| --- | --- |
| 全量 `--force` 同步（副本库） | 60 s / 78 s / 85 s 三次（README 基线 1–1.5 min） |
| `request_hourly` 总量（改动后连续两次全量） | 与改动前基线**逐字节一致**（`diff` 退出码 0） |
| `messages` 行级比对（首次改动） | `only_in_work = 13`、`only_in_base = 0`；13 条全部是 DSH 活动会话的新增 |
| `source_file_id` 为空的消息 | 同步前后均为 **0** |
| 增量同步 | 36 s；totals 与上一轮逐字节一致；未变化客户端 `parsed=0` |
| WAL | 同步后 `-wal` 为 0 字节（`Checkpoint()` 生效） |
| 失败语义 | 迭代器中途抛异常 / 重复会话 id 用例均验证"库内无任何部分写入" |
| Kimi 流式对账（3 个项目目录的副本 fixture） | 93 会话 / 11,283 消息；与生产库比对：会话 token 差异 **0**、消息 token 字段差异 **0**、消息条数差异 **0** |
| GC A/B（`TokenView.Probe sync --force <client>`，单进程） | Codex：峰值 147 → **103 MB**、私有 109 → **59 MB**、堆 41 → 26 MB；DSH：340 → **179 MB**、私有 153 → **62 MB**、堆 118 → 28 MB；Devin：无变化（堆 14 MB）；耗时无回归（Codex 808–924 ms vs 821–869 ms，Devin 7.3–8.3 s 两档相同） |
| GUI 首屏（Release，采集完成后稳定 60 秒） | 私有 256 → **146–149 MB**、工作集 321 → **230–233 MB**；启动 18 秒内 177 / 280 MB，同步末 GC 后回落 |
| `dotnet build` / `dotnet test` | Debug + Release 均 0 警告 0 错误；34 个用例全绿 |

Kimi / DSH 在活动会话被写入时会返回 `ChangedDuringScan`（retryable）并保留旧数据——这是既有语义，不是回归；本次探索本身就在写 Kimi 的会话文件。

> 路径规范化问题是在 fixture 上暴露的**潜在缺陷**：生产根路径全是 `\` 所以从未触发；根路径含 `/` 时（例如通过环境变量注入）Kimi 的整客户端快照会因"源文件未登记"而失败。已修复并加回归用例。

### 7.3 未做与原因

- **其余 15 个采集器仍是"先物化再写"**（17 个用整客户端快照的客户端里，Claude 与 Kimi 已改为流式），分两类：
  1. **跨文件合并同一个会话**（PiAgent、WorkBuddy、CommandCode、Cline、Gemini、Grok、DSH）：`sessions` 字典以会话 id 聚合多个文件后再产出，单独 `yield` 会改变去重/取最新语义，需先按会话分组文件或引入"先扫描再流式"的两遍策略；
  2. **单库快照**（Devin、Copilot、OpenCode、MCode、MiMoCode）：读取函数一次返回 `List<SessionImport>`，改成迭代器要把"源读取错误"与"写库错误"分开分类（否则 `SqliteException` 会被误报成"无法写入用量库"），改动面大于收益。
  `source_files` 的写入顺序已不再是阻塞点（见 7.1 第 9 条）。
- **GUI 首屏已复测**（Release，启动采集完成后稳定 60 秒）：私有 **146–149 MB**、工作集 **230–233 MB**，
  对比改动前 256 / 321 MB；启动后 18 秒内为 177 / 280 MB（采集与首屏叠加），同步结束的 LOH 压缩与
  `ConserveMemory` 生效后回落。采样期间未交互、未联网。
- `LoadLedger` 分页、WinForms 剥离、`UpsertMessage` 的逐行 SELECT 合并为集合写入，仍未做（P1/P2）。

---

## 8. 收尾：未做项、理由与复访触发条件（2026-10-20）

P0/P1 清单中未落地的项，按"收益 / 风险 / 是否有验证手段"逐条记录，避免以后重复讨论：

| 项 | 状态 | 理由与复访触发条件 |
| --- | --- | --- |
| 其余 15 个采集器仍"先物化再写" | 不做 | 实测其托管堆已不是主因（Codex 26 MB、Devin 14 MB，GUI 稳定后私有 146 MB）；两类改造分别会改变"跨文件去重/取最新"或"源错误 vs 库错误"的语义。触发条件：某客户端历史再增长 3–5 倍，或出现 `OutOfMemoryException`；届时按 §7.3 的两类方案推进。 |
| Claude / DSH 行级增量（`content_offset`） | 不做 | 属"重解析频率"而非"峰值内存"；LOH 池化已把单次成本压下去。改动会变更水位语义，需要同步 `docs/data-model.md` 与 fixture 回归，收益不在内存维度。 |
| `UpsertMessage` 的逐行 SELECT 改集合写入 | 不做 | 只能减少往返与 gen0 分配（CPU），不降峰值；ConserveMemory 后堆已降到 26 MB 量级。触发条件：全量同步耗时超过 2 分钟。 |
| `LoadLedger` 分页 / 聚合下推 SQL | 不做 | 本机 1,220 个会话约 1–2 MB，且只在打开总账时占用、关窗即释放。触发条件：会话数达到 1 万级，或总账窗口打开时间超过 300 ms。 |
| 剥离 `UseWindowsForms`（托盘） | 不做 | 预计 10–20 MB，但需改用 `Shell_NotifyIcon` + 消息窗口重写托盘，菜单行为只能人工验证；当前 146 MB 已接近 WPF + .NET 桌面应用的地板。触发条件：有可交互的验证环境（能在托盘上点开菜单）时再做。 |
| ReadyToRun / trimming | 不做 | 只影响 JIT 内存（几 MB）；WPF 不支持完整裁剪，单文件发布也不省内存。 |
| GUI 首屏复测 | 已完成 | 私有 146–149 MB / 工作集 230–233 MB（Release，采集完成后稳定 60 秒）。 |

### 8.1 最终验收快照

| 项 | 结果 |
| --- | --- |
| `dotnet build wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release` | 0 警告 0 错误 |
| `dotnet test wpf/TokenView.Tests/TokenView.Tests.csproj -c Release` | 34 通过 |
| `TokenView.Probe sync` 对账 | 全量重扫后 `request_hourly` 与改动前基线逐字节一致；Kimi fixture 93 会话 / 11,283 消息零差异 |
| 常驻内存 | 256 → **146–149 MB** 私有；321 → **230–233 MB** 工作集 |
| 采集峰值 | Codex 147 → 103 MB；DSH 340 → 179 MB；Devin 91 → 91 MB |
