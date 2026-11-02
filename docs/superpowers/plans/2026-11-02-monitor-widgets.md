# Performance and Network Widgets Implementation Plan

> **For agentic workers:** Implement this authorized feature in the current session. Preserve the pending weather/clock changes and do not commit or publish. Use focused parallel workers for the independent native samplers, then integrate and validate the complete feature.

**Goal:** Add performance and network desktop widgets with real CPU/GPU/memory/disk readings, network throughput, latency, adapter type, local IPv4/IPv6 and on-demand public IP.

**Architecture:** Immutable snapshots in Core; background Windows samplers in Native; one runtime sampling gate shared by all monitor surfaces. Samples trigger visual repaint and a dedicated preview event, without layout normalization, configuration writes or editor rebuilding. Existing widget add/remove/size/backup rules remain in use. Unavailable counters and first-sample rates display an em dash, rather than fabricated zeroes.

**Tech Stack:** .NET 8, Windows PDH, GlobalMemoryStatusEx, NetworkInterface, Ping, HTTPS ipify; native card painting and WinUI previews/settings.

### Task 1: Models and native samplers

- [x] Add `CrabDesk.Core/WidgetMonitoring.cs` contracts and `[JsonIgnore]` runtime snapshots to `DesktopWidgets.cs`. Append the enum kinds to retain existing numeric values. Network defaults to the wide card so IP lines remain readable.
- [x] Implement `CrabDesk.Native/WidgetPerformanceMonitor.cs`: PDH CPU/GPU/disk utilization and read/write rates; physical memory and fixed-volume capacity. Collect counters on one worker, treat warming/missing counters as unavailable, and dispose native query handles.
- [x] Implement `CrabDesk.Native/WidgetNetworkMonitor.cs`: selected or routed active adapter, byte deltas using real elapsed time, adapter/IP/type/link data, bounded background ping every five seconds. Reset the rate baseline after interface changes or counter resets. Query public IP only on explicit action.
- [x] Verify calculation behavior with `dotnet test CrabDesk.Tests --filter FullyQualifiedName~WidgetMonitor` and exercise real samplers using standard APIs.

### Task 2: Runtime lifecycle

- [x] Add `CrabDesk.Runtime/CrabDeskRuntime.Monitors.cs` with a single-flight one-second sampler, cancellation on disposal, transient snapshots and an independent `WidgetMonitorUpdated` event. Sample only when the corresponding widget exists and desktop takeover is active.
- [x] Extend the existing widget timer to request samples. Add a visual-only repaint method instead of calling the layout/save path for each sample.
- [x] Public IP refresh must cancel on removal/disposal and reject results from a superseded interface. Validate the returned address before displaying it.

### Task 3: Cards, preview and settings

- [x] Add native performance card rows with utilization bars and numeric CPU/GPU/memory/disk metrics; the wide version includes memory use and disk read/write, with capacity in settings and context actions.
- [x] Add the network card with download/upload speeds, connection type, latency and local/public IP rows. Clicking the public-IP row queries or refreshes it; context actions copy full addresses and open network settings.
- [x] Update gallery descriptions, illustrative catalog samples, actual previews and settings. Network settings expose adapter selection and ping target; performance help explains warm-up/unavailable counters. Reuse native WinUI controls and preserve card radius/material.

### Task 4: Verification and delivery

- [x] Test persistence compatibility, duplicate prevention, counter reset/rate calculation, GPU aggregation, disconnected interfaces, HTTP timeout/retry, explicit-only public IP and lifecycle cancellation. Exercise live default-route selection, latency sampling and the public endpoint in the integration harness.
- [x] Build and run widget/runtime regressions; capture light/dark native and WinUI cards at supported sizes, including unavailable/first-sample/disconnected states. All 1,650 solution tests passed; live sampling did not trigger configuration-save events and preview updates preserved the input caret.
- [x] Launch the updated local test build with `--show-widgets`; desktop takeover and responsiveness verified. The live HTTPS public-IP probe returned a valid address. GPU availability depends on driver counters; ICMP and public IP follow system routing.
