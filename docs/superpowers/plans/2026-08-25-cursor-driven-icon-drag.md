# Cursor-Driven Icon Drag Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make desktop and box item drags stay attached to the physical cursor without repainting the full monitor or recalculating the desktop layout on every OLE `DragOver` event.

**Architecture:** `DesktopIconSurface` owns every icon drag ghost in its small layered child overlay. A lightweight UI timer samples `Cursor.Position` during the nested OLE loop, while box surfaces redraw only when visible target feedback changes. Box-to-desktop insertion is computed once at drop time.

**Tech Stack:** .NET 8, Windows Forms layered windows, WinUI host, xUnit.

---

### Task 1: Lock the compositor decisions with tests

**Files:**
- Modify: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`
- Test: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`

- [x] Rename the parent-frame reuse test input from desktop-only activity to small drag-overlay activity and cover both desktop and box item ghosts.
- [x] Add coverage for cursor synchronization policy: active drags accept a changed physical cursor point, inactive or unchanged drags do not schedule a frame.
- [x] Run the focused tests and confirm the new cases fail before production changes.

### Task 2: Drive drag ghosts from the physical cursor

**Files:**
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs`
- Test: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`

- [x] Add a 16 ms WinForms timer whose lifetime is limited to an active desktop OLE drag or virtual box-item drag.
- [x] On each tick, convert `Cursor.Position` to surface DIPs, update the applicable desktop/box ghost pointer, and coalesce one overlay render only when the point changed.
- [x] Synchronize once again immediately before a queued frame is painted so no frame uses a stale `DragOver` coordinate.
- [x] Start and stop the timer from `TryStartDesktopOleDrag`, `SetVirtualBoxDropTargetEnabled`, cleanup, and disposal.

### Task 3: Put box-item ghosts in the small overlay

**Files:**
- Modify: `CrabDesk.Runtime/DesktopBoxForm.DragDrop.cs`
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs`
- Test: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`

- [x] Forward box `ItemKeysFormat` keys to `DesktopIconSurface` instead of suppressing them.
- [x] Populate `_boxDropItemKeys`, `_boxDragPrimaryKey`, and `_boxDragPointer` in the icon surface for box drags.
- [x] Disable the box form's full-surface floating card; keep its drop target/tab/folder feedback event-driven.
- [x] Retain box ghost state across the OLE handoff from desktop surface to a box and clear it only when the virtual drag session ends.

### Task 4: Reuse unchanged parent frames during every icon drag

**Files:**
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs`
- Test: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`

- [x] Generalize `ShouldReuseBoxParentFrame` to accept any active small ghost overlay, including box-item drags.
- [x] Pass the generalized activity flag from `PresentLayerCore`; dynamic version changes still force a parent redraw for real target feedback.
- [x] Keep box move/resize transforms excluded so their live pixels continue to repaint normally.

### Task 5: Defer box-to-desktop layout calculation until drop

**Files:**
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs`
- Test: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`

- [x] Replace the per-move placement dictionary with one target cell.
- [x] Make `UpdateBoxDropPlacement` update only keys, pointer, and target cell; do not call `CalculateInsertion` there.
- [x] Calculate insertion once in `BuildBoxDropDesktopLayout` during `OnDragDrop`, returning failure when no valid layout exists.
- [x] Remove unnecessary geometry invalidation from box ghost cleanup and remove the duplicate grid creation in desktop preview tracking.

### Task 6: Verify and launch

**Files:**
- Verify: `CrabDesk.Tests/CrabDesk.Tests.csproj`
- Verify: `CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj`
- Build: `CrabDesk.WinUI/CrabDesk.WinUI.csproj`

- [x] Run both test projects.
- [x] Build the x64 Debug WinUI application with zero errors and inspect warnings.
- [x] Restart the running Debug application and verify startup remains healthy.

No commit is included because the user has not authorized committing this dirty worktree.
