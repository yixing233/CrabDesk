# Adaptive Lyrics Layout Implementation Plan

**Goal:** Let users adjust lyric width and height directly, fit surrounding lyrics to the available height, and tune line spacing with a slider.

**Architecture:** Continue using the existing independent edge/corner resizing and custom dimension persistence. Store a line-spacing multiplier, calculate an odd number of visible rows around the active lyric from the viewport height, and retain lyric masks for the visible window. Put width/height inputs and the spacing slider in the lyrics editor and offer the same spacing slider in the desktop menu.

**Tech Stack:** .NET 8, WinForms/GDI+, WinUI 3, xUnit.

## Model and rendering

- [x] Replace `DesktopWidget.LyricsLineCount` with `double LyricsLineSpacing = 1.75` in `CrabDesk.Core/DesktopWidgets.cs`; normalize finite values to `[1, 3]` through `WidgetLyricsSettings.NormalizeLineSpacing` in `CrabDesk.Core/WidgetLyrics.cs`.
- [x] Update `CrabDesk.Runtime/CrabDeskRuntime.Lyrics.cs` and all lyric drawing/settings callers to accept the multiplier.
- [x] In `CrabDesk.Runtime/DesktopLyricsVisual.cs`, use this layout and include `spacing` in the static-row cache key:

```csharp
var spacing = fontSize * WidgetLyricsSettings.NormalizeLineSpacing(lineSpacing);
var radius = Math.Max(0, (int)Math.Floor((bounds.Height - fontSize) / (2 * spacing)));
var lineCount = radius * 2 + 1;
```

- [x] Size the mask cache for the visible window plus transition rows. Count expired surrounding rows for the static-frame key, so tall viewports retain correct cache invalidation.
- [x] Verify persistence/defaults, visible text bands at different heights/spacings, cache refresh when spacing changes without changing row count, and mask reuse during tall-viewport transitions.

## Editor and desktop controls

- [x] In `CrabDesk.WinUI/Views/WidgetsPage.xaml`, place lyric width and height inputs first; replace the row-count picker with `Slider Minimum="1" Maximum="3" StepFrequency="0.05" SmallChange="0.05" LargeChange="0.25"` and a current multiplier label.
- [x] Use the existing resize operation from `WidgetsPage.Sizing.cs`, synchronize the lyric dimension fields and spacing value on model refresh, and suppress gallery PNG regeneration during slider movement.
- [x] In `CrabDesk.Runtime/DesktopBoxForm.Lyrics.cs`, offer a spacing trackbar using integer hundredths (`100..300`, small change `5`) with live updates and a default-value reset. Reuse the existing themed control-host sizing.
- [x] Verify width-only, height-only, corner dragging, locks, and spacing-slider updates at 100%, 125%, and 150% DPI.

## Verification and local run

- [x] Exit the running test instance with `artifacts/test-build/CrabDesk.WinUI.exe --exit` before desktop tests.
- [x] Run:

```powershell
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Release --filter 'FullyQualifiedName~WidgetLyricsSettings|FullyQualifiedName~WidgetSizing'
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Release -p:Platform=x64 --filter 'FullyQualifiedName~DesktopLyrics|FullyQualifiedName~WidgetCornerResize|FullyQualifiedName~FluentContextMenuStrip'
dotnet publish CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Release -r win-x64 --self-contained false -p:WindowsAppSDKSelfContained=false -p:Platform=x64 -o artifacts/test-build
```

- [x] Inspect exported lyric frames, restart the updated local instance, and verify the actual width/height inputs and spacing slider through UI Automation, restoring any temporary verification values.
