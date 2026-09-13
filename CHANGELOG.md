# 更新日志

## v20260913.02（基于线上最新版本 v20260913.01）

对比基准：`origin/main` / `e5e19eb`（`docs: clarify v20260913.01 release assets`）。

### 安装器修复

#### Windows App Runtime 检测完整修复（#1）

- 新增 `WindowsAppRuntimeDetector`：以结构化包查询替代 PowerShell `Get-AppxPackage` 检测，完整校验 Framework、Main、Singleton 与 DDLM 四类 x64 组件。
- DDLM 组件改为「最低版本兼容」判定而非精确匹配：framework/Main 包被系统服务更新到高于 DDLM 基线的版本（如 `8000.946` 配 `8000.921`）时不再误报缺少依赖。
- Windows App Runtime 安装不再请求提权（MSIX 运行时为按用户部署），并在静默安装参数中加入 `--force`，降低重复安装被跳过导致检测不一致的概率。

#### 安装器下载体验

- 新增 `DownloadSpeedEstimator`：使用 2 秒滑动窗口 + 指数平滑计算下载速度，替代每 150 ms 瞬时差值导致的速度显示抖动；下载完成时保留最终速度而非归零。
- 新增 `InstallerLogger` 结构化日志（敏感字段脱敏、日志注入清洗），记录下载尝试、重试与失败原因，便于远程排障。

### 验证与构建产物

- Release 全量测试：903 项通过（CrabDesk.Bootstrapper.Tests 46、CrabDesk.Tests 328、CrabDesk.WinUI.Tests 529）。
- 已重新生成框架依赖的安装载荷：`artifacts/installer/CrabDesk-Payload-x64.exe`。
- 已重新生成在线安装包：`artifacts/release/CrabDesk-Setup-x64.exe`。
- Release 同时提供 `CrabDesk-Payload-x64.exe` 与 `CrabDesk-Setup-x64.exe`。

## v20260913.01（基于线上最新版本 v20260911.02）

对比基准：`origin/main` / `65879ef`（`release: v20260911.02`）。

### 新增与改进

#### 桌面盒子外观设置

- 将盒子外观设置从折叠表单重构为分组卡片式设置页面。
- 使用独立的二级导航切换「盒子表面」「标题栏」「图标与标签」「桌面图标名称」，避免页面内容被 `Pivot` 的内部标题区域挤压或裁切。
- 统一卡片宽度、居中布局和响应式内容区域；移除卡片内部的分割线。
- 将颜色设置改为色块、HEX 值与原生颜色选择器组合，并将「选择颜色」明确为「选中高亮颜色」。
- 所有设置项统一为“标题 + 说明 + 右侧控件”布局。
- 统一开关尺寸，隐藏冗余的开/关文字并减少无效占位。
- 使用字体搜索/选择控件替代应用标签字体的普通文本输入框。
- 保留「恢复默认外观」操作并统一图标与文案。

#### 桌面图标缩放与网格布局

- 修正图标缩放时空白区域被同比放大的问题，只调整图标部分，保留文字和内边距比例。
- 限制稀疏网格单元格的额外宽度，避免大图标下出现过宽的选择卡片。
- 修正滚动条缩略块在极小轨道上的最小高度计算，避免固定最小值超过可用轨道。
- 新增缩放、可见布局、窄视口和大图标网格回归测试。

#### 桌面图标拖拽与跨屏操作

- 新增独立的跨显示器多选拖拽布局算法；跨屏移动时保留其他显示器的布局，不再覆盖全局布局数据。
- 当源显示器上的多选排列无法适配目标显示器时，自动采用紧凑排列，并保证操作原子性。
- 为桌面图标拖拽新增独立的顶层、透明、免激活指针预览层，使用物理像素坐标保持跨 DPI/负坐标显示器时的抓取点稳定。
- 统一盒子、桌面图标、资源管理器和无目标区域的拖拽预览，避免重复绘制拖拽幽灵。
- 混合系统图标与文件图标选择时不再生成不完整的文件拖放列表，改用 CrabDesk 私有拖拽数据。
- 盒子手动标签支持在拖拽经过标签区域时选中对应标签，并显示当前标签标题。
- 只在完整文件选择时暴露 Shell 文件拖放数据，降低误移动系统/虚拟项目的风险。

#### 桌面右键菜单

- 启动时重新注册 CrabDesk 桌面右键菜单入口，并绑定当前实际运行程序路径；退出时仍清理自有注册项。

#### 安装器与运行时依赖检测

- 修正 Windows App Runtime 版本比较：安装器现在使用实际 MSIX Runtime 版本 `8000.921.1539.0`，不再将 NuGet 包版本 `1.8.260710003` 当作已安装运行时版本。
- Windows App Runtime 检测同时确认 Framework、Main 和 Singleton 组件，降低“安装完成但启动时报 Required components missing”的误判。
- 构建脚本增加包版本格式与 MSIX Runtime 版本格式校验。

### 验证与构建产物

- Release 全量测试：896 项通过（CrabDesk.Bootstrapper.Tests 39、CrabDesk.Tests 328、CrabDesk.WinUI.Tests 529）。
- 安装器诊断验证通过，Framework、Main、Singleton 运行时组件均可检测。
- 已重新生成框架依赖的 CrabDesk 核心安装载荷：`artifacts/installer/CrabDesk-Payload-x64.exe`。
- 已重新生成在线安装包：`artifacts/release/CrabDesk-Setup-x64.exe`。
- 安装包 SHA-256：`b9f9cfcf9dfd2c22c166007772cad3cb2e82758a95a3399db10f7a796ba3475a`。
- Release 同时提供 `CrabDesk-Payload-x64.exe` 与 `CrabDesk-Setup-x64.exe`。
- Release 构建保留现有 `MVVMTK0045` 警告，未引入新的编译错误。
