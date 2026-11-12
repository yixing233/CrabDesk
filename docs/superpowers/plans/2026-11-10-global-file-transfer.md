# Global File Transfer Station Implementation Plan

> Execute inline in this session. Preserve existing uncommitted work; do not commit or push.

**Goal:** Rename the FileShelf UI to 文件中转站 and add an optional, persistent, always-on-top window with draggable placement and edge auto-hide.

**Architecture:** Each existing FileShelf widget owns its files and floating presentation settings. The desktop and floating window share that widget and the existing runtime/store operations. App reconciles floating windows independently of desktop takeover; closing the floating window disables its switch without deleting files.

**Tech Stack:** C#/.NET 8, WinUI 3 ListView and AppWindow, existing Shell icon loader, pure Core placement calculations. Incoming files use the runtime native CF_HDROP receiver, completing the OLE handoff before scheduling file operations on the UI dispatcher.

## 1. Model and placement

- [x] Add `FileShelfFloatingSettings` (Enabled=false, AutoHide=true, MonitorId, optional Bounds in monitor work-area-relative DIP, DockEdge) to DesktopWidget; normalize invalid positions/enums on load.
- [x] Implement `FileShelfPlacement.Snap(LayoutRect, LayoutRect, double)` and `Collapsed(LayoutRect, LayoutRect, FileShelfDockEdge, double)` for all four work-area edges. Test negative monitor coordinates, clamping, nearest-edge snapping and visible tab bounds.
- [x] Rename user-facing strings and migrate the old default title only; retain FileShelf identifiers and managed paths for compatibility.

## 2. Floating window

- [x] Create `CrabDesk.WinUI/Windows/FileShelfWindow.cs`, `.Files.cs`, `.Placement.cs`. Use a borderless, always-on-top AppWindow with a 380×420 DIP expanded surface and a 32×112 DIP vertical or 112×32 DIP horizontal edge tab.
- [x] Build a ListView with extended selection, file names, source/mode detail, async Shell icons, empty/drop states, add/mode/close controls, and open/copy-path/remove actions. Reuse runtime add/remove/resolve operations; outgoing StorageItems use Copy and the existing source marker.
- [x] Capture header pointer movement for dragging; save only completed positions. Snap within 20 DIP; after 650 ms outside collapse if AutoHide is enabled and no drag, dialog, flyout or file operation is active. Hover for 180 ms or drag over the tab to expand. Keep the tab inside the monitor work area.
- [x] Restore positions using monitor identity and DIP coordinates; clamp when the monitor/work area changes. Release timers, subscriptions and icon work when closed.

## 3. Settings and lifecycle

- [x] Add labeled global-floating and edge-auto-hide switches plus reset-position action to WidgetsPage.FileShelf. Wire runtime setters and load guards.
- [x] Add `App.FileShelf.cs` to reconcile enabled widget windows at startup and on runtime changes, without checking desktop pause state. Dispose all windows on shutdown; disable flags only for user close.
- [x] Allow file availability refresh when floating mode is active during desktop pause. Expose batch removal so one confirmation produces one operation.

## 4. Validation and delivery

- [x] Run `dotnet test CrabDesk.Tests -c Release --filter FullyQualifiedName~FileShelf` and the WinUI FileShelf tests into a fresh `artifacts/file-transfer-build` directory.
- [x] Extend the isolated widget harness to exercise real topmost windows, shared data, dragging/snap/expand/collapse, settings persistence, disabled auto-hide, close/reopen, and monitor clamping; capture expanded and edge styles.
- [x] Update `docs/desktop-widgets.md`, build successfully, normally exit the previous CrabDesk process and run the new build.
