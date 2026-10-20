# TokenView

[English](README.md) | **简体中文**

TokenView 是一款本地优先的 Windows 桌面应用，用于分析各类 AI 编程客户端的使用元数据。
数据保存在本地 SQLite 数据库中，采集过程只读、不修改客户端数据，也不会上传用量数据或包含遥测。
仅有的联网请求是固定的更新检查，以及下文所述的公开公告/关于页镜像。程序集、数据目录、数据库与
设置文件统一使用 TokenView 名称。

![TokenView 总览](docs/images/overview.png)

当前实现为 C# / .NET 10 / WPF。此前的 Tauri + React + Rust 实现已从工作区移除，完整快照保留在
`backup/webview-2026-10-19` 分支，仅供对照。

## 极简设计

TokenView 刻意保持极简界面：单一窗口、单一强调色，扁平卡片上只放数字与图表。没有账号、没有功能
侧栏、没有广告或推销——打开应用即可看到用量。浅色与深色主题下都保持同样的留白布局，窄至
800×600 的窗口也能正常使用。这种克制的外观对应同样克制的实现：一切都在本地，无遥测、无上传。

## 功能

- 总览、趋势、模型排行、会话元数据与数据源状态。
- 对 Codex、Claude Code、OpenCode、DSH、Kimi Code、MiMoCode、Pi Desktop、Pi Agent、
  WorkBuddy、GitHub Copilot、Gemini CLI、Grok CLI、MCode、Devin、Command Code、Cline、
  Qoder、CodeArts 的只读采集。
- 明确区分实测（measured）、直接换算（derived）、估算（estimated）、未知与部分数据。
- 本地数据清除、可配置的采集来源，以及统计/设置存储目录迁移。
- 浅色与深色主题、托盘集成、开机自启与可配置的同步计划。
- 针对固定 GitHub Releases `latest.json` 端点的更新检查，可选每日自动下载，并在校验 SHA-256 后
  由用户确认重启安装。
- GitHub Actions 发布流程：推送 `v*` 标签后执行测试、发布、打包，并上传 Windows zip 与
  `latest.json`。
- 远程公告（启动时 + 每 20 分钟）与关于页动态信息，从固定的公开镜像列表以 Markdown 获取，
  仅缓存在内存中，未读状态持久化。

模型与客户端图标随应用本地打包；未知模型保留原始名称并使用中性占位图标。

可在 设置 → 数据与隐私 中修改存储目录。已有统计与偏好会一并迁移；重置偏好会保留所选目录。
系统应用目录中会保留一个小型位置文件。窗口状态仍由桌面应用管理。

## 环境要求

- Windows 10 或 11（x64）
- .NET SDK 10

## 开发

```bash
dotnet build wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release
dotnet run --project wpf/TokenView.Wpf
```

正在运行的 TokenView 实例可能锁定 `bin/Debug`；请使用 `-c Release`，或先关闭应用再重新构建 Debug。

## 验证

```bash
dotnet test wpf/TokenView.Tests/TokenView.Tests.csproj -c Release
```

`wpf/TokenView.Probe` 是无界面工具，可在数据库副本上执行同步并核对总量：

```bash
P=wpf/TokenView.Probe/bin/Release/net10.0-windows/TokenView.Probe.dll
dotnet $P copy "$APPDATA/com.tokenview.app/tokenview-usage.sqlite3" /tmp/db.sqlite3
dotnet $P sync /tmp/db.sqlite3 --force
dotnet $P totals /tmp/db.sqlite3
dotnet $P announcement
dotnet $P about-info
```

## 仓库结构

```text
wpf/TokenView.Wpf/        WPF 桌面应用
  Data/                 SQLite 存储、查询、采集与 Token 语义
  Services/             同步、托盘、自启、主题、窗口状态、更新
  Controls/             自绘图表与日期范围选择器
  Settings/             settings.json 模型与存储
  Assets/               应用图标与图标源文件
wpf/TokenView.Probe/      无界面同步/查询/内容验证工具
wpf/TokenView.Tests/      镜像获取、配置合并与未读状态的 xUnit 测试
docs/                   规范、数据源调研与 README 图片
scripts/                只读数据库对账辅助脚本
```

## 隐私

- 采集器只读取客户端元数据；会话正文绝不持久化。
- 实测、直接换算与估算值在界面上保持可区分。
- 无遥测、无上传。仅有的联网请求是固定更新检查（绝不自动下载或安装），以及从固定公开镜像列表
  获取公告/关于页 Markdown（启动时 + 每 20 分钟；不上传任何本地数据，不加载图片与 HTML）。
  公告正文只留在内存中，仅持久化已读哈希。

完整规则见 `AGENTS.md` 与 `docs/`。
