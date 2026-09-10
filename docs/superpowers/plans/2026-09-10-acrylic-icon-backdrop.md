# Acrylic Icon Backdrop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make each acrylic box blur desktop icons beneath it while keeping that box's foreground sharp.

**Architecture:** Keep the desktop icon surface attached to Explorer, below the top-level acrylic host. In acrylic mode, render each monitor's boxes through its existing standalone layered `DesktopBoxForm` child above the host backdrop; retain the existing shared icon/box bitmap path when acrylic is disabled. Use separate parent and anchor handles for icon and box surfaces so lifecycle and visibility operations preserve both stacks.

**Tech Stack:** .NET 8, WinForms layered windows, Windows Composition `HostBackdropBrush`, xUnit.

---

### Task 1: Lock down the separated layer topology

**Files:**
- Modify: `CrabDesk.WinUI.Tests/DesktopAcrylicTests.cs`

- [x] **Step 1: Write a source-structure regression test**

Assert that `DesktopSurfaceManager` attaches icon surfaces to `iconParentHandle`, box surfaces to `boxParentHandle`, and configures shared icon-layer box composition only when no acrylic host exists.

- [x] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Release --nologo --filter FullyQualifiedName~DesktopAcrylicTests -v quiet`

Expected: the new topology test fails against the single-parent implementation.

### Task 2: Split icon and box window stacks

**Files:**
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs`

- [x] **Step 1: Introduce separate parents and anchors**

Keep icon surfaces under `host.DesktopView` with `host.DesktopListView` as their anchor. Use the acrylic host and its anchor only for box surfaces.

- [x] **Step 2: Select the rendering path by material**

Call `PrepareIconLayerComposition` and `ConfigureBoxIconLayerComposition` only without acrylic. In acrylic mode, allow `DesktopBoxForm.PresentLayer` to render sharp box content above the host while the host samples the icon surface below.

- [x] **Step 3: Update show, refresh, readiness, and ordering**

Operate icon surfaces against the icon anchor and box surfaces against the box anchor. Skip sibling-only box-versus-icon ordering assertions when the acrylic stack deliberately uses different parents.

- [x] **Step 4: Run focused tests**

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~DesktopAcrylicTests|FullyQualifiedName~DesktopStartupTakeoverTests" -v quiet`

Expected: all focused tests pass.

### Task 3: Exercise the real stack and regressions

**Files:**
- Modify: `tools/AcrylicSmokeTest/Program.cs`
- Modify: `tools/AcrylicSmokeTest/README.md`
- Modify: `docs/superpowers/specs/2026-09-09-real-background-acrylic.md`

- [x] **Step 1: Make manager smoke coverage assert separated parents**

Verify that the icon surface remains a child of the synthetic desktop view and the box surface is a child of the acrylic host.

- [x] **Step 2: Run the manager smoke test**

Run: `dotnet run --project tools/AcrylicSmokeTest/AcrylicSmokeTest.csproj -c Release -- --manager`

Expected: three create/hide/show/refresh/dispose cycles pass.

- [x] **Step 3: Run full validation**

Run: `dotnet build CrabDesk.sln -c Release --nologo`

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Release --nologo -v quiet`

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Release --nologo -v quiet`

Expected: build succeeds and all tests pass, with only the existing MVVMTK0045 warnings.

- [x] **Step 4: Document the layer change**

Record that desktop icons now sit below the host and participate in backdrop sampling, while overlapping boxes still require a future Composition visual-tree migration.
