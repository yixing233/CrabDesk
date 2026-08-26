# Desktop Folder Drop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow one or more real desktop files/folders to be dragged onto a real folder icon on the CrabDesk desktop and moved into it, with Explorer-style target feedback and Ctrl-to-copy behavior.

**Architecture:** Keep the existing OLE `FileDrop` drag session and file-operation service. Add a small, testable eligibility policy for safe folder targets, route accepted drops before the existing desktop-grid placement branch, and paint the target highlight in the existing small drag overlay. Use a desktop-specific runtime import transaction so path collisions and partial failures remain consistent with box folder drops while the moved icons are reconciled through the existing local dirty-region renderer instead of clearing the full icon cache.

**Tech Stack:** C# 12, .NET 8, WinForms layered desktop surfaces, xUnit

---

### Task 1: Specify safe desktop-folder targets

**Files:**
- Create: `CrabDesk.Runtime/DesktopFolderDropPolicy.cs`
- Modify: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`

- [x] Add failing xUnit cases proving that an ordinary filesystem folder accepts dragged desktop files.
- [x] Add failing cases for a non-folder target, a target without a filesystem path, a folder included in the dragged selection, and a target nested inside a dragged source folder.
- [x] Run `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter DesktopFolderDrop` and confirm the new tests fail for the missing policy.
- [x] Implement `DesktopFolderDropPolicy.CanAccept` with normalized, case-insensitive Windows path comparisons and no filesystem mutation.
- [x] Re-run the filtered tests and confirm they pass.

### Task 2: Route internal desktop drags into folders

**Files:**
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs`
- Modify: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`

- [x] Add tests for the drag-effect policy: a valid folder uses Move by default, Ctrl requests Copy, and rejected targets retain the existing desktop-placement effect.
- [x] Track the currently accepted desktop-folder target by stable item key and clear it on target changes, drag leave, drop completion, and drag teardown.
- [x] In `OnDragOver`, resolve folder targets before Recycle Bin/background placement, advertise Move by default and Copy while Ctrl is held, and preserve existing box/recycle/grid behavior.
- [x] In `OnDragDrop`, revalidate the target and call `CrabDeskRuntime.ImportDesktopItemsIntoFolderAsync`; mark the desktop drag session handled and show any per-item failures with the existing desktop dialog style.
- [x] Ensure a failed or stale target falls back safely without moving files.
- [x] Run the focused interaction tests.

### Task 3: Add lightweight target feedback

**Files:**
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs`
- Modify: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`

- [x] Add a focused test that the folder target contributes only its local icon visual bounds to the drag overlay.
- [x] Draw an Explorer-style accent fill and border behind the accepted folder icon in `DrawDynamicDragVisuals`.
- [x] Include the current folder target in `GetDragOverlayBounds` so feedback never forces a monitor-wide upload.
- [x] Verify changing or leaving a target removes the old highlight without disturbing the floating drag preview.

### Task 4: Verify the complete feature

**Files:**
- Modify only if verification exposes a defect in the files above.

- [x] Stop only the verified CrabDesk executable that locks the Debug output.
- [x] Run `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore`.
- [x] Run `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --no-restore`.
- [x] Run `dotnet build CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Debug --no-restore` and require zero errors.
- [x] Review `git diff --check` and the scoped diff for accidental changes.
- [x] Start `CrabDesk.WinUI/bin/Debug/net8.0-windows10.0.19041.0/win-x64/CrabDesk.WinUI.exe` and confirm the new process path and startup log.
