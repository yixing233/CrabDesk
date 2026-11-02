# Note and Todo Free Sizing Implementation Plan

> **For agentic workers:** Execute the tasks in this session using the executing-plans workflow. Keep existing uncommitted monitoring and appearance changes; do not commit or publish.

**Goal:** Let desktop notes and todos resize freely from all edges/corners and from width/height settings, preserving their sizes across reload and backup.

**Architecture:** Append `Custom` to the stored size enum and keep custom dimensions in the existing `Bounds`. A Core geometry helper owns edge hit testing and anchored, work-area constrained resizing; the native pointer gesture uses existing capture/render/save paths. WinUI previews and settings resolve actual widget dimensions.

**Tech Stack:** .NET 8, WinForms/GDI desktop surfaces, WinUI 3 settings, xUnit.

## Task 1: Persisted sizes and geometry

Files: `CrabDesk.Core/DesktopWidgets.cs`, new `CrabDesk.Core/WidgetSizing.cs`, new `CrabDesk.Tests/WidgetSizingTests.cs`.

- [x] Append the enum value and provide actual dimensions and labels:

```csharp
public enum DesktopWidgetSize { Small, Medium, Wide, Large, Compact, Landscape, Custom }
// DesktopWidgets additions:
public static bool SupportsFreeResize(DesktopWidgetKind kind) => kind is DesktopWidgetKind.Note or DesktopWidgetKind.Todo;
// Dimensions(DesktopWidget) reads finite Bounds dimensions, at least 180 x 120, for Custom notes/todos.
// SizeLabel(Custom) is "自定义尺寸"; SizeLabel(widget) includes its actual dimensions.
```

- [x] Keep custom dimensions when normalizing supported widgets; retain preset migration and fallback for all other kinds. Clamp custom sizes to the monitor work area after loading.
- [x] Implement `WidgetSizing.HitTest(widget, x, y)` and `Resize(start, edges, deltaX, deltaY, workArea)` with 180 x 120 DIP minimum; resizing one edge keeps the opposite edge fixed. Locked widgets and other kinds return no resize hit.
- [x] Test both kinds with fractional custom sizes through `JsonLayoutStore.SaveAsync/LoadAsync`, minimum sizes, non-finite input, smaller monitor work areas, all eight directions, edge anchoring, and no hit in content/locked/other kinds.

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --no-restore --filter 'FullyQualifiedName~WidgetSizing|FullyQualifiedName~DesktopWidget|FullyQualifiedName~WidgetClock'`. Expected: all pass.

## Task 2: Native interaction

Files: `CrabDesk.Runtime/CrabDeskRuntime.Widgets.cs`, `DesktopBoxForm.Widgets.cs`, new `DesktopBoxForm.Widgets.Resize.cs`, tests in `CrabDesk.WinUI.Tests/DesktopWidgetRuntimeTests.cs`.

- [x] Add the numeric resize overload, validating membership, supported kind, finite positive dimensions and available monitor before modifying state:

```csharp
public void ResizeWidget(DesktopWidget widget, double width, double height);
```

- [x] Add resize-edge/gesture state separate from movement. Test edges before content/menu clicks, show directional cursors, update custom bounds while captured, and refresh visuals without saving each frame.
- [x] On mouse-up apply the final pointer delta; commit once and suppress content clicks. On lost capture commit the last frame. A click without resizing retains the preset. Preserve header movement and lock behavior.
- [x] Test an edge/corner pointer gesture, final delta on release, lock behavior, capture loss, minimum bounds, numeric changes and preset restoration, with config-change counts and content preservation.

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -p:Platform=x64 -o artifacts/free-widget-sizing-build --no-restore --filter 'FullyQualifiedName~DesktopWidgetRuntimeTests|FullyQualifiedName~WidgetMonitorRuntimeTests|FullyQualifiedName~WidgetGalleryItemTests'`. Expected: all pass.

## Task 3: WinUI and verification

Files: `CrabDesk.WinUI/Controls/WidgetPreview.xaml.cs`, `Views/WidgetsPage.xaml`, `Views/WidgetsPage.xaml.cs`, `Views/WidgetsPage.Monitors.cs`, new `Views/WidgetsPage.Sizing.cs`, `docs/desktop-widgets.md`, `docs/design/desktop-widgets.md`.

- [x] Resolve preview dimensions with `DesktopWidgets.Dimensions(widget)`, including todo row layout and wrapping notes.
- [x] Add width/height NumberBox controls and an apply button for Note/Todo; update dimensions and size selection after desktop resizing. Keep preset choices and other component layouts.
- [x] Document free sizing and its minimum/lock behavior. Use the existing ignored GUI harness with isolated config to check custom note/todo renderings at minimum, tall and wide sizes in light/dark themes and at 125%/200% DPI, plus settings/native synchronization.
- [x] Build the application, gracefully replace the running test app, and verify desktop takeover on both monitors. Report checks honestly, leaving changes uncommitted.

Run: `dotnet build CrabDesk.WinUI/CrabDesk.WinUI.csproj -p:Platform=x64 -o artifacts/free-widget-sizing-build --no-restore`. Expected: zero errors, existing warnings reported as applicable.

Validation completed: 89 Core tests and 162 runtime/gallery tests passed; GUI harness passed custom-size rendering and settings/save-load checks. Final application build had zero errors (41 existing warnings). Test app PID 27864 connected to both monitors; changes remain uncommitted.
