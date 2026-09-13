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
| `X.Y.Z`（如 `1.0.0`，可带 `-预发布后缀`） | 稳定版 | 标记为正式 release，**强制要求签名证书** |

- 同一天发多版递增 `.NN`（`20260913.01` → `20260913.02`）。
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

1. 校验 tag 格式与发布配置（stable 版无签名证书会直接失败）
2. `dotnet restore` + `dotnet test` 全量测试
3. `build/publish.ps1` 发布 framework-dependent 的 WinUI（win-x64）
4. 签名应用 exe（仅当配置了 `SIGNING_CERTIFICATE_BASE64` / `SIGNING_CERTIFICATE_PASSWORD` secret）
5. `build/build-installer.ps1`（Inno Setup）生成 `CrabDesk-Payload-x64.exe` 并签名
6. `build/publish-bootstrapper.ps1` 生成 `CrabDesk-Setup-x64.exe` 并签名
7. 仅 stable：校验 Authenticode 签名、时间戳、证书一致性
8. 生成 `SHA256SUMS.txt`，创建 GitHub Release，上传 **Setup + SHA256SUMS**，
   发布说明取 `docs/releases/v<版本>.md`（不存在则自动生成）

### 第 3 步：发布后校验

```bash
gh run watch                                    # 盯 workflow
gh release view v20260913.01                    # 确认资产齐全
```

确认资产为三个：`CrabDesk-Setup-x64.exe`、`CrabDesk-Payload-x64.exe`、
`SHA256SUMS.txt`。若 Payload 缺失（workflow 目前不自动上传），手动补传：

```bash
gh release upload v20260913.01 artifacts/installer/CrabDesk-Payload-x64.exe
```

---

## 4. 已知问题与绕行方案

### ⚠ `CrabDesk.WinUI.Tests` 在 CI 上挂起

自 2026-08-30 起，`CrabDesk.WinUI.Tests` 的 test host 在 GitHub Actions runner 上
崩溃或挂起（无桌面环境，60 秒无活动触发 blame hang dump），导致 `release.yml`
在测试阶段失败或跑满 25 分钟超时。**`v20260826.01` 之后再没有一次正式 workflow
成功发布过。**

绕行方案（当前实际使用的发布路径）：

1. 本地确认全量测试通过。
2. 推 tag 后手动触发 recovery workflow（不跑测试，从 tag 构建）：
   `Actions → Publish prerelease recovery → Run workflow`，输入 `YYYYMMDD.NN`
   格式的版本号。它会构建 Setup + SHA256SUMS，以 **draft → 发布** 的方式更新
   对应 release。
3. 手动补传 `CrabDesk-Payload-x64.exe`（recovery 同样不上传它）。
4. 照常做第 3 步的发布后校验。

> 根因修复前，每发一版都要走这条路。修复方向：定位 WinUI 测试在 headless
> runner 上挂起的用例，或将其标记为需要桌面环境并从 CI 集合中排除。

### ⚠ `Directory.Build.props` 里的 AssemblyVersion 未随版本更新

`AssemblyVersion` / `FileVersion` 固定为旧值且优先级高于 `-p:Version` 传入值，
编译产物的文件版本会与 `InformationalVersion` 不一致。发布提交暂不需要处理它，
但对外报版本号时以 `InformationalVersion`（csproj 的 `<Version>`）为准。

---

## 5. 稳定版（X.Y.Z）补充说明

- `assert-release-configuration.ps1` 会拒绝没有签名证书的 stable 发布，
  证书通过仓库 secrets 提供，签名的 exe 必须带可信时间戳且为同一发布者证书。
- 稳定版的发布说明固定文件名 `docs/releases/vX.Y.Z.md`（`v1.0.0` 有特殊约定）。
- 稳定版会走完整签名校验闭环；prerelease 只签名、不校验。

## 6. 发布失败的恢复

- workflow 在任意构建步骤失败：修正后**重新打 tag**（删除远端 tag 重建）或改用
  recovery workflow。
- release 资产损坏 / 需要替换：直接重跑 recovery workflow，它会删除旧资产后
  重新上传并取消 draft 状态。
- 回滚线上版本：不做删 release 处理，而是发布一个更高版本号的新 release 修复问题。
