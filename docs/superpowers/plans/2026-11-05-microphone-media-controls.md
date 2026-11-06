# Microphone and Media Controls Implementation Plan

> Execute the tasks inline in the current workspace, preserving the existing control-center changes. Do not commit or push.

**Goal:** Add a microphone popup with device selection, volume, mute and input level; add album artwork and seekable playback progress to the control center.

**Architecture:** Keep the 372 × 292 control-center footprint. Add the microphone entry beside the output selector. Read capture endpoints through Core Audio on a worker, serialize and coalesce writes by endpoint ID, and refresh while the popup is open. Extend media snapshots with artwork and seek capabilities; keep a captured track identity through a drag and issue one seek on release.

**Tech Stack:** .NET 8, WinForms/GDI+, WPF popup controls, Core Audio COM, Windows media transport sessions, xUnit/Moq.

## 1. Microphone model and native adapter

- [x] Add `CrabDesk.Core/Microphone.cs` with `MicrophoneDevice` and `MicrophoneSnapshot` (selected device, volume, mute, peak and device list).
- [x] Add `CrabDesk.Native/Microphone.cs` and `AudioSession.Microphone.cs`; enumerate `EDataFlow.Capture`, activate endpoint volume and `IAudioMeterInformation`, release COM objects in `finally`, and reuse the endpoint-routing policy for all roles.
- [x] Validate capture endpoint identity before writes; unavailable devices produce nullable readings.

## 2. Microphone interaction and popup

- [x] Add `CrabDesk.Runtime/MicrophoneController.cs` with serialized background reads/writes, latest slider value coalescing, device IDs captured at interaction time, and read-back after failed commands.
- [x] Add `MicrophonePopup.cs` using the existing audio popup styles, a device selector, volume slider, mute button and input meter. Provide keyboard names, Escape dismissal and dark/light colors.
- [x] Add `DesktopBoxForm.Microphone.cs`; integrate popup dismissal/disposal and refresh with the existing form lifecycle. Add the entry rectangle to shared presentation and WinUI preview.

## 3. Media model, read and seek

- [x] Extend `ControlCenterMedia` with track identity, artwork bytes, seek capability/bounds and playback rate.
- [x] Extend `IWidgetControlCenterMonitor` with `Task<bool> SeekMediaAsync(ControlCenterMedia media, TimeSpan position, CancellationToken token = default)`.
- [x] Read the current session's thumbnail, cache a small PNG by track, normalize timeline offsets, and verify the captured session/track immediately before `TryChangePlaybackPositionAsync`.
- [x] Route seeks through the runtime's serialized control command path and refresh the resulting snapshot.

## 4. Compact rendering and drag lifecycle

- [x] Share cover/title/artist/button/progress/time rectangles in `ControlCenterPresentation`; keep clickable progress separate from transport buttons.
- [x] Draw cached rounded artwork or a music glyph, preview the position throughout dragging and commit once on mouse-up, including release outside the card.
- [x] Cancel stale drags when media changes or the widget disappears. Dispose cached images with the form. Update the WinUI preview using the same layout.

## 5. Verification and launch

- [x] Add tests for microphone coalescing, endpoint selection and stale read-back, failure recovery, mute/volume behavior, unavailable inputs and popup rendering.
- [x] Add tests for media layout, progress clamping, unsupported seeking, changed-track cancellation, drag-outside release and artwork rendering/replacement.
- [x] Run the relevant core and runtime suites with `dotnet test` (`-c Debug -p:Platform=x64` for WinUI tests), inspect rendered popup/widget images, and run `git diff --check`.
- [x] Normally exit the running application with `--exit` before building its executable, then start the updated Debug build and verify it remains responsive.

Validation: 101 runtime/UI tests and 33 core tests passed. Light/dark popup and widget images were inspected. A read-only native probe found three microphone endpoints and stable media track identity/artwork across consecutive reads. The active player reported no duration or seek capability. The updated application started and remained responsive.
