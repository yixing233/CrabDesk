# Widget Color Icons Integration Plan

**Goal:** Integrate the approved eleven widget identity icons, matching colored weather imagery, and actual file icons in File Shelf.

**Architecture:** Canonical SVG and derived transparent PNG assets live in `CrabDesk.Runtime/Assets/WidgetIcons`. WinUI reads SVG resources; the WinForms renderer uses embedded PNG assets. Core resolves stable widget/weather asset names. File icons reuse the existing asynchronous Shell loader and effective reference/copy paths.

**Tech Stack:** Existing WinUI 3, WinForms/System.Drawing, ShellIconProvider, xUnit. No new application dependencies.

- [x] Copy all eleven approved SVG assets, retaining the repaired Lyrics and ControlCenter designs; generate raster derivatives for desktop rendering. Add colored weather variants with matching silhouettes and palette.
- [x] Add `CrabDesk.Core/WidgetIconCatalog.cs` mapping all widget kinds and weather descriptions to assets; test day/night with known sunrise/sunset and weather fallback.
- [x] Add `CrabDesk.Runtime/WidgetColorIcons.cs`; embed PNG resources and copy SVG assets to application output/publish. Cache decoded static PNG assets, preserving original colors and alpha.
- [x] Add a WinUI color icon control, component gallery identity column and editor title icon. Replace weather glyphs with the matching shared resource; load File Shelf real file images asynchronously with stale-result/lifetime checks. Existing operation icons and layout geometry remain stable.
- [x] Reuse desktop icon cache/queue for visible File Shelf entries; preserve missing file labels and lower image opacity. Include file-shelf keys in retention and refresh invalidation.
- [x] Replace the desktop weather summary glyph with the shared color asset; keep existing weather click/drag geometry. Support transparent rendering across DPI scales.
- [x] Add focused asset, presentation, mapping, and cache tests; run related gallery/file-shelf/weather tests. Inspect rendered cards and published resources.
- [x] Stop the running instance through `--exit`, publish Release, start the new build with `--show-widgets`, and verify gallery/title icons and runtime startup.

Verification on 2026-11-10: 72 related Core tests and 96 WinUI/runtime tests passed. Release publish succeeded with existing project warnings. All 25 published SVG files match source hashes. Transparent PNGs were inspected on light/dark backgrounds at 24 and 72 pixels; rendering tests cover 20/24/32/48/64 pixels. The running app's complete eleven-item gallery, weather preview, file-type samples, and File Shelf editor identity icon were checked with UI Automation and window captures. Other physical DPI settings and Windows high contrast were not manually exercised.

Changes stay uncommitted. Preserve the current workspace's other component development.
