# Widget Gallery Implementation Plan

> **For agentic workers:** Execute inline with executing-plans. Keep existing widget implementations and user data. Do not commit or push.

**Goal:** Add widgets through visual preview cards and open instance editing by clicking a managed preview card.

**Architecture:** WidgetsPage has an overview with My Widgets/Add Widgets galleries and a separate detail view. A reusable WinUI WidgetPreview control shows sample data for creation cards and actual instance content for management cards. Desktop edit requests are consumed once, allowing Back to remain on the gallery during background updates.

**Tech Stack:** WinUI 3, native XAML controls, existing CrabDesk widget models/runtime.

- [x] Add `CrabDesk.WinUI/Controls/WidgetPreview.xaml` and `.xaml.cs`: six read-only thumbnails, compact size proportions, inherited colors/radius, empty/loading states, no network side effects.
- [x] Update `CrabDesk.WinUI/Views/WidgetsPage.xaml`: two gallery tabs with wrapping preview tiles; separate editor with Back and a live preview, retaining existing per-kind controls.
- [x] Update `WidgetsPage.xaml.cs`: catalog samples, actual instance cards, add/edit/back/remove navigation, refresh preview after saves and background results.
- [x] Update `CrabDesk.Runtime/CrabDeskRuntime.Widgets.cs`: consumable navigation requests so desktop clicks open a specific editor and the management menu opens the overview.
- [x] Build to a fresh ignored output directory, preserving the running process during compilation.
- [x] Use the isolated WinUI validation harness to click catalog/instance cards; verify Back, autosave, desktop edit requests, retained translation, empty states and narrow layouts. Inspect rendered gallery/editor images.
- [x] Run relevant existing checks and update `docs/desktop-widgets.md` with gallery entry/edit behavior.
- [x] Gracefully replace the running development build with the verified build and open the gallery, following the user's existing run-new-version request. Verified PID 21544 responds and startup reaches MainWindow activated.
