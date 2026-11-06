# Control Center Connections, Shortcuts and Display Plan

**Goal:** Add Wi-Fi and Bluetooth detail cards, a persistent customizable quick-action row, and display projection controls to the existing compact control center.

**Architecture:** Keep the current widget dimensions and audio/media controls. Share quick-action layout and hit testing between runtime and preview. Use separate native services for Wi-Fi, Bluetooth and display topology; UI cards report real capabilities. Network scan and credential access stay behind opening/refreshing the card. System settings handles Bluetooth pairing and profile-specific management.

**Tech stack:** .NET 8, WinForms/GDI+ widget, WPF popup cards, WinRT connectivity APIs, Win32 display configuration, xUnit/Moq.

## Native services

- [x] Implement `WiFiConnectionService` against `IControlConnectionService`: explicit scan, passive cached report, connected SSID, signal/security, saved/open/password connections, disconnect and radio control.
- [x] Implement `BluetoothConnectionService`: paired Classic/LE devices, connection/type details, duplicate grouping, radio control and system settings handoff for device management.
- [x] Implement `WindowsDisplayControl` against `IDisplayControl`: current topology and attached display count, clone/extend/internal/external modes, wireless casting selector and display settings.

## Quick actions and persistence

- [x] Add `ControlCenterQuickAction` and `ControlQuickActions` normalization/labels/status helpers. Default to Wi-Fi, Bluetooth, NightLight and Display; permit 1–6 selected actions.
- [x] Persist the ordered action list on `DesktopWidget`, normalize missing/invalid saved values, and save changes through `CrabDeskRuntime.SetControlQuickActions`.
- [x] Share tile/detail-chevron geometry and target mapping with the WinUI preview. Add an editor to the widget menu with select/hide, move up/down and restore defaults.

## Cards and integration

- [x] Add a shared compact popup shell with existing audio-card colors, thin scrolling, Escape/outside dismissal and accessible button names.
- [x] Add a serialized connection controller and detail card with radio control, refresh, network/device rows, credentials when needed and appropriate settings links. Initial Wi-Fi access runs on the UI thread.
- [x] Add the display card with current-mode feedback and four topology choices, plus wireless casting/settings actions.
- [x] Keep popup ownership, widget move/removal/hide/disposal and repeat-click behavior consistent with existing components. Keep passwords transient.

## Verification

- [x] Test saved action order/normalization, hidden-action hit testing, radio/detail separation and editor callbacks.
- [x] Test connection refresh/command ordering, stale work after disposal, password-request behavior, native capabilities and display dispatch with mocks.
- [x] Render and inspect the new cards and widget row in dark/light variants. Read-only hardware probes may enumerate connectivity/topology; do not alter live network connections or monitor layout during verification.
- [x] Run relevant core/runtime suites and build, then normally exit the old application with `--exit`, start the updated executable and check responsiveness. Do not commit or push.

## Results

- Full WinUI application build succeeded; existing unrelated compiler/analyzer warnings remain.
- Core suite: 120 passed. Runtime/UI suite: 189 passed. Four added audio-popup repeat-click regressions reproduced the defect, then passed after the capture fix; the accompanying 19 device-menu tests also passed.
- Light/dark card and widget renders inspected. `git diff --check` passed with line-ending notices only.
- Read-only hardware probe succeeded: three display targets, Bluetooth enabled with five paired devices, Wi-Fi enabled. Wi-Fi scan/connect, radio changes, display-mode changes and casting were not exercised against live hardware.
- Updated executable started successfully; process 41708 reported `Responding=True`.
