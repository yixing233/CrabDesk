# Widget Page Fluent Layout Implementation Plan

**Goal:** Make widget management a coherent preview gallery and grouped contextual editor.

**Architecture:** Keep the existing unpackaged WinUI shell and SplitView navigation. Use the native GridView as the gallery scroll owner. Pin editor identity/close and removal outside its form scroller. Put primary widget content first and common placement/appearance fields in a native Expander. Use native AutoSuggestBox query submission and suggestions for adding weather cities, with existing runtime services and explicit network actions.

**Tech Stack:** Existing C# / WinUI 3 / Windows App SDK 1.8; Microsoft winui-design examples and API discovery; no new app dependencies.

**Page contract:** Primary task is selecting and editing an existing widget; secondary tasks are adding and removing. Preview tiles are low-density; detail fields are grouped by task. The shell remains list/detail. A 372 DIP editor is inline above 800 DIP of available page width, otherwise an overlay. All actions remain reachable with mouse, keyboard and touch. The existing slim editor scrollbar stays in its own gutter.

- [x] In `CrabDesk.WinUI/Views/WidgetsPage.xaml`, separate gallery header from its GridView, pin editor header/footer, group content and common settings, and replace the multi-step city search form with AutoSuggestBox.
- [x] In `CrabDesk.WinUI/Views/WidgetsPage.xaml.cs`, adapt search/add/cancellation state, reset detail scroll and collapsed settings on selection, shorten weather status, and retain duplicate prevention, explicit desktop editing, saves, multi-city selection and removal confirmation.
- [x] Verify native selection/switch/close/add, all six editors, focused note saves, collapsed/expanded settings, city suggestion addition, async state dismissal, keyboard scrolling and field bounds at narrow/medium/wide widths. Inspect light and dark renders and theme resource usage for contrast support.
- [x] Update `docs/desktop-widgets.md`, run the WinUI tests and final project build, gracefully replace the running CrabDesk with the verified output, and confirm startup. Do not commit or push.

**Verification:** The isolated native harness passed all six primary editors, note focus/persistence, native search and candidate invocation, city removal confirmation/cancellation, multi-city selection, empty/failed/canceled searches, pinned header/footer, common settings edits, scroll automation, control bounds, and catalog reuse. Wide/medium/narrow light and dark renders were inspected. The existing semantic brushes are retained; local thumb opacity resolves to 1 in HighContrast resources. The actual Windows contrast mode was not toggled. The WinUI suite passed 746/746 tests.
