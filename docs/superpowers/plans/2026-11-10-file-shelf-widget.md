# File Shelf Widget Implementation Plan

> **For agentic workers:** Use parallel agents for the independent core storage and WinUI work; integrate the desktop runtime in this workspace. Steps use checkbox syntax for tracking.

**Goal:** Add a desktop file shelf with user-selectable references or managed copies, multiple-file collection, drag-out, and confirmed removal.

**Architecture:** Append `FileShelf` to the persisted widget enum. Each item retains its own storage mode and source path; copies live under the configuration directory with widget/item GUID directories. The desktop renderer and WinUI preview use the same item metadata. Filesystem work runs away from the UI thread.

**Tech Stack:** .NET 8, existing WinForms desktop surface, WinUI 3 settings, xUnit.

## Core storage and persistence

- [x] Add `CrabDesk.Core/WidgetFileShelf.cs` and `WidgetFileShelfStore.cs`; update `DesktopWidgets.cs` and add `CrabDesk.Tests/WidgetFileShelfTests.cs`.
- [x] Implement the following contract, retaining mode independently for every entry:

```csharp
public enum WidgetFileShelfMode { Reference, Copy }
// DesktopWidget: FileShelfMode, List<WidgetFileShelfItem> FileShelfItems;
// JsonIgnore: bool FileShelfBusy; string FileShelfStatus;
// WidgetFileShelfItem: Id, OriginalPath, StoredRelativePath, Mode, AddedAt;
// JsonIgnore: Name, bool? IsAvailable;
// WidgetFileShelfStore(string configDirectory):
// Task<WidgetFileShelfItem> AddAsync(Guid widgetId, string sourcePath,
//     WidgetFileShelfMode mode, CancellationToken cancellationToken = default);
// string ResolvePath(Guid widgetId, WidgetFileShelfItem item);
// void Remove(Guid widgetId, WidgetFileShelfItem item);
```

- [x] Test references, independent copies, same-name files, cancellation, path containment, mixed-mode persistence, normalization, and missing files. Removal can only delete the owned copy path, never the original.
- [x] Run `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --filter FullyQualifiedName~WidgetFileShelfTests`.

## Desktop runtime and interactions

- [x] Add `CrabDeskRuntime.FileShelf.cs` and `DesktopBoxForm.WidgetFileShelf.cs`; integrate widget drawing/input, OLE drop routing, runtime removal/disposal, and background availability refresh.
- [x] Implement runtime APIs `SetFileShelfMode`, `AddFileShelfFilesAsync`, `PickFileShelfFilesAsync`, `GetFileShelfPath`, `RemoveFileShelfItemAsync`, and `ClearFileShelfAsync`.
- [x] Provide file rows, selection with Ctrl/Shift, scroll, double-click open, right-click open/location/copy-path/remove, file picker, and multi-file drag-out. Source files remain at their original locations during collection.
- [x] Confirm copy removal and list clearing; snapshot the batch mode before asynchronous work. Surface partial import errors, preserve successful entries, cancel copies on disposal, and avoid filesystem reads during painting.
- [x] Add `CrabDesk.WinUI.Tests/FileShelfRuntimeTests.cs` covering drop routing, mode retention, selection/drag payload, and confirmed removal behavior without attaching to the desktop.

## WinUI gallery and settings

- [x] Update `WidgetsPage.xaml`, `WidgetsPage.xaml.cs`, and `WidgetPreview.xaml.cs`; add `WidgetsPage.FileShelf.cs` and focused settings tests.
- [x] Expose a labeled reference/copy selector, current-mode explanation, add/clear buttons, operation status, and component preview. Changes apply to future additions; existing entries retain their recorded mode.
- [x] Describe the file shelf in `docs/desktop-widgets.md`, including managed-copy location and metadata-only layout backups.

## Integration verification

- [x] Build the WinUI application through the test project and run focused widget storage, runtime, settings, and existing widget tests. Do not run tests that attach to the live desktop.
- [x] Render representative light/dark file-shelf cards for visual inspection and inspect the task-owned diff. Leave changes uncommitted.
