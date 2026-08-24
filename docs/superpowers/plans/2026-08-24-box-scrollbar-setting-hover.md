# Box Scrollbar Setting and Hover Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persistent appearance setting for box content scrollbars and prevent an icon hover highlight from persisting while the scrollbar is dragged.

**Architecture:** The global appearance model owns the default-on setting and the WinUI settings page binds it through the existing runtime/service/view-model path. The desktop form treats a disabled setting as no scrollbar layout, which removes drawing and hit-testing together. Scrollbar capture explicitly clears the current icon hover before directly scrolling the content.

**Tech Stack:** C# 12, .NET 8, WinUI 3, Windows Forms desktop overlay, xUnit.

---

### Task 1: Persist the global appearance setting

**Files:**
- Modify: `CrabDesk.Core/Models.cs:506-515`
- Modify: `CrabDesk.Tests/PersistenceTests.cs:35-45, 84-91`

- [ ] **Step 1: Extend the round-trip persistence test**

Add a false value before saving and assert the false value after loading:

```csharp
state.Settings.Appearance.ShowBoxScrollBar = false;
// ...
Assert.False(loaded.Settings.Appearance.ShowBoxScrollBar);
```

- [ ] **Step 2: Run the focused test and verify it fails to compile**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter FullyQualifiedName~PersistenceTests.StateIsSavedAndLoadedAtomically -c Release`

Expected: FAIL because `GlobalAppearanceSettings.ShowBoxScrollBar` does not exist.

- [ ] **Step 3: Add the default-on model property**

Insert the setting next to other global box-surface controls:

```csharp
public bool ShowResizeGrip { get; set; } = true;
public bool ShowBoxScrollBar { get; set; } = true;
```

The default must remain `true` so existing saved configurations and current behavior continue to show a scrollbar when content overflows.

- [ ] **Step 4: Run the focused persistence test**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter FullyQualifiedName~PersistenceTests.StateIsSavedAndLoadedAtomically -c Release`

Expected: PASS.

### Task 2: Expose the setting in the appearance UI

**Files:**
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs:1942-1957`
- Modify: `CrabDesk.WinUI/Services/ICrabDeskService.cs:90-94`
- Modify: `CrabDesk.WinUI/Services/CrabDeskService.cs:115-119`
- Modify: `CrabDesk.WinUI/ViewModels/AppearanceViewModel.cs:34-38`
- Modify: `CrabDesk.WinUI/Views/AppearancePage.xaml:93-118`

- [ ] **Step 1: Add the runtime mutation method**

Add a sibling method which persists and redraws via the existing workspace notification:

```csharp
public void SetShowBoxScrollBar(bool enabled)
{
    State.Settings.Appearance.ShowBoxScrollBar = enabled;
    NotifyWorkspaceChanged(true);
}
```

- [ ] **Step 2: Forward it through the WinUI service**

Add the interface member and delegation:

```csharp
void SetShowBoxScrollBar(bool enabled);
// ...
public void SetShowBoxScrollBar(bool enabled) => _runtime.SetShowBoxScrollBar(enabled);
```

- [ ] **Step 3: Bind the property in the appearance view model**

Add a property beside `ShowResizeGrip`:

```csharp
public bool ShowBoxScrollBar
{
    get => _service.State.Settings.Appearance.ShowBoxScrollBar;
    set => _service.SetShowBoxScrollBar(value);
}
```

- [ ] **Step 4: Add the two-column toggle**

Place this control alongside the existing `悬停高亮` toggle:

```xml
<ToggleSwitch Grid.Row="3"
              Grid.Column="1"
              Header="显示盒子滚动条"
              IsOn="{Binding ShowBoxScrollBar, Mode=TwoWay}" />
```

- [ ] **Step 5: Build the UI project**

Run: `dotnet build CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Release -p:Platform=x64`

Expected: build succeeds with no new errors.

### Task 3: Disable scrollbar rendering/hit-testing and clear stale item hover

**Files:**
- Modify: `CrabDesk.Runtime/DesktopBoxForm.ScrollBar.cs:11-15, 41-51`

- [ ] **Step 1: Gate scrollbar layout from the global setting**

Expand the early return in `GetScrollBarLayout`:

```csharp
if (geometry.IsCollapsed ||
    _runtime.AreDesktopItemsHidden ||
    !_runtime.State.Settings.Appearance.ShowBoxScrollBar)
{
    return null;
}
```

Because both rendering and pointer hit-testing obtain their geometry through this method, the switch disables the entire scrollbar interaction without special-case branches.

- [ ] **Step 2: Clear icon hover before scrollbar capture**

Insert the existing cleanup call immediately after direct-scroll animation cancellation:

```csharp
CancelScrollAnimationForDirectManipulation();
ClearItemHover();
```

This hides the native hover overlay and invalidates its prior item before `ApplyScrollOffset` moves content under the pointer.

- [ ] **Step 3: Run scrollbar and persistence tests**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter "FullyQualifiedName~DesktopScrollBarLayoutTests|FullyQualifiedName~PersistenceTests.StateIsSavedAndLoadedAtomically" -c Release`

Expected: PASS.

### Task 4: Validate the integrated desktop application

**Files:**
- Verify: `CrabDesk.sln`

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test CrabDesk.sln -c Release -p:Platform=x64`

Expected: all test projects pass; report any pre-existing warnings separately.

- [ ] **Step 2: Check patch formatting**

Run: `git diff --check`

Expected: no whitespace errors.

- [ ] **Step 3: Rebuild and relaunch the test application**

Stop the previously launched test process, rebuild `CrabDesk.WinUI`, then launch the Release executable with `--show-settings`. Manually verify that the new switch hides/re-shows the overflow scrollbar and that beginning a scrollbar drag clears an already-hovered icon immediately.
