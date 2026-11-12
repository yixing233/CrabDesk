# Widget Acrylic Settings Implementation Plan

**Goal:** 在组件页提供统一的亚克力背景色、不透明度与模糊强度，并明确“跟随盒子”的含义。

**Architecture:** `GlobalAppearanceSettings.WidgetAcrylic` 保存所有显式亚克力组件及歌词的共享材质。`WidgetAppearance.Resolve` 与预览共用该配置；桌面合成区域携带所属材质的模糊值，盒子及继承组件继续使用盒子设置。组件编辑器只选择材质，实色时选择主题。

**Tech Stack:** .NET 8, WinUI 3, WinForms, Windows Composition, xUnit.

在当前工作区依次执行；保留已有修改，不提交。

### 1. 共享设置与解析

- [x] 新建 `CrabDesk.Core/WidgetAcrylicSettings.cs`，定义 `Background = "#FF20242C"`、`BackgroundOpacity = .45`、`BlurStrength = 50`，颜色规范为不透明 ARGB，数值限制为 0–1 / 0–100。
- [x] 在 `CrabDesk.Core/Models.cs` 添加 `WidgetAcrylicSettings WidgetAcrylic { get; set; } = new();`，在 `CrabDesk.Core/JsonLayoutStore.cs` 加载时补齐并规范化。
- [x] 修改 `CrabDesk.Core/WidgetAppearance.cs`：显式 Acrylic 与歌词读取共享背景和不透明度，强调色按共享背景明暗解析，Solid / Inherit 保留现有行为。
- [x] 更新 `CrabDesk.Tests/WidgetAppearanceTests.cs`，增加 `CrabDesk.Tests/WidgetAcrylicSettingsTests.cs`，验证跨类型和主题一致、独立于盒子、旧配置默认值、保存加载及边界值。

### 2. 桌面合成与预览

- [x] 新建 `CrabDesk.Runtime/CrabDeskRuntime.WidgetAppearance.cs`，添加 `SetWidgetAcrylicBackground`、`SetWidgetAcrylicOpacity`、`SetWidgetAcrylicBlurStrength`；修改后调用 `NotifyTargetedWorkspaceChanged(manager => manager.RefreshBackdrop())`。
- [x] 修改 `CrabDesk.Runtime/DesktopBoxForm.Widgets.Rendering.cs` 与 `CrabDesk.WinUI/Controls/WidgetPreview.xaml.cs`，向 `Resolve` 传递共享配置。
- [x] 修改 `CrabDesk.Runtime/DesktopAcrylicHost.cs`：区域增加 `float? BlurStrength = null`，`UpdateBlur` 使用区域覆盖值；盒子保持主机默认值。
- [x] 修改 `CrabDesk.Runtime/DesktopBoxForm.Acrylic.cs`：显式 Acrylic、歌词和所属日历提示使用共享模糊强度，Inherit 使用盒子强度；前景重叠模糊读取同一规则。
- [x] 在 `CrabDesk.WinUI.Tests/BackdropControlsIntegrationTests.cs` 验证不同区域独立模糊、已有画刷实时更新及运行时设置保存通知；更新 `CrabDesk.WinUI.Tests/WidgetMaterialRuntimeTests.cs` 的共享背景预期。

### 3. 设置界面与验证

- [x] 在 `CrabDesk.WinUI/Views/WidgetsPage.xaml` 顶栏右侧提供“外观设置”按钮，进入可返回的二级视图，包含共享背景 ColorPicker、不透明度 Slider、模糊强度 Slider，两个滑块范围均为 0–100，步长 1。
- [x] 在 `CrabDesk.WinUI/Views/WidgetsPage.Navigation.cs` 管理列表、组件详情和外观设置导航；详情采用宽窗口左侧预览、右侧分组设置，窄窗口上下布局，返回保留搜索筛选与列表位置。
- [x] 在 `CrabDesk.WinUI/Views/WidgetsPage.Appearance.cs` 连接加载和事件，使用独立加载标志避免回写；在 `WidgetsPage.xaml.cs` 刷新时同步全局配置。
- [x] 将材质选项命名为“跟随盒子”；主题仅实色可见；更新 `CrabDesk.WinUI/Views/AppearancePage.xaml` 盒子亚克力说明、`docs/desktop-widgets.md` 与 `docs/design/widget-signature-palettes.md`。
- [x] 运行 `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Release --no-restore`：970 项通过。
- [x] 运行 `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~WidgetMaterialRuntimeTests|FullyQualifiedName~BackdropControlsIntegrationTests|FullyQualifiedName~WidgetGalleryItemTests|FullyQualifiedName~WidgetContentRenderingTests|FullyQualifiedName~WidgetEditorInteractionTests"`：70 项通过。
- [x] 运行 `dotnet build CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Release -p:Platform=x64 --no-restore --verbosity minimal -clp:Summary`：成功，3 条已有警告；新构建正常响应。
- [x] 实际窗口验证：二级页进入、返回详情、返回列表；不透明度和模糊滑块自动保存及恢复；颜色选择器正常展开；1040 × 720 和 760 × 520 DIP 下检查详情布局与可滚动设置。
