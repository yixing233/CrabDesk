# 发布指引（RELEASING）

本文档描述 CrabDesk 的日常推送约定和 release 发布流程。发布自动化由
[`.github/workflows/release.yml`](../.github/workflows/release.yml) 和
[`.github/workflows/publish-prerelease-recovery.yml`](../.github/workflows/publish-prerelease-recovery.yml)
承担，构建脚本统一放在 `build/` 目录。

---

## 1. 日常推送

- 日常开发在 `main` 或 `codex/<主题>` 分支上进行；每个 push 和 PR 都会触发 `ci.yml`
  （restore + test），push 前最好本地先跑一遍：

  ```powershell
  dotnet test CrabDesk.sln -c Release
  ```

- 提交信息使用约定式前缀：`feat:` / `fix:` / `docs:` / `perf:` / `release:` 等。
- 发布相关改动（版本号、CHANGELOG、发布说明）单独作为一个 `release: vX...` 提交，
  不和功能改动混在一起。

---

## 2. 版本号规则

| 格式 | 类型 | 标记 |
|---|---|---|
| `YYYYMMDD.NN`（如 `20260913.01`） | 日常快速发布 | GitHub Release 标记为 **prerelease** |
| `X.Y.Z`（如 `1.0.0`，可带 `-预发布后缀`） | 稳定版 | 标记为正式 release |

- 同一天发多版递增 `.NN`（`20260913.01` → `20260913.02`）。
- **签名是可选的**：项目当前没有代码签名证书，安装包以未签名状态发布；应用内
  更新链依赖 HTTPS + `SHA256SUMS.txt` 校验完整性，签名只作提示不拦截。若未来
  配置了 `SIGNING_CERTIFICATE_BASE64` / `SIGNING_CERTIFICATE_PASSWORD` secret，
  workflow 会自动签名并做完整签名校验。
- 版本号需要**手动同步到两处**，发布提交里都要改：
  - `CrabDesk.WinUI/CrabDesk.WinUI.csproj` 的 `<Version>`
  - `CrabDesk.Bootstrapper/CrabDesk.Bootstrapper.csproj` 的 `<Version>`
- tag 格式为 `v` + 版本号（如 `v20260913.01`），只有匹配 `v*.*` 的 tag 才会触发发布。

---

## 3. 发布步骤（标准流程）

### 第 1 步：准备发布提交

1. 改两个 csproj 的 `<Version>`（见上）。
2. 在 `CHANGELOG.md` 顶部追加新版本章节，注明对比基准（线上最新版本号 + commit）。
3. 新建 `docs/releases/v<版本号>.md` 发布说明。规则：
   - 内容与 CHANGELOG 对应章节一致，语言为中文；
   - 末尾「验证」小节写明测试结果（如「Release 全量测试：N 项通过」）；
   - 末尾注明发布资产清单（见第 5 步，当前为三个资产）。
4. 提交为 `release: v<版本号>` 并推送到 `main`。

### 第 2 步：打 tag 触发发布

```bash
git tag v20260913.01
git push origin v20260913.01
```

`release.yml` 会依次执行：

1. 校验 tag 格式与发布配置
2. `dotnet restore` + `dotnet test`（CrabDesk.Tests 与 CrabDesk.Bootstrapper.Tests；
   WinUI 测试因 CI headless 崩溃不在 CI 运行，本地发布前自行跑全量）
3. `build/publish.ps1` 发布 framework-dependent 的 WinUI（win-x64）
4. 签名应用 exe（仅当配置了签名证书 secret）
5. `build/build-installer.ps1`（Inno Setup）生成 `CrabDesk-Payload-x64.exe` 并签名
6. `build/publish-bootstrapper.ps1` 生成 `CrabDesk-Setup-x64.exe` 并签名
7. 仅当配置了证书：校验 Authenticode 签名、时间戳、证书一致性
8. 生成覆盖两个安装程序的 `SHA256SUMS.txt`，创建 GitHub Release，上传 **Setup + Payload + SHA256SUMS**，
   发布说明取 `docs/releases/v<版本>.md`（不存在则自动生成）

### 第 3 步：发布后校验

```bash
gh run watch                                    # 盯 workflow
gh release view v20260913.01                    # 确认资产齐全
```

确认自动上传的三个资产齐全：`CrabDesk-Setup-x64.exe`、`CrabDesk-Payload-x64.exe`、
`SHA256SUMS.txt`，下载后分别核对两个 exe 的 SHA-256。

- Setup 是推荐安装入口：内嵌应用负载，并按需下载缺少的运行依赖。
- Payload 为应用安装负载：供已安装所需运行依赖的环境使用，不负责补装依赖。
- 附件校验值必须来自同一次发布构建，不混用其他本地构建的安装负载。

---

## 4. 已知问题与绕行方案

### ⚠ `CrabDesk.WinUI.Tests` 在 CI 上挂起（已从 CI 排除）

自 2026-08-30 起，`CrabDesk.WinUI.Tests` 的 test host 在 GitHub Actions runner 上
崩溃或挂起（无桌面环境，60 秒无活动触发 blame hang dump）。CI 与 release
workflow 的测试步骤已改为只跑 `CrabDesk.Tests` 和 `CrabDesk.Bootstrapper.Tests`；
**发布前应在本地跑全量** `dotnet test CrabDesk.sln -c Release`（WinUI 测试本地正常）。
待定位根因（疑似需要桌面会话的用例）后恢复 CI 全量并删除本节。

### 应用内更新的签名策略

发布包当前未签名。应用内更新器对安装包的 Authenticode 校验是**提示性**的：
签名可信时展示发布者并校验发布者一致性；不可信或未签名时仅提示，不拦截启动
（`CrabDeskRuntime.cs` 中 `DownloadUpdateAsync` / `LaunchUpdateInstaller`）。
拦截发生在 `SHA-256` 与 `SHA256SUMS.txt` 不匹配或下载后文件被改动时。

> 注意过渡期：`v20260913.02` 及更早版本的应用内更新器仍保留旧行为——对
> **非 prerelease** 的 release 强制验签。因此在存量用户升级到 `v20260913.03`
> （首个带宽松校验的版本）之前，发布仍应使用日期格式（自动标为 prerelease）；
> 之后如需发正式版（`X.Y.Z` 或修改 `release.yml` 的 prerelease 判定），存量
> 用户的应用内更新才不会因强制验签失败。

### ⚠ `Directory.Build.props` 里的 AssemblyVersion 未随版本更新

`AssemblyVersion` / `FileVersion` 固定为旧值且优先级高于 `-p:Version` 传入值，
编译产物的文件版本会与 `InformationalVersion` 不一致。发布提交暂不需要处理它，
但对外报版本号时以 `InformationalVersion`（csproj 的 `<Version>`）为准。

---

## 5. 稳定版（X.Y.Z）补充说明

- 稳定版的发布说明固定文件名 `docs/releases/vX.Y.Z.md`（`v1.0.0` 有特殊约定）。
- 配置了签名证书时，稳定版会走完整签名校验闭环；未配置时与 prerelease 相同，
  依赖 `SHA256SUMS.txt` 校验。

## 6. 发布失败的恢复

- workflow 在任意构建步骤失败：修正后**重新打 tag**（删除远端 tag 重建）或改用
  recovery workflow。
- release 资产损坏 / 需要替换：直接重跑 recovery workflow，它会删除旧资产后
  重新上传并取消 draft 状态。
- 回滚线上版本：不做删 release 处理，而是发布一个更高版本号的新 release 修复问题。
