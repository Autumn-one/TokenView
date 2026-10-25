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

将发布目录内容压缩为 `TokenView-<version>-win-x64.zip`。如需免运行时安装，可改用
`--self-contained true`，但包体与首次启动会明显增大；切换发布模式时在提交说明中写明。

## GitHub Release 自动化

仓库：`Autumn-one/TokenView`。`.github/workflows/release.yml` 在推送 `v*` 标签时自动执行：

1. `dotnet test`。
2. `dotnet publish` 生成框架依赖的 `win-x64` 包。
3. 压缩为 `TokenView-<version>-win-x64.zip` 并计算 SHA-256。
4. 生成 `latest.json`。
5. 创建 GitHub Release 并上传 zip 与 `latest.json`。

标签必须与 csproj 的 `Version` 一致，否则工作流直接失败。应用固定读取：

```text
https://github.com/Autumn-one/TokenView/releases/latest/download/latest.json
```

`latest.json` 格式与 `Services/UpdateService.cs` 对应，`sha256` 是必填项：

```json
{
  "version": "0.1.12",
  "notes": "本次更新说明（纯文本，最多展示两条摘要）",
  "platforms": {
    "windows-x86_64": {
      "url": "https://github.com/Autumn-one/TokenView/releases/download/v0.1.12/TokenView-0.1.12-win-x64.zip",
      "sha256": "更新包 zip 的 SHA-256 十六进制小写值"
    }
  }
}
```

发布步骤：

1. 更新 csproj 的 `Version`、`AssemblyVersion`、`FileVersion`。
2. 提交并推送 `main`。
3. 创建并推送标签：`git tag vX.Y.Z && git push origin vX.Y.Z`。
4. 等待 Actions 完成，确认 Release 附件包含 zip 与 `latest.json`，并核对 `latest.json` 中的
   `version`、`url`、`sha256`。
5. 在仓库根目录维护 `RELEASE_NOTES.md`，工作流会将它作为 Release 正文，同时写入 `latest.json` 的 `notes`。

工作流使用 GitHub 内置的 `GITHUB_TOKEN`（`permissions: contents: write`），不需要额外 SSH 密钥；
本地 SSH 只用于推送代码与标签。

## 自动更新行为

- 设置 · 更新：手动检查 → “下载并准备更新” → “重启并更新”。
- 开启“自动检查并下载更新（最多每天一次）”后，应用按 `settings.json` 的 `lastUpdateCheckMs`
  限制每天最多检查一次；命中新版本会后台下载、校验并在托盘提示，点击气泡后重启安装。
- 更新包只接受 `github.com/Autumn-one/TokenView/releases/download/` 下的 HTTPS 地址；
  `sha256` 缺失或校验失败时拒绝安装。
- 替换由临时 PowerShell 助手在进程退出后执行：先备份当前程序文件，覆盖失败时恢复并重启旧版本。
- 不静默覆盖正在运行的程序；只有用户点击“重启并更新”或托盘气泡才会退出并替换。
- 默认不联网。已登记的联网入口只有更新检查/下载（手动或每天最多一次）与公告/关于 Markdown
  （启动一次 + 每 20 分钟，固定公开镜像列表，不上传本地数据）；发布说明不得把公告正文说成版本更新。
- 当前没有代码签名；SHA-256 只能校验更新包与 `latest.json` 一致，不能替代发布者签名。
  Windows SmartScreen 仍可能提示未知发布者，不要用“已签名”描述发布包。

## 发布检查

1. 版本号已同步到 csproj，标签为 `vX.Y.Z`。
2. Actions 的 Test、Publish、Package、Create release 全部成功。
3. Release 附件包含 zip 与 `latest.json`；`latest.json` 的版本、下载地址与 SHA-256 均可访问并匹配。
4. 在旧版本上于设置 · 更新手动检查，完成下载与重启，关于页版本号更新。
5. 开启自动更新后确认每天最多检查一次、托盘气泡出现，点击后能完成重启更新。
6. 在不可写目录（例如未提权的 `Program Files`）验证覆盖失败时会恢复旧文件并重新启动。
