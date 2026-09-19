# 盒子拖拽对齐实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 盒子拖拽过程中，接近其他盒子或工作区边缘时自动吸附对齐，并在释放时以盒子对齐优先于网格取整。

**Architecture:** 在 `CrabDesk.Core` 增加无 UI 依赖的 `DesktopBoxAlignmentEngine`，按水平/垂直轴分别比较移动盒子的左中右、上中下锚点与同屏盒子及工作区边缘，距离小于阈值时返回吸附后的坐标。`DesktopBoxForm.UpdateMovingBox` 在现有 Clamp 后调用引擎，使每个拖拽帧都使用对齐后的坐标；最终提交先做网格取整，再针对目标显示器重新对齐，确保对齐结果优先。

**Tech Stack:** .NET 8、C#、xUnit、WinForms desktop surface。

---

### Task 1: 定义盒子对齐计算与测试

**Files:**
- Create: `CrabDesk.Core/DesktopBoxAlignmentEngine.cs`
- Create: `CrabDesk.Tests/DesktopBoxAlignmentEngineTests.cs`

- [x] **Step 1: Write the failing tests**

覆盖同轴边缘、中心线、工作区边缘、阈值外不吸附，以及只改变坐标不改变尺寸的行为。

- [x] **Step 2: Run the focused tests and verify they fail**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~DesktopBoxAlignmentEngineTests`

Expected: FAIL because `DesktopBoxAlignmentEngine` does not exist.

- [x] **Step 3: Implement the minimal pure alignment engine**

Expose `Align(LayoutRect moving, IEnumerable<LayoutRect> peers, LayoutRect workArea, double threshold = DefaultThreshold)`. Compare each moving edge/center against peer edge/center anchors and work-area start/end anchors independently on X/Y; choose the nearest candidate within `threshold`, then clamp the result back into `workArea` while preserving width and height.

- [x] **Step 4: Run the focused tests and verify they pass**

Run the same `dotnet test` command; Expected: PASS.

### Task 2: 接入盒子拖拽帧

**Files:**
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Input.cs:1183-1192`

- [x] **Step 1: Add alignment after the existing drag Clamp**

Build the peer list from `DesktopBoxes`, excluding the moving box, and pass the current monitor work area plus the engine threshold to `DesktopBoxAlignmentEngine.Align` before `ApplyBoxTransform`.

- [x] **Step 2: Run focused and regression tests**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~DesktopBoxAlignmentEngineTests`

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore`

Expected: all tests pass.

### Task 3: Final verification

- [x] **Step 1: Check formatting and repository diff**

Run: `git diff --check` and confirm only the new alignment engine, tests, integration code, and this plan are changed.

- [x] **Step 2: Build the affected projects**

Run: `dotnet build CrabDesk.sln -c Debug --no-restore`

Expected: build succeeds.
