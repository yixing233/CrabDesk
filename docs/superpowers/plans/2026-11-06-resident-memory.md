# Resident Desktop Memory Implementation Plan

> Execute inline in the existing workspace; preserve the current changes and do not commit or push.

**Goal:** Reduce retained native bitmap and composition resources while preserving desktop input, cross-monitor transfers, and partial lyrics painting.

**Architecture:** Keep the operating system's uploaded one-alpha input surface, but release the runtime bitmap and upload DIB immediately after a successful presentation. Explicitly clear and dispose an acrylic monitor's foreground when it has no box/widget regions; recreate the foreground on demand when content returns. Retain buffers needed for active lyrics animation.

**Tech Stack:** .NET 8, WinForms/GDI+, Windows.UI.Composition, xUnit, dotnet-counters.

## Evidence

- Running process 13804: System.Runtime GC heap averages 22.2 MB and GC committed memory is 25.1 MB; private memory is approximately 400 MiB.
- Each 2560×1440 / 2400×1350 32-bit screen buffer holds 14.06 / 12.36 MiB of pixels.
- Both monitors retain a CPU hit-mask bitmap and LayeredWindowPresenter upload DIB even though only one initial upload is required.
- The second monitor has no boxes or widgets, but retains a full CPU acrylic bitmap, two composition surfaces, and an upload texture.

## Changes and verification

- [x] Add `CrabDesk.WinUI.Tests/DesktopSurfaceMemoryTests.cs`: verify empty → occupied → empty → occupied foreground allocation, upload-resource disposal, independent monitor cleanup, and hit-mask upload release while retaining size/presentation state. Also verify actual native alpha hit testing after releasing upload buffers, without sending input events.
- [x] Cleanly exit CrabDesk with its `--exit` command and run the new tests before implementation; all three original resource-lifecycle tests fail at the missing lifecycle hooks/state.
- [x] In `DesktopBoxForm.cs`, retain only hit-mask size/presentation state. Use a temporary bitmap for the one-alpha upload and call `LayeredWindowPresenter.Release(Handle)` after success. Preserve retry and resize handling.
- [x] In `DesktopBoxForm.RenderPipeline.cs`, use a temporary 1×1 logical-DPI bitmap for hover text measurement instead of retaining the hit-mask bitmap.
- [x] In `DesktopBoxForm.Acrylic.cs`, register an empty-frame callback and release `_acrylicFrameBitmap` when there are no acrylic regions. Keep full painting as fallback when no empty callback is registered.
- [x] In `DesktopAcrylicHost.cs`, remove/dispose the cleared monitor's foreground, synchronize remaining backdrop regions, and commit. In `DesktopSurfaceManager.cs`, wire the callback for each monitor.
- [x] Update the source-boundary marker in `DesktopStartupTakeoverTests.cs` to the renamed hit-mask helper.
- [x] Run the listed Release test command while CrabDesk is stopped, additionally covering header/item hover and desktop icon interaction: 577 passed, 0 failed, 0 skipped. Release build succeeds with existing warnings.
- [x] Restart the Release executable normally, warm up, measure 40 seconds of working set/private memory/CPU, and compare GC counters and resource samples with the previous build under the same desktop configuration.

## Measurement rules

Report working set and private memory separately. Do not force GC or trim the working set. Record process age and any workload difference that materially affects the comparison.

## Results

Both baseline and optimized builds use the same two monitors, nine collapsed boxes, 170 desktop items, and lyrics/control-center widgets on the primary monitor. Both are normally restarted, allowed to warm up for 60 seconds, and sampled for 40 seconds; process age at completion is 105.6 seconds.

| Metric | Before (PID 48032) | After (PID 17080) | Reduction |
| --- | ---: | ---: | ---: |
| Average working set | 558.75 MiB | 497.59 MiB | 61.16 MiB (10.95%) |
| Average private memory | 402.78 MiB | 348.09 MiB | 54.70 MiB (13.58%) |

The initial after sample included System.Runtime counter collection; an additional 40-second sample without diagnostic collection, ending at process age 456.9 seconds, reports 495.29 MiB working set, 340.44 MiB private memory, and 1.71% average CPU / 2.62% peak on 20 logical processors. Memory remains lower after several minutes without GC forcing or working-set trimming. CPU varies with lyrics/workload; the fixed 120-frame rendering benchmark remains 9.11 ms/frame and 811 allocated bytes/frame, compared with 9.18 ms/frame / 811 bytes before this memory change.

Resource samples and counters are saved under `C:/Users/Administrator/.codex/visualizations/2026/11/06/01a1ae97-d9ca-79f2-a3b1-19817007098e/` as `resident-memory-before-*`, `resident-memory-after-*`, `resident-memory-after-clean-*`, and `memory-counters-*.json`. The regression report is `CrabDesk.WinUI.Tests/TestResults/resident-memory.trx`. PID 17080 is running the optimized Release build and responds normally.
