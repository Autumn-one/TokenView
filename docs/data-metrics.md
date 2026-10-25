# 数据统计规范

会话检索与下钻（2026-10-13）只筛选已有元数据，不新增口径版本或重算历史值。默认日期依据会话开始时间；从请求统计进入时改为筛选区间内存在非估算、时间已知请求的会话，端点仍为起点包含、终点排除。项目按会话元数据精确匹配（包括 NULL 组）；模型精确匹配覆盖会话主模型与请求级模型（存在该模型请求的会话）；列表总量始终为完整会话累计，不能与区间请求合计直接对账。搜索只匹配项目、会话主模型和会话 ID，不读取正文。

本文是 TokenView 统计口径的唯一事实来源。任何数字在进入界面之前，都必须能回答：
**数据从哪来、属于哪个等级、如何计算、时间范围是什么、单位是什么**。
新增或修改指标时，必须同时更新本文与 `docs/ui-guidelines.md` 中对应的展示要求。

## 1. 范围

存储位置迁移（2026-10-13）只移动 TokenView 自有统计数据库与设置，不重新计算数据，不改变 Token 等级、来源记录、去重键或成功采集水位。
迁移由用户显式触发；等待采集完成后使用包含已提交 WAL 的一致快照，验证成功再切换，失败保留原数据与位置。系统应用目录只保留小型位置记录及运行时管理的窗口状态等文件。

统计对象（客户端）当前为：**Codex、Claude Code、OpenCode、DSH、Kimi Code、MiMoCode、Pi Desktop、Pi Agent、WorkBuddy、GitHub Copilot、Gemini CLI、Grok CLI、MCode、Devin、Command Code、Cline、Qoder**，后续可扩展。
统计维度：会话、请求、token、模型、成本、时间分布、客户端与项目维度。
统计范围限于**本机会话/日志文件**，不涉及任何远端数据或账号数据。

## 2. 术语

| 术语      | 含义                                                         |
| --------- | ------------------------------------------------------------ |
| 客户端    | 产生数据的 AI 编程客户端（Codex / Claude Code / OpenCode）   |
| 会话      | 客户端记录的一次对话/任务单元，具有稳定标识                  |
| 事件      | 归一化后的最小统计单位，通常对应一次请求或一次响应           |
| 请求      | 一次模型调用                                                 |
| token     | 模型计的输入/输出/缓存 token 计数，按类别分列，不混合求和    |
| 成本      | 客户端自报或按价格表换算的金额（当前阶段仅保存客户端自报值） |
| measured  | 真实采集：客户端文件中直接记录的字段                         |
| derived   | 派生：由真实字段直接换算（如 token 总量 = 输入 + 输出）      |
| estimated | 估算：客户端未记录、由间接信息推算（如按字符数估算 token）   |

## 3. 数据来源与等级

每个字段、每个指标都必须标注等级，规则如下：

| 等级      | 判定标准                                         | 展示要求                                 |
| --------- | ------------------------------------------------ | ---------------------------------------- |
| measured  | 直接读取客户端文件中记录的数值                   | 正常展示，可在 tooltip 标注来源文件/字段 |
| derived   | 由 measured 字段经确定性公式计算（无猜测系数）   | 正常展示，tooltip 说明计算方式           |
| estimated | 含假设或经验系数（字符数、平均长度、价格推算等） | 必须显式标注"估算"，样式上与实测区分     |

强制要求：

1. 不允许把 estimated 与 measured 相加后作为一个数字展示；需要合并时必须拆分呈现。
2. 缺失值用"缺失"表达（UI 显示 `—`），不得用 0 代替。
3. 任何估算规则必须写在本文第 7 节，并配有单测。

## 4. 归一化事件模型

存储列与统计口径字段统一使用 `snake_case`（SQLite 列名即下表字段名，C# 记录属性按 .NET 命名一一对应，界面直接读取领域记录，不再有独立的跨端转换层），字段含义与等级如下：

| 字段                                       | 类型               | 必需 | 等级     | 说明                                                                                                                                                                                                           |
| ------------------------------------------ | ------------------ | ---- | -------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `client`                                   | string             | 是   | measured | `codex` / `claude_code` / `opencode` / `dsh` / `kimi_code` / `mimocode` / `pi_desktop` / `pi_agent` / `workbuddy` / `copilot` / `gemini_cli` / `grok` / `mcode` / `devin` / `command_code` / `cline` / `qoder` |
| `session_id`                               | string             | 是   | measured | 客户端提供的会话标识，缺失时用文件路径哈希兜底                                                                                                                                                                 |
| `event_id`                                 | string             | 是   | derived  | 会话内序号/记录哈希，用于去重（落库列 `dedup_key`）                                                                                                                                                            |
| `timestamp`                                | ISO 8601（含时区） | 是   | measured | 事件时间（落库列 `created_at_ms`，Unix 毫秒）                                                                                                                                                                  |
| `model`                                    | string?            | 否   | measured | 模型标识，缺失时按"未知模型"处理                                                                                                                                                                               |
| `input_tokens`                             | long?              | 否   | measured | 输入 token                                                                                                                                                                                                     |
| `output_tokens`                            | long?              | 否   | measured | 输出 token                                                                                                                                                                                                     |
| `cache_read_tokens` / `cache_write_tokens` | long?              | 否   | measured | 缓存读写 token，单列统计                                                                                                                                                                                       |
| `cost_usd`                                 | double?            | 否   | measured | 当前仅保存客户端自报值（OpenCode）；价格换算未实现（落库列 `client_cost_usd`）                                                                                                                                 |
| `source`                                   | string             | 是   | -        | 该事件整体可信等级（落库列 `source_kind`：`measured` / `derived` / `estimated`）                                                                                                                               |
| `confidence`                               | double?            | 否   | -        | 估算置信度（0–1），仅 estimated 事件需要；当前已用于 Qoder CLI 占位 usage（E5），置信度记录在第 7 节，暂不落库                                                                                                  |

字段可以不落库，但含义与等级必须稳定；新增字段时在本文补一行。

## 5. 指标口径

### 数据源诊断（2026-10-13，不改变 Token 口径）

数据源诊断独立于页面日期筛选，描述全部已导入本地记录及本次应用运行的扫描状态，不是客户端全部真实活动的完整率。
“Token 总量未知会话”为该客户端 `sessions.total_tokens IS NULL` 的会话数（measured 元数据）；真实 0 不纳入未知。
“最近用量记录”为该客户端 `messages.created_at_ms` 的最大非 NULL 值，仅纳入 measured / derived 请求，排除 estimated；没有原生时间保持未知，不以会话开始 / 结束、导入或当前时间补造。Grok 的原生明细时间仍为轮结束时间。
“最近导入”沿用 `source_files.last_scan_ms` 的最大成功解析水位，可能仅导入元数据；跳过未变化文件、缺失来源和失败读取不推进，不等同最近请求时间。
“最近检查”是本次运行该客户端最后完成扫描的时间，包括缺失与失败，保存在内存，重启后未知；根路径可访问仅验证根目录 / 文件，不代表全部记录可读取或可解析。
不存在、读取失败、部分解析、有会话但 Token 未知分别提示；失败保留已有统计，单客户端重扫继续遵守现有幂等与源只读规则。脱敏诊断仅在用户点击复制时写入本机剪贴板，不含路径、会话标识、项目、错误原文和正文。

同步状态分级（2026-10-19）：`ChangedDuringScan`（源数据在扫描期间变化）为可重试错误，不把整次同步标为失败；该客户端保留上次成功快照，其他客户端继续导入。只有读取失败、数据库错误等硬错误才设置整体同步错误。

每个指标必须同时声明五要素：**定义、单位、时间范围、来源等级、计算方式**。当前登记如下（状态列区分数据模型、采集器与查询的落地程度）：

| 指标                   | 单位  | 时间范围 | 等级                   | 计算方式                                                                                    | 状态                                 |
| ---------------------- | ----- | -------- | ---------------------- | ------------------------------------------------------------------------------------------- | ------------------------------------ |
| 会话数                 | 个    | 选定区间 | measured               | 去重后的 `session_id` 数量                                                                  | 十八客户端采集与查询已实现           |
| 请求数                 | 次    | 选定区间 | measured               | 事件条数（去重后）                                                                          | 十八客户端采集与查询已实现           |
| 输入 token             | token | 选定区间 | measured / derived     | 未命中缓存的输入分桶求和（Codex 需扣除 `cache_read`）                                       | 十八客户端采集与查询已实现           |
| 输出 token             | token | 选定区间 | measured / derived     | 非推理输出分桶求和（Codex / DSH 扣除 `reasoning`）；缺失保持未知；Qoder CLI 占位 usage 按 E5 估算并单列           | 十八客户端采集与查询已实现           |
| 缓存 token             | token | 选定区间 | measured               | `cache_read_tokens` / `cache_write_tokens` 独立分桶；`cache_write` 是否计入总量按客户端口径 | 十八客户端采集与查询已实现           |
| 成本                   | USD   | 选定区间 | measured（客户端自报） | 仅 OpenCode 客户端自报值；TokenView 价格换算未实现（见 `docs/data-model.md` 第 6 节）         | OpenCode 自报值已采集；UI 金额未开放 |
| 活跃时长               | 小时  | 选定区间 | estimated              | 相邻事件间隔去尾求和（见第 7 节）                                                           | 未实现                               |
| 按客户端/模型/项目分布 | %     | 选定区间 | derived                | 已知规范用量（measured / derived，排除 estimated）子集 ÷ 已知总量                           | 十八客户端采集与查询已实现           |

已接入 Codex、Claude Code、OpenCode、DSH、Kimi Code、MiMoCode、Pi Desktop、Pi Agent、WorkBuddy、GitHub Copilot、Gemini CLI、Grok CLI、MCode、Devin、Command Code、Cline、Qoder、CodeArts。新采集器的格式、快照 / 去重与限制见 `data-model.md` 第 10–23 节。
默认使用请求时间口径，详见第 9 节 2.3；会话累计口径保留在“会话视角”次级区块与“会话总账”弹窗，
按会话开始时间过滤，其趋势按会话开始日期归属且仍使用固定 24 小时桶（不表示该日实际请求活动）。

口径细则：

- **Token 字段语义（实测，禁止重复求和）**：Codex 的 `cache_read` 属于 `input`、`reasoning` 属于 `output`，
  客户端总量 `total = input + output`；Claude Code 的 `cache_read` / `cache_creation` 与 `input` 互不重叠
  （本机存在 `cache_read > input` 的记录）且无总量字段；OpenCode 的 `total = input + output + reasoning + cache.read`。
  换算公式、未确认项（如 Codex/OpenCode 的 `cache_write` 与 `input` 关系）见 `docs/data-model.md`。
- **去重与幂等**：Codex 按 `thread_id` 取最大累计值（多个 rollout 文件不得相加）；Claude Code 按
  `(sessionId, message.id)` 去重（同一消息最多被重复写入 15 次）；OpenCode 按 `message.id`，且 `session` 与
  `session_v2` 按 `time_updated` 取新、不得相加。Codex 增量扫描依据文件 `mtime` + 大小 + 解析版本，变化时整文件重解析并覆盖该文件明细。
- **时区与边界**：按系统本地时区聚合；"今天/本周/本月"按本地日历边界计算，跨时区展示需注明时区。
- **成本与价格表（待实现）**：价格表需内置、本地、版本化并含生效日期；未匹配价格的模型显示"未定价"并在 UI 提示，
  不得静默按 0 处理。价格表更新视为口径变更（见第 9 节）。当前阶段成本只保存客户端自报值，不做换算。
- **成本与 Token 解耦**：价格表落地前不计算成本，只保存客户端自报值（OpenCode）；未定价模型保持 NULL 并在 UI 提示。
- **取整与显示**：token 取整；成本保留 4 位小数，`<$0.01` 时显示 `<$0.01`；百分比保留 1 位小数。
- **缓存 token 口径**：缓存读写作为独立分桶；Codex 的 `cache_read` 需先从 `input` 中扣除再分桶，
  客户端提供总量时采用客户端值并校验分桶之和；否则由完整的必需分桶求和（见 `docs/data-model.md` 第 1 节），禁止对存在包含关系的字段重复求和。

## 6. 数据清洗

- 不修改原始文件：清洗只发生在内存中的归一化阶段。
- 无法解析的记录：跳过并计数，在界面显示"未识别记录 N 条"，不猜测字段含义。
- 当前时间解析仅支持 ISO 8601 UTC `Z` 形式，无法解析时保持缺失。按日期范围识别异常时间戳
  （早于 2023 或晚于当前时间 + 1 天）与计数上报尚未实现，不宣称已过滤。
- 重复记录：按去重键处理（Codex `thread_id` 累计值取最大、`response_id` 为消息键；Claude Code `(sessionId, message.id)`；
  OpenCode `message.id`），以绝对值快照 upsert，不重复计入统计；陈旧保护与覆盖式导入见 `data-model.md` 第 5、8 节。

## 7. 估算规则（必须可追溯）

每条估算规则必须写明：适用条件、公式、系数来源、置信度、生效日期，并有单元测试。

| 编号 | 适用条件               | 公式 / 依据                                     | 置信度       | 状态                             |
| ---- | ---------------------- | ----------------------------------------------- | ------------ | -------------------------------- |
| E1   | 客户端未记录输出 token | `字符数 ÷ 4`（英文为主的经验值）                | 0.5          | 未实现                           |
| E2   | 客户端未记录输入 token | 同上，按提示词字符数估算                        | 0.5          | 未实现                           |
| E3   | 成本换算               | 价格表单价 × token 数（价格表版本需随结果展示） | -（derived） | 未实现（当前只保存客户端自报值） |
| E4   | 活跃时长               | 事件间隔 ≤ 30 分钟的相邻事件间隔之和            | 0.7          | 未实现                           |
| E5   | Qoder CLI assistant 记录的 usage 全为占位 0，且同条带 `context_usage_ratio` | `input = round(ratio × 200 000)`；`output = ceil(输出字符数 ÷ 4)`，字符数取 `content` 字符串、`text` / `thinking` 块与 `tool_use.input` 的原始 JSON 长度；`cache_read` / `cache_write` 置 0 仅为让总量可求和 | 0.8          | 已实现（口径 2.20）              |

约定：

- 估算结果必须携带 `source = estimated`；规则置信度记录在本表，暂不落库。
- 估算公式变更必须更新本表并说明对历史展示的影响。
- 界面不得自行做估算，估算只在 `Data/Usage/` 的归一化与查询层完成，并随记录返回来源等级。

## 8. 数据质量与校验

- **对账**：每个客户端适配器上线前，抽样与客户端自身统计对比，记录差异率、差异原因（如缓存口径、时区）。
- **fixture**：解析与聚合的边界验证使用**合成数据**（未知字段、字段缺失、空文件、异常时间戳等）。
  仓库当前的 xUnit 项目 `wpf/TokenView.Tests` 只覆盖 Services 层，采集与聚合口径的验证按
  `AGENTS.md` 第 7 节通过 `wpf/TokenView.Probe` 在数据库副本上重跑同步与总量查询完成；
  新增测试框架或 fixture 目录前先在 `docs/architecture.md` 登记。
- **覆盖率优先项**：估算公式（E1/E2/E4）、去重逻辑、时间边界聚合必须有测试。
- **不做**：依赖真实数据目录、当前时间或网络的自动化测试。

## 9. 口径版本与变更

- 本文维护一个口径版本号，格式 `MAJOR.MINOR`，当前为 `2.20`。
- 破坏性变更（去重规则、token 计入口径、价格表来源、时区处理）升 `MAJOR`；新增指标或字段升 `MINOR`。
- 每次变更需在提交说明中记录：变更内容、原因、对历史数据展示的影响。
- 界面展示的统计需与本文版本一致；发布说明中注明本次口径版本。
- 统计报告导出（2026-10-16 起已移除）曾使用导出模块里的 `METRICS_VERSION` 常量；该功能删除后，口径版本以本文为准，不再有独立常量载体。

### 统计报告导出（2026-10-13，口径保持 2.12；2026-10-16 功能已移除）

仅由用户显式触发本地 CSV / JSON / Markdown 导出。按已导入 `messages.created_at_ms` 精确筛选，起点包含、终点排除；无时间请求不补造日期，单独记录所选客户端全历史的未知时间请求数，不能将此数误解为所选日期内的缺口。
日期按 UTC 分组，筛选仍采用界面所选本地时间对应的 Unix 毫秒；报告写明 `dateBasis=UTC`。分组键为日期、客户端、会话主模型、项目和来源等级。主模型来自 `sessions.model`，不是逐请求模型；项目沿用客户端记录的项目路径，不输出来源文件路径、会话 ID、请求 ID、来源引用或正文。
每组请求数为去重入库后的明细行数，Token 为各已知规范字段求和；全部缺失保持未知，真实 0 保留。`requestsWithoutUsage` 标明总量未知请求数，`unknownTokenFields` 标明至少一条请求缺失的字段，数值可能是已知子集。总量不从分项重新推算，缓存写入与其它分项不能默认再相加。
实测、换算、估算各自成行；默认排除 estimated，显式纳入时仍不混加。排除的估算请求数仅指已知时间且符合所选日期与客户端的请求。Grok 请求数仍指轮数，日期使用原生轮结束时间 `turnEndedAt`。
JSON 以 null、CSV 以空单元格表示未知，Markdown 明确说明未知单元格的含义；CSV 的 `recordType=metadata` 行在空报告中仍保留版本、筛选和说明，数据行使用 `recordType=data`。CSV 为 UTF-8 BOM 并转义引号、逗号和换行，公式型文本加保护前缀；Markdown 转义外部元数据，JSON 保留原始元数据字符串。
本次不重算历史、不新增估算公式，不改变既有页面的本地日桶或去重规则。报告范围不受图表 400 日展示上限影响。

此前模块重构（2026-10-11）只调整文件组织，并修正文档中“最新累计值”“追加续读”和落地状态的过时说明。
统计公式、去重键、迁移与跨端返回结构不变，口径版本保持 `1.0`；测试使用原有合成记录验证行为。

### 1.1 — 新客户端与展示字段（2026-10-11）

新增 Claude Code / OpenCode 采集链路与 DSH 口径；Codex / Claude Code / OpenCode 的既有字段关系不变。
DSH 已知总量为 inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens，reasoning 属 output 子集，不重复相加；
省略可选缓存字段按 DSH 协议默认 0；必需 input / output 或整个 usage 缺失保持未知，不按字符或均值估算。去重键为 session header id + turn + step，排除 header.seedLength 内的继承事件。
新客户端完整快照允许反映源数据修正 / 删除，读取失败时不更新历史；Codex 仍保留累计值不回退规则。
新增 `countedCacheWriteTokens`（仅计入总量的缓存写入）与后端客户端占比 `shareBps`（已知总量子集 ÷ 已知总量，基点整数向下取整）。
`cacheWriteTokens` 仍保留全部客户端自报缓存写入，混合图表只能堆叠 counted 子集。
缺失必需字段的请求使会话相应字段未知；不得将部分已知字段当成完整会话总量。

### 1.2 — Kimi Code 采集口径（2026-10-11）

新增 `kimi_code.usage_record.v1`。`usage.record` 是增量记录，turn / session scope 均计入；
总量为 inputOther + output + inputCacheRead + inputCacheCreation，四桶独立，reasoning 不单列。
缺少任一必需桶时总量保持未知；合法 0 与缺失区分，不估算。
会话键为 state.id（旧版回退到 session_* 目录名），请求键为 agent 目录 + wire 物理行号，
相同内容的不同记录必须分别计数；重扫通过原子快照替换保持幂等。
排除上下文长度事件、插件状态与服务端镜像。缓存写入纳入 `countedCacheWriteTokens`。
旧客户端口径及数据库 schema 不变；不重算旧客户端历史数据。格式与限制见 `data-model.md` 第 11 节。

### 1.3 — MiMoCode / Pi Desktop（2026-10-11）

新增 mimocode.message.v1 和 pi_desktop.operation.v1。两者会话总量按去重请求求和，缓存写入计入 countedCacheWriteTokens。
MiMoCode 持久化字段 input / output / reasoning / cache.read / cache.write 独立；有 total 时保留客户端总量，否则完整五桶求和。
按数据库 claude_import / external_import.message_ids 排除外部消息副本，保留本地续聊请求，避免与原客户端重复。
Pi Desktop input / 两个缓存独立，reasoning 包含于 output；物理 operationId 去重，展开 operations 后不叠加父聚合，legacy 残差单独保留。
估算成本不冒充 measured，不导入这两种客户端的成本字段；不新增价格公式。
旧客户端口径及 schema 不变；旧客户端历史数据不重算。采集限制与去重规则见 data-model.md 第 12 节。

## 10. 隐私与安全

默认保护本地会话隐私，以下为硬性要求：

1. **只读**：不修改、不移动、不删除客户端数据文件。
2. **不采集正文**：默认不读取、不保存、不显示消息正文；解析时只提取第 4 节的元数据字段。
3. **不落盘**：统计结果与缓存仅存元数据，不复制原始会话文件到应用目录；统计报告导出功能已于 2026-10-16 移除，界面不再提供落盘入口。
4. **无网络出口**：除 `docs/architecture.md` 登记的两个固定公开入口外，默认不发起任何网络请求、不上报遥测。
   更新检查仅在手动或主动开启自动检查后访问固定 GitHub 发布源；公告/关于在启动时及每 20 分钟从固定公开镜像列表
   GET Markdown。两者都不携带本地用量、项目路径、会话标识或正文；公告/关于不加载远程图片/HTML，正文只存内存，
   仅持久化已读哈希。
5. **不写日志正文**：日志、错误信息、崩溃信息不得包含会话正文与完整用户路径。
6. **可清除**：设置页提供统计缓存清除：等待当前扫描结束后，事务删除 messages / sessions / source_files，保留迁移、设置和客户端原始数据。清除前持久化暂停采集，重启保持暂停；用户恢复后可从原记录幂等重建。
7. **无导出入口**：导出功能（2026-10-13 引入）已于 2026-10-16 移除；如日后重新引入，必须保持用户显式触发、不含会话正文，并说明文件名与保存位置。

采集控制（2026-10-11）：十八个客户端独立启停，停用仅停止未来采集，不过滤或删除历史统计。
默认启动采集及每 5 分钟后台采集；间隔可为关闭 / 1 / 5 / 15 / 30 分钟。暂停优先于启动、定时和手动采集。
同一时间仅允许一个扫描；恢复系统休眠后最多触发一次，不补跑积压任务。此行为不修改 Token 口径，版本仍为 1.3。

### 2.0 — DSH 版本化日志与重试修正（2026-10-11）

修复正式 `session.v1`–`session.v4` 日志被误当备份忽略的问题；同目录最高代优先，迁移前后不重复相加。
v0/v1 继承排除使用 seedLength；v2–v4 使用 isSeeded / inherited end-seed，支持内嵌 stream 与 assistant/attempt。
重试请求去重键增加 attempt 后缀，因此按去重规则变更升级主口径版本。Token 四桶公式与 `dsh.token_usage.v1` 字段语义不变。
DSH 解析器版本为 2，重扫后此前漏掉的会话和重试费用对应 Token 会纳入统计，历史数值可能上升；不做估算或补造缺失 usage。
单文件失败不再阻止健康会话导入；失败水位及历史保持，完整成功扫描才清理删除来源。所有兼容 / 去重测试使用合成数据。

### 2.1 — 逐日完整度展示字段（2026-10-11）

每日趋势及客户端逐日条目新增总量未知会话数与请求明细不完整会话数，客户端日条目同时补充会话数。
仅增加完整度元数据；现有 Token 求和、去重、日期归属、请求数与历史数值不变，无 schema 或迁移变化。
日历热力图曾支持总 Token（按客户端口径归一化的 measured / derived 已知求和）、会话数与去重请求数（measured）；
客户端 × 日期热力图曾展示已知总 Token。该界面已于 2026-10-16 移除，下述完整度字段仍由趋势图使用，沿用会话开始日归属，不表示该日请求实际发生时间。
起点为调用方传入的本地零点，每桶固定 24 小时；夏令时跨日偏差仍存在，本次不调整时区实现。
无会话日期是零活动；有会话且全部 `total_tokens` 为 NULL 时展示 `—`，与真实 0 区分。
混合已知 / 未知时显示已知子集与未知会话数，不估算缺失部分；请求明细不完整单独标注，不能据此判定 Token 总量未知。

### 2.2 — 精确日期时间范围（2026-10-11）

自定义范围按本地日期时间输入到分钟，开始分钟包含、结束分钟包含；查询转换为 UTC 毫秒半开区间 `[开始分钟, 结束分钟 + 1 分钟)`。
总览及其下图表统一按会话 `started_at_ms` 筛选；请求数仍归属于所选会话，即使请求自身时间位于范围外。
每日图表先按精确区间筛选，再按传入本地零点起的固定 24 小时桶归属，首尾部分日期不包含范围外会话。
自定义范围确认后固定；滚动预设使用应用统一的当前时间更新，本周从本地周一开始、本月从本地月初开始。
每日图表最多展示所选范围最近 400 天，界面注明实际展示日期与截断；区间指标仍覆盖完整所选范围。日历独立的最近 365 天模式保持不变。
本次扩展范围选择能力，不修改 Token 公式、来源等级、去重键、返回结构或迁移；旧每日查询参数继续兼容。测试仅使用合成数据。

### 2.3 — 请求时间统计与元数据会话详情（2026-10-11）

总览主视图固定使用“请求时间”；“会话累计”口径保留在会话视角区块与“会话总账”弹窗，不再提供页面级切换（2026-10-16 单页收束后不再有独立的用量分析页）。请求时间按
`messages.created_at_ms` 的半开区间 `[fromMs, toMs)` 查询，仅纳入 `measured` / `derived` 记录；
缺失请求时间及 `estimated` 记录不进入日期统计，不借用会话时间、完成时间或当前时间补造。
会话累计仍以 `sessions` 为唯一累计 Token 事实来源；请求时间仅对 `messages` 求和，二者不相加，也不互相兜底。
因此跨日会话、缺失请求明细及累计快照可能使两种口径数值不同，不能宣称请求求和就是完整会话累计。

请求总量只求和已记录的已知 `total_tokens`，不在查询阶段由分项补总量；分项各自对已知字段独立求和，
不因该请求总量未知而丢弃已知分项。无已记录请求日期显示 0，并说明不代表客户端没有实际活动；存在请求但全部总量未知显示 `—`；
已知与未知混合时显示已知子集及未知请求数。真实 0 保持 0，不把 NULL 当成实测零，不估算缺失部分。
区间会话数为区间内有合格请求的去重会话数；每日会话数在各日独立去重，跨日会话可重复出现，不能相加得到区间会话数。

区间汇总覆盖完整所选范围，每日展示最多最近 400 天；调用方传入连续本地日历零点边界，
请求查询不按固定 24 小时整除，夏令时日期可为 23 / 25 小时。首尾日期同时受精确范围约束。
覆盖信息对所选客户端 / 项目的全部已导入日期计数，与页面日期范围无关：已记录请求、有时间的非估算请求、
缺少时间的非估算请求、估算请求、明细 partial 会话和无请求会话。它仅说明库中记录情况，不是客户端全部真实请求的完整率，禁止显示完整度百分比。

会话详情按 `(client, client_session_id)` 唯一定位，忽略全局日期与项目筛选，包含该会话所有日期的请求元数据。
每页默认 100 条、最多 200 条，使用 offset 分页；按创建时间、序号、稳定 ID 排序，缺失创建时间排在最后。
详情保留归一化 Token 的 NULL、来源等级、来源定位与未知关系，明确累计值和请求记录各自来源；
覆盖计数针对整个会话而非当前页。来源定位只用于溯源，不读取源文件正文，不提供 prompt、回答或工具正文。
不新增 schema、迁移、依赖、权限或网络能力；时间边界、未知值及分页回归使用合成数据。

托盘今日摘要（2026-10-12）复用上述 2.3 请求口径：本地当天零点至当前显示分钟的下一分钟，排他终点。
不受页面日期与客户端筛选影响，覆盖当前用量库全部客户端；不回填会话累计值，不包含估算或缺失时间记录。
无已记录请求时 Token 显示“未记录”、请求数显示 0；有请求但全部总量未知显示“未知”，混合时明确“已知子集”和缺失计数。
摘要只作现有口径的快捷展示，不新增统计公式或口径版本。

展示调整（2026-10-19）：仅调整界面入口，请求时间与会话累计的查询、筛选、求和与去重不变；两种口径仍不可相加或互相兜底。

### 2.4 — 请求用量分析（2026-10-12）

今日小时趋势（2026-10-13）复用 2.3 请求时间统计：界面提供本地当天零点至显示分钟下一分钟的连续小时边界，
查询按半开区间聚合；末桶仅包含查询截止前记录，未来小时不填零。小时桶沿用已知总量求和、未知请求数与来源计数，
不由分项补总量、不以会话累计回填。夏令时重复 / 跳过小时按实际时间边界保留，不假定一天总是 24 个桶。
无记录显示 0 并注明未记录，不代表没有真实活动；全未知显示 —，混合只显示已知子集。现有采集覆盖不能确认昨日同期完整，
因此不计算同比或涨跌。只新增既有口径的小时展示，不改变统计公式、数据库 schema 或口径版本。

分析页复用 2.3 的请求时间与来源过滤：完整区间的客户端比较及模型 / 项目归因，不以截断每日桶求和。
请求均量（derived）＝已知 total_tokens 求和 ÷ total_tokens 非 NULL 的请求数；分母为零时未知，真实零均量保留。
输入缓存命中率（derived）＝缓存读取求和 ÷（未命中输入求和＋缓存读取求和），只使用 input_tokens 和 cache_read_tokens 同时非 NULL 的请求；提供参与请求数，分母为零时未知。
客户端与来源占比（derived）使用完整区间已知总量作为分母；所有总量未知或分母为零时占比未知。模型返回全部分组，项目 Top 8 不改变分母。
模型按请求记录模型归属（`messages.model`），请求未记录模型时回退会话层记录的模型（`sessions.model`；Kimi Code 该字段取自最新一条带模型的请求，不保证来自 main 智能体）；项目按会话目录归属；NULL 分组保留。客户端比较不受下钻客户端影响，分项与排行受其影响。
已知分项独立求和，并提供字段是否有记录；没有已知记录显示未知，真实零仍显示零。分项不补造未知总量，不推算成本、效率或生产力。
所有查询使用同一只读事务快照。本次新增直接换算与归因展示，不改变已有总量、Token 桶、去重键或数据库 schema，不重算旧数据。

模型浏览调整（2026-10-12）：解除分析模型列表的 8 条展示限制，按客户端选择并展开已有分项及来源计数；项目仍为 Top 8。仅改变返回分组数量与呈现方式，过滤、求和、占比分母及历史数值不变，不新增口径版本。

模型归属调整（2026-10-13）：模型分组改为请求实际记录的模型（`messages.model`），请求未记录模型时回退会话主模型（`sessions.model`），使
Kimi Code 子智能体等逐请求模型形成独立分组；会话列表的精确模型筛选同步匹配请求级模型（`EXISTS messages.model = ?`），搜索仍只匹配会话主模型。（该会话列表筛选入口于 2026-10-16 随页面删除，查询参数与口径未变。）
模型筛选不受日期范围限制，与会话元数据筛选一致。
总览模型排行（`sessions.model`）与已移除的导出报告都按会话主模型，未改变。历史区间的模型分组与各组数值按新归属在查询时即时重算，总量与占比分母不变，不重写已存储数据。

### 2.5 — Codex 无 ordinal 日志兼容（2026-10-12）

Codex 的两种用量事件不再要求外层 `ordinal`。此前此类文件只导入会话元数据，丢失已有的请求用量与累计快照；
本次兼容以合成日志复现和验证，不代表已核验用户公司电脑的日志。Token 字段仍来自客户端真实计数，分桶公式与来源等级不变。

已有 `response_id` 与 `token_count:<ordinal>` 去重键保持不变；仅为此前无法导入的记录增加身份回退：
无两种原生标识时，若累计 `total_tokens` 已知，则按 input / output / cache_read / cache_write / reasoning / total 六个累计字段生成键（未知值用 `?`，不等同零）。
同会话相同累计快照只计一次；同文件重复通知保留首次观测时间，包括新格式镜像的重复通知。
累计增长但请求增量相同仍是两个请求。累计值仍取最大快照，不求和。
该回退依赖客户端累计计数稳定；没有原生标识时无法辨别计数器重置后的相同快照或累计不变的零用量请求，不能宣称请求明细完整。

无已知累计总量时回退为源文件绝对路径 + 从 1 开始的物理行号。追加 / 重扫幂等，不同文件不碰撞；
此时无法识别跨文件复制记录，不用 Token 增量相等猜测重复。没有原生序号的 `seq` 保持 NULL；
没有时间的请求仍不参与请求时间统计。没有 `ordinal 0` 的线程保持 `partial`，不得用文件行号伪造完整度。
旧格式的 `info` / `last_token_usage` 为 null 时作为状态通知忽略，不生成空用量请求。

Codex parser_version 升到 2，下一次同步自动重读原日志，历史展示可能补回此前漏导入的真实 Token 与请求，或清理 null 通知生成的空请求。
无需删除客户端日志或清空用量库；无数据库迁移、依赖、权限或联网变化。

### 口径 2.6（2026-10-12，纳入 v0.1.4）：会话聚合的未知值与缓存样本

- 会话、客户端及模型 / 项目总量继续只累加已知值；客户端与排行额外返回总量未知会话数。空集合显示无记录，有会话且总量全未知显示 —，已知零仍为 0，混合标记已知子集；未知不计入占比。
- 会话累计缓存命中率改为后端直接换算：只纳入 input_tokens 与 cache_read_tokens 同时已知的会话，用 SUM(cache_read) / SUM(input + cache_read)，返回基点及样本会话数。分母为零返回未知；缺字段的会话不进入分子或分母。输入是已扣除缓存的统一字段，不含输出。影响此前缺字段时的缓存比率，Token 合计不变。
- 模型 / 项目排行的请求明细数修复为组内全部会话请求数之和，之前可能只取一条会话；全未知组排在已知零之后。
- Codex 不完整尾行、I/O 中断、解析期间变化不更新成功水位也不替换旧明细；下次同步重试。已完整结束的损坏行继续计数跳过，合法无换行末行接受。仅用文件大小和修改时间复核，不能识别同大小同修改时间的替换。
- CODEX_HOME 优先于默认用户目录，支持 HOME 回退；只读 sessions，未扩展归档扫描。
- 会话列表日期仍按开始时间筛选，新增分页不改变跨日请求归属。（会话列表页于 2026-10-16 删除，查询本身保留。）

### 口径 2.7（2026-10-12，纳入 v0.1.5）：WorkBuddy 原生输出用量

- 只读 WORKBUDDY_CONFIG_DIR/projects，未配置时读取 ~/.workbuddy/projects 的 JSONL；只统计 message / assistant / function_call 中携带 usage 的模型调用。其他输出事件用于会话时间范围，不虚构缺用量请求；完全未记录的模型调用数量无法确认。
- providerData.usage 是原始归一化计数，message.usage 为落盘镜像；二者择字段补缺，绝不求和。inputTokensDetails.cached_tokens 是输入子集，outputTokensDetails.reasoning_tokens 是输出子集；规范输入 / 输出分别扣除这两个子集。显式零保留，缺失保持 NULL；不读取 rawUsage 中的 credit，不把 prompt_cache_miss_tokens 当缓存写入。
- 规范总量优先使用 totalTokens / total_tokens；缺失时仅在必需分桶全部已知时换算。cache_creation_input_tokens 关系未知，单独展示，不计入总量。客户端总量与分桶不一致或推理 / 缓存超出父字段时标记异常，保留客户端总量。
- 请求去重键为 (sessionId, id)，跨文件副本按事件时间取新；providerData.messageId、conversationRequestId、traceId 都可能复用，不能作为请求键。会话按去重后请求总量求和，任一请求总量未知时会话总量未知；明细聚合仍可呈现已知子集。
- 只扫描日志白名单，不采集标题、正文、工具结果、凭据、账户或上下文占用；反序列化忽略未知正文。合成测试覆盖镜像 / 副本去重、共享 provider ID、未知 / 零、超大计数、尾行和读取中断恢复。
- 完整扫描快照原子替换；不完整尾行、未知文件格式、读取失败与文件集合 / 指纹变化不提交，保留旧数据和水位。已识别格式中的完整损坏行计数并使覆盖标为 partial。无数据库迁移、依赖、权限或默认联网变化；其他七客户端的口径不变。

### 口径 2.8（2026-10-12）：Copilot CLI 原生 SQLite 用量

- 只读 COPILOT_HOME/session-store.db（默认 ~/.copilot），查询 sessions 和 assistant_usage_events 元数据；会话键 sessions.id，请求键 usage:<用量表主键>。turn_index 和 agent_id 不作为去重键，一轮可以包含多次模型调用。
- 原生 input 包含缓存读写；规范输入为 input − cache_read − cache_write，输出拆出 reasoning。总量由原生 input + output 直接换算，领域标记 derived_from_buckets，请求来源等级为 derived，不冒充客户端自报总量。缓存写入纳入 countedCacheWriteTokens。
- 输入 / 输出缺失或总量超出 SQLite 有符号范围时总量未知；缓存缺失时输入分桶未知但仍保留已知总量，异常缓存关系可见，不用零补值。无用量的会话保持未知，不将 session.shutdown.currentTokens 等上下文占用当作用量。
- 同步使用只读 WAL 可见事务和完整快照替换，修正 / 删除可反映，源缺失 / SQL 失败保留历史与水位。旧库没有用量表时仍导入会话，明细完整度无法确认。
- premium requests、AIU、费用、正文和 token_details_json 不采集；时间支持 UTC ISO 8601 与 SQLite UTC datetime 默认形式。无时间请求不进入请求时间统计。
- 不改变旧客户端口径或 schema；本机 Copilot CLI 1.0.91 / schema 8 有 3 个会话、0 条用量记录，shutdown.modelMetrics 为空，与请求数一致。正数 Token 暂无本机样本可对账；公式依据安装包 FJt 用量换算契约并以合成用例验证，不宣称完成正数实测对账。

### 口径 2.9（2026-10-12）：Copilot Agent Host 整轮用量

- 修复只读取 CLI session-store.db 导致 VS Code Agent Host 会话漏采。Windows 默认发现 APPDATA/Code/agentSessionData/_/session.db，并关联 COPILOT_HOME/session-state/_/events.jsonl；不扩大到普通 Copilot Chat workspace 历史、其他编辑器或无 SDK 事件的 Host 数据。
- 会话键 SDK sessionId；通过 provider sdkSessionId / turns.event_id / user.message.id 关联。请求键 apiCallId；镜像轮级聚合按 SDK 会话 + 原始用户事件择一，不跨源叠加，也不把轮数冒充 API 请求数。
- 会话使用 turnTokenTotals 聚合；顶层和 vscode.modelCall 是最后一次调用，直接补其对应请求，不与轮总量相加。不采集 directTurnTokenTotals、contextAttribution、cost、当前上下文或 premium requests。
- 零缓存条件下原生 input + output 为 derived 总量；原始分项为客户端实测，聚合与相加为直接换算。缺失 reasoning / cache_write 保持未知并标注；非零缓存的包含关系仍待验证，不推断总量。缺用户轮用量时会话总量保持未知；请求明细为 partial，未提供 Token 的 API 调用仍可计身份数。
- 会话趋势仍按会话开始时间归属；请求活动按事件时间归属，不能把整轮总量均分到调用或日历桶。请求统计只显示已知子集，因此可小于会话 Token 总量。
- 本机实测一个 Host SDK 会话含 3 个用户轮、10 个独立 apiCallId；整轮 input=314042、output=3372、缓存=0，直接换算总量317414。顶层最后调用合计仅 input=98680、output=2353，不能代替整轮；不宣称取得了全部逐请求 Token。
- parser_version 升至2，下一次同步重读补采；SQLite/WAL 只经 `SqliteSnapshot` 的只读临时副本读取，客户端主库 / WAL / SHM 不修改。失败或扫描中变化不推进水位；无新依赖包、迁移、权限或网络能力。

### 口径 2.10（2026-10-13）：Gemini CLI 变更日志

- `gemini_cli.response.v1`，只读 tmp 项目 chats/session-*.jsonl 与必要 .project_root 元数据，排除 antigravity。安装包 recorder 映射原生 usage；prompt 包含缓存，thoughts 与 candidates 独立。tool 未获非零语义证据，不映射为缓存写入。
- 会话键 sessionId，请求键日志回放后的 gemini.id；同 ID 完整记录替换、$set.messages 全量替换与 $rewindTo 必须先回放，不将变更行当新增调用。info/user 不计请求，unknown type 计未识别记录。
- 自报 total 为 measured；tool 明确为 0 且必需桶完整时才可 derived 换算，缓存缺失/矛盾使未命中输入未知。会话按最终请求完整求和（derived），缺失不补零；不实现估算或费用。
- 本机 10 日志 / 28 最终 gemini 记录：input554736/output4196/thoughts15541/cached0/tool0，自报总量574473；与原生总量求和一致。缓存关系来自安装包恢复指标契约，不能声称已有非零缓存本机样本。旧 .json 会话暂未采集。

### 口径 2.11（2026-10-13）：Grok CLI 轮级快照

- `grok.turn.v1`，读取 GROK_HOME/sessions（默认 ~/.grok/sessions）下 usage.json、summary.json 白名单；不读 chat_history/events/updates/prompt_context。
- 会话累计采用 session.totalTokens（measured、client_reported），不与 turns 相加。明细键 sessionId+turnNumber；一行代表一轮，不代表一次模型调用；modelCalls 仅核对覆盖，不伪造调用。界面请求计数与均量均明确 Grok 轮级含义，全部客户端汇总也保留说明。
- input 包含 cachedRead、output 包含 reasoning；归一化减去子集，缺失或矛盾使相应桶未知。cacheCreation 无正样本、关系 unknown，不计入 countedCacheWriteTokens。totalTokens 缺失保持未知，不按零缓存样本猜测总量。没有估算或 costUsdTicks 换算。
- Grok明细是轮级记账事件，发生于原生endedAt，存入created_at_ms并注明timeBasis=turnEndedAt，按轮结束日归属；不是独立调用开始时间，也不借用session时间。与2.3普通逐调用记录的时间含义明确区分，缺endedAt不补时间；原生纳秒UTC转换到毫秒为derived。
- 本机仅 1 会话、3轮、9次modelCalls；session 与 turns 五计数一致：原生input233668/output3959/cacheRead142848/reasoning2202/total237627。规范未缓存input90820、普通output1757；总量与客户端一致。多会话聚合关系尚未本机验证，明细不能确认覆盖时为 partial。

### 口径 2.12（2026-10-13）：MCode SQLite 请求账本

- `mcode.request.v1`，MINIMAX_DATA_DIR 优先于 MAVIS_DATA_DIR，默认 ~/.minimax；只读 v2/sqlite/runtime-state.sqlite 会话和用量白名单。复用已验证的 SQLite/WAL 内存快照，不打开客户端 SHM、不持久化数据库镜像。
- 请求键为用量 id，会话键 session_id。缓存读写与输入独立；reasoning 与 output 未确认，明确 reasoning=0 时总量=input+output+cache_read+cache_write（derived），否则普通 output 与总量 NULL。缓存写入参与 countedCacheWriteTokens；字段缺失保持未知，不把客户端曾存零当缺失默认。
- 模型 NULL 保持未知，不回读 messages/llm-call/manifest 补造归属；只使用 SQLite 账本，正文 activeRevision/generation 不参与采集，因此不跨多代重复计数。cost/raw/正文不读取、不换算。
- 主库只读样本 1会话/17请求：input61869/output5675/cache_read814226/cache_write0/reasoning0，四桶881770；17模型均NULL。客户端 fresh/goal summary 为67544，明确排除缓存，差814226来自口径，不能把它当完整用量。最终 WAL 可见对账在验收后登记，不把主库数字冒充 WAL 结果。
- 三客户端采用原子完整快照：重复同步幂等，稳定成功扫描可反映修正/删除；源缺失、格式失败、扫描变化不推进水位或删除历史。单客户端失败不阻塞其他来源。无迁移、依赖、权限、联网、费用或估算新增；旧客户端历史规则不变。

### 口径 2.13（2026-10-13）：Devin SQLite 请求用量

- 新增 `devin.request.v1`，只读 `%APPDATA%\Devin\cli\sessions.db`（Unix 为 `$XDG_DATA_HOME/devin/cli/sessions.db`，默认 `~/.local/share/devin/cli/sessions.db`；macOS 未实测），`DEVIN_DATA_DIR` 为显式覆盖。采集 Devin Desktop 本地 agent 与 Devin CLI 的共享存储。字段与限制见 `data-model.md` 第 18 节。
- 规范分桶 input=input_tokens（不因缓存扣减）、output=output_tokens、cache_read=cache_read_tokens、cache_write=cache_creation_tokens，四桶互不重叠；reasoning 客户端不提供，为 NULL 并标 `reasoning.missing`。规范总量 `derived_from_buckets` = input + output + cache_read + cache_write，`client_reports_total = false`；`input_tokens` / `output_tokens` 缺失则总量未知，不为 0。缓存写入计入 countedCacheWriteTokens。
- 独立性依据：`message_nodes.metadata` 列的键 `num_tokens_preceding` 逐行等于 `input + cache_read + cache_creation`（抽样 15/15，全库 28 080/28 883 = 97.2%；未吻合的 803 行 `cache_read_tokens` 均为 JSON null，前导长度不可计算），证伪"cache_read 是 input 子集"（存在 pre=27 218 而 input=1 293）。`num_tokens_preceding` 是 `message_nodes.metadata` 列（独立 TEXT 列）的键，不在 `chat_message` JSON 内；它是上下文长度，**禁止**当作单次请求用量累加或展示，性质同 Kimi Code 的 `token_counting.*`；`chat_message` 内 `metadata.num_tokens` 与 output_tokens 相等，属冗余镜像；`metadata.extensions` 不读取。
- 分桶缺失的不对称处理（口径 2.13 内的更正，非新版本）：`cache_read_tokens` / `cache_creation_tokens` 的显式 JSON null **按 0 计**，登记为**已记录的推断**（非估算、非实测零）。`cache_read`：有 `num_tokens_preceding` 值的行中，803 条 null `cache_read_tokens` 全部（803/803，零反例）满足 `pre = input + coalesce(cache_creation,0)`。`cache_creation`：26 835 条 null 样本中 26 443 条（98.5%）满足 `pre = input + cache_read + 0`，其余 392 条的 `cache_read_tokens` 同时为 null；null 若表示未知则客户端无法算出确切前导长度。`input_tokens` / `output_tokens` 为 null 时**保持未知**，该请求总量为 NULL、会话总量为 NULL，不得补 0：本机 29 条 null `input_tokens`（分布于 15 个会话，模型均为 `swe-2-max`，`output_tokens` 均为 0）没有任何 `num_tokens_preceding` 值（0/29），无证据支持按 0，且缺失必需分桶不得用 0 代替。影响面：null input 由补零改为未知后，未知总量会话数最多 +15；1 676 条 null cache_read 继续按 0 计，四桶合计不变。缺失分桶是"未知"而非"非法"，这类行照常导入，不计 `unparsed`，会话也不因它被标记 `partial`。
- 会话键 `sessions.id`，请求键 `request_id`；54 595 条 assistant 行归并为 24 434 个唯一请求，重复组四元组完全一致（0 冲突组）且不跨会话复用。组内 token 冲突不静默修正，计 `unparsed` 并标该会话 `partial`。`sessions.hidden` 不用作过滤或删除标记：177 个 hidden 会话中 154 个携带真实用量。27 个无用量会话按未知总量导入（界面 `—`），不显示 0。
- 读取不使用整库内存反序列化（2.15 GiB 库会有约 4.4–4.7 GiB 峰值）；改为 `sessions.db` + `sessions.db-wal` 的流式临时副本（`Data/Collectors/SqliteSnapshot.cs`），不复制 `-shm`，副本以 `query_only` 只读打开，复制前后签名不一致即放弃并保留历史，成功与错误路径都删除临时副本。`source_files.path` 记录客户端原始路径。
- 本机对账（2026-10-13，与实时库一致）：24 434 请求；input 162 230 907、output 15 389 071、cache_read 2 394 388 158、cache_creation 23 004 668、合计 2 595 012 804；27 会话总量未知；完整同步 6.0 s、约 2.28 GB 临时占用；`%APPDATA%\Devin` 主库 / `-wal` / `-shm` 逐字节未变。`swe-2-*` / `summarizer` / `compactor` 等模型无公开价格，`cost_source = none`，不做费用换算。
- 同步成本门（同日追加，不改变任何 Token 口径）：签名未变化的同步整体跳过复制与解析，直接以 `files_seen = 1`、`files_skipped_unchanged = 1` 返回，不是错误、不算 `root_missing`，也不推进 `source_files.last_scan_ms`。缓存只按数据库路径保存在进程内存（静态字典 + 锁），重启即失效，重启后首次同步仍全量扫描；失败与扫描中变化不记录签名，下次同步重试；`SyncOptions(ForceRescan: true)` 绕过门。本机实测首次同步 6.0 s、第二次 0.0002 s，两次总量一致。门比的是主库 + WAL 的完整签名，**不**复用 `source_files` 的主库水位：SQLite 只在 checkpoint 时写主库，仅 WAL 追加（常见情形）时主库水位不变，只比主库会漏采真实用量。此机制与 MiMoCode / OpenCode 有意总是重读属于不同取舍，其他客户端口径不变。
- 影响：仅新增第 13 个客户端及其数据，其他客户端口径、去重键、数据库 schema 与迁移不变；历史展示不重算，未接入 Devin 的用户看不到新增条目，已导入的 Devin 数据在新版本首次同步后出现。`is_sidechain` 恒为 false，不宣称子代理归属；legacy Cascade 数据（`~/.codeium/windsurf/cascade/*.pb`）不采集；按会话的增量重读是已记录的后续工作，本次未实现。无新权限、依赖或联网能力。

### 口径 2.14（2026-10-19）：Command Code 请求用量

- `command_code.ai_sdk_usage.v1`，只读 `~/.commandcode/projects/<project-slug>/<session-id>.jsonl`；忽略 `.checkpoints.jsonl`、`.prompts.jsonl`、`.share.json` 等 sidecar。
- 每个 assistant `message` 事件的 `usage` 是一条请求；`inputTokens` 是 AI SDK 总输入（包含 cacheRead/cacheWrite），规范化时 `input = inputTokens - cacheReadTokens - cacheWriteTokens`；`output = outputTokens`，cacheRead/cacheWrite 独立计入；reasoning 未提供，保持 NULL。
- 本地成本对照（`deepseek/deepseek-v4.1-flash`，input 21 151 / cacheRead 7 424 / output 147 / costUsd 0.002169522）验证 `inputTokens` 包含 cacheRead；`inputTokens` 小于 cache 之和时 input 保持未知，不用 0 或饱和值伪造。
- 会话总量 = 去重请求求和（`sum_of_messages`）；开始取 header timestamp，结束取最大消息时间；模型取最新 assistant 模型；坏行计 unparsed 并标 partial；重复 event id 只计一次；所有分支上的 assistant 用量都计入，不按当前分支过滤。
- `cache_write_counts_in_total = true`，计入 countedCacheWriteTokens；无 schema、迁移、权限、依赖或联网变化。

### 口径 2.15（2026-10-19）：Cline CLI / VS Code 任务用量

- `cline.agent_events.v1`：CLI 读取 `CLINE_DATA_DIR`、`CLINE_DIR/data` 或 `~/.cline/data` 下的 `sessions/<id>/<id>.json` 与 `<id>.messages.json`；同时读取 VS Code / Insiders / Cursor / VSCodium 的 `globalStorage/saoudrizwan.claude-dev` 下 `state/taskHistory.json`、`tasks/<id>/ui_messages.json` 与 `task_metadata.json`。
- CLI `metadata.aggregateUsage`（优先）或 `metadata.usage` 为会话总量，消息 `metrics` 为请求明细；VS Code `taskHistory` 为会话总量，`api_req_started.text` 的 `{tokensIn,tokensOut,cacheReads,cacheWrites}` 为请求明细。优先客户端聚合，缺失时按请求求和。
- 字段语义遵循 Cline 自身契约：`tokensIn`/`inputTokens` 是未缓存输入，cacheRead/cacheWrite 独立，四桶相加为总量；reasoning 未提供（NULL），不伪造。聚合与请求明细不一致时标 `partial`，无请求明细为 `none`。
- 去重：CLI session id + message id；VS Code task id + `api_req_started` ts/index；跨源同 id 只保留请求更多的一侧，不重复计数。坏 JSON 计 unparsed；不读取任务标题、提示词或消息正文。
- `cache_write_counts_in_total = true`；无 schema、迁移、权限、依赖或联网变化。

### 口径 2.16（2026-10-19）：Pi Agent v3 会话用量

- `pi_agent.session.v3`，只读 `PI_CODING_AGENT_SESSION_DIR`、`PI_CODING_AGENT_DIR/sessions` 或 `~/.pi/agent/sessions` 下的 v3 JSONL；header 为 `type/version/id/timestamp/cwd`，assistant `message.usage` 为请求级用量。
- `input/output/cacheRead/cacheWrite` 四桶互不重叠并计入总量；`reasoning` 是 `output` 的子集，归一化时从 output 拆出；`totalTokens` 为客户端自报总量。本机 9 866 条 usage 记录全部满足 `totalTokens == input + output + cacheRead + cacheWrite`，9 176 条 `cacheRead > input`，reasoning 均不超过 output。
- 会话总量按去重请求求和；开始取 header timestamp，结束取最大请求时间；模型取最新 assistant 模型；坏行计 unparsed 并标 partial，无 assistant 用量为 none。事件 id 去重，不读取 content/tool 参数/成本。
- `cache_write_counts_in_total = true`；无 schema、迁移、权限、依赖或联网变化。

### 口径 2.17（2026-10-19）：Qoder IDE / CLI 用量

- IDE（`qoder.ide_chat_message.v1`）：只读 `%APPDATA%/Qoder` 与 `%APPDATA%/QoderCN`（macOS/Linux 对应目录）下 `SharedClientCache/cache/db/local.db` 的 `chat_session` / `chat_message` 身份、时间、模型与 `token_info`；`prompt_tokens` 是包含 `cached_tokens` 的总输入，规范化为 `input = prompt_tokens − cached_tokens`，`cache_read = cached_tokens`，`output = completion_tokens`，`cache_write`/`reasoning` 未提供（NULL）。本机 64 条 assistant 行：prompt 4 584 220、cached 4 442 233、completion 34 385，换算后 input 141 987、cache_read 4 442 233、output 34 385、总量 4 618 605。
- CLI（`qoder.cli_message.v1`）：读取 `QODER_HOME/projects`、`QODER_CN_HOME/projects`（默认 `~/.qoder/projects`、`~/.qoder-cn/projects`）下每个项目目录的 `*.jsonl`；`message.usage` 为 Anthropic 形状，`input_tokens/output_tokens/cache_read_input_tokens/cache_creation_input_tokens` 四个独立桶均计入总量，reasoning 未提供。本机 102 条 usage 行全部为 0，且伴随非零 `credits`；这些 0 是客户端占位符，自口径 2.19 起按未知处理（保留请求、不写 0），TokenView 仍不估算、不读取 `credits`。
- 会话总量按去重请求求和（`sum_of_messages`）；IDE 会话开始取 `chat_session.gmt_create`，结束取 `gmt_modified` 与最大消息时间的较大值；CLI 时间取条目 `timestamp`。模型优先取消息 `model_info.model_key`，回退 `preferred_model_info`；CLI 取 `message.model`。
- 去重：IDE `chat_message.id`（缺失时 rowid），CLI `message.id`（缺失时 `uuid`，再缺失时行号）；CLI 流式重写保留最后一条同 id 记录。跨 IDE/CLI 同会话 id 只保留请求更多的一侧（相同偏好 IDE）。坏 JSON/`token_info` 计 `unparsed` 并使会话 `partial`，无 assistant 用量为 `none`。
- `cache_write_counts_in_total = true`（仅 CLI 可能提供非零值）；无 schema、迁移、权限、依赖或联网变化。不读取 IDE 消息正文、工具结果、记忆内容或 CLI 提示词/回答正文。

### 口径 2.18（2026-10-24）：Kimi Code 并发写入容错

- 背景：`usage.record` 只追加到 `wire.jsonl`，而 Kimi Code 会话在 TokenView 扫描期间持续写入。整客户端快照原先只要任一文件在解析期间增长就抛 `ChangedDuringScan`，本机 2.0 GB / 1021 个 wire 的一轮解析约 90 秒，只要有一个会话活着就几乎必然被打断，导致该客户端长期没有一次成功提交（`kimi_code` 水位停在 2026-10-23 02:52，新会话与 `kimi-code/k3.1-pro` 从未入库）。
- 新行为：读取期间追加、末行未写完或 wire 头部尚未写完时，本次快照只提交已读到的**完整** `usage.record` 行，会话 `message_coverage = partial`，`source_files` 水位保留扫描开始时的 size / mtime；下次同步因指纹不同必然重解析并补齐。空 wire 的新会话按 `partial` + 总量未知导入，不阻断其它会话。不再二次校验目录列表（扫描期间新建的会话留给下次同步）。
- 仍保留的拒绝条件：`state.json` 缺失 / 未知 version、wire 首行为**完整**行但协议不是 1.4 / 1.5、目录或文件读取失败；这些保留上次快照与水位，不提交半成品。
- 口径影响：不估算、不补零、不改变去重键（`agent:<agent>:line:<物理行号>`）与四桶语义；此前因整客户端中止而缺失的 Kimi 会话会在升级后首次成功同步补上，历史 Token 只增不减，可能看到 `partial` 会话。其它客户端口径、schema、迁移、依赖与联网不变。

### 口径 2.19（2026-10-26）：Qoder CLI 全零 usage 视为未知

- 背景：复核本机 Qoder CLI 的 4 个 transcript，带 `message.usage` 的 assistant 记录从 415 条增长到 594 条，`input_tokens/output_tokens/cache_read_input_tokens/cache_creation_input_tokens` 全部为 0（594/594，无一非零），同期 `credits` 非零并带 `context_usage_ratio`；Qoder Desktop 的 `chat_session_context_usage` 同样只有 `tokenCountsAvailable=false`、`totalTokens=0` 与百分比，消息 payload 不含 token 字段。这些 0 是客户端占位符，不是实测零用量。
- 新行为：assistant 的 usage 对象存在、但所有已提供的 token 字段均为 0 时，保留这条请求（请求数、会话与时间分布照常统计），四个 token 桶与总量按 NULL 写入；parser_version 由 1 升为 2。部分字段非零、实测 0 与缺失字段混合、或整条记录没有任何 token 字段时，保持原有“未知即 NULL、已知零保留 0”的规则；没有 `message.usage` 的 assistant 行仍按设计忽略。
- 展示：聚合范围内全部请求的用量都未知时，总指标、图表 tooltip、每日条带、会话与排行显示 `—`，不再显示 0；客户端分布中该客户端的 Token、占比同样显示 `—`，占比条隐藏。请求数照常显示，已知 / 未知请求数继续由 `requests_with_usage` / `requests_without_usage` 区分。Qoder IDE 的真实 `token_info` 记录不受影响，仍按口径 2.17 展示。
- 影响：升级后首次同步会把历史 Qoder CLI 的占位 0 重写为未知，之前显示为 0 的假总量变为 `—`；Qoder IDE 已有的 4 618 605 Token 保持不变。不估算、不读取 `credits`、不用 `context_usage_ratio` 推算 prompt 大小；去重键、schema、依赖与联网不变。

### 口径 2.20（2026-10-26）：Qoder CLI 占位 usage 按 E5 估算

- 背景：口径 2.19 把 Qoder CLI 的全零占位 usage 记为未知后，界面只剩 `—` 与请求数。用户在意的具体 Token 仍可从同一批 transcript 中取得可靠依据：每条带 usage 的 assistant 记录同时带 `usage.context_usage_ratio`（本机 828/828 条，0.1376–0.8352）与完整输出内容；它们是客户端写入的真实字段，只是 token 计数被占位为 0。
- 新行为（规则 E5）：占位 usage 且 `context_usage_ratio` ∈ (0, 1] 时写入估算值：`input = round(ratio × 200 000)`，`output = ceil(输出字符数 ÷ 4)`，字符数取 `content` 字符串、`text` / `thinking` 块字符串与 `tool_use.input` 的原始 JSON 长度；`cache_read` / `cache_write` 置 0 仅为让总量可求和，不代表客户端提供了实测缓存。比率缺失 / 非法或字符数为 0 时仍按未知处理。请求与全部请求均为估算的会话标 `source_kind = estimated`；`parser_version` 由 2 升为 3，升级后首次同步自动重扫。
- 置信度依据：7 次 compaction 的 `preTokens` 与本机历史用量对账，估算误差 −0.21% 至 −2.31%（均值 −1.26%）；用同一批 `content` 字符数换算输出量与实际 compaction 输出相差约 0.14%。置信度记为 0.8，仅为估算，不宣称实测精度。
- 隔离规则：估算值不进入任何实测聚合。请求数、会话数与时间分布照常计入（`request_hourly` 保留这些请求，Token 列与 `requests_with_usage` 记 0，`requests_without_usage` 计数）；总指标、客户端分布、排行、缓存命中率、会话总账与会话详情都把估算与实测分开，估算以 `≈` 与“未计入实测”标注，汇总行 `EstimatedTokens` / `EstimatedRequests` 单独返回。实测与估算绝不相加。
- 隐私：读取 `content` 只为在内存中累计字符数，正文、工具参数与 `credits` 不落库、不写日志、不外传；IDE 路径不变，仍不读取消息正文。
- 影响：升级后首次同步会把 Qoder CLI 的未知记录改写为估算记录，界面从 `—` 变为 `≈` 数值；Qoder IDE 的 4 618 605 实测 Token 与所有其他客户端的总量、排行、缓存口径保持不变。去重键、schema、迁移、依赖与联网不变。


### 口径 2.21（2026-10-26）：Qoder 自动开启真实记录与缓存去重

- Qoder 采集启用时，由 `QoderUsageRecordingService` 在启动及同步前后台启用用户级与进程级 `QODER_EXPOSE_TOKEN_USAGE` / `QODERCN_EXPOSE_TOKEN_USAGE=true`，仅值变化时写入并广播，无弹窗、不重启 Qoder、不修改客户端文件。初始化失败保留脱敏结果，现有数据仍继续采集；未启用 Qoder 采集时不读写这些环境变量。Probe 与采集器保持只读。
- 运行时代码证明 token 的全零记录源于默认隐藏开关，并不能说明上游没有计数。真实计数可用时优先采集；Qoder 原生 `input_tokens` 直接来自上游 prompt，包含两个缓存桶，先扣除 cache_read / cache_creation 得到未缓存 input，避免重复累计。CLI 语义升级 `qoder.cli_message.v2`、parser_version 4，首次同步重算；IDE 语义与结果不变。缓存缺失 / 超过输入时总量未知，不伪造零。
- 请求优先按 usage / 条目 request_id 去重，再回退 message id / uuid / 行号。同请求拆分 / 重放只保留一条，实测优先于估算，估算优先于未知，同等级取最后一条。历史 E5 估算继续单列；同会话跨越开关启用时，估算不能进入实测会话总量，混合总量保持未知，实测请求聚合照常统计。
- 不新增 schema、依赖、管理员权限、网络或客户端正文持久化。历史已清零记录不自动补齐；新进程继承开关后可记录真实 token。
