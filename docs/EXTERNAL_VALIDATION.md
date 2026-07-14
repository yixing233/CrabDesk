# CrabDesk 外部验收清单

本文记录无法只依靠当前工作区完成的 1.0 发布门槛。执行结果应附上系统版本、显示器拓扑、时间、截图或命令输出，并同步回 `DEVELOPMENT_PLAN.md`。

所有分阶段验证和正式 Release 验证完成后执行：

```powershell
.\build\audit-external-readiness.ps1 -RequireReady
```

审计会递归聚合各机器输出的 `session.json`，要求 15 分钟完整发布门禁无跳过、真实睡眠唤醒、Windows 10/11 各一份混合 DPI 多屏完整会话，以及受信任签名的 `v1.0.0` GitHub Release 下载后验收。报告写入 `artifacts\external-validation\readiness`。

## 当前环境限制

- 正式仓库已创建为 `https://github.com/yixing233/CrabDesk`；首次推送、Actions 运行、签名 Secrets 和正式 Release 仍需分别验证。
- 当前机器只有一台 `1920x1080`、`100%` DPI 显示器。
- 当前用户证书库没有有效的代码签名证书。
- 自动触发系统休眠会中断当前开发连接，因此真实休眠必须由用户在本机确认并手动执行。

## 休眠与唤醒

休眠验收使用分阶段脚本留存进程、桌面宿主、显示器、盒子、映射目录和 Explorer 图标状态。脚本不会主动让系统休眠，避免未确认的数据丢失：

```powershell
# CrabDesk 已运行，并且至少创建一个可用的文件夹映射盒子
.\build\verify-sleep-resume.ps1 -Stage Baseline

# 从 Windows 电源菜单手动睡眠，唤醒并等待 20 秒后执行
.\build\verify-sleep-resume.ps1 -Stage AfterResume

# 完成新增/删除映射文件等人工检查后，验证正常退出与图标恢复
.\build\verify-sleep-resume.ps1 -Stage Finalize
```

结果保存在 `artifacts\external-validation\sleep-resume\session.json` 和 `latest.md`。`AfterResume` 会验证同一进程继续运行、每个显示器只有一个可见桌面子窗口、`WS_CHILD`/`SHELLDLL_DefView` 层级、盒子与映射关系未丢失，并连续执行四次 `Win+D`；`Finalize` 会验证进程完全退出及 `HideIcons` 恢复。

1. 使用 `build\publish.ps1` 和 `build\build-installer.ps1` 生成当前版本。
2. 启动 CrabDesk，创建普通盒子和文件夹映射盒子，并记录盒子位置、映射文件数量和 Explorer 图标显示状态。
3. 关闭设置窗口，确认进程只驻留托盘。
4. 从 Windows 电源菜单手动进入睡眠，等待至少 30 秒后唤醒并登录。
5. 等待 20 秒，确认 CrabDesk 进程仍在、桌面盒子重新连接、映射目录重新枚举。
6. 在映射目录新增和删除文件，确认盒子实时刷新。
7. 连续执行 `Win+D`、`Win+M` 和任务视图，确认盒子仍在桌面层且普通窗口可以覆盖。
8. 从托盘退出，确认 Explorer 图标状态和位置恢复。

至少分别覆盖一次普通睡眠、显示器关闭后唤醒和睡眠期间断开映射磁盘。若 Explorer 在唤醒后重启，还需确认 CrabDesk 自动重连。

## 多屏与混合 DPI

需要 Windows 10 和 Windows 11 x64 真实设备，至少覆盖：

```powershell
# 双屏同 DPI 场景
.\build\verify-multi-monitor.ps1 -Stage Baseline

# 100% + 150% 等混合 DPI 场景，基线阶段强制检查不同 DPI
.\build\verify-multi-monitor.ps1 -Stage Baseline -RequireMixedDpi -Force

# 按脚本提示依次跨屏移动盒子、拔出/调整显示器、恢复原拓扑并退出
.\build\verify-multi-monitor.ps1 -Stage CrossScreen
.\build\verify-multi-monitor.ps1 -Stage TopologyChanged
.\build\verify-multi-monitor.ps1 -Stage Restored
.\build\verify-multi-monitor.ps1 -Stage Finalize
```

结果保存在 `artifacts\external-validation\multi-monitor\session.json` 和 `latest.md`。脚本要求至少两台显示器，记录设备名、主屏、像素矩形、工作区和有效 DPI；每个检查点会验证桌面子窗口数量及矩形、盒子可见边界、跨屏移动、拓扑确实变化、盒子 ID 不丢失或重复，以及恢复原拓扑后的正常退出。

Windows 10 与 Windows 11 的结果必须写入不同子目录，避免后一台机器覆盖前一份证据。例如在所有阶段分别传入 `-OutputDirectory ..\artifacts\external-validation\multi-monitor\windows10` 或 `...\windows11`；就绪审计会递归识别两套完整会话及其系统版本。

| 场景 | 验收内容 |
| --- | --- |
| 单屏 `100%` | 基础布局、拖放、`Win+D`、全屏覆盖 |
| 双屏相同 DPI | 每屏盒子、跨屏移动、主屏切换 |
| 双屏 `100% + 150%` | DIP/像素换算、边界命中、文字清晰度 |
| 负坐标副屏 | 左侧或上方副屏的保存、恢复和拖动 |
| 显示器拔出 | 盒子迁移到主屏并限制在可见区域 |
| 显示器重新接入 | 不产生重复盒子，布局可继续保存 |
| 分辨率切换 | 盒子尺寸和位置保持可操作 |
| 睡眠唤醒 | 桌面宿主、映射目录和托盘状态恢复 |

每个场景都要验证盒子不进入任务栏或 Alt+Tab、普通窗口能覆盖、退出后 Explorer 图标恢复。

## 正式签名

准备受信任的 Authenticode 代码签名证书及密码，然后运行：

```powershell
.\build\sign-artifacts.ps1 `
  -CertificatePath <CrabDesk-signing.pfx> `
  -CertificatePassword <password> `
  -Files @(
    "artifacts\publish\win-x64\CrabDesk.App.exe",
    "artifacts\publish\win-x64\CrabDesk.IconGuard.exe",
    "artifacts\installer\CrabDesk-Setup-x64.exe"
  )
```

签名后必须确认：

```powershell
Get-AuthenticodeSignature artifacts\publish\win-x64\CrabDesk.App.exe
Get-AuthenticodeSignature artifacts\publish\win-x64\CrabDesk.IconGuard.exe
Get-AuthenticodeSignature artifacts\installer\CrabDesk-Setup-x64.exe
```

三个状态都必须为 `Valid`，发布者名称与证书主体一致，并带有可信 SHA-256 时间戳。GitHub 仓库需要配置 `SIGNING_CERTIFICATE_BASE64` 和 `SIGNING_CERTIFICATE_PASSWORD` 两个 Actions Secret。
正式稳定版工作流会在构建开始时强制检查两个 Secret，并在创建 Release 前复核三个产物的信任状态、时间戳、代码签名 EKU 和证书指纹一致性；任一检查失败都不得生成稳定版 Release。

## GitHub 首次发布

1. 使用正式仓库 `https://github.com/yixing233/CrabDesk`，确认本地 `main` 已推送且 CI 通过。
2. 确认仓库 Actions 已启用，签名 Secrets 已配置。
3. 将版本更新为 `1.0.0`，重新执行构建、测试、主题、稳定性、安装和签名验证。
4. 创建并推送带注释标签 `v1.0.0`。
5. 确认 `release.yml` 成功生成以下资产：
   - `CrabDesk-Setup-x64.exe`
   - `CrabDesk-portable-win-x64.zip`
   - `SHA256SUMS.txt`
6. 下载 GitHub Release 资产，重新验证 SHA-256、Authenticode 签名、安装和卸载。
7. 确认 Release 使用 `docs\releases\v1.0.0.md`，并在已配置正式仓库的客户端中检查到 `1.0.0`。

正式 Release 可用后，使用以下命令一次性下载并验证发布说明、固定资产、SHA-256、便携版与安装包 Authenticode 签名、可信时间戳、发布者一致性以及隔离安装/卸载：

```powershell
.\build\verify-github-release.ps1 `
  -Owner yixing233 `
  -Repository CrabDesk `
  -Tag v1.0.0 `
  -ExpectedPublisherSubject "CN=<正式证书主体>"
```

通过后证据写入 `artifacts\external-validation\github-release\session.json` 和 `latest.md`。该证据与 GitHub Actions 成功记录共同作为首次正式发布、资产、签名和发布说明四项门槛的验收依据。

正式 Release 验证完成前，不得将开发计划中的签名、GitHub 标签发布或发布说明展示标记为完成。

推送标签前可先运行 `build\verify-release-workflow.ps1`，确认稳定版签名门槛、固定资产名称、校验文件和 `v1.0.0` 发布说明策略仍然存在；该检查不替代正式 GitHub Actions 和下载后签名验证。
