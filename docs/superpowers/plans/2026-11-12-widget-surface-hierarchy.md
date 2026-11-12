# Widget Surface Hierarchy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give solid widgets black/white backgrounds, readable content hierarchy, and clear control states.

**Architecture:** Resolve solid black/white surfaces separately from acrylic palettes in `WidgetAppearance`, then mirror content styling in the desktop renderer and WinUI preview. Functional color belongs to switches, symbols, charts, and weather illustrations. Preserve acrylic appearances, calendar white/black backgrounds, lyrics background modes, saved materials, geometry, and interactions.

**Tech Stack:** .NET 8, System.Drawing, WinUI 3, xUnit.

Work in the existing workspace and preserve unrelated changes. Execute inline as requested; do not commit or push.

### Task 1: Shared Surfaces

**Files:** `CrabDesk.Core/WidgetAppearance.cs`, `CrabDesk.Tests/WidgetAppearanceTests.cs`

- [x] Resolve solid backgrounds as `#FFFFFFFF` for Light and `#FF1C1C1E` for Dark; the calendar uses `#FF000000` in Dark. Signature uses Dark for clock, weather, performance, network, and controls, and Light for other cards. Set `GradientEnd = null`, `Opacity = 1`, and `Acrylic = false`. Derive content accents from the existing Light/Dark palette.
- [x] Leave acrylic palette and weather condition resolution intact. Keep the theme selection when switching materials.
- [x] Update existing coverage to check solid neutrality/opacity and acrylic palette readability separately, then run:

```powershell
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --no-restore --verbosity minimal
```

### Task 2: Content And Controls

**Files:** `CrabDesk.Runtime/DesktopBoxForm.WidgetControlCenter.cs`, `CrabDesk.Runtime/DesktopBoxForm.Microphone.cs`, `CrabDesk.Runtime/DesktopBoxForm.WidgetMonitoring.cs`, `CrabDesk.Runtime/DesktopBoxForm.Widgets.Rendering.cs`, `CrabDesk.WinUI/Controls/WidgetPreview.xaml.cs`

- [x] In solid mode, use automatic secondary ink `#B0B7C3` for dark backgrounds and `#667180` for light backgrounds in the desktop renderer and preview.
- [x] In solid mode, use a 4% mix toward surface ink for control panels. Device and microphone rows mix a further 4%, 8%, or 12% for ordinary, hovered, or pressed states.
- [x] In solid mode, use neutral inactive quick actions: mix panel toward surface ink by 3%, 7%, or 11%. Active actions derive from the dark semantic tone with a 12% black mix, white label/icon ink, a 6% white hover mix, and a 10% black pressed mix. Apply the same ordinary state in previews.

```csharp
var active = Mix(ColorTranslator.FromHtml(WidgetContentVisuals.Tone(asset, false)), Color.Black, .12);
var fill = on
    ? Mix(active, pressed ? Color.Black : Color.White, pressed ? .10 : hovered ? .06 : 0)
    : Mix(panel, surfaceInk, pressed ? .11 : hovered ? .07 : .03);
```

- [x] In solid mode, draw performance row backgrounds using neutral text at alpha 6; retain the four functional icons and progress colors. Reduce network speed panel washes to alpha 10.
- [x] In solid mode, put performance/network titles at the left with 12 DIP inset and 11.5 DIP secondary ink; preserve header and interaction bounds. Mirror these settings in previews.

### Task 3: Verify And Show The Result

**Files:** `CrabDesk.WinUI.Tests/WidgetMaterialRuntimeTests.cs`, `docs/design/widget-signature-palettes.md`, `docs/desktop-widgets.md`, `artifacts/widget-signature-20261112/render-sheet.ps1`

- [x] Update existing fixtures for neutral solid backgrounds and clock faces. Verify active tile contrast and state distinction on all themes.
- [x] Use the app's `--exit` channel before desktop checks/builds; retain the previous running state for restart.
- [x] Run material/content/control rendering checks:

```powershell
$env:CRABDESK_WIDGET_SOLID_OUTPUT = 'E:\Code\CrabDesk\artifacts\widget-signature-20261112'
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter 'FullyQualifiedName~WidgetMaterialRuntimeTests|FullyQualifiedName~WidgetContentRenderingTests' --verbosity minimal
dotnet build CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Release -p:Platform=x64 --no-restore --verbosity minimal
```

- [x] Export actual production-rendered cards, compose the preview sheets, and inspect Signature/Light/Dark. Keep lyrics modes and calendar black/white checks passing.
- [x] Update the design specification, widget appearance documentation, and settings hints with the solid/acrylic rules and content hierarchy. Describe the clock as an analog dial in the gallery.
- [x] Restart the verified executable if an instance was running; verify the new process stays alive and responds.

## Validation

- Core: 961 passed.
- Material and content rendering: 40 passed, including six active/inactive control contrast cases at 100% and 200% DPI.
- Gallery: 16 passed.
- Final Release x64 build: succeeded, zero warnings and errors.
- Inspected production-rendered Signature, Light, and Dark sheets in `artifacts/widget-signature-20261112`.
- Restarted the verified Release executable; PID 39016 stayed alive and responded.
