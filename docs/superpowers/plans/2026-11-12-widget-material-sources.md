# Widget Material Sources Implementation Plan

**Goal:** 组件提供实色与亚克力两种材质，统一选择亚克力来源，并保留原有组件内容层次。

**Architecture:** `WidgetAcrylicSettings.FollowBoxAppearance` 默认开启；旧的 Inherit 值兼容为亚克力。桌面、预览与模糊区域共用来源判断，独立参数保留在原设置中。

**Tech Stack:** C#, .NET 8, WinUI 3, GDI+, Windows Composition, xUnit.

- [x] 更新 `CrabDesk.Core/WidgetAppearance.cs`、`WidgetAcrylicSettings.cs`，统一来源解析；在 `CrabDesk.Tests/WidgetAcrylicSettingsTests.cs` 验证新旧材质一致、配置往返和主机条件。
- [x] 更新 `CrabDesk.Runtime/CrabDeskRuntime.WidgetAppearance.cs`、`DesktopBoxForm.Acrylic.cs`，切换来源时刷新合成主机，统一取用模糊参数。
- [x] 更新 `CrabDesk.WinUI/Views/WidgetsPage.xaml` 与 `WidgetsPage.Appearance.cs`：两种材质、全局跟随开关、独立参数区。
- [x] 更新桌面与预览的时钟绘制：亚克力保留独立表盘，实色保留现有设计。验证内容面板与旧样式相同。
- [x] 更新材质文档，运行 Core 与相关 WinUI 测试及 Release 构建，启动应用检查设置交互。

验证：Core 976 项、相关 WinUI 测试 73 项通过；Release 构建成功，3 个已有警告。原生 UI 自动化确认材质只有两个选项，来源开关正常切换控件、保留独立参数并持久化。应用已启动并返回组件列表。
