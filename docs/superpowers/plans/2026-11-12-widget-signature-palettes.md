# Widget Signature Palettes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the colorful widget surfaces and preserve their palette when switching between solid and acrylic.

**Architecture:** Keep the shared card palette in `WidgetAppearance`. Desktop rendering and WinUI previews consume that resolved appearance; inherited card materials retain the saved global appearance. Lyrics use the global appearance with acrylic or transparent background. Keep the existing model and settings controls.

**Tech Stack:** .NET 8, System.Drawing, WinUI 3, xUnit.

Execute in the current workspace, which contains the widget work this change extends. Do not commit or replace existing changes.

---

### Task 1: Palette And Material

**Files:** `CrabDesk.Core/WidgetAppearance.cs`, `CrabDesk.Tests/WidgetAppearanceTests.cs`

- [x] Add coverage for acrylic preserving every kind/theme palette, its global tint strength, and opaque fallback. Retain legacy-inheritance coverage.
- [x] Run `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter FullyQualifiedName~WidgetAppearanceTests`.
- [x] Resolve inherited material separately, then resolve solid/acrylic from the same palette:

```csharp
return widget.BackgroundMaterial == WidgetBackgroundMaterial.Acrylic
    ? surface with { Opacity = acrylic ? inherited.ResolveBackgroundOpacity(true) : 1, Acrylic = acrylic }
    : surface;
```

- [x] Use these signature backgrounds/gradient endpoints, keeping the existing light/dark presets:

| Kind | Background | End | Accent |
| --- | --- | --- | --- |
| Clock | #20395A | #2D5076 | #F2C879 |
| Performance | #164D46 | #23685A | #9AE9C5 |
| Network | #174C78 | #226594 | #A2E3FF |
| ControlCenter | #343F7A | #4B5996 | #C6D5FF |

### Task 2: Desktop And Preview

**Files:** `CrabDesk.Runtime/DesktopBoxForm.Widgets.Rendering.cs`, `CrabDesk.Runtime/DesktopBoxForm.WidgetClock.cs`, `CrabDesk.WinUI/Controls/WidgetPreview.xaml.cs`, `CrabDesk.WinUI/Views/WidgetsPage.Appearance.cs`, `CrabDesk.WinUI/Views/WidgetGalleryItem.cs`

- [x] Apply resolved opacity to both gradient stops:

```csharp
new LinearGradientBrush(bounds, ApplyOpacity(background, tint),
    ApplyOpacity(ParseOpaqueColor(gradientEnd), tint), LinearGradientMode.ForwardDiagonal)
```

- [x] Give the clock its resolved background and accent. Tint the dial from its background toward its text by 6%; use accent for major ticks, minute hand and pivot, retaining neutral readable labels and a coral second hand.
- [x] Use the resolved background for control-center preview panels.
- [x] Show the theme selector for both solid and acrylic; hide it for inherited material. Update hints and the clock gallery description.
- [x] Verify acrylic alpha at multiple interior pixels and the colored clock dial across all three themes and 100%, 150% and 200% DPI.

### Task 3: Verify And Deliver

**Files:** `CrabDesk.WinUI.Tests/WidgetMaterialRuntimeTests.cs`, `docs/desktop-widgets.md`, `docs/design/widget-signature-palettes.md`

- [x] Export production-rendered samples using `CRABDESK_WIDGET_SOLID_OUTPUT`, with representative contents and actual widget dimensions.
- [x] Run core tests and the relevant widget material/content/clock/popup tests.
- [x] Build `CrabDesk.WinUI/CrabDesk.WinUI.csproj` in Release for x64.
- [x] Inspect the exported images; document palette/material behavior and provide a local preview image.

## Validation Results

- Core: 953 passed.
- Selected runtime/UI tests: 126 passed, 4 failed. All widget material, dial, content rendering, gallery and popup material checks passed. The four failures are `WidgetMonitorRuntimeTests.InkChoiceChangesLabelsAndOperationsWhileKeepingArtworkAndPanels`: the test expects the mute icon's pixels to remain unchanged when text color is overridden, while the unchanged `DrawControlCenterMuteButton` implementation paints it with that text color. The failing widgets use inherited material.
- Release x64 app build: succeeded with zero warnings and zero errors.
- Exported 33 images (11 kinds, three themes) and inspected the focused Signature/Light/Dark sheets under `artifacts/widget-signature-20261112`.
