# Widget Corner Radius Implementation Plan

**Goal:** 在小组件外观设置中统一调节组件圆角，独立保存并同步桌面与预览。

**Architecture:** `GlobalAppearanceSettings.WidgetCornerRadius` 保存独立参数，加载旧配置时从现有 `CornerRadius` 初始化。组件绘制、布局与裁切使用 `ResolveWidgetCornerRadius()`，盒子保留原参数。原生窗口区域支持每个矩形各自的圆角。

**Tech Stack:** .NET 8, C#, WinUI 3, GDI+, Win32, xUnit.

- [x] 在 `CrabDesk.Core/Models.cs` 增加参数与解析，`JsonLayoutStore.cs` 处理旧配置；验证保存恢复、兼容与范围。
- [x] 在 `CrabDesk.Runtime/CrabDeskRuntime.WidgetAppearance.cs` 增加更新入口；组件绘制、布局、输入和亚克力区域使用组件参数。
- [x] 在 `CrabDesk.Native/DesktopWindowTools.cs` 支持不同圆角的区域合并，保留原调用兼容。
- [x] 在 `WidgetsPage.xaml` 外观页增加独立的“组件圆角”分组与 0–20 DIP 滑块，连接加载、实时更新和预览。
- [x] 运行 Core 与相关 WinUI 测试、Release 构建，启动并验证调节、保存与原值恢复。

验证结果：Core 983 项、相关 WinUI 测试 283 项通过。Release 构建成功（0 个错误）。原生 UI 自动化确认 0、20 DIP 均持久化且盒子圆角不变，最后恢复原有 20 DIP。新构建保持运行在外观设置页，并完成窗口截图检查。
