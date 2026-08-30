# Box Drag Scroll And Targeted Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 盒内图标重排和创建盒子只更新目标盒子区域；盒内图标拖拽期间滚轮仍可滚动目标盒子；折叠并重新展开盒子后保留当前视图及其滚动距离。

**Architecture:** 将盒子内容变化和新增盒子从 `DesktopSurfaceManager.Refresh()` 全量刷新拆到目标显示器上的盒子表面局部更新。拖拽滚轮复用现有低级鼠标钩子，在盒内 OLE 拖拽期间把普通滚轮事件路由回目标 `DesktopBoxForm`，并消费原消息避免重复滚动。折叠几何只隐藏标签栏，不再用空标签集合重置活动视图，因此现有按视图保存的 `_scrollOffsets` 能继续命中。

**Tech Stack:** .NET 8、WinForms、Win32 low-level mouse hook、GDI+/layered windows、xUnit

---

## File map

- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs` — 将重排和创建盒子的通知改为定向刷新，并接入拖拽滚轮事件。
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs` — 定向刷新盒子内容/新增盒子，暴露盒内拖拽状态与坐标滚动入口。
- Modify: `CrabDesk.Runtime/DesktopBoxForm.cs` — 泛化盒子内容局部刷新并暴露盒内拖拽状态。
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Input.cs` — 抽取可由普通鼠标事件和低级钩子共用的滚动入口。
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Geometry.cs` — 折叠时保留活动标签视图，避免切换滚动偏移键。
- Modify: `CrabDesk.Core/Interfaces.cs` — 增加拖拽滚轮事件和盒内拖拽状态谓词。
- Modify: `CrabDesk.Native/DesktopDoubleClickMonitor.cs` — 仅在盒内图标拖拽且指针位于盒子上时路由普通滚轮。
- Modify: `CrabDesk.WinUI.Tests/DesktopAssignmentRefreshTests.cs` — 覆盖重排/创建盒子的定向刷新路径。
- Modify: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs` — 覆盖拖拽滚轮策略和折叠视图保留。

### Task 1: 固定定向刷新回归

**Files:**
- Test: `CrabDesk.WinUI.Tests/DesktopAssignmentRefreshTests.cs`
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs`
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.cs`

- [ ] **Step 1: 添加失败测试**

断言 `ReorderBoxItems` 使用盒子内容定向通知且不调用 `NotifyWorkspaceChanged(true)`；断言 `AddBox` 与 `AddMappedFolderBoxAsync` 使用新增盒子定向通知；断言新增盒子路径更新目标表面的交互区域而不调用全量 `Refresh()`。

- [ ] **Step 2: 运行定向测试并确认失败**

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~BoxItemReorderUsesTargetedRefresh|FullyQualifiedName~BoxCreationUsesTargetedRefresh"
```

Expected: FAIL，因为当前方法仍直接调用 `NotifyWorkspaceChanged(true)`。

- [ ] **Step 3: 实现盒子级局部刷新**

将表面方法重命名为通用的盒子内容刷新；管理器提供 `RefreshBoxItems` 与 `RefreshBoxAdded`，后者额外更新目标表面的窗口交互区域。运行时用统一的定向通知维护 revision、`Changed` 和保存调度。

- [ ] **Step 4: 再次运行定向测试**

Expected: PASS；正常重排和创建不再进入全显示器/全表面刷新。

### Task 2: 拖拽期间路由滚轮

**Files:**
- Test: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`
- Modify: `CrabDesk.Core/Interfaces.cs`
- Modify: `CrabDesk.Native/DesktopDoubleClickMonitor.cs`
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs`
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Input.cs`

- [ ] **Step 1: 添加失败测试**

覆盖普通滚轮、盒内拖拽、桌面表面、指针位于盒子内四个条件，确认 Ctrl+滚轮继续走缩放路径且不会触发拖拽滚动。

- [ ] **Step 2: 运行定向测试并确认失败**

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~BoxDragWheel"
```

Expected: FAIL，因为低级鼠标监听器尚无盒内拖拽滚轮策略。

- [ ] **Step 3: 实现低级钩子到目标盒子的滚动链路**

在 `IDesktopInputMonitor` 增加滚轮事件与 `IsBoxItemDragActive` 谓词；低级钩子只在非 Ctrl、盒内拖拽、指针位于盒子时发送并消费事件。运行时将事件切回 UI 上下文，管理器按屏幕坐标定位表面，盒子表面复用原平滑滚动计算。

- [ ] **Step 4: 再次运行定向测试**

Expected: PASS；非拖拽滚轮和 Ctrl+滚轮行为不变。

### Task 3: 折叠后保留滚动视图

**Files:**
- Test: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Geometry.cs`

- [ ] **Step 1: 添加失败测试**

验证折叠几何仍以完整标签集合解析活动手动/映射标签，仅把最终展示集合置空，避免 `_activeManualTabIds` 或 `_activeMappedFolderCategories` 被折叠状态重置。

- [ ] **Step 2: 实现视图状态保留**

先从完整标签集合解析并校验活动视图，再根据 `isCollapsed` 决定是否展示标签栏。保持 `_scrollOffsets` 的现有按 `ItemViewKey` 存储方式。

- [ ] **Step 3: 运行相关交互测试**

Expected: PASS；展开后回到折叠前标签和对应滚动距离。

### Task 4: 完整回归与测试版本

**Files:**
- No additional code changes.

- [ ] **Step 1: 运行 WinUI 交互测试**

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore
```

- [ ] **Step 2: 运行核心测试与 Debug 构建**

```powershell
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --no-restore
dotnet build CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Debug --no-restore
```

- [ ] **Step 3: 启动测试版本**

先通过 Debug 可执行文件的 `--exit-existing` 正常关闭旧实例，再启动新构建，并确认进程路径指向当前工作区的 Debug 输出。
