# Widget Content Visuals

Improve the contents of the existing desktop widgets and their WinUI previews with colored filled symbols and restrained semantic color. Preserve widget dimensions, hit regions, saved preferences, and data behavior.

- [x] Add original 32-unit content SVGs and same-source transparent PNGs for controls, hardware, networking, weather metrics, translation, task states, and music. Use the existing resource pipeline.
- [x] Add shared Core asset/tone lookup in `WidgetContentVisuals.cs`. Hardware uses blue/violet/mint/amber; networking uses blue download and violet upload; controls use semantic colors and fade unavailable icons.
- [x] Update `DesktopBoxForm.WidgetControlCenter.cs` and `WidgetPreview`: colored quick actions, audio/display symbols, distinct sliders, and music artwork fallback. Keep operation affordances and interaction states.
- [x] Update monitoring renderers: filled hardware icons, tinted progress tracks, colored network direction symbols and connection context; keep stable speed slots and sampling.
- [x] Update task check states, translation field headings and surfaces, weather metrics, note/empty file visuals, clock hands and calendar weekends. Keep content readable and retain typography preferences.
- [x] Run resource rendering and related widget/control/translation/task/network tests. Inspect generated desktop cards and the running gallery on light/dark backgrounds.
- [x] Exit normally via `--exit`, publish Release, start the new build with `--show-widgets`, and check actual gallery contents.

Completed 2026-11-10. Added 30 content symbols to the 25 existing assets. Related Core tests: 128 passed; WinUI/runtime tests: 216 passed. Resource tests check alpha and color at 20/24/32/48/64 pixels; desktop rendering exercises all eleven widget kinds at 180/372 DIP widths, light/dark surfaces, and 1/1.5/2 DPI. Text alignment checks include translation, note, and task editors. Release publish succeeded with existing project warnings. The running application has all eleven gallery entries and visibly uses the content artwork; captured first/last gallery pages are under `artifacts/widget-content`. Windows high contrast and physical monitor DPI transitions were not manually exercised.

Validation: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --filter 'FullyQualifiedName~WidgetContentVisuals|FullyQualifiedName~WidgetMonitor|FullyQualifiedName~WidgetNetwork|FullyQualifiedName~WidgetTodo|FullyQualifiedName~WidgetClock'`; `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug -p:Platform=x64` with related non-desktop-attach filters; `dotnet publish CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Release -r win-x64 --self-contained false -p:Platform=x64 -o artifacts/file-shelf-build`.

Changes remain uncommitted. Existing user edits stay in place.
