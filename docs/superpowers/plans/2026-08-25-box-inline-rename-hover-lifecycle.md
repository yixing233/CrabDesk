# Box Inline Rename Hover Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 盒子图标进入重命名后，鼠标移出盒子不再触发悬停收起；点击任一桌面表面时提交重命名，并在编辑结束后按真实指针位置恢复正常收起逻辑。

**Architecture:** 保留 `DesktopRenameEditor` 作为唯一编辑器，在 `DesktopSurfaceManager` 增加跨桌面层、跨盒子层的编辑提交协调。`DesktopBoxForm` 将活动中的图标重命名视为与上下文菜单相同的“悬停状态暂停原因”，并在编辑结束后只排队一次指针重算。

**Tech Stack:** .NET 8、WinForms layered desktop surfaces、xUnit、CrabDesk.Runtime、CrabDesk.WinUI.Tests

---

## File map

- Modify: `CrabDesk.Runtime/DesktopRenameEditor.cs` — 让外部提交返回是否真正结束了活动编辑。
- Modify: `CrabDesk.Runtime/DesktopBoxForm.cs` — 暴露活动重命名状态，扩展悬停暂停策略。
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Input.cs` — 在鼠标离开、悬停重算和计时器路径中冻结自动收起。
- Modify: `CrabDesk.Runtime/DesktopBoxForm.ContextMenus.cs` — 编辑结束后恢复一次真实指针状态。
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs` — 桌面表面点击时提交任意表面的活动重命名。
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs` — 统一查找并提交桌面或盒子中的活动编辑器。
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs` — 为两个表面提供协调入口。
- Test: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs` — 覆盖重命名期间的悬停冻结策略。

### Task 1: 用测试固定悬停暂停条件

**Files:**
- Modify: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs:172`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.cs:563`

- [ ] **Step 1: 扩展现有悬停暂停测试**

把现有 `OpenBoxMenuSuspendsHoverStateUntilItCloses` 替换为同时覆盖菜单和重命名的测试：

```csharp
[Theory]
[InlineData(false, false, false)]
[InlineData(true, false, true)]
[InlineData(false, true, true)]
[InlineData(true, true, true)]
public void MenuOrInlineRenameSuspendsHoverStateUntilInteractionCompletes(
    bool menuOpen,
    bool inlineRenameActive,
    bool expected)
{
    Assert.Equal(
        expected,
        DesktopBoxForm.ShouldSuspendHoverState(
            menuOpen ? Guid.NewGuid() : null,
            inlineRenameActive));
}
```

- [ ] **Step 2: 运行定向测试并确认失败**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~MenuOrInlineRenameSuspendsHoverStateUntilInteractionCompletes"
```

Expected: FAIL，原因是 `ShouldSuspendHoverState` 尚未接受 `inlineRenameActive` 参数。

- [ ] **Step 3: 扩展纯策略方法**

在 `DesktopBoxForm.cs` 中修改为：

```csharp
internal static bool ShouldSuspendHoverState(
    Guid? openBoxMenuBoxId,
    bool inlineRenameActive) =>
    openBoxMenuBoxId is not null || inlineRenameActive;
```

- [ ] **Step 4: 暂时更新全部调用点以恢复编译**

将 `DesktopBoxForm.Input.cs` 中原有的两个调用先改为：

```csharp
ShouldSuspendHoverState(_openBoxMenuBoxId, inlineRenameActive: false)
```

- [ ] **Step 5: 再次运行定向测试**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~MenuOrInlineRenameSuspendsHoverStateUntilInteractionCompletes"
```

Expected: PASS。

### Task 2: 在盒子表面冻结重命名期间的自动收起

**Files:**
- Modify: `CrabDesk.Runtime/DesktopBoxForm.cs:1142`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Input.cs:428-615`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.ContextMenus.cs:499-546`

- [ ] **Step 1: 暴露准确的活动编辑状态**

在 `DesktopBoxForm` 的内部状态属性附近加入：

```csharp
internal bool HasActiveInlineRename =>
    _renamingBoxId is not null &&
    _renamingItemKey is not null &&
    _renameEditor?.IsActive == true;
```

这个判断同时要求编辑器活动且盒子、图标标识存在，避免编辑器隐藏后的异步尾部短暂误判。

- [ ] **Step 2: 鼠标离开时直接保留悬停展开状态**

把 `OnMouseLeave` 的保护条件改为：

```csharp
if (ShouldSuspendHoverState(_openBoxMenuBoxId, HasActiveInlineRename) ||
    _runtime.IsDesktopIconPointerInteractionActive ||
    _movingBox is not null || _resizingBox is not null)
{
    return;
}
```

- [ ] **Step 3: 阻止排队后的重算绕过保护**

把 `ReconcileHoverAtCursor` 的保护条件改为：

```csharp
if (ShouldSuspendHoverState(_openBoxMenuBoxId, HasActiveInlineRename) ||
    _runtime.IsDesktopIconPointerInteractionActive ||
    _movingBox is not null || _resizingBox is not null || IsDisposed)
{
    return;
}
```

- [ ] **Step 4: 阻止 25ms 悬停计时器收起盒子**

把 `OnHoverTimer` 中的菜单保护改为：

```csharp
if (ShouldSuspendHoverState(_openBoxMenuBoxId, HasActiveInlineRename))
{
    return;
}
```

该分支必须位于读取指针位置和 `ClearHoverState()` 之前，保证移出窗口不会清空 `_hoverExpandedBoxes`。

- [ ] **Step 5: 编辑结束后恢复一次真实指针判定**

在 `ShowInlineRenameAsync` 的 `finally` 中按以下顺序清理：

```csharp
finally
{
    _renamingBoxId = null;
    _renamingItemKey = null;
    RequestVisualLayerRender();
    QueueHoverReconcile();
}
```

先清除编辑状态，再排队重算；如果指针已在盒外，重算会正常收起，如果仍在盒内则继续保持展开。

- [ ] **Step 6: 运行悬停策略测试集**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~DesktopIconInteractionTests"
```

Expected: PASS。

### Task 3: 统一跨表面的外部点击提交

**Files:**
- Modify: `CrabDesk.Runtime/DesktopRenameEditor.cs:112-121`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.cs:1152`
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs:590`
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs:456`
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs:519`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Input.cs:16`
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs:2328`

- [ ] **Step 1: 让编辑器报告外部提交结果**

将 `DesktopRenameEditor.CommitExternally` 改为：

```csharp
internal bool CommitExternally()
{
    if (!IsActive)
    {
        return false;
    }

    Complete(commit: true);
    return true;
}
```

- [ ] **Step 2: 两种表面都提供一致的提交入口**

在 `DesktopBoxForm` 和 `DesktopIconSurface` 各加入：

```csharp
internal bool CommitActiveInlineRename() =>
    _renameEditor?.CommitExternally() == true;
```

- [ ] **Step 3: 由表面管理器提交唯一活动编辑器**

在 `DesktopSurfaceManager` 加入：

```csharp
internal bool CommitActiveInlineRename()
{
    foreach (var iconSurface in _iconSurfaces)
    {
        if (iconSurface.CommitActiveInlineRename())
        {
            return true;
        }
    }

    foreach (var surface in _surfaces)
    {
        if (surface.CommitActiveInlineRename())
        {
            return true;
        }
    }

    return false;
}
```

只提交第一个活动编辑器，因为产品同时只允许一个桌面内联重命名会话。

- [ ] **Step 4: 通过运行时暴露协调入口**

在 `CrabDeskRuntime` 的桌面表面辅助方法附近加入：

```csharp
internal bool CommitActiveDesktopInlineRename() =>
    _surfaceManager?.CommitActiveInlineRename() == true;
```

- [ ] **Step 5: 替换两个表面的本地提交调用**

将 `DesktopBoxForm.OnMouseDown` 和 `DesktopIconSurface.OnMouseDown` 开头的：

```csharp
_renameEditor?.CommitExternally();
```

替换为：

```csharp
_runtime.CommitActiveDesktopInlineRename();
```

点击重命名输入框本身由独立编辑窗口接收，不会经过表面 `OnMouseDown`；点击同盒空白、其他盒子、桌面或其他显示器时则会提交编辑，并继续处理该次点击的正常选择行为。

- [ ] **Step 6: 构建运行时与测试项目**

Run:

```powershell
dotnet build CrabDesk.Runtime/CrabDesk.Runtime.csproj -c Debug --no-restore
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore
```

Expected: build 成功，测试全部通过；已有分析器警告可以保留，但不得新增编译错误或测试失败。

### Task 4: 手工验收重命名生命周期

**Files:**
- No code changes.

- [ ] **Step 1: 启动 Debug 版本**

Run:

```powershell
dotnet run --project CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Debug --no-build
```

- [ ] **Step 2: 验证移出不收起**

在启用“悬停自动展开”的盒子中开始图标重命名，把鼠标移到盒外并停留至少 1 秒。

Expected: 输入框仍存在，盒子保持展开，名称可继续输入。

- [ ] **Step 3: 验证盒内点击**

再次重命名，点击同一盒子的空白区域或其他图标。

Expected: 当前名称提交，退出重命名；点击目标继续执行正常选择行为，盒子不会异常闪烁。

- [ ] **Step 4: 验证盒外点击**

再次重命名，点击普通桌面、其他盒子以及另一显示器的桌面。

Expected: 当前名称提交；原盒子在编辑结束后的指针重算中收起；点击目标正常响应。

- [ ] **Step 5: 验证键盘和外部应用**

分别测试 Enter、Esc、Alt+Tab 后点击其他应用。

Expected: Enter 提交，Esc 取消，切换到其他应用仍按现有失焦逻辑提交；没有卡住、重复提交或盒子抽搐。

### Task 5: 最终回归

**Files:**
- No code changes.

- [ ] **Step 1: 运行完整测试**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --no-restore
```

Expected: 两个测试项目全部通过。

- [ ] **Step 2: 检查变更范围**

Run:

```powershell
git diff -- CrabDesk.Runtime/DesktopRenameEditor.cs CrabDesk.Runtime/DesktopBoxForm.cs CrabDesk.Runtime/DesktopBoxForm.Input.cs CrabDesk.Runtime/DesktopBoxForm.ContextMenus.cs CrabDesk.Runtime/DesktopIconSurface.cs CrabDesk.Runtime/DesktopSurfaceManager.cs CrabDesk.Runtime/CrabDeskRuntime.cs CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs
```

Expected: 只包含重命名协调、悬停暂停、编辑结束重算和相应测试，不夹带布局、AI 分类或备份功能修改。
