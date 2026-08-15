# Desktop Hover Render Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task with review checkpoints.

**Goal:** Keep pointer movement across desktop icons responsive by rendering hover feedback in a small layered child instead of rebuilding and uploading the full monitor bitmap for every hover transition.

**Architecture:** The monitor-sized icon layer remains a settled, hover-neutral bitmap. A second click-through layered child owns only the current hover highlight and expanded label; its bitmap is cleared and redrawn using the union of the previous and current visual bounds so old feedback disappears without a full-screen present. Hover requests use their own coalescer and are kept separate from drag rendering; any geometry, selection, or drag change still uses the existing full render path and then synchronizes the hover child.

**Tech Stack:** .NET 8, WinForms, GDI+ `System.Drawing`, Win32 `UpdateLayeredWindow`, xUnit.

---

### Task 1: Lock Down Hover Scheduling Behavior

**Files:**
- Create: `CrabDesk.Runtime/DesktopHoverRenderState.cs`
- Create: `CrabDesk.WinUI.Tests/DesktopHoverRenderStateTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void PublishKeepsOnlyTheNewestHoverKey()
{
    var state = new DesktopHoverRenderState();

    Assert.True(state.Publish("one"));
    Assert.False(state.Publish("two"));
    Assert.True(state.TryTake(out var key));
    Assert.Equal("two", key);
    Assert.False(state.TryTake(out _));
}

[Fact]
public void ClearingHoverPublishesNullAndCoalesces()
{
    var state = new DesktopHoverRenderState();

    Assert.True(state.Publish("one"));
    Assert.False(state.Publish(null));
    Assert.True(state.TryTake(out var key));
    Assert.Null(key);
    Assert.False(state.TryTake(out _));
}
```

- [ ] **Step 2: Run the focused test to verify it fails**

Run: `dotnet test CrabDesk.WinUI.Tests\CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~DesktopHoverRenderStateTests`

Expected: FAIL because `DesktopHoverRenderState` does not exist.

- [ ] **Step 3: Implement the minimal coalescing state**

```csharp
namespace CrabDesk.Runtime;

internal sealed class DesktopHoverRenderState
{
    private bool _pending;
    private string? _latestKey;

    internal bool Publish(string? key)
    {
        var shouldQueue = !_pending;
        _latestKey = key;
        _pending = true;
        return shouldQueue;
    }

    internal bool TryTake(out string? key)
    {
        if (!_pending)
        {
            key = null;
            return false;
        }

        _pending = false;
        key = _latestKey;
        _latestKey = null;
        return true;
    }
}
```

- [ ] **Step 4: Run the focused test to verify it passes**

Run: `dotnet test CrabDesk.WinUI.Tests\CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~DesktopHoverRenderStateTests`

Expected: PASS.

### Task 2: Add a Reusable Small Hover Overlay

**Files:**
- Create: `CrabDesk.Runtime/DesktopHoverOverlay.cs`
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs:40-160`

- [ ] **Step 1: Add the click-through layered child using the existing `DesktopDragOverlay` bounds quantization and `LayeredWindowPresenter` path.** The overlay exposes `Present(RectangleF, double, Action<Graphics, RectangleF>, out string)` and `HideOverlay()`, clears its bitmap with `CompositingMode.SourceCopy`, and never owns mouse activation.

- [ ] **Step 2: Add the overlay lifetime fields and dispose it with the icon surface.** Keep it separate from `_dragOverlay` so drag composition cannot accidentally erase hover feedback.

- [ ] **Step 3: Build and run `dotnet build CrabDesk.Runtime\CrabDesk.Runtime.csproj -c Debug --no-restore` to catch WinForms/native signature errors before integrating the draw path.**

### Task 3: Route Hover Feedback Through the Local Overlay

**Files:**
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs:316-670,1129-1260,1690-1985`

- [ ] **Step 1: Add a dedicated hover render request queue.** `OnMouseMove`, `ReconcileHoverAtCursor`, and the hover-changing mouse-down path publish the latest key and queue one `BeginInvoke`; the callback calls `PresentHoverOverlay()` and never calls `PresentLayer()` unless geometry or the monitor layer is not ready.

- [ ] **Step 2: Make `DrawSettledLayer` call `DrawDesktopItems` with hover feedback disabled.** Continue calculating the expanded hit rectangle for the active key, but draw compact labels in the settled bitmap so changing hover keys cannot invalidate the full monitor layer.

- [ ] **Step 3: Implement `PresentHoverOverlay()` and `DrawHoverOverlay()`.** Compute the current icon plus expanded label bounds, union them with the previous overlay bounds, clear the small bitmap, draw the hover fill/border and the active label, and hide the child when no hover key is active or when drag/marquee composition owns the foreground.

- [ ] **Step 4: After every full `PresentLayer()` call, synchronize or hide the hover overlay.** This covers refresh, selection, DPI, and drag completion without leaving stale pixels.

- [ ] **Step 5: Run all focused tests and the two existing suites.**

Run: `dotnet test CrabDesk.WinUI.Tests\CrabDesk.WinUI.Tests.csproj -c Debug --no-restore`

Run: `dotnet test CrabDesk.Tests\CrabDesk.Tests.csproj -c Debug --no-restore`

Expected: all existing tests plus the new scheduling tests pass.

### Task 4: Release Verification

**Files:**
- Modify: `build/verify-desktop.ps1` only if the verification harness needs a hover traversal assertion.

- [ ] **Step 1: Build the release application.**

Run: `dotnet build CrabDesk.sln -c Release --no-restore`

Expected: build succeeds with no errors.

- [ ] **Step 2: Start the release executable and exercise a horizontal and vertical pointer traversal across at least six icons.** Confirm that hover highlight changes without visible frame stalls, selection and drag still work, and the overlay disappears when the pointer leaves the icon surface.

- [ ] **Step 3: Stop the test process and report test/build results plus any environment-only verification limitation.**
