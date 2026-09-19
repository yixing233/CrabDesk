# 盒子真实背景亚克力：方案与原型验证

日期：2026-09-09

## 目标

盒子实时采样其后方内容，包括 Wallpaper Engine 动态壁纸，呈现背景模糊、染色与细微噪点组成的亚克力材质。文字、图标及交互反馈保持清晰。

## 结论

Windows Composition 的 HostBackdropBrush 已在本机独立顶层窗口原型中成功采样动态背景。当前 Explorer 子窗口上直接启用该功能失败。建议采用独立顶层 Composition 展示层承载盒子材质与前景，再验证它与现有桌面宿主的交互和层级整合。

这是一项展示层改造，不能只给现有盒子 HWND 设置背景属性。原型验证的是实时背景采样、模糊与圆角裁剪，完整亚克力的染色、噪点及产品接入仍需实现。

## 当前代码约束

- `CrabDesk.Runtime/DesktopSurfaceManager.cs` 将盒子和图标窗口挂到 `SHELLDLL_DefView`。
- `CrabDesk.Native/DesktopWindowTools.cs` 的 `AttachAsDesktopChild` 设置 `WS_CHILD` 并调用 `SetParent`。
- `CrabDesk.Runtime/DesktopBoxForm.cs` 中盒子窗口主要承担输入；盒子视觉通过共享图标层呈现。
- `CrabDesk.Runtime/DesktopBoxForm.Rendering.cs` 的 `DrawBox` 用 GDI+ 绘制背景、标题及内容。
- `CrabDesk.Runtime/DesktopIconSurface.cs` 和 `CrabDesk.Native/LayeredWindowPresenter.cs` 使用位图与 `UpdateLayeredWindow`。
- HostBackdropBrush 的像素不能读回，因此不能将系统背景画刷转成位图，直接接到现有 `UpdateLayeredWindow` 流程。

## 实测结果

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

构建成功，0 个警告、0 个错误。原型各次运行正常退出，临时窗口已关闭，桌面切换已恢复。产品业务代码未修改。

## 推荐的接入结构

### 1. 增加专用 Composition 展示层

使用独立顶层 HWND，采用 `WS_EX_NOREDIRECTIONBITMAP`、`WS_EX_NOACTIVATE`、`WS_EX_TOOLWINDOW`，不设置 `WS_CHILD`。通过 `ICompositorDesktopInterop.CreateDesktopWindowTarget` 建立视觉树。

窗口排序目标是位于桌面内容之上、普通应用窗口之下，不设置长期置顶。此目标必须专门验证与实现，原型的创建时排序不等于完整的桌面排序策略。桌面切换、普通窗口覆盖和 Explorer 重启需要纳入宿主生命周期处理。

首个接入样例只处理一个盒子，验证后再决定每盒窗口或每屏共享窗口的组织方式。

### 2. 在同一展示层组织背景与前景

从下到上：

1. `CreateHostBackdropBrush()` 产生的实时模糊背景。
2. 染色、亮度调节与轻微噪点。
3. 清晰的盒子标题、文件图标、文字和交互反馈。

背景通过圆角几何裁剪限制在盒子区域。系统画刷提供背景模糊，强度是否需要额外效果链，应在明确视觉要求后决定。

初期可复用现有 GDI+ 前景绘制，上传到 Composition DrawingSurface；这仍需实现图形互操作与资源管理。现有共享图标层必须停止重复绘制已由新层接管的盒子。仅在旧层之上放一个透明模糊窗口会采样并模糊旧层中的盒子内容。

### 3. 接入盒子状态与输入

复用现有盒子位置、大小、展开状态、排序和文件操作逻辑。新宿主需要将点击、拖放、滚动、标题编辑等事件接入这些处理流程；不能假设旧输入子窗口能自动收到被新顶层窗口覆盖区域的输入。

收起/展开时同步更新背景裁剪、前景和输入区域。多个盒子连续或交错收起时，必须验证每个盒子最终状态仍可见，避免旧位图缓存与新展示层重复拥有同一区域。

### 4. 正式接入前的验收

- 动态背景持续更新，文字和图标不被模糊。
- 自动收起/展开、快速移入移出、多个盒子交错动画均正常。
- 拖动、缩放、跨屏和不同 DPI 下的圆角、前景及输入范围一致。
- 普通应用及全屏应用遮挡正常，盒子不抢焦点。
- 连续桌面切换、任务视图往返及 Explorer 重启后恢复正确。
- 空白桌面输入、框选、右键菜单和盒子拖放可用。
- 系统关闭透明效果或进入省电策略时正确使用实色背景；这是系统材质的回退状态。
- 使用动态壁纸时测量 CPU、GPU、帧时间和内存，再确定性能指标。

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

原型目录在仓库的 `artifacts/` 忽略规则内；它是本地研究工具，不随正常 Git 提交保存。正式接入时应将需要保留的复现工具迁移到受版本控制的工具目录。

## 官方依据

- [HostBackdropBrush](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush)：采样窗口绘制之前的背景；不允许应用读回像素；受系统透明度和电源策略影响。
- [DWMWA_USE_HOSTBACKDROPBRUSH](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)：官方 Win32 启用属性，Windows 11 build 22000 起支持。
- [WinForms Composition 接入示例](https://learn.microsoft.com/en-us/windows/uwp/composition/using-the-visual-layer-with-windows-forms)：DispatcherQueue、Compositor 和 DesktopWindowTarget 的互操作结构。
