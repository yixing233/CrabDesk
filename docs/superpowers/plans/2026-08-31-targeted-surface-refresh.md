# Targeted Surface Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reduce unnecessary full desktop redraws while preserving correct updates for desktop icon assignment and cross-box operations.

**Architecture:** Add a targeted box refresh path through `DesktopSurfaceManager`, route box-local settings and manual-tab changes through it, and coalesce data refreshes so imports/renames perform one final full refresh. Mapped-folder watcher updates refresh only changed boxes.

**Tech Stack:** .NET 8, WinForms layered desktop surfaces, xUnit.

---

### Task 1: Add targeted box refresh capability

**Files:**
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs`
- Test: `CrabDesk.WinUI.Tests/FluentContextMenuStripTests.cs`

- [ ] Add `RefreshBox(Guid)` and `RefreshBoxes(IReadOnlyCollection<Guid>)` that call `DesktopBoxForm.RefreshBoxItems` and update only affected interaction regions.
- [ ] Add a regression test covering targeted refresh API behavior without invoking all-surface refresh.

### Task 2: Route box-local mutations to targeted refresh

**Files:**
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs`

- [ ] Route manual-tab create/rename/delete, mapped-folder settings, and box appearance/view/sort setters through targeted refresh when `boxId` is present.
- [ ] Preserve full refresh for global settings, box deletion/stack changes, and desktop assignment changes.
- [ ] Refresh only changed mapped-folder boxes from watcher callbacks.

### Task 3: Coalesce duplicate import and rename refreshes

**Files:**
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs`

- [ ] Update item snapshots with `refreshSurfaces:false`, mutate assignments, then perform one final refresh.
- [ ] Apply the same pattern to mapped-folder imports and rename paths.

### Task 4: Verify

**Files:**
- Test: `CrabDesk.WinUI.Tests/FluentContextMenuStripTests.cs`
- Test: `CrabDesk.Tests`

- [ ] Run targeted and full WinUI/core tests.
- [ ] Publish a direct-run EXE and verify the process starts.
