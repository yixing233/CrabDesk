# Independent Widget Fonts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Execute inline under the existing user authorization; do not commit.

**Goal:** Give each desktop widget independent typography and smooth text edges on transparent desktop layers.

**Architecture:** Persist font family, scale, weight override, and italic on each widget. Scope font selection and grayscale antialiasing to widget drawing through the existing Graphics parameter so ordinary desktop icons keep their rendering. Reuse the existing settings and preview controls; keep lyrics on their established typography settings.

**Tech Stack:** C#/.NET 8, System.Drawing, WPF text inputs, WinUI 3.

### Task 1: Persist typography

Files: `CrabDesk.Core/DesktopWidgets.cs`, `CrabDesk.Core/WidgetTypography.cs`, `CrabDesk.Tests/WidgetTypographyTests.cs`.

- [x] Add `FontFamily`, `FontScale`, `FontWeight`, and `FontItalic` to `DesktopWidget` with compatible defaults.

```csharp
public string FontFamily { get; set; } = BoxAppearance.DefaultFontFamily;
public double FontScale { get; set; } = 1;
public int FontWeight { get; set; }
public bool FontItalic { get; set; }
```

- [x] Normalize empty family to the existing default, scale to 0.75–1.5, and weight to 0/400/700. Test independent instance settings and JSON reload.

### Task 2: Render the selected typography

Files: `CrabDesk.Runtime/WidgetTextRendering.cs`, `DesktopBoxForm.Widgets.Rendering.cs`, `DesktopBoxForm.WidgetMonitoring.cs`, `DesktopWidgetTextVisual.cs`, `DesktopWidgetTextInput.cs`, widget input and text measurement callers.

- [x] Scope settings by Graphics for each `DrawWidget` call and restore the prior settings on disposal.

```csharp
using var typography = WidgetTextRendering.Begin(graphics, widget);
```

- [x] Use `TextRenderingHint.AntiAliasGridFit` during widget rendering. Create drawing and measuring fonts from the same widget settings, including network number slots. Apply the same family, scale, weight, and italic to WPF widget text and editors.
- [x] Test partial-alpha edge pixels at 125%/150% DPI, selected-font drawing, graphics-state restoration, and number/unit alignment.

### Task 3: Edit and preview typography

Files: `CrabDesk.WinUI/Views/WidgetsPage.xaml`, `WidgetsPage.xaml.cs`, `WidgetsPage.Typography.cs`, `CrabDesk.WinUI/Controls/WidgetPreview.xaml.cs`.

- [x] Add a typography expander using font ComboBox, NumberBox percentage, weight ComboBox, and italic ToggleSwitch. Load without saving and persist user changes through the existing `Save` helper.
- [x] Apply settings to preview TextBlocks while keeping Lucide icons on their icon font. Keep persistent heading sizes from accumulating scaling on refresh.
- [x] Run focused model and WinUI tests, publish Release to `artifacts/test-build`, restart the authorized test instance, and verify it remains running.

```powershell
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --filter 'FullyQualifiedName~WidgetTypographyTests|FullyQualifiedName~WidgetNetworkGraphTests'
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --filter 'FullyQualifiedName~WidgetTypography|FullyQualifiedName~SpeedValuesKeepTheNumberAndUnitEdgesFixed|FullyQualifiedName~InkChoice'
dotnet publish CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Release -r win-x64 --self-contained false -p:WindowsAppSDKSelfContained=false -o artifacts/test-build
```
