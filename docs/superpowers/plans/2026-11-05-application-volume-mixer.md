# Application Volume Mixer Implementation Plan

**Goal:** Open a scrollable application volume card from the control center, with endpoint volume and independent application volume/mute controls.

**Architecture:** Read and write Core Audio sessions on a worker through `IAudioMixer`. Group sessions belonging to the same executable. An `AudioMixerController` coalesces slider changes, targets the captured output device, and publishes immutable snapshots. A WPF card in a WinForms popup renders the controls and closes on Escape or loss of focus.

**Tech Stack:** .NET 8, Core Audio COM, WinForms/ElementHost, WPF, xUnit/Moq. Continue in the current workspace and preserve the existing control-center changes.

## Task 1: Audio session access

- [x] Add `CrabDesk.Core/AudioMixer.cs` with endpoint and application snapshot records.
- [x] Add `CrabDesk.Native/AudioMixer.cs` with this service contract:

```csharp
public interface IAudioMixer
{
    AudioMixerSnapshot Read();
    bool SetVolume(string deviceId, string? applicationId, double percent);
    bool SetMuted(string deviceId, string? applicationId, bool muted);
}
```

- [x] Implement session enumeration in `AudioSession.Mixer.cs`, matching `audiopolicy.h` from the installed Windows SDK. Skip expired sessions, resolve process names/icons, group by executable, and release COM objects on the calling worker.
- [x] Use the captured endpoint ID for writes; a null application ID addresses its master volume. Correct the shared COM signatures used by these operations to preserve HRESULTs and pointer arguments.

## Task 2: Interaction and popup

- [x] Add `AudioMixerController.cs`: serialize I/O, merge pending changes by endpoint/application, update the view immediately, read back after writes, and discard stale reads.
- [x] Add `AudioMixerPopup.cs`: title/close button, fixed master row, scrollable app rows, icons, names, percentages, mute buttons, keyboard-operable sliders, loading/empty/error states, and bounded placement within the monitor work area.
- [x] Add a mixer icon at the right of the control-center volume caption. Share its hit rectangle between painting, hit-testing, and the preview. Wire opening, owner cleanup, and popup dismissal in `DesktopBoxForm`.

## Task 3: Verification and launch

- [x] Test application-specific writes, coalescing, mute/volume ordering, read-back failure handling, grouping, entry hit-testing, and screen-edge placement.
- [x] Render the popup with several mock applications and verify the exported image for spacing, scroll area, and slider visibility.
- [x] Build the app and run `WidgetControlCenterTests`, `WidgetMonitorRuntimeTests`, and the new mixer tests. Read the real endpoint/session list without changing host audio as a native smoke check.
- [x] Stop the current app through `--exit`, launch the newly built Debug executable, and verify that it stays responsive for manual testing.

No commit or push is part of this request.
