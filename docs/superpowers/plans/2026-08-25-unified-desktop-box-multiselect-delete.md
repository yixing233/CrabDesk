# Unified Desktop and Box Multiselect Delete Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 桌面与所有盒子共享一套全局多选语义，Ctrl 点击和 Ctrl 框选可以跨区域保留选择，Delete 一次处理全部已选且可删除的项目。

**Architecture:** 各表面继续拥有自己的 `_selection`，避免重写绘制和拖拽代码；`DesktopSurfaceManager` 负责在非追加手势开始时清理其他表面。新增纯 `DesktopSelectionPolicy` 统一判断“保留还是替换全局选择”，同时构建稳定、去重的删除快照；删除期间不预先清空选择，让刷新自然移除成功删除项并保留失败或不可删除项。

**Tech Stack:** .NET 8、WinForms layered desktop surfaces、xUnit、Microsoft.VisualBasic.FileIO Recycle Bin API

---

## Required interaction semantics

| Gesture | Global selection result |
|---|---|
| 普通点击未选图标 | 清空所有区域后只选当前图标 |
| 普通点击已选图标 | 保留完整多选，确保可拖动现有选择 |
| Ctrl + 点击图标 | 跨桌面、跨盒子切换该图标的选择状态 |
| 普通框选 | 清空全局选择，再加入当前框选结果 |
| Ctrl + 框选 | 保留其他区域选择，并合并当前框选结果 |
| 右键已选图标 | 保留完整全局选择并打开项目菜单 |
| 右键未选图标 | 清空全局选择，只选右键目标 |
| 普通点击空白 | 清空全局选择并开始空框选 |
| Ctrl + 点击/框选空白 | 保留现有全局选择 |
| Delete | 删除所有表面中已选、可删除的唯一文件系统项目 |

## File map

- Create: `CrabDesk.Runtime/DesktopSelectionPolicy.cs` — 定义手势策略与删除快照构建规则。
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs` — 协调跨表面选择，执行全局删除快照。
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs` — 提供桌面表面和盒子表面的选择协调入口。
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs` — 桌面点击、框选、右键接入全局策略。
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Input.cs` — 盒子点击、框选、右键接入同一策略。
- Test: `CrabDesk.WinUI.Tests/DesktopSelectionPolicyTests.cs` — 覆盖所有选择手势和删除快照。
- Test: `CrabDesk.Tests/FileOperationServiceTests.cs` — 保留并补强回收站批量删除的文件服务契约。

### Task 1: 定义并测试全局选择手势策略

**Files:**
- Create: `CrabDesk.Runtime/DesktopSelectionPolicy.cs`
- Create: `CrabDesk.WinUI.Tests/DesktopSelectionPolicyTests.cs`

- [ ] **Step 1: 新增失败的策略测试**

创建 `DesktopSelectionPolicyTests.cs`：

```csharp
using CrabDesk.Runtime;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopSelectionPolicyTests
{
    [Theory]
    [InlineData(DesktopSelectionGesture.PrimaryItem, false, false, false)]
    [InlineData(DesktopSelectionGesture.PrimaryItem, false, true, true)]
    [InlineData(DesktopSelectionGesture.PrimaryItem, true, false, true)]
    [InlineData(DesktopSelectionGesture.PrimaryItem, true, true, true)]
    [InlineData(DesktopSelectionGesture.Marquee, false, false, false)]
    [InlineData(DesktopSelectionGesture.Marquee, true, false, true)]
    [InlineData(DesktopSelectionGesture.ContextItem, false, false, false)]
    [InlineData(DesktopSelectionGesture.ContextItem, false, true, true)]
    public void PreserveExistingSelectionMatchesUnifiedGestureRules(
        DesktopSelectionGesture gesture,
        bool additive,
        bool targetAlreadySelected,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopSelectionPolicy.PreserveExistingSelection(
                gesture,
                additive,
                targetAlreadySelected));
    }
}
```

- [ ] **Step 2: 运行定向测试并确认失败**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~DesktopSelectionPolicyTests"
```

Expected: FAIL，原因是策略类型尚不存在。

- [ ] **Step 3: 创建纯策略类型**

创建 `CrabDesk.Runtime/DesktopSelectionPolicy.cs`：

```csharp
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal enum DesktopSelectionGesture
{
    PrimaryItem,
    Marquee,
    ContextItem
}

internal sealed record DesktopDeleteSelection(
    IReadOnlyList<DesktopItemRef> DeletableItems,
    int SelectedCount,
    int BlockedCount);

internal static class DesktopSelectionPolicy
{
    internal static bool PreserveExistingSelection(
        DesktopSelectionGesture gesture,
        bool additive,
        bool targetAlreadySelected) => gesture switch
    {
        DesktopSelectionGesture.PrimaryItem => additive || targetAlreadySelected,
        DesktopSelectionGesture.Marquee => additive,
        DesktopSelectionGesture.ContextItem => targetAlreadySelected,
        _ => false
    };

    internal static DesktopDeleteSelection BuildDeleteSelection(
        IEnumerable<DesktopItemRef> selectedItems,
        IEnumerable<DesktopItemRef> deletableItems)
    {
        var selected = selectedItems
            .GroupBy(
                item => item.FileSystemPath ?? item.Key.ToString(),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var deletable = deletableItems
            .Where(item => item.FileSystemPath is not null)
            .GroupBy(item => item.FileSystemPath!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var deletablePaths = deletable
            .Select(item => item.FileSystemPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockedCount = selected.Count(item =>
            item.FileSystemPath is null ||
            !deletablePaths.Contains(item.FileSystemPath));

        return new DesktopDeleteSelection(
            deletable,
            selected.Length,
            blockedCount);
    }
}
```

- [ ] **Step 4: 运行策略测试**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~DesktopSelectionPolicyTests"
```

Expected: PASS。

### Task 2: 增加跨表面“保留或替换”协调入口

**Files:**
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs:456`
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs:519`

- [ ] **Step 1: 在管理器中实现两个强类型重载**

在 `DesktopSurfaceManager` 的 `ClearSelection` 后加入：

```csharp
internal void PrepareSelection(
    DesktopIconSurface source,
    bool preserveExisting)
{
    if (preserveExisting)
    {
        return;
    }

    foreach (var iconSurface in _iconSurfaces.Where(surface => surface != source))
    {
        iconSurface.ClearSelection();
    }
    foreach (var surface in _surfaces)
    {
        surface.ClearSelection();
    }
}

internal void PrepareSelection(
    DesktopBoxForm source,
    bool preserveExisting)
{
    if (preserveExisting)
    {
        return;
    }

    foreach (var iconSurface in _iconSurfaces)
    {
        iconSurface.ClearSelection();
    }
    foreach (var surface in _surfaces.Where(surface => surface != source))
    {
        surface.ClearSelection();
    }
}
```

源表面仍由自己的鼠标处理器更新本地 `_selection`，管理器只负责其他表面，避免额外重绘源表面。

- [ ] **Step 2: 通过运行时暴露入口**

用以下方法替代只会清空盒子选择的单向入口：

```csharp
internal void PrepareDesktopSelection(
    DesktopIconSurface source,
    bool preserveExisting) =>
    _surfaceManager?.PrepareSelection(source, preserveExisting);

internal void PrepareDesktopSelection(
    DesktopBoxForm source,
    bool preserveExisting) =>
    _surfaceManager?.PrepareSelection(source, preserveExisting);

internal void ClearDesktopSelection() =>
    _surfaceManager?.ClearSelection();
```

删除不再使用的 `ClearDesktopBoxSelection()`；用 `rg -n "ClearDesktopBoxSelection" CrabDesk.Runtime` 确认引用归零。

- [ ] **Step 3: 构建确认接口编译**

Run:

```powershell
dotnet build CrabDesk.Runtime/CrabDesk.Runtime.csproj -c Debug --no-restore
```

Expected: 此时允许因旧调用尚未替换而失败；失败只应指向 `ClearDesktopBoxSelection` 调用点，下一任务完成后恢复。

### Task 3: 桌面表面接入全局选择语义

**Files:**
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs:2330-2450`

- [ ] **Step 1: 修正右键选择**

在右键空白分支开始处调用：

```csharp
_runtime.ClearDesktopSelection();
```

在右键图标分支用策略决定是否清理其他表面：

```csharp
var key = item.Item.Key.ToString();
var targetAlreadySelected = _selection.Contains(key);
var preserveExisting = DesktopSelectionPolicy.PreserveExistingSelection(
    DesktopSelectionGesture.ContextItem,
    additive: false,
    targetAlreadySelected);
_runtime.PrepareDesktopSelection(this, preserveExisting);

var selectionChanged = false;
if (!targetAlreadySelected)
{
    _selection.Clear();
    _selection.Add(key);
    selectionChanged = true;
}
```

- [ ] **Step 2: 删除桌面点击时无条件清盒子选择的逻辑**

删除：

```csharp
_runtime.ClearDesktopBoxSelection();
```

以及描述该单向行为的注释。

- [ ] **Step 3: 接入桌面空白框选**

在 `item is null` 分支中，读取 Ctrl 后先协调全局选择：

```csharp
var additive = (Forms.Control.ModifierKeys & Forms.Keys.Control) != 0;
var preserveExisting = DesktopSelectionPolicy.PreserveExistingSelection(
    DesktopSelectionGesture.Marquee,
    additive,
    targetAlreadySelected: false);
_runtime.PrepareDesktopSelection(this, preserveExisting);
```

之后保留现有 `_selectionBase`、`_selection.Clear()` 和框选初始化逻辑。

- [ ] **Step 4: 接入桌面图标点击**

在切换本地图标选择之前加入：

```csharp
var itemKey = item.Item.Key.ToString();
var controlPressed = (Forms.Control.ModifierKeys & Forms.Keys.Control) != 0;
var targetAlreadySelected = _selection.Contains(itemKey);
var preserveExisting = DesktopSelectionPolicy.PreserveExistingSelection(
    DesktopSelectionGesture.PrimaryItem,
    controlPressed,
    targetAlreadySelected);
_runtime.PrepareDesktopSelection(this, preserveExisting);
```

后续 Ctrl 取消、普通点击未选图标时清理本地选择、拖拽捕获逻辑保持不变。

- [ ] **Step 5: 构建项目**

Run:

```powershell
dotnet build CrabDesk.Runtime/CrabDesk.Runtime.csproj -c Debug --no-restore
```

Expected: build 成功，桌面表面不再引用 `ClearDesktopBoxSelection`。

### Task 4: 盒子表面接入相同语义

**Files:**
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Input.cs:16-155`

- [ ] **Step 1: 修正盒子右键选择**

在右键图标分支中加入：

```csharp
var itemKey = item.Item.Key.ToString();
var targetAlreadySelected = _selection.Contains(itemKey);
var preserveExisting = DesktopSelectionPolicy.PreserveExistingSelection(
    DesktopSelectionGesture.ContextItem,
    additive: false,
    targetAlreadySelected);
_runtime.PrepareDesktopSelection(this, preserveExisting);
if (!targetAlreadySelected)
{
    _selection.Clear();
    _selection.Add(itemKey);
}
```

右键盒子空白并打开盒子菜单时不改变项目选择；只有右键未选图标才替换全局选择。

- [ ] **Step 2: 接入盒子图标点击**

在修改 `_selection` 前加入：

```csharp
var key = item.Item.Key.ToString();
var controlPressed = (Forms.Control.ModifierKeys & Forms.Keys.Control) != 0;
var targetAlreadySelected = _selection.Contains(key);
var preserveExisting = DesktopSelectionPolicy.PreserveExistingSelection(
    DesktopSelectionGesture.PrimaryItem,
    controlPressed,
    targetAlreadySelected);
_runtime.PrepareDesktopSelection(this, preserveExisting);
```

将该分支后续重复读取 `Forms.Control.ModifierKeys` 的代码改用 `controlPressed`，其余本地选择和拖拽逻辑不变。

- [ ] **Step 3: 接入盒子框选**

在 `box.Body.Contains(point)` 分支初始化框选前加入：

```csharp
var additive = (Forms.Control.ModifierKeys & Forms.Keys.Control) != 0;
var preserveExisting = DesktopSelectionPolicy.PreserveExistingSelection(
    DesktopSelectionGesture.Marquee,
    additive,
    targetAlreadySelected: false);
_runtime.PrepareDesktopSelection(this, preserveExisting);
```

随后以 `additive` 替换该分支中对 Ctrl 的重复读取；Ctrl 框选保留桌面和其他显示器盒子的选择，普通框选只清理其他表面并重建当前表面的选择。

- [ ] **Step 4: 运行选择策略测试和构建**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~DesktopSelectionPolicyTests"
dotnet build CrabDesk.Runtime/CrabDesk.Runtime.csproj -c Debug --no-restore
```

Expected: 测试通过，build 成功。

### Task 5: 构建稳定且可解释的全局删除快照

**Files:**
- Modify: `CrabDesk.WinUI.Tests/DesktopSelectionPolicyTests.cs`
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs:468-615`

- [ ] **Step 1: 增加删除快照测试数据辅助方法**

在测试类中加入：

```csharp
private static DesktopItemRef Item(string key, string? path = null) => new()
{
    Key = new DesktopItemKey(path is null ? "shell" : "path", key),
    DisplayName = key,
    ParsingName = path ?? key,
    FileSystemPath = path,
    Kind = path is null ? DesktopItemKind.Shell : DesktopItemKind.File
};
```

- [ ] **Step 2: 增加去重与阻止项测试**

```csharp
[Fact]
public void DeleteSelectionDeduplicatesPathsAndCountsBlockedItems()
{
    var firstPath = @"C:\Desktop\first.txt";
    var secondPath = @"C:\Mapped\second.txt";
    var firstDesktop = Item("desktop-first", firstPath);
    var firstBox = Item("box-first", firstPath.ToUpperInvariant());
    var readOnlyBox = Item("readonly-second", secondPath);
    var systemItem = Item("ThisPC");

    var result = DesktopSelectionPolicy.BuildDeleteSelection(
        [firstDesktop, firstBox, readOnlyBox, systemItem],
        [firstDesktop]);

    Assert.Equal(3, result.SelectedCount);
    Assert.Single(result.DeletableItems);
    Assert.Equal(firstPath, result.DeletableItems[0].FileSystemPath);
    Assert.Equal(2, result.BlockedCount);
}
```

- [ ] **Step 3: 运行测试并确认快照实现通过**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~DeleteSelectionDeduplicatesPathsAndCountsBlockedItems"
```

Expected: PASS；Task 1 创建的 `BuildDeleteSelection` 已提供所需行为。

- [ ] **Step 4: 管理器统一生成删除快照**

在 `DesktopSurfaceManager` 加入：

```csharp
private DesktopDeleteSelection GetDeleteSelection() =>
    DesktopSelectionPolicy.BuildDeleteSelection(
        GetSelectedItems(),
        GetSelectedFileSystemItems());
```

将 `CanDeleteSelectedItems` 改为检查任意 CrabDesk 选择，以确保只读项或系统图标被选中时 Delete 仍由 CrabDesk 吸收：

```csharp
internal bool CanDeleteSelectedItems =>
    !_deleteInProgress &&
    !_surfaces.Any(surface => surface.IsTitleEditing) &&
    GetDeleteSelection().SelectedCount > 0;
```

- [ ] **Step 5: 不再在删除前清空全局选择**

将 `DeleteSelectedItemsAsync` 开头改为快照模式，并删除当前的 `ClearSelection();`：

```csharp
var selection = GetDeleteSelection();
if (selection.SelectedCount == 0)
{
    return;
}

_deleteInProgress = true;
```

删除目标固定使用：

```csharp
await _runtime.FileOperations.DeleteAsync(selection.DeletableItems);
```

刷新成功后，各表面的现有 `RemoveWhere` 会移除已不存在项目；失败或只读项目仍保持选择，便于用户识别和重试。

### Task 6: 删除确认、不可删除提示和失败反馈

**Files:**
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs:571-609`

- [ ] **Step 1: 添加统一确认方法**

在 `DesktopSurfaceManager` 加入：

```csharp
private async Task<bool> ConfirmDeleteAsync(DesktopDeleteSelection selection)
{
    var owner = (System.Windows.Forms.Form?)_surfaces.FirstOrDefault() ??
        _iconSurfaces.FirstOrDefault();
    if (owner is null)
    {
        return false;
    }

    var count = selection.DeletableItems.Count;
    var skipped = selection.BlockedCount > 0
        ? $"\n其中 {selection.BlockedCount} 个只读或系统项目不会被删除。"
        : string.Empty;
    var title = count == 1
        ? "将所选项目移入回收站？"
        : $"将所选的 {count} 个项目移入回收站？";
    var message = $"项目将移入回收站，可从回收站恢复。{skipped}";
    var handler = _runtime.DesktopConfirmationHandler;
    if (handler is null)
    {
        return DesktopConfirmationDialog.Show(
            owner,
            _runtime.IsDarkTheme,
            title,
            message,
            "移入回收站");
    }

    return await handler(new DesktopConfirmationRequest(
        owner.Handle,
        title,
        message,
        "移入回收站"));
}
```

- [ ] **Step 2: 添加不可删除与失败消息辅助方法**

```csharp
private void ShowDeleteMessage(string title, string message, DesktopDialogKind kind)
{
    var owner = (System.Windows.Forms.Form?)_surfaces.FirstOrDefault() ??
        _iconSurfaces.FirstOrDefault();
    if (owner is not null)
    {
        DesktopConfirmationDialog.ShowMessage(
            owner,
            _runtime.IsDarkTheme,
            title,
            message,
            kind);
    }
}
```

- [ ] **Step 3: 完成删除控制流**

把 `DeleteSelectedItemsAsync` 的主体调整为：

```csharp
var selection = GetDeleteSelection();
if (selection.SelectedCount == 0)
{
    return;
}

_deleteInProgress = true;
var deleteAttempted = false;
try
{
    if (selection.DeletableItems.Count == 0)
    {
        ShowDeleteMessage(
            "无法删除所选项目",
            "所选项目属于只读映射目录或系统桌面项目。",
            DesktopDialogKind.Warning);
        return;
    }

    if (!await ConfirmDeleteAsync(selection))
    {
        return;
    }

    deleteAttempted = true;
    await _runtime.FileOperations.DeleteAsync(selection.DeletableItems);
}
catch (Exception exception)
{
    DiagnosticLog.Error("Failed to delete selected desktop items.", exception);
    ShowDeleteMessage("删除失败", exception.Message, DesktopDialogKind.Error);
}
finally
{
    if (deleteAttempted)
    {
        try
        {
            await _runtime.RefreshItemsAsync(false);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to refresh desktop items after deletion.", exception);
        }
    }
    _deleteInProgress = false;
}
```

确认取消时不刷新、不清除选择；操作部分失败时刷新会去掉已成功删除的项，并保留仍存在的失败项。

- [ ] **Step 4: 运行完整 WinUI 测试**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore
```

Expected: 全部通过，不新增测试失败。

### Task 7: 验证文件服务仍使用回收站批量删除

**Files:**
- Modify: `CrabDesk.Tests/FileOperationServiceTests.cs`
- No change expected: `CrabDesk.Native/FileOperationService.cs:91-110`

- [ ] **Step 1: 增加空集合无操作测试**

```csharp
[Fact]
public async Task DeleteWithNoFileSystemItemsCompletesWithoutError()
{
    var systemItem = new DesktopItemRef
    {
        Key = new DesktopItemKey("shell", "ThisPC"),
        DisplayName = "此电脑",
        ParsingName = "::{ThisPC}",
        FileSystemPath = null,
        Kind = DesktopItemKind.Shell
    };

    await new FileOperationService().DeleteAsync([systemItem]);
}
```

- [ ] **Step 2: 运行文件服务测试**

Run:

```powershell
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~FileOperationServiceTests"
```

Expected: PASS；现有服务继续使用 `RecycleOption.SendToRecycleBin`，不引入永久删除路径。

### Task 8: 手工验收跨区域多选删除

**Files:**
- No code changes.

- [ ] **Step 1: 启动 Debug 版本**

Run:

```powershell
dotnet run --project CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Debug --no-build
```

- [ ] **Step 2: 验证跨区域 Ctrl 点击**

依次 Ctrl 点击桌面图标、盒子 A 图标、盒子 B 图标。

Expected: 三个区域的选中高亮同时保留；再次 Ctrl 点击任一项只取消该项。

- [ ] **Step 3: 验证跨区域 Ctrl 框选**

先选择盒子图标，再按 Ctrl 在桌面框选；随后按 Ctrl 在另一个盒子框选。

Expected: 每次框选都与全局选择合并，不清除先前区域。

- [ ] **Step 4: 验证普通手势替换选择**

在已有跨区域多选时，普通点击一个未选图标，然后在空白处普通框选。

Expected: 普通点击只留下当前图标；普通框选只留下当前框选结果。

- [ ] **Step 5: 验证右键规则**

在跨区域多选中右键一个已选图标，再右键一个未选图标。

Expected: 第一次保留所有选择；第二次只选择右键目标。

- [ ] **Step 6: 验证全局 Delete**

创建可恢复的临时文件，分别放在桌面和可写盒子中，跨区域选中后按 Delete 并确认。

Expected: 确认框显示去重后的可删除数量；所有目标进入回收站；界面刷新后成功删除项不再选中。

- [ ] **Step 7: 验证阻止项和失败项**

选择只读映射项目或系统图标后按 Delete；再混合选择可删除项目与只读项目。

Expected: 只有阻止项时显示不可删除提示且按键不透传给 Explorer；混合选择时确认框显示跳过数量，可删除项进入回收站，只读项保持选择。

### Task 9: 最终回归与范围检查

**Files:**
- No code changes.

- [ ] **Step 1: 运行完整测试**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --no-restore
```

Expected: 两个项目全部通过。

- [ ] **Step 2: 检查选择与删除变更范围**

Run:

```powershell
git diff -- CrabDesk.Runtime/DesktopSelectionPolicy.cs CrabDesk.Runtime/DesktopSurfaceManager.cs CrabDesk.Runtime/CrabDeskRuntime.cs CrabDesk.Runtime/DesktopIconSurface.cs CrabDesk.Runtime/DesktopBoxForm.Input.cs CrabDesk.WinUI.Tests/DesktopSelectionPolicyTests.cs CrabDesk.Tests/FileOperationServiceTests.cs
```

Expected: 只包含全局选择协调、删除快照、用户反馈和相关测试；不修改文件永久删除方式，也不夹带其他功能改动。
