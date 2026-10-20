# 桌面构建与发布

## 版本号

版本号只维护在 `wpf/TokenView.Wpf/TokenView.Wpf.csproj` 的 `Version`、`AssemblyVersion` 与
`FileVersion`，发布标签使用 `vX.Y.Z`，三者必须一致。应用内“关于”与更新检查读取程序集版本。

## 构建

```bash
dotnet build wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release
dotnet build wpf/TokenView.Probe/TokenView.Probe.csproj -c Release
```

Debug 构建可能被正在运行的 TokenView 实例锁定；发布构建使用 Release，或先关闭应用。

发布包使用框架依赖发布（与 `wpf/README.md` 的说明一致），用户需要安装 .NET 10 Desktop Runtime：

```bash
dotnet publish wpf/TokenView.Wpf/TokenView.Wpf.csproj -c Release -r win-x64 --self-contained false -o release-assets
```

将 `release-assets` 目录压缩为 `TokenView-<version>-win-x64.zip`。如需免运行时安装，可改用
`--self-contained true`，但包体与首次启动会明显增大；切换发布模式时在提交说明中写明。

## GitHub Release

发布到 `MY-Final/TokLens` 的 GitHub Releases，应用固定读取：

```text
https://github.com/MY-Final/TokLens/releases/latest/download/latest.json
```

`latest.json` 至少包含版本、说明与 Windows 下载地址，格式与 `Services/UpdateService.cs` 对应：

```json
{
  "version": "0.1.10",
  "notes": "本次更新说明（纯文本，最多展示两条摘要）",
  "platforms": {
    "windows-x86_64": {
      "url": "https://github.com/MY-Final/TokLens/releases/download/v0.1.10/TokenView-0.1.10-win-x64.zip"
    }
  }
}
```

- 下载地址必须是本仓库的 HTTPS Release 资源；应用不会从第三方地址更新。
- 应用只做版本比较与提示：发现新版本后显示版本与说明，并打开下载页；**不自动下载、不自动安装**。
- 默认不联网。已登记的联网入口只有更新检查（手动或每天最多一次）与公告/关于 Markdown
  （启动一次 + 每 20 分钟，固定公开镜像列表，不上传本地数据）；发布说明不得把公告正文说成版本更新。
- 当前没有代码签名；Windows SmartScreen 可能提示未知发布者，不要用“已签名”描述发布包。
- 升级到 TokenView 命名版本时，首次启动会把旧 TokLens 目录与库文件迁移为 `com.tokenview.app` /
  `tokenview-usage.sqlite3`；旧文件保留作回滚副本，统计口径不变。之后的覆盖安装不再改动统计数据库与
  `settings.json`。卸载或覆盖安装前提醒用户备份可选。

## 发布检查

1. 版本号已同步到 csproj。
2. Release 构建、`dotnet test wpf/TokenView.Tests/TokenView.Tests.csproj -c Release` 与 Probe 构建均通过。
3. 启动 Release 包，确认总览、设置（含更新分类的手动检查）、托盘、公告窗口与关于页动态信息可打开。
4. Release 附件包含 zip 与 `latest.json`，`latest.json` 中的版本和下载地址可访问。
5. 在旧版本上于设置 · 更新手动检查一次，确认提示版本、说明与下载入口正确；开启自动检查后确认命中新版本时托盘气泡出现且点击可打开下载页。
