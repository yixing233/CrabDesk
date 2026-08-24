# Box Overflow Scrollbar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (\`- [ ]\`) syntax for tracking.

**Goal:** Show a right-side draggable scrollbar inside a desktop box only when its item content overflows.

**Architecture:** A Core layout helper converts the existing body viewport and scroll extent into a track, thumb, and reversible thumb-to-offset mapping. `DesktopBoxForm` reuses the current per-tab scroll offsets, paints the helper output after clipped icons, and handles scrollbar pointer capture before icon hit testing. Dragging updates only the box’s existing item geometry and visual layer, so it avoids a full desktop layout refresh.

**Tech Stack:** .NET 8, C#, System.Drawing/WinForms, existing layered desktop renderer, xUnit.

---

### Task 1: Define and test scrollbar geometry

**Files:**
- Create: `CrabDesk.Core/DesktopScrollBarLayoutEngine.cs`
- Create: `CrabDesk.Tests/DesktopScrollBarLayoutTests.cs`

- [x] **Step 1: Write failing geometry tests**

```csharp
[Fact]
public void CalculateVerticalReturnsNullWhenContentDoesNotOverflow()
{
    var layout = DesktopScrollBarLayoutEngine.CalculateVertical(
        new LayoutRect(20, 40, 240, 180), 0, 0);

    Assert.Null(layout);
}

[Fact]
public void ScrollOffsetForThumbTopMapsBothTrackEnds()
{
    var layout = Assert.IsType<VerticalScrollBarLayout>(
        DesktopScrollBarLayoutEngine.CalculateVertical(
            new LayoutRect(20, 40, 240, 180), 160, 720));

    Assert.Equal(0, DesktopScrollBarLayoutEngine.GetScrollOffsetForThumbTop(
        layout, layout.Track.Y));
    Assert.Equal(layout.MaxScroll, DesktopScrollBarLayoutEngine.GetScrollOffsetForThumbTop(
        layout, layout.Track.Y + layout.Track.Height - layout.Thumb.Height));
}
```

- [x] **Step 2: Run the focused test to verify it fails**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~DesktopScrollBarLayoutTests`

Expected: FAIL because `DesktopScrollBarLayoutEngine` does not exist.

- [x] **Step 3: Implement the immutable geometry helper**

```csharp
public sealed record VerticalScrollBarLayout(
    LayoutRect Track,
    LayoutRect Thumb,
    double ScrollOffset,
    double MaxScroll);

public static VerticalScrollBarLayout? CalculateVertical(
    LayoutRect viewport,
    double requestedScroll,
    double maxScroll,
    double thickness = 6,
    double inset = 4,
    double minimumThumbHeight = 28);
```

Return `null` for no overflow or an unusably small viewport. Clamp the scroll offset and thumb height; map the thumb’s travel linearly to `[0, MaxScroll]`.

- [x] **Step 4: Run the focused test to verify it passes**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~DesktopScrollBarLayoutTests`

Expected: PASS.

### Task 2: Add rendered scrollbar and captured thumb dragging

**Files:**
- Create: `CrabDesk.Runtime/DesktopBoxForm.ScrollBar.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Input.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Rendering.cs`

- [x] **Step 1: Add per-surface scrollbar state and layout lookup**

```csharp
private ScrollBarDragState? _scrollBarDrag;

private VerticalScrollBarLayout? GetScrollBarLayout(BoxGeometry geometry)
{
    var body = new LayoutRect(geometry.Body.X, geometry.Body.Y, geometry.Body.Width, geometry.Body.Height);
    var extent = DesktopItemLayoutEngine.GetScrollExtent(
        geometry.Box.ViewMode, body, GetVisibleItemsForBox(geometry).Count,
        geometry.Box.Appearance.IconSize, horizontalSpacing, verticalSpacing);
    return DesktopScrollBarLayoutEngine.CalculateVertical(
        body, _scrollOffsets.GetValueOrDefault(GetItemViewKey(geometry)), extent);
}
```

Use the same filtered item collection, view key, icon size, and scaled spacing as the wheel handler, so every tab/mapped category gets an independent thumb and offset.

- [x] **Step 2: Handle pointer capture before icon selection**

```csharp
if (eventArgs.Button == Forms.MouseButtons.Left &&
    box is not null &&
    TryBeginScrollBarDrag(box, point))
{
    return;
}
```

On mouse move while a scrollbar drag is active, calculate the new offset from the pointer and stored thumb grab offset, call the existing `ApplyScrollOffset`, and return. On mouse up or capture loss, clear the drag state without changing selection, box movement, resize state, or stored tab scroll offset.

- [x] **Step 3: Render the overflow indicator after clipped box items**

```csharp
graphics.Restore(state);
DrawVerticalScrollBar(
    graphics,
    geometry,
    ParseOpaqueColor(geometry.Box.Appearance.Accent),
    textColor);
```

Draw a low-alpha rounded track and a higher-contrast rounded thumb only when `GetScrollBarLayout` returns a value. Put it inside the body’s right inset, above icon painting but below header controls and resize grip. Use the accent only while the thumb is being dragged.

- [x] **Step 4: Build the solution**

Run: `dotnet build CrabDesk.sln -c Release --no-restore`

Expected: success with no errors.

### Task 3: Regress, inspect, and start the test build

**Files:**
- Test: `CrabDesk.Tests/DesktopScrollBarLayoutTests.cs`

- [x] **Step 1: Run the full test suite**

Run: `dotnet test CrabDesk.sln -c Release --no-restore`

Expected: all Core and WinUI test projects pass.

- [x] **Step 2: Start the isolated Release executable**

Run: `Start-Process 'CrabDesk.WinUI/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/CrabDesk.WinUI.exe' -ArgumentList '--show-settings'`

Expected: an oversized box shows the right scrollbar; dragging its thumb scrolls only that box; boxes without overflow show no scrollbar.
