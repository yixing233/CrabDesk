# Detail Cards Implementation Plan

> **For agentic workers:** Implement in this session in the task order below; preserve the existing workspace changes. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Improve weather, Bluetooth and Wi-Fi detail card styling and entrance smoothness.

**Architecture:** Keep the existing WinUI weather window and WPF connection popup. Animate content inside a stationary card surface after the initial layout, respect application/system animation preferences, and retain stable controls during data refresh.

**Tech Stack:** .NET 8, WinUI 3, WPF, Windows DWM.

---

### Task 1: Connection card presentation

Files: `CrabDesk.Runtime/ControlCenterPopup.cs`, `CrabDesk.Runtime/ConnectionDetailsPopup.cs`.

- [x] Add an opt-in detail-card shell with 16-DIP padding, a clearer heading and a separated footer. Preserve other popup layouts.
- [x] Give connection rows a 32-DIP icon surface, 13-DIP names, 11-DIP details, 8-DIP spacing and a distinct connected state. Retain row identity and command handling.

### Task 2: Entrance lifecycle

Files: `CrabDesk.Runtime/ControlCenterPopup.Motion.cs`, `CrabDesk.Runtime/DesktopBoxForm.ControlActions.cs`, `CrabDesk.WinUI/Windows/WeatherDetailsWindow.Motion.cs`.

- [x] Keep native surfaces hidden until content is laid out. Animate the inner content with `Opacity` from 0 to 1 in 160 ms and `TranslateTransform.Y` from 8 to 0 in 220 ms using cubic ease-out.
- [x] Pass `State.Settings.Appearance.AnimationEnabled` into connection details, combine it with the system animation preference, and clean up pending render callbacks on disposal.
- [x] For weather, target the inner layout instead of the outer acrylic shell and preserve first-frame preparation.

### Task 3: Weather presentation

File: `CrabDesk.WinUI/Windows/WeatherDetailsView.cs`.

- [x] Use a spacious current-weather card with a larger temperature and weather icon. Place forecast headings inside their cards and align section spacing.
- [x] Reuse brushes and skip surface styling when appearance is unchanged; retain chart offsets and expanded alerts during weather refresh.

### Task 4: Verification

- [x] Run the existing connection interaction/rendering tests and inspect the generated light/dark Wi-Fi/Bluetooth PNGs.
- [x] Exercise the new entrance lifecycle, including disabled animation and dismissal before the first frame.
- [x] Build the solution and run the existing core and bootstrapper tests. Report failures or unverified live rendering explicitly.

```powershell
dotnet build CrabDesk.sln -c Release --no-restore
dotnet test CrabDesk.WinUI.Tests -c Release --no-restore --filter "FullyQualifiedName~ConnectionDetails|FullyQualifiedName~ControlCenterDeviceMenu|FullyQualifiedName~ControlActionIntegration|FullyQualifiedName~QuickActionsEditorPopup|FullyQualifiedName~DisplayControlsPopup"
dotnet test CrabDesk.Tests -c Release --no-build
dotnet test CrabDesk.Bootstrapper.Tests -c Release --no-build
```

Results: 80 focused UI/runtime tests, 894 core tests and 46 bootstrapper tests passed. The existing WinUI harness passed weather light/dark/narrow rendering, chart/alert refresh preservation, acrylic switching and all eight combinations of panel kind, acrylic and animation preferences. Initial verification exposed and resolved fractional-DPI layout rounding and icon reparenting after forecast refresh. Existing unrelated build warnings remain. Connection and weather verification use mock data and issue no hardware connection commands.
