# Contextual Widget Management Implementation Plan

**Goal:** Keep component previews and editing in one continuous management surface.

**Architecture:** Retain the My Widgets/Add Widgets preview catalog. A right-side SplitView hosts the existing instance controls; wide layouts show it alongside the gallery and narrow layouts use an overlay. Opening a card selects it, additions return to the managed collection, and saves refresh visible previews without leaving the current screen. Preserve runtime navigation, duplicate prevention, all widget editors and local content.

- [x] Restructure WidgetsPage XAML into gallery and contextual editor, with compact form fields, clear close action and responsive drawer.
- [x] Update page navigation/refresh to keep the gallery visible, highlight selection, preserve gallery scroll and close cleanly on light dismissal or removal.
- [x] Verify add/edit/switch/close/remove, automatic saves, duplicate prevention, explicit desktop navigation and narrow/wide layouts using the isolated native harness. Inspect rendered screens.
- [x] Build/test, update usage docs and run the verified app. Do not commit or push.

**Validation:** WinUI suite passed 746/746 tests. The isolated native harness passed contextual edit, focus retention, autosave, switching, creation, duplicate prevention, dismissal, removal and responsive layouts; rendered wide/narrow screens were inspected. Final build succeeded with 39 existing warnings and no errors. The verified build is running from `artifacts/widgets-management-build` with the widgets page open.
