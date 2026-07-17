# WinUI 3 Migration Plan

## Current Architecture

CrabDesk currently uses two presentation technologies:

- WinUI 3 for application startup, settings, dialogs, resources, themes, and
  file pickers.
- WinForms for the desktop box surface and notification-area UI.
- Win32 and Shell APIs for Explorer hosting, desktop icon state and placement,
  global hotkeys, context-menu registration, shell icons, and file operations.

`CrabDeskRuntime` is hosted through a dispatcher delegate supplied by WinUI.
Views and ViewModels communicate with it through the application facade.

## Directly Reusable Code

### Business and data layer

- `CrabDesk.Core/Models.cs`: schema 16 state, settings, boxes, assignments,
  organization rules, update state, hotkeys, backups, and monitor geometry.
- `JsonLayoutStore`: existing `%LocalAppData%/CrabDesk/config.json` path,
  migrations, validation, backup file, and serialization format.
- `JsonBackupService`: backup, import, export, cleanup, and retention behavior.
- `OrganizationRuleEngine`: preview, conflict detection, apply, and undo inputs.
- `BoxLayoutPlanner`, `LayoutCoordinator`, `DesktopItemLayoutEngine`,
  `MonitorCoordinateConverter`, `AnimationMath`, and `SemanticVersion`.
- `GitHubUpdateService` and its existing request/result models.

### Windows service layer

- Desktop item and mapped-folder providers.
- Explorer desktop-host discovery and icon visibility/position services.
- File operations and clipboard codec.
- Monitor topology, global hotkeys, shell icons, context-menu registration, and
  double-click monitoring.

These implementations stay behind interfaces or an application facade. Views
and ViewModels do not call Win32, registry, Shell, or Explorer APIs directly.

## Code That Must Be Rewritten

- WPF `App.xaml` and `App.xaml.cs` become a WinUI 3 application entry using DI.
- `MainWindow.xaml` and its code-behind become a `NavigationView`, `Frame`, and
  page-specific ViewModels.
- WPF resource dictionaries become WinUI theme dictionaries and design tokens.
- WPF dialogs and `TextInputDialog` become `ContentDialog` implementations.
- WPF file/folder pickers become Windows App SDK picker adapters initialized
  with the WinUI window handle.
- WPF theme/chrome handling becomes WinUI requested theme plus system backdrops.
- Window activation, close-to-tray, single-instance activation, and navigation
  require WinUI-specific lifecycle code.
- The WinForms desktop surface ultimately becomes a WinUI/Composition desktop
  host. During staged migration it remains an isolated platform adapter so the
  existing Explorer behavior and data contract do not regress.
- The WinForms tray menu is isolated as a platform service and can be replaced
  after the WinUI settings surface reaches feature parity.

## Target Structure

```text
CrabDesk.Runtime/              Shared Windows runtime and legacy surface adapter
CrabDesk.WinUI/                WinUI 3 entry, views, ViewModels, DI and resources
  Resources/                  Design tokens, themes and control styles
  Services/                   Runtime facade, navigation, dialogs, pickers, theme
  ViewModels/                 Page-specific presentation state and commands
  Views/                      Navigation pages and ContentDialogs
CrabDesk.WinUI.Tests/          ViewModel and UI-logic tests
```

Development entry point:

```powershell
dotnet run --project .\CrabDesk.WinUI\CrabDesk.WinUI.csproj -c Debug
```

The legacy WPF settings project has been removed. `CrabDesk.WinUI` is the only
application entry point.

## Migration Risks

1. Reparenting a XAML desktop HWND into Explorer is not equivalent to the
   existing WinForms child surface. It needs a dedicated composition/content
   island spike and desktop input recovery tests before replacement.
2. Runtime timers marshal callbacks through the WinUI dispatcher delegate;
   timer and shutdown ordering remain covered by stability tests.
3. Single-instance events, tray activation, startup registration, and Explorer
   context-menu commands must all resolve to the same executable during the
   side-by-side period.
4. WinUI pickers require HWND initialization in unpackaged applications.
5. Theme and backdrop support varies by OS and accessibility settings; Mica and
   Mica Alt need Acrylic fallback and reduced-motion checks.
6. DPI, monitor-origin, Explorer restart, sleep/resume, and recovery-marker
   behavior have a larger regression surface than ordinary settings pages.

## Implementation Order

1. Add the WinUI project and shared runtime adapter.
2. Add DI, MVVM, navigation, theme, backdrop, dialog, picker, and window services.
3. Add design tokens and the `NavigationView` main shell.
4. Migrate General and About, then Hotkeys, Backup, Organization, Appearance,
   and Boxes pages. Build and validate after every page.
5. Add ViewModel tests and parity tests against schema 16 configuration.
6. Replace the desktop surface with a WinUI/Composition host behind the same
   interface, then repeat desktop hardware validation.
7. Packaging and startup now target WinUI exclusively. The legacy WPF
   presentation project was removed after independent launch, recovery, tray,
   update, and Explorer smoke tests passed.
