# Network Adapter Controls Implementation Plan

> Execute inline in the current workspace. Preserve existing edits and saved quick-action selections. Do not commit or push.

**Goal:** Add a selectable Network Adapters quick action that opens a card with an enable/disable control for each adapter.

**Architecture:** Append the action and hit target to preserve stored enum values. Read adapter administration state from MSFT_NetAdapter using the existing System.Management dependency. Execute an explicit per-adapter command through an elevated Windows PowerShell process, selected by GUID. The WPF detail card inherits the widget surface and refreshes actual adapter state after commands.

**Tech Stack:** C#, .NET 8, WinForms/WPF, System.Management, Windows NetAdapter PowerShell module, xUnit/Moq.

## Task 1: Action And Entry

- [x] Append `NetworkAdapters` to `ControlCenterQuickAction` and `ControlCenterTarget`. Map its label to `网卡`, status to `启用 / 禁用`, content asset to `content-network`, and icon to the existing Lucide `Network` icon. Keep existing defaults and selected actions.
- [x] In `DesktopBoxForm.ControlActions.cs`, route the action through `ShowControlPopup` into `NetworkAdaptersPopup`, passing the resolved surface, scale, animation preference, and network-settings callback.
- [x] Update the management preview and editor tests for the additional selectable item. Add tests for the action's stable value, hit target and JSON round trip.

## Task 2: Adapter Service And Card

- [x] Add `ControlNetworkAdapter(Guid Id, string Name, string Description, bool? Enabled, bool Connected, bool Wireless)` to `CrabDesk.Core/NetworkAdapters.cs`.
- [x] Add `INetworkAdapterControl.Read()` and `SetEnabledAsync(Guid id, bool enabled)` in `CrabDesk.Native/NetworkAdapterControl.cs`. Query visible adapters including disabled devices. Use administrative state rather than link status.
- [x] Build a Windows PowerShell encoded command from the GUID and fixed `Enable-NetAdapter` or `Disable-NetAdapter` command. Select exactly one adapter and pass its object to the command. Return an explicit cancellation or failure result; do not change any adapter during verification.
- [x] Add `CrabDesk.Runtime/NetworkAdaptersPopup.cs` with scrollable adapter rows, status, accessible enable/disable controls, refresh and network-settings actions. Run I/O off the UI thread, serialize commands and refresh, preserve row controls between polls, and read back actual state. Keep the popup open while an authorization dialog is active.
- [x] Add mock-based tests for command routing, failed/cancelled operations, unavailable/empty states, light/dark backgrounds, long names, scrolling and busy controls.

## Task 3: Verification And Delivery

- [x] Run `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~ControlQuickActionsTests`. Passed: 44.
- [x] Run focused WinUI tests for adapter controls, quick-action editing, integration and existing detail cards. Passed: 80.
- [x] Inspect rendered adapter-card PNGs. Run a read-only adapter enumeration against this machine. Dark/light renders verified; the native service returned eight adapters, including two disabled adapters. Both command scripts passed PowerShell parsing. Enable/disable commands were verified using mocks.
- [x] Publish Release to `artifacts/network-adapter-controls-build/`, then gracefully restart the app. Release publish succeeded; the new process is running and responding. Add the shortcut through the control center's quick-action editor.
