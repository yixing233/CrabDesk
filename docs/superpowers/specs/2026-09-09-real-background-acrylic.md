# 盒子真实背景亚克力：实现与验证

日期：2026-09-09

## 目标

盒子实时采样其后方内容，包括 Wallpaper Engine 动态壁纸，呈现背景模糊、染色与细微噪点组成的亚克力材质。文字、图标及交互反馈保持清晰。

## 结论

已在 `codex/real-background-acrylic` 接入真实背景亚克力。入口为“盒子外观 → 盒子表面 → 亚克力背景”，默认关闭，现有配置保持原显示方式。开启后，不透明度滑块显示为“染色强度”，背景色继续决定材质色调。

顶层 Composition 宿主提供背景模糊，现有 GDI+ 分层子窗口提供染色、缓存噪点、清晰前景和输入。没有新增图形 NuGet 依赖。Windows 10 保留原绘制路径；系统禁用高级透明效果或启用高对比度时，回到原背景绘制。

## 接入前的结构

- `CrabDesk.Runtime/DesktopSurfaceManager.cs` 将盒子和图标窗口挂到 `SHELLDLL_DefView`。
- `CrabDesk.Native/DesktopWindowTools.cs` 的 `AttachAsDesktopChild` 设置 `WS_CHILD` 并调用 `SetParent`。
- `CrabDesk.Runtime/DesktopBoxForm.cs` 中盒子窗口主要承担输入；盒子视觉通过共享图标层呈现。
- `CrabDesk.Runtime/DesktopBoxForm.Rendering.cs` 的 `DrawBox` 用 GDI+ 绘制背景、标题及内容。
- `CrabDesk.Runtime/DesktopIconSurface.cs` 和 `CrabDesk.Native/LayeredWindowPresenter.cs` 使用位图与 `UpdateLayeredWindow`。
- HostBackdropBrush 的像素不能读回，因此不能将系统背景画刷转成位图，直接接到现有 `UpdateLayeredWindow` 流程。

## 初始可行性验证

环境：Windows build 26100.3194；CrabDesk.WinUI 和 Wallpaper Engine 正在运行。原型为 .NET 8 WinForms，使用系统 `Windows.UI.Composition`。

| 场景 | 结果 | 证据与限制 |
| --- | --- | --- |
| 独立顶层窗口＋移动条纹和交替红蓝背景 | 通过 | 背景模糊随颜色变化，绿色前景标记清晰 |
| 独立顶层窗口收起至 60 像素，再展开 | 通过 | 通过更新视觉大小及圆角裁剪实现；不是现有盒子的完整动画流程 |
| 挂到现有 `SHELLDLL_DefView` 后启用背景采样 | 失败 | 属性返回 `0x80070006`，背景黑色，但绿色 Composition 标记仍可见 |
| 顶层时启用属性，再挂到 `SHELLDLL_DefView` | 失败 | 挂接前成功，挂接后背景仍为黑色 |
| 非置顶顶层窗口＋Wallpaper Engine | 通过 | 能看到动态壁纸被模糊，圆角外的壁纸保持清晰 |
| 上述顶层窗口中切换桌面两次 | 本轮通过 | 返回桌面后仍有模糊与绿色标记；尚未覆盖连续多轮、任务视图、Explorer 重启 |

对 Wallpaper Engine 场景第 2 秒和第 6 秒截图的盒子内部固定区域采样：33,000 个像素中有 18,625 个发生变化，RGB 绝对差累计 91,915。两帧均为展开状态。该结果支持背景持续更新，不能替代帧率或延迟测试。

初始原型构建成功，0 个警告、0 个错误。

## 已实现结构

### 1. 增加专用 Composition 展示层

`DesktopAcrylicHost` 使用独立顶层 HWND，保留窗口重定向，设置 `WS_EX_NOACTIVATE`、`WS_EX_TOOLWINDOW`，通过 `DwmExtendFrameIntoClientArea` 扩展透明客户区。`CreateDesktopWindowTarget` 的 `isTopmost=false` 使背景视觉树位于分层子窗口之下。测试发现 `WS_EX_NOREDIRECTIONBITMAP` 会使当前分层子窗口前景无法显示，因而采用保留重定向的组合。

`DesktopAcrylicWindowTools` 将宿主关联为桌面根窗口的 owned popup，并将它放在桌面根窗口紧上方；每 500 毫秒校正排序。普通应用位于其上方，宿主不请求激活、不长期置顶。

一个宿主覆盖虚拟桌面，窗口区域限制为各显示器工作区的并集。开启亚克力时，桌面图标层继续挂在 Explorer 桌面下，位于宿主背后并参与 HostBackdropBrush 采样；盒子前景与输入层挂在宿主上方，从而保持文字和图标清晰。未开启亚克力时仍使用原有共享图标/盒子位图路径。隐藏桌面图标、隐藏应用及恢复显示时，由管理器统一恢复两个窗口栈。

### 2. 在同一展示层组织背景与前景

从下到上：

1. `CreateHostBackdropBrush()` 产生的实时模糊背景。
2. 染色与轻微噪点。
3. 清晰的盒子标题、文件图标、文字和交互反馈。

背景通过圆角几何裁剪限制在盒子区域。系统画刷提供背景模糊，强度是否需要额外效果链，应在明确视觉要求后决定。

前景继续由现有 `UpdateLayeredWindow` 子窗口显示，无须读回系统背景或上传 DrawingSurface。桌面图标位于宿主背后，因此盒子能实时模糊壁纸与其下方图标。其他盒子的前景仍位于宿主上方，不进入当前 HostBackdropBrush 采样；若需要盒子间互相模糊，仍需把各盒子迁入有明确前后顺序的 Composition 视觉树。亚克力开启时，原背景不透明度映射为其 72% 的染色透明度，保留动态背景可见性。64×64 噪点纹理按盒子绘制表面缓存并随其释放。

### 3. 接入盒子状态与输入

复用现有盒子位置、大小、展开状态、排序、文件操作及输入窗口。顶层宿主通过 `HTTRANSPARENT` 将命中交给子窗口；全局鼠标监听通过宿主标记识别新的桌面子树，保留 Ctrl+滚轮等路由。

每次静态或动态盒子绘制时，从全部盒子的当前几何更新背景区域，包括已完成收起的盒子。区域使用显示器物理坐标及 DPI 换算；跨屏拖动由正在变换的源表面优先提供几何。

## 接入后的验证

- Release 构建成功；存在原有 19 条 `MVVMTK0045` 警告。
- WinUI 完整测试：500 通过；Core/Native 测试：297 通过。
- 新测试覆盖配置兼容与往返保存、透明度、负坐标和 DPI 换算、已收起盒子保留及删除后的区域移除。
- `tools/AcrylicSmokeTest` 使用真实 Runtime 盒子与背景宿主。动态红蓝条纹、清晰标题、两个盒子交错收起/展开已视觉验证。
- Wallpaper Engine 下，整屏宿主的模糊背景、38 像素标题条和桌面切换已验证，指针命中可识别为新桌面表面。
- 管理器创建、隐藏桌面图标、应用隐藏/显示、恢复图标、刷新及释放连续执行三轮通过。

尚需实机覆盖混合 DPI 多屏拖动、Explorer 重启、系统高对比度/电源策略切换，以及长时间 GPU/帧时间测量。原有交互函数被复用，但这不等于已逐项实测所有文件拖放和标题编辑路径。

当前接入验证截图：`%TEMP%/CrabDesk-AcrylicSmokeTest/20260909-230306/frame-10.png`。运行方式见 `tools/AcrylicSmokeTest/README.md`。

## 复现与证据

原型源码：`artifacts/acrylic-probe-20260909/Program.cs`。

```powershell
dotnet build artifacts/acrylic-probe-20260909/AcrylicProbe.csproj -c Release
dotnet run --project artifacts/acrylic-probe-20260909/AcrylicProbe.csproj -c Release --no-build
dotnet run --project artifacts/acrylic-probe-20260909/AcrylicProbe.csproj -c Release --no-build -- --desktop
dotnet run --project artifacts/acrylic-probe-20260909/AcrylicProbe.csproj -c Release --no-build -- --desktop --pre-enable
dotnet run --project artifacts/acrylic-probe-20260909/AcrylicProbe.csproj -c Release --no-build -- --wallpaper-top
dotnet run --project artifacts/acrylic-probe-20260909/AcrylicProbe.csproj -c Release --no-build -- --wallpaper-top --toggle-test
```

每次约运行 7 秒；桌面场景会临时切换到桌面后恢复。程序使用固定测试位置 `(650,260)` 和大小 `640×330`，换屏幕后需调整。`--toggle-test` 与 `--wallpaper-top` 一起使用。

截图和日志位于 `%TEMP%/CrabDesk-AcrylicProbe-20260909`。当前机器的展开截图为 `C:/Users/Administrator/AppData/Local/Temp/CrabDesk-AcrylicProbe-20260909/wallpaper-top-2.png`，收起截图为同目录的 `wallpaper-top-4.png`。

早期原型在仓库的 `artifacts/` 忽略规则内；正式实现的复现工具已放到可版本控制的 `tools/AcrylicSmokeTest`。

## 官方依据

- [HostBackdropBrush](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush)：采样窗口绘制之前的背景；不允许应用读回像素；受系统透明度和电源策略影响。
- [DWMWA_USE_HOSTBACKDROPBRUSH](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)：官方 Win32 启用属性，Windows 11 build 22000 起支持。
- [WinForms Composition 接入示例](https://learn.microsoft.com/en-us/windows/uwp/composition/using-the-visual-layer-with-windows-forms)：DispatcherQueue、Compositor 和 DesktopWindowTarget 的互操作结构。
