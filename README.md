# CrabDesk

CrabDesk 是一个面向 Windows 10/11 的桌面盒子整理工具。盒子与桌面项目嵌入 Explorer 桌面层，不使用全局置顶窗口。

项目主页：[github.com/yixing233/CrabDesk](https://github.com/yixing233/CrabDesk)

完整功能范围和迭代顺序见 [开发计划](docs/DEVELOPMENT_PLAN.md)。

需要真实硬件、正式证书和 GitHub 仓库的发布门槛见 [外部验收清单](docs/EXTERNAL_VALIDATION.md)。

许可与隐私说明见 [LICENSE](LICENSE) 和 [PRIVACY.md](PRIVACY.md)。

## 当前功能

- 桌面嵌入式盒子，`Win+D` 和 `Win+M` 后保持可见
- 首次启动不创建演示盒子；通过桌面右键菜单“CrabDesk > 智能整理”按实际项目和规则按需创建盒子
- 接管期间隐藏 Explorer 原生图标，由 CrabDesk 单份绘制桌面项目，避免盒内项目与桌面副本重复
- 未分类图标按 Explorer 记录的位置显示；拖入盒子只更新虚拟分组，不复制文件、不改变路径
- 盒子收起时立即移除内部项目的绘制与命中区域；暂停接管或退出后恢复 Explorer 原生图标及原位置，强制结束时由 IconGuard 恢复
- 盒子圆角使用抗锯齿绘制，透明边缘保持壁纸透出，不再使用锯齿明显的二值圆角窗口裁剪
- 桌面表面只在盒子和自绘图标区域路由鼠标输入；普通窗口覆盖盒子后仍优先接收点击
- 可移动、缩放、折叠和重命名桌面盒子，分组不复制文件或改变路径
- 文件菜单支持剪切、复制、粘贴、重命名、批量移出盒子、移入回收站和属性
- 支持盒内拖拽框选、`Ctrl` 多选，以及手动排序模式下拖放调整图标顺序
- 外部拖入和映射盒子传输支持 `Shift` 移动、`Ctrl` 复制，并能跨磁盘卷安全移动
- 可配置盒子圆角、边框、缩放手柄、图标尺寸与间距，并支持网格或列表视图
- 可配置盒子背景与强调色、`35%-100%` 不透明度、标题栏高度、标签字号、仅图标模式、快捷方式角标及悬停反馈
- 折叠盒子可在鼠标悬停标题栏时临时展开，离开后自动恢复折叠
- 可控制桌面重命名、新增和删除后的自动刷新；手动重连与实时整理不受该开关阻断
- 可关闭盒子折叠/展开和主题切换动画；默认使用短时缓动，不影响桌面窗口层级
- 支持按类型、扩展名和名称通配符预览并应用自动整理规则，整理只改变虚拟分组
- 整理规则采用紧凑表格编辑视图，可直接查看启用状态、文件名模式、后缀和目标盒子
- 支持手动与每日布局备份、保留策略、导入导出及恢复前自动回滚备份
- 支持文件夹映射盒子，直接显示指定目录内容，具备只读模式、实时刷新和离线重连状态
- 支持可配置的“显示桌面”和“立即整理”全局快捷键，并实时提示注册冲突
- Windows 桌面空白处右键菜单提供“智能整理”和“打开设置”，无需管理员权限
- 关于页提供 Explorer 桌面宿主、显示器/DPI 和桌面表面诊断，可复制完整诊断信息
- 支持重置默认布局，操作前自动备份并恢复被盒子占用的 Explorer 原生图标位置
- 可选双击桌面空白区域隐藏或恢复 Explorer 与盒子中的项目图标
- 已验证与 Wallpaper Engine 的 WorkerW 动态壁纸层级兼容
- 使用 Shell 缩略图并按尺寸与文件状态缓存，缓存有容量上限且可在“关于”页手动清理
- 通过 GitHub Releases 手动或启动时检查更新，支持稳定版/测试版通道、ETag 缓存和发行说明
- 多显示器布局、盒子跨屏拖放、Explorer 重启自动恢复、配置原子保存
- 托盘、单实例、开机启动和 Explorer 图标状态看护
- 设置窗口、托盘菜单和标题栏支持浅色、深色及跟随系统主题，原生输入控件与滚动条同步换色

## 开发

```powershell
dotnet build CrabDesk.sln -c Debug
dotnet test CrabDesk.Tests\CrabDesk.Tests.csproj -c Debug
dotnet run --project CrabDesk.App\CrabDesk.App.csproj
.\build\verify-desktop.ps1
.\build\verify-hardware-validation-common.ps1
.\build\verify-opacity.ps1
.\build\verify-mapped-folders.ps1
.\build\verify-organization-stress.ps1
.\build\verify-desktop-double-click.ps1
.\build\verify-github-updates.ps1
.\build\verify-release-workflow.ps1
.\build\verify-backup-restore-ui.ps1
.\build\verify-settings-themes.ps1 -OutputDirectory artifacts\theme-validation
.\build\verify-runtime-stability.ps1 -DurationSeconds 900
.\build\verify-installer.ps1
.\build\verify-dynamic-wallpaper.ps1
.\build\verify-signing.ps1
```

正式 GitHub Release 创建后执行下载后验收：

```powershell
.\build\verify-github-release.ps1 `
  -Owner yixing233 `
  -Repository CrabDesk `
  -Tag v1.0.0 `
  -ExpectedPublisherSubject "CN=<publisher>"
```

统一验收入口：

```powershell
# 当前机器完整验收，包含真实桌面和 30 秒稳定性烟测
.\build\verify-all.ps1 -IncludeDesktop -StabilitySeconds 30

# 1.0 发布门槛，会重启 Explorer 并运行 15 分钟稳定性测试
.\build\verify-all.ps1 -IncludeDesktop -IncludeExplorerRestart -StabilitySeconds 900
```

结果写入 `artifacts\verification\latest.json` 和 `latest.md`。Explorer 重启测试会短暂关闭任务栏及资源管理器窗口，只有显式传入 `-IncludeExplorerRestart` 才会执行。

真实休眠和多屏/混合 DPI 验收采用分阶段脚本，脚本只采集和验证状态，不会自行触发休眠或断开显示器：

```powershell
.\build\verify-sleep-resume.ps1 -Stage Baseline
.\build\verify-sleep-resume.ps1 -Stage AfterResume
.\build\verify-sleep-resume.ps1 -Stage Finalize

.\build\verify-multi-monitor.ps1 -Stage Baseline -RequireMixedDpi
.\build\verify-multi-monitor.ps1 -Stage CrossScreen
.\build\verify-multi-monitor.ps1 -Stage TopologyChanged
.\build\verify-multi-monitor.ps1 -Stage Restored
.\build\verify-multi-monitor.ps1 -Stage Finalize

.\build\audit-external-readiness.ps1 -RequireReady
```

外部硬件报告写入 `artifacts\external-validation`，完整操作顺序见 [外部验收清单](docs/EXTERNAL_VALIDATION.md)。

配置保存在 `%LocalAppData%\CrabDesk`。接管期间 CrabDesk 会隐藏 Explorer 原生图标并按原位置绘制未分类项目；程序正常退出或异常终止时，`CrabDesk.IconGuard` 会恢复用户之前的显示状态和位置。

## 发布

```powershell
.\build\publish.ps1
.\build\build-installer.ps1
```

输出目录为 `artifacts\publish\win-x64`。安装包脚本位于 `installer\CrabDesk.iss`，可使用 Inno Setup 6 编译。

应用默认使用 `yixing233/CrabDesk` 检查更新，也可以在发布时覆盖构建元数据：

```powershell
.\build\publish.ps1 -GitHubOwner yixing233 -GitHubRepository CrabDesk
```

推送 `vX.Y.Z` 标签后，[release.yml](.github/workflows/release.yml) 会运行测试、构建自包含程序和 Inno Setup 安装包，并将安装包、便携版和 `SHA256SUMS.txt` 上传到 GitHub Releases。客户端不保存 GitHub Token。

`v1.0.0` 会使用 [首个正式版发布说明](docs/releases/v1.0.0.md)；其他版本默认使用 GitHub 自动生成的变更说明。
