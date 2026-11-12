# Background Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Independently adjust background color, background opacity (0–100%) and acrylic blur strength (0–100%) for desktop boxes and widgets.

**Architecture:** Keep the existing color picker and foreground rendering. Add an explicit background opacity to preserve the appearance of older configurations, and use a Win2D Gaussian effect in the existing Windows compositor for stronger blur; fading the backdrop supplies the clear-to-native part of the strength range. Maintain sharp foreground text and icons. Settings affect the shared box/widget surface and persist through the existing store.

**Tech Stack:** .NET 8, Windows.UI.Composition, Microsoft.Graphics.Win2D 1.4.0, WinUI 3 settings, xUnit.

## Task 1: Persistent surface parameters

Files: `CrabDesk.Core/Models.cs`, `CrabDesk.Core/JsonLayoutStore.cs`, `CrabDesk.Tests/BackdropAppearanceSettingsTests.cs`.

- [x] Add tests for transparent/opaque endpoints, legacy acrylic tint preservation, saving/loading the three parameters, and repeated normalization.
- [x] Add `double? BackgroundOpacity` to `BoxAppearance`. Resolve explicit values directly in `[0,1]`; resolve missing values with the previous `Opacity` and acrylic tint multiplier. Normalize missing values once when loading.
- [x] Add `AcrylicBlurStrength = 50` to `GlobalAppearanceSettings`, normalized to `[0,100]`.
- [x] Run `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter FullyQualifiedName~BackdropAppearanceSettingsTests`.

## Task 2: Background composition

Files: `CrabDesk.WinUI/CrabDesk.WinUI.csproj`, `CrabDesk.WinUI/Services/AcrylicBlurEffects.cs`, `CrabDesk.WinUI/App.xaml.cs`, `CrabDesk.Runtime/DesktopAcrylicHost.cs`, `DesktopSurfaceManager.cs`, `DesktopBoxForm.Acrylic.cs`, `AcrylicOverlapBlur.cs`, `DesktopBoxForm.Rendering.cs`, `DesktopBoxForm.Widgets.Rendering.cs`.

- [x] Reference Win2D 1.4.0 in the WinUI application and inject the effect factory into the runtime, keeping native runtime resource packaging intact.
- [x] Create one Gaussian effect factory and one brush per backdrop visual. Animate no layout; update opacity and the brush property on parameter changes.

```csharp
nativeOpacity = Math.Clamp(strength / 50f, 0, 1);
extraBlur = Math.Max(0, strength - 50) * .6f * dpiScale;
```

- [x] Keep strength 50 equivalent to the previous native backdrop, strength 0 clear, and scale stronger blur by each region's monitor DPI.
- [x] Apply the same strength to the existing overlap blur, bypassing that filter at zero.
- [x] Use resolved background opacity for box/widget fills and preserve foreground text alpha.
- [x] Verify actual effect brush creation and property updates with desktop acrylic tests and the synthetic moving-stripe smoke tool.

## Task 3: Settings and previews

Files: `CrabDeskRuntime.cs`, `CrabDesk.WinUI/Services/ICrabDeskService.cs`, `CrabDeskService.cs`, `ViewModels/AppearanceViewModel.cs`, `Views/AppearancePage.xaml`, `Controls/WidgetPreview.xaml.cs`, relevant detail windows.

- [x] Keep the color picker, rename the slider to background opacity, and use 0–100 with 1% steps.
- [x] Add a separate blur-strength slider with the same compact row and value display, enabled while acrylic is enabled.
- [x] Expose the runtime setter, notify the view model, persist the setting, and copy explicit opacity when cloning appearances.
- [x] Update previews and related surfaces to use the same resolved opacity.
- [x] Add focused settings binding/range and runtime independence tests.

## Task 4: Verification and local build

- [x] Run relevant core persistence tests and WinUI acrylic, overlap, appearance, and lyrics regression tests.
- [x] Validate 0%, native strength and strong blur over the moving synthetic background; verify that changing opacity leaves foreground text clear.
- [x] Run `git diff --check`.
- [x] Exit the current local instance normally, publish into `artifacts/test-build`, and restart with `--show-widgets`.

Validation: 60 core tests and 353 WinUI regression tests passed. Screen comparisons verified strengths 0/25/50/75/100 and opacity 0/25/100. The running settings UI exposes both sliders with bounds 0–100. Lyrics cadence samples use a posted dispatcher deadline while leaving the animation timer active for pause assertions.

Execution stays in this authorized conversation and workspace. Do not commit or push.
