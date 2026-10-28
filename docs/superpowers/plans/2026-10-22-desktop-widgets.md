# Desktop Widgets Implementation Plan

> **For agentic workers:** Execute inline using executing-plans, task by task. User scope: implement clock, calendar, note, todo, translation and weather. Do not commit or push.

**Goal:** Add six persistent desktop widgets matching the compact desktop card design.

**Architecture:** Store independent widget instances in CrabDeskState. Render them on the existing DesktopBoxForm canvas and include their shapes in its native hit region and shared acrylic host. Use a WinUI management page for creation and rich editing; desktop actions open the selected instance in that page. Weather uses Open-Meteo with explicit city selection; translation reuses the existing configurable AI profile, as selected by the user.

**Tech Stack:** .NET 8, WinForms/GDI+, WinUI 3, System.Text.Json, HttpClient, xUnit.

## 1. Data and offline behavior

- [x] Add `CrabDesk.Core/DesktopWidgets.cs`: kinds, supported compact sizes, instances, todo items, cached weather, calendar week generation and normalization.
- [x] Extend `Models.cs` state with `List<DesktopWidget> Widgets`; normalize through `JsonLayoutStore` without changing old box migrations.
- [x] Add persistence and calendar tests in `CrabDesk.Tests/DesktopWidgetTests.cs`: old JSON has no widgets, content/check states survive reload, invalid sizes normalize, leap February and six-week calendars are correct.
- [x] Run `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter FullyQualifiedName~DesktopWidget`.

## 2. Online services

- [x] Add `CrabDesk.Core/WidgetWeatherService.cs`: city search, current temperature/code and daily min/max, explicit cancellation, attributed data, no automatic location lookup.
- [x] Add `CrabDesk.Core/WidgetTranslationService.cs`: user-triggered translation, source auto detection via prompt, selected target language, configured chat-completions endpoint, no transmission on typing.
- [x] Add injected HttpMessageHandler tests for URL escaping, response parsing, failures, cancellation and translation request authentication/content.
- [x] Run `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter FullyQualifiedName~Widget`.

## 3. Runtime and desktop rendering

- [x] Add `CrabDesk.Runtime/CrabDeskRuntime.Widgets.cs`: create, update, remove, resize, lock, place within monitor work area and maintain refresh/save notifications.
- [x] Add `DesktopBoxForm.Widgets.cs` and `DesktopBoxForm.Widgets.Rendering.cs`: six content layouts, management menu, calendar navigation, todo toggling, drag placement, scrollable text/list views and time updates.
- [x] Include widgets in normal/static/acrylic render passes, hit regions, backup foreground capture and safe pointer routing. Dispose widget timers on surface teardown.
- [x] Add runtime geometry tests and run `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --filter FullyQualifiedName~DesktopWidgetRuntime`.

## 4. Management and editing

- [x] Add `CrabDesk.WinUI/Views/WidgetsPage.xaml` and `.xaml.cs`, runtime operations and a navigation item. Provide six creation buttons, instance selection, content editors, size/monitor/lock controls and explicit removal confirmation.
- [x] Provide note autosave, todo add/edit/check/remove, clock settings, translation target/input/result/copy/cancel, city search/result selection and weather refresh/error/cache states.
- [x] Add “小组件” entry to CrabDesk desktop context menu and existing single-instance settings routing; use selected-widget state when opening an editor from the desktop.
- [x] Keep callbacks scoped to the selected instance and cancel online requests on navigation/removal.

## 5. Verification and delivery

- [x] Build `CrabDesk.sln` using its default Any CPU configuration, which maps WinUI to x64, and run existing test projects; inspect failures before edits.
- [x] Use isolated validation data/output for UI checks; preserve the running user instance and its data.
- [x] Check all six cards, 0/8/20 corner settings, solid/acrylic rendering, note/list clipping, drag/lock and persistence; validate real weather connectivity separately from unit fixtures.
- [x] Update design/docs with initial behavior and online configuration requirements. Report implemented scope, build/tests and material remaining limits honestly.

Validation: solution build succeeds with existing warnings; all 1,278 tests pass (Core/Native 499, WinUI/Runtime 733, Bootstrapper 46). Isolated WinUI harness verifies six editor selections, note save/reload, task creation, retained translation, compact viewport and 0/8/20 DIP rendering. Live Open-Meteo city search and forecast requests succeed. Translation transport is covered with an injected HTTP client; no personal API credentials were used for validation.

Native composition validation also succeeds using an offscreen synthetic desktop: moving a widget updates the composed parent bitmap and clears its old pixels; the real acrylic host enables effects, accepts all six widget regions and reports unified acrylic composition. The user's running instance remains unchanged.

Sources: [Open-Meteo forecast](https://open-meteo.com/en/docs), [geocoding](https://open-meteo.com/en/docs/geocoding-api), [terms and attribution](https://open-meteo.com/en/terms).
