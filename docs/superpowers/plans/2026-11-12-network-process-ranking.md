# Network process ranking implementation plan

**Goal:** Make the network widget's detailed view show the three busiest processes, selectable by download or upload.

**Architecture:** Keep adapter speed and latency sampling. Add a disposable ETW collector for actual TCP/UDP process traffic, enabled only in the detailed view. Share ranking and layout between the desktop renderer and WinUI preview; persist the selected direction. Show permission, warming-up and failure states explicitly.

**Tech Stack:** .NET 8, Microsoft TraceEvent, WinForms/GDI+, WinUI 3, xUnit.

- [x] Add process snapshots, direction normalization, ranking and compact three-row layout in Core. Verify ordering, invalid rates, serialization and geometry.
- [x] Add the ETW collector in Native; subscribe to IPv4/IPv6 TCP/UDP send/receive, exclude loopback and release the session when unused. Keep adapter totals working when process collection is unavailable.
- [x] Wire collection into Runtime, replace IP rows with ranking and add desktop direction buttons. Preserve IP tools in the context menu.
- [x] Update the WinUI preview and editor with the same ranking and a download/upload selector. Explain the all-adapter scope and Windows permission requirement.
- [x] Compact the detailed view's speed cells to a single line of direction icon and speed, and increase process rows to 18 DIP.
- [x] Run Core tests, focused desktop/WinUI tests and a Release build. Restart the app and inspect the detailed editor and permission state.

Validation:

- Core suite: 991 passed. After the final spacing adjustment, all 23 monitor presentation tests passed.
- Focused desktop and editor suite: 88 passed. After the final spacing adjustment, 15 rendering, ranking interaction and editor tests passed.
- Release build: 0 warnings, 0 errors.
- Inspected the rendered light/dark detailed cards and the running WinUI editor. UI Automation confirmed upload/download selection persists as 1/0, and the final preview has one label for each direction (the ranking controls).
- Restarted the new build, process 50096 responding.
- This environment is not elevated. Verified real adapter sampling and the process permission state; privileged ETW traffic collection still needs an elevated-machine smoke test.

No commit or push is included.
