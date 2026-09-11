using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices.ComTypes;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;
using FormsIntegration = System.Windows.Forms.Integration;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm : Forms.Form
{

    private string? _folderDropTargetName;
    private string? _lastLoggedFolderDropTarget;

    /// <summary>
    /// The mapped-folder item under the pointer, when the drop point lands on
    /// a real subfolder. Files dropped there import into that subfolder.
    /// </summary>
    private DesktopItemRef? GetFolderDropTarget(BoxGeometry box, PointF point)
    {
        if (box.Box.MappedFolder?.IsReadOnly == true)
        {
            return null;
        }
        var item = GetItemAtPoint(box, point)?.Item;
        return item is { Kind: DesktopItemKind.Folder, FileSystemPath: not null }
            ? item
            : null;
    }

    internal static bool ShouldUseFolderDropTarget(
        bool folderTargetAvailable,
        bool internalBoxItemDrag) =>
        folderTargetAvailable && !internalBoxItemDrag;

    private DesktopItemRef? GetFolderDropTargetForDrag(
        BoxGeometry box,
        PointF point,
        Forms.DragEventArgs eventArgs)
    {
        var folderTarget = GetFolderDropTarget(box, point);
        return ShouldUseFolderDropTarget(
                folderTarget is not null,
                IsInternalBoxItemDrag(eventArgs))
            ? folderTarget
            : null;
    }

    private static bool IsInternalBoxItemDrag(Forms.DragEventArgs eventArgs) =>
        eventArgs.Data?.GetDataPresent(ItemKeysFormat) == true &&
        eventArgs.Data.GetDataPresent(SourceBoxFormat);

    /// <summary>
    /// Imports the active drag payload into a mapped folder's subfolder. The
    /// payload may be an external FileDrop or a CrabDesk desktop-icon drag;
    /// both carry filesystem paths that can be moved or copied into the target.
    /// </summary>
    private async Task ImportIntoTargetFolderAsync(
        BoxGeometry box,
        DesktopItemRef folderTarget,
        Forms.DragEventArgs eventArgs,
        bool move)
    {
        IReadOnlyList<string>? paths = null;
        if (eventArgs.Data?.GetDataPresent(Forms.DataFormats.FileDrop) == true &&
            eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is string[] droppedPaths)
        {
            paths = droppedPaths;
        }
        else if (eventArgs.Data?.GetDataPresent(DesktopIconSurface.DesktopIconDragSessionFormat) == true &&
                 eventArgs.Data.GetData(DesktopIconSurface.DesktopIconDragSessionFormat) is DesktopIconSurfaceDragSession desktopDrag)
        {
            var itemsByKey = _runtime.Items
                .Where(item => item.FileSystemPath is not null)
                .ToDictionary(item => item.Key.ToString(), StringComparer.OrdinalIgnoreCase);
            paths = desktopDrag.ItemKeys
                .Where(key => itemsByKey.ContainsKey(key))
                .Select(key => itemsByKey[key].FileSystemPath!)
                .ToArray();
        }

        DiagnosticLog.Info(
            $"FolderImport box={box.Box.Id:N} folder={folderTarget.DisplayName} " +
            $"path={folderTarget.FileSystemPath} move={move} paths={(paths is null ? 0 : paths.Count)}");
        if (paths is not { Count: > 0 })
        {
            DiagnosticLog.Info("FolderImport skipped: no filesystem paths resolved");
            return;
        }
        var imported = await _runtime.ImportFilesIntoFolderAsync(
            paths,
            folderTarget.FileSystemPath!,
            move);
        DiagnosticLog.Info(
            $"FolderImport result ok={imported.SucceededCount} failed={imported.FailedCount}");
        ShowImportFailures(imported);
    }

    private void LogFolderDropProbe(
        string source,
        string? folderTargetName,
        PointF point)
    {
        if (string.Equals(_lastLoggedFolderDropTarget, folderTargetName, StringComparison.Ordinal))
        {
            return;
        }
        _lastLoggedFolderDropTarget = folderTargetName;
        DiagnosticLog.Verbose(
            $"FolderDropProbe source={source} box={_runtime.State.Boxes.FirstOrDefault(b => b.Id == _boxes.LastOrDefault(x => x.Bounds.Contains(point))?.Box.Id)?.IsMappedFolder} " +
            $"folderTarget={folderTargetName ?? "(none)"} point=({point.X:0},{point.Y:0}) items={_items.Count}");
    }

    private bool SetFolderDropTargetName(string? folderTargetName)
    {
        if (string.Equals(_folderDropTargetName, folderTargetName, StringComparison.Ordinal))
        {
            return false;
        }

        _folderDropTargetName = folderTargetName;
        return true;
    }

    private int AssignDesktopItemsAtDrop(
        BoxGeometry target,
        PointF point,
        IReadOnlyList<string> itemKeys)
    {
        var incoming = itemKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (incoming.Length == 0)
        {
            return 0;
        }
        var incomingKeys = incoming.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var beforeKey = ResolveInsertBeforeKey(target, point, incomingKeys);
        var manualTab = GetManualBoxTabAtPoint(target, point);
        return _runtime.AssignDesktopItemsAtDrop(
            incoming,
            target.Box.Id,
            beforeKey,
            manualTab?.Id);
    }

    private void SetDropPreview(DropPreviewState? preview, bool requestRender = true)
    {
        var previousPreview = _dropPreview;
        if (_dropPreview == preview)
        {
            if (requestRender)
            {
                _dynamicVisualVersion++;
                RequestDropPreviewVisualUpdate(previousPreview, preview);
            }
            return;
        }

        if (requestRender)
        {
            _dynamicVisualVersion++;
        }
        _dropPreview = preview;
        if (requestRender)
        {
            RequestDropPreviewVisualUpdate(previousPreview, preview);
        }
    }

    private void ClearDropPreview()
    {
        var previousPreview = _dropPreview;
        var folderTargetChanged = SetFolderDropTargetName(null);
        if (_dropPreview is null)
        {
            if (folderTargetChanged)
            {
                _dynamicVisualVersion++;
                RequestDropPreviewVisualUpdate(previousPreview, null);
            }
            return;
        }

        _dropPreview = null;
        _dynamicVisualVersion++;
        RequestDropPreviewVisualUpdate(previousPreview, null);
    }

    private void InvalidateDropPreview(Guid? boxId)
    {
        if (boxId is not { } id)
        {
            return;
        }

        var box = _boxes.FirstOrDefault(candidate => candidate.Box.Id == id);
        if (box is not null)
        {
            InvalidateDip(box.Bounds);
        }
    }

    private void RequestDropPreviewVisualUpdate(
        DropPreviewState? previousPreview,
        DropPreviewState? currentPreview)
    {
        if (_isCompositedByIconSurface &&
            _iconLayerPartialRenderRequest is not null)
        {
            EnsureGeometry();
            RectangleF? dirtyBounds = null;
            foreach (var boxId in new[] { previousPreview?.BoxId, currentPreview?.BoxId }
                         .Where(id => id is not null)
                         .Select(id => id!.Value)
                         .Distinct())
            {
                var geometry = _boxes.FirstOrDefault(box => box.Box.Id == boxId);
                if (geometry is null)
                {
                    continue;
                }
                var candidate = RectangleF.Inflate(geometry.Bounds, 4, 4);
                dirtyBounds = dirtyBounds is { } existing
                    ? RectangleF.Union(existing, candidate)
                    : candidate;
            }

            if (dirtyBounds is { } localBounds)
            {
                _iconLayerPartialRenderRequest(localBounds);
                return;
            }
        }

        RequestDragRender();
    }

    private DragImage? CreateDragImage(
        IReadOnlyList<DesktopItemRef> selected,
        DesktopItemRef? pressedItem)
    {
        if (selected.Count == 0)
        {
            return null;
        }

        var primary = pressedItem is null
            ? selected[0]
            : selected.FirstOrDefault(item => item.Key == pressedItem.Key) ?? selected[0];
        var iconSize = (float)(
            _runtime.State.Boxes.FirstOrDefault(box => box.Id == _pressedBoxId)?.Appearance.IconSize ?? 40);
        var icon = GetIconBitmap(primary, iconSize) ?? ShellIconProvider.GetGenericFileIcon();
        using var font = ResolveDragLabelFont();
        Bitmap? bitmap = null;
        try
        {
            bitmap = DragGhostRenderer.CreateBitmap(
                icon,
                primary.DisplayName,
                selected.Count,
                font,
                _scale,
                out _,
                iconSize);

            var sourceGeometry = pressedItem is null
                ? null
                : _items.LastOrDefault(item => item.Item.Key == pressedItem.Key);
            var sourceIconBounds = sourceGeometry is null
                ? RectangleF.Empty
                : GetItemIconBounds(sourceGeometry);
            var relativeCursorX = sourceIconBounds.IsEmpty
                ? 0.5f
                : Math.Clamp((_pressPoint.X - sourceIconBounds.X) / sourceIconBounds.Width, 0f, 1f);
            var relativeCursorY = sourceIconBounds.IsEmpty
                ? 0.5f
                : Math.Clamp((_pressPoint.Y - sourceIconBounds.Y) / sourceIconBounds.Height, 0f, 1f);
            // DragGhostRenderer places the primary icon at pointer + 10 DIP.
            // Keep the original grab point inside that icon for OLE drags.
            var stackOffset = (Math.Min(3, Math.Max(1, selected.Count)) - 1) * 3f;
            var iconLeftDip = 50f + 10f - stackOffset * 0.5f;
            var iconTopDip = 10f + 10f - stackOffset * 0.5f;
            var cursorOffset = new Point(
                (int)Math.Round((iconLeftDip + relativeCursorX * iconSize) * (float)_scale),
                (int)Math.Round((iconTopDip + relativeCursorY * iconSize) * (float)_scale));
            cursorOffset = new Point(
                Math.Clamp(cursorOffset.X, 0, bitmap.Width - 1),
                Math.Clamp(cursorOffset.Y, 0, bitmap.Height - 1));
            return new DragImage(bitmap, cursorOffset);
        }
        catch
        {
            bitmap?.Dispose();
            return null;
        }
    }

    private Font ResolveDragLabelFont()
    {
        var appearance = _runtime.State.Settings.Appearance;
        var family = appearance.IconLabelFontFamily;
        var size = appearance.IconLabelFontSize;
        if (string.IsNullOrWhiteSpace(family) || size <= 0)
        {
            var systemFont = SystemFonts.IconTitleFont;
            family = systemFont?.FontFamily.Name ?? "Segoe UI";
            size = systemFont?.Size ?? 9;
        }
        return CreateFont(family, (float)size, FontStyle.Regular, GraphicsUnit.Point);
    }

    private void OnDragOver(object? sender, Forms.DragEventArgs eventArgs)
    {
        var point = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
        ForwardDragStateToIconSurface(eventArgs, point);
        // Box geometry is static during an OLE item drag; the shared compositor
        // already rebuilt it on the previous frame. Rebuilding per DragOver
        // event stalls the drag loop on fast mice.
        EnsureGeometry();
        // Desktop-item OLE drags keep the icon surface in an active pointer
        // interaction state, which intentionally pauses the normal hover
        // timer. Feed the current drag position into the expansion controller
        // here as well; the timer above continues the delay while stationary.
        UpdateHoverState(point, updateItemHover: false);
        EnsureGeometry();
        var targetGeometry = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (targetGeometry is null)
        {
            ClearDropPreview();
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }
        var target = targetGeometry.Box;

        if (eventArgs.Data?.GetDataPresent(DesktopIconSurface.DesktopIconDragSessionFormat) == true &&
            eventArgs.Data.GetData(DesktopIconSurface.DesktopIconDragSessionFormat) is DesktopIconSurfaceDragSession desktopDrag)
        {
            // This private payload only represents a CrabDesk desktop item.
            // Do not mark it handled until DragDrop: a pointer may pass over a
            // box and then return to the desktop before the button is released.
            // A mapped folder only accepts the drag when it lands on a real
            // subfolder (an internal drop there imports into that folder).
            var deskDropFolderTarget = GetFolderDropTarget(targetGeometry, point);
            var folderTargetChanged = SetFolderDropTargetName(deskDropFolderTarget?.DisplayName);
            LogFolderDropProbe("DeskIconDrag", deskDropFolderTarget?.DisplayName, point);
            var acceptsDrop = deskDropFolderTarget is not null ||
                              target.MappedFolder?.IsReadOnly != true;
            UpdateOleDropPreview(
                targetGeometry,
                point,
                desktopDrag.ItemKeys,
                desktopDrag.ItemKeys.Count,
                acceptsDrop,
                floatingCard: false,
                folderTargetChanged: folderTargetChanged);
            if (deskDropFolderTarget is not null)
            {
                var sourcePaths = ExtractSourcePaths(eventArgs);
                var isMove = ResolveFilesystemDropEffect(eventArgs, deskDropFolderTarget.FileSystemPath ?? string.Empty, sourcePaths);
                eventArgs.Effect = isMove ? Forms.DragDropEffects.Move : Forms.DragDropEffects.Copy;
            }
            else
            {
                eventArgs.Effect = acceptsDrop
                    ? (target.IsMappedFolder ? ToDragDropEffects(ResolveTransferEffect(eventArgs, target)) : Forms.DragDropEffects.Move)
                    : Forms.DragDropEffects.None;
            }
            return;
        }

        // Legacy pointer-only desktop drags still render their preview from
        // DesktopIconSurface. Keep that path out of the generic thumbnail
        // renderer while an OLE session is not present.
        if (_runtime.IsDesktopIconPointerInteractionActive)
        {
            var acceptsDesktopDrop = !targetGeometry.Box.IsMappedFolder &&
                                     targetGeometry.Box.MappedFolder?.IsReadOnly != true;
            eventArgs.Effect = acceptsDesktopDrop
                ? Forms.DragDropEffects.Move
                : Forms.DragDropEffects.None;
            return;
        }

        var desktopVirtualAssignment = IsDesktopVirtualAssignment(eventArgs, target);
        if (target!.MappedFolder?.IsReadOnly == true)
        {
            var folderTargetChanged = SetFolderDropTargetName(null);
            UpdateOleDropPreview(
                targetGeometry,
                point,
                GetDragItemKeys(eventArgs),
                GetDragItemCount(eventArgs),
                false,
                floatingCard: false,
                folderTargetChanged: folderTargetChanged);
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }
        var effect = ResolveTransferEffect(eventArgs, target);
        var mappedFolderTarget = GetFolderDropTargetForDrag(targetGeometry, point, eventArgs);
        var mappedFolderTargetChanged = SetFolderDropTargetName(mappedFolderTarget?.DisplayName);
        LogFolderDropProbe("FileDrop", mappedFolderTarget?.DisplayName, point);
        if (mappedFolderTarget is not null && targetGeometry is not null)
        {
            // Dropping onto a folder item inside any box imports into that
            // real folder: default Move, Ctrl forces Copy.
            UpdateOleDropPreview(
                targetGeometry,
                point,
                GetDragItemKeys(eventArgs),
                GetDragItemCount(eventArgs),
                true,
                floatingCard: false,
                folderTargetChanged: mappedFolderTargetChanged);
            var sourcePaths = ExtractSourcePaths(eventArgs);
            var isMove = ResolveFilesystemDropEffect(eventArgs, mappedFolderTarget.FileSystemPath ?? string.Empty, sourcePaths);
            eventArgs.Effect = isMove ? Forms.DragDropEffects.Move : Forms.DragDropEffects.Copy;
            return;
        }
        if (effect == BoxTransferEffect.VirtualMove && targetGeometry is not null &&
            GetMappedFolderTabAtPoint(targetGeometry, point) is not null)
        {
            // File-type tabs are filtered views, not drop destinations.
            UpdateOleDropPreview(
                targetGeometry,
                point,
                GetDragItemKeys(eventArgs),
                GetDragItemCount(eventArgs),
                false,
                floatingCard: false,
                folderTargetChanged: mappedFolderTargetChanged);
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }
        // A desktop file dropped into a normal box is a virtual assignment,
        // displaying Move (no misleading plus sign).
        // For mapped folders or external imports, effect follows BoxTransferPolicy
        // with volume-aware default and modifier key overrides.
        eventArgs.Effect = desktopVirtualAssignment
            ? Forms.DragDropEffects.Move
            : ToDragDropEffects(effect);
        UpdateOleDropPreview(
            targetGeometry!,
            point,
            GetDragItemKeys(eventArgs),
            GetDragItemCount(eventArgs),
            eventArgs.Effect != Forms.DragDropEffects.None,
            // DesktopIconSurface owns the box-item ghost in its small layered
            // overlay. This surface only renders target/tab/folder feedback.
            floatingCard: false,
            folderTargetChanged: mappedFolderTargetChanged);
    }

    private void ForwardDragStateToIconSurface(
        Forms.DragEventArgs eventArgs,
        PointF point)
    {
        var forward = _iconDragStateForward;
        if (forward is null || eventArgs.Data is null)
        {
            return;
        }

        try
        {
            // A CrabDesk desktop-icon drag carries FileDrop paths too. Treat
            // it as a desktop drag: the dragged icons are the ghost.
            if (eventArgs.Data.GetDataPresent(DesktopIconSurface.DesktopIconDragSessionFormat) &&
                eventArgs.Data.GetData(DesktopIconSurface.DesktopIconDragSessionFormat) is
                    DesktopIconSurfaceDragSession desktopDrag)
            {
                forward(point, null, desktopDrag.ItemKeys, null);
                return;
            }

            // Box-item drags use the icon surface's small layered ghost. Pass
            // the stable keys so the ghost stays independent of box redraws.
            if (eventArgs.Data.GetDataPresent(ItemKeysFormat))
            {
                forward(point, null, GetDragItemKeys(eventArgs), _dragIconGrabOffset);
                return;
            }

            IReadOnlyList<string>? externalPaths = null;
            if (eventArgs.Data.GetDataPresent(Forms.DataFormats.FileDrop) &&
                eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is string[] paths)
            {
                externalPaths = paths;
            }
            forward(point, externalPaths, null, null);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Box drag state forward failed", exception);
        }
    }

    private void OnDragLeave(object? sender, EventArgs eventArgs)
    {
        ClearDropPreview();
        _iconDragStateForward?.Invoke(PointF.Empty, null, null, null);
    }

    private void UpdateOleDropPreview(
        BoxGeometry target,
        PointF point,
        IReadOnlyList<string> itemKeys,
        int itemCount,
        bool acceptsDrop,
        bool floatingCard = false,
        bool folderTargetChanged = false)
    {
        var manualTabIndex = GetManualBoxTabIndex(target, point);
        var targetVisualChanged = HasDesktopDropTargetVisualChanged(
                                      _dropPreview?.BoxId,
                                      _dropPreview?.AcceptsDrop,
                                      _dropPreview?.TargetManualTabIndex,
                                      target.Box.Id,
                                      acceptsDrop,
                                      manualTabIndex) ||
                                  _dropPreview?.FloatingCard != floatingCard;
        var pointerChanged = _dropPreview?.Pointer != point;
        var renderNeeded = ShouldRenderOleDropPreview(
            floatingCard,
            targetVisualChanged,
            folderTargetChanged,
            pointerChanged);
        SetDropPreview(
            new DropPreviewState(
                target.Box.Id,
                point,
                itemKeys,
                itemCount,
                acceptsDrop,
                manualTabIndex,
                floatingCard),
            renderNeeded);
    }

    private static int GetDragItemCount(Forms.DragEventArgs eventArgs)
    {
        if (eventArgs.Data?.GetDataPresent(ItemKeysFormat) == true &&
            eventArgs.Data.GetData(ItemKeysFormat) is string[] keys)
        {
            return Math.Max(1, keys.Length);
        }
        if (eventArgs.Data?.GetDataPresent(Forms.DataFormats.FileDrop) == true &&
            eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is string[] paths)
        {
            return Math.Max(1, paths.Length);
        }
        return 1;
    }

    private IReadOnlyList<string> GetDragItemKeys(Forms.DragEventArgs eventArgs)
    {
        if (eventArgs.Data?.GetDataPresent(ItemKeysFormat) == true &&
            eventArgs.Data.GetData(ItemKeysFormat) is string[] keys)
        {
            return keys;
        }

        return [];
    }

    private bool IsDesktopVirtualAssignment(Forms.DragEventArgs eventArgs, DesktopBox target)
    {
        if (target.IsMappedFolder || eventArgs.Data is null ||
            eventArgs.Data.GetDataPresent(ItemKeysFormat) ||
            !eventArgs.Data.GetDataPresent(Forms.DataFormats.FileDrop) ||
            eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is not string[] paths)
        {
            return false;
        }

        var desktopPaths = _runtime.Items
            .Where(item => item.FileSystemPath is not null)
            .Select(item => Path.GetFullPath(item.FileSystemPath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return paths.Length > 0 && paths.All(path => desktopPaths.Contains(Path.GetFullPath(path)));
    }

    private async void OnDragDrop(object? sender, Forms.DragEventArgs eventArgs)
    {
        DiagnosticLog.Info($"Surface drag drop monitor={_monitor.Id} effects={eventArgs.AllowedEffect}");
        // The OLE drag ends with the drop. WinForms does not reliably raise
        // DragLeave afterwards, so clear the icon surface's ghost state here
        // or a stale card can stay frozen on screen after the drop.
        _iconDragStateForward?.Invoke(PointF.Empty, null, null, null);
        try
        {
            if (eventArgs.Data is null)
            {
                return;
            }
            var point = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
            RebuildGeometry();
            var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
            if (box is null)
            {
                return;
            }
            var manualTargetTab = GetManualBoxTabAtPoint(box, point);
            var mappedTargetTab = GetMappedFolderTabAtPoint(box, point);
            var mappedFolderTarget = GetFolderDropTargetForDrag(box, point, eventArgs);
            DiagnosticLog.Info(
                $"FolderDrop point=({point.X:0},{point.Y:0}) boxMapped={box.Box.IsMappedFolder} " +
                $"target={mappedFolderTarget?.DisplayName ?? "(none)"} items={_items.Count}");
            if (mappedFolderTarget is not null)
            {
                var sourcePaths = ExtractSourcePaths(eventArgs);
                var move = ResolveFilesystemDropEffect(eventArgs, mappedFolderTarget.FileSystemPath ?? string.Empty, sourcePaths);
                await ImportIntoTargetFolderAsync(box, mappedFolderTarget, eventArgs, move);
                return;
            }
            if (eventArgs.Data.GetDataPresent(DesktopIconSurface.DesktopIconDragSessionFormat) &&
                eventArgs.Data.GetData(DesktopIconSurface.DesktopIconDragSessionFormat) is DesktopIconSurfaceDragSession desktopDrag)
            {
                desktopDrag.HandledByBox = true;
                if (box.Box.IsMappedFolder || box.Box.MappedFolder?.IsReadOnly == true)
                {
                    return;
                }

                AssignDesktopItemsAtDrop(box, point, desktopDrag.ItemKeys);
                return;
            }
            var transferEffect = ResolveTransferEffect(eventArgs, box.Box);
            DiagnosticLog.Info($"Surface drag drop resolved monitor={_monitor.Id} effect={transferEffect}");
            if (transferEffect == BoxTransferEffect.None)
            {
                return;
            }
            if (eventArgs.Data.GetDataPresent(ItemKeysFormat) &&
                eventArgs.Data.GetData(ItemKeysFormat) is string[] keys &&
                eventArgs.Data.GetDataPresent(SourceBoxFormat) &&
                eventArgs.Data.GetData(SourceBoxFormat) is string sourceValue &&
                Guid.TryParse(sourceValue, out var sourceBoxId))
            {
                if (eventArgs.Data.GetDataPresent(DragSessionFormat) &&
                    eventArgs.Data.GetData(DragSessionFormat) is InternalDragSession dragSession)
                {
                    dragSession.HandledByBox = true;
                }
                if (sourceBoxId == box.Box.Id)
                {
                    if (manualTargetTab is not null)
                    {
                        _runtime.MoveItemsToManualTab(box.Box.Id, keys, manualTargetTab.Id);
                        return;
                    }
                    if (mappedTargetTab is not null)
                    {
                        return;
                    }
                    var beforeKey = GetReorderBeforeKey(box, point);
                    _runtime.ReorderBoxItems(box.Box.Id, keys, beforeKey);
                    return;
                }
                try
                {
                    var imported = await _runtime.TransferBoxItemsAsync(
                        sourceBoxId,
                        keys,
                        box.Box.Id,
                        transferEffect == BoxTransferEffect.MoveFiles);
                    ShowImportFailures(imported);
                    if (manualTargetTab is not null)
                    {
                        _runtime.MoveItemsToManualTab(box.Box.Id, keys, manualTargetTab.Id);
                    }
                }
                catch (Exception exception)
                {
                    DesktopConfirmationDialog.ShowMessage(
                        this,
                        _runtime.IsDarkTheme,
                        "CrabDesk",
                        exception.Message,
                        DesktopDialogKind.Error);
                }
                return;
            }
            if (!eventArgs.Data.GetDataPresent(Forms.DataFormats.FileDrop) || eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is not string[] paths)
            {
                return;
            }

            if (box.Box.IsMappedFolder)
            {
                try
                {
                    var isMove = transferEffect == BoxTransferEffect.MoveFiles;
                    var imported = await _runtime.ImportFilesToBoxAsync(
                        paths,
                        box.Box.Id,
                        isMove);
                    ShowImportFailures(imported);
                }
                catch (Exception exception)
                {
                    DesktopConfirmationDialog.ShowMessage(
                        this,
                        _runtime.IsDarkTheme,
                        "CrabDesk",
                        exception.Message,
                        DesktopDialogKind.Error);
                }
                return;
            }

            var desktopPaths = _runtime.Items
                .Where(item => item.FileSystemPath is not null)
                .ToDictionary(item => Path.GetFullPath(item.FileSystemPath!), StringComparer.OrdinalIgnoreCase);
            var assignedKeys = new List<string>();
            var external = new List<string>();
            foreach (var path in paths)
            {
                var fullPath = Path.GetFullPath(path);
                if (desktopPaths.TryGetValue(fullPath, out var item))
                {
                    assignedKeys.Add(item.Key.ToString());
                }
                else
                {
                    external.Add(path);
                }
            }
            AssignDesktopItemsAtDrop(box, point, assignedKeys);
            if (external.Count > 0)
            {
                var isMove = transferEffect == BoxTransferEffect.MoveFiles;
                var imported = await _runtime.ImportFilesAsync(
                    external,
                    box.Box.Id,
                    isMove);
                ShowImportFailures(imported);
            }
            // Assigned desktop icons are parked outside the visible work area by
            // the runtime; no per-drop Explorer move is needed here.
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop box drag-drop failed.", exception);
            DesktopConfirmationDialog.ShowMessage(
                this,
                _runtime.IsDarkTheme,
                "导入失败",
                exception.Message,
                DesktopDialogKind.Error);
        }
        finally
        {
            ClearDropPreview();
        }
    }

    private static bool IsShiftPressed(Forms.DragEventArgs eventArgs)
    {
        const int shiftKeyState = 4;
        return (eventArgs.KeyState & shiftKeyState) != 0;
    }

    private static bool IsControlPressed(Forms.DragEventArgs eventArgs)
    {
        const int controlKeyState = 8;
        return (eventArgs.KeyState & controlKeyState) != 0;
    }

    private IReadOnlyList<string> ExtractSourcePaths(Forms.DragEventArgs eventArgs)
    {
        if (eventArgs.Data?.GetDataPresent(Forms.DataFormats.FileDrop) == true &&
            eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is string[] droppedPaths &&
            droppedPaths.Length > 0)
        {
            return droppedPaths;
        }

        if (eventArgs.Data?.GetDataPresent(ItemKeysFormat) == true &&
            eventArgs.Data.GetData(ItemKeysFormat) is IReadOnlyList<string> itemKeys &&
            itemKeys.Count > 0)
        {
            return itemKeys
                .Select(key => _runtime.FindItemByKey(key)?.FileSystemPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToList();
        }

        if (eventArgs.Data?.GetDataPresent(DesktopIconSurface.DesktopIconDragSessionFormat) == true &&
            eventArgs.Data.GetData(DesktopIconSurface.DesktopIconDragSessionFormat) is DesktopIconSurfaceDragSession desktopDrag &&
            desktopDrag.ItemKeys.Count > 0)
        {
            return desktopDrag.ItemKeys
                .Select(key => _runtime.FindItemByKey(key)?.FileSystemPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToList();
        }

        return [];
    }

    private bool ResolveFilesystemDropEffect(
        Forms.DragEventArgs eventArgs,
        string targetDirectory,
        IReadOnlyList<string>? sourcePaths)
    {
        var shiftPressed = IsShiftPressed(eventArgs);
        var controlPressed = IsControlPressed(eventArgs);
        var isSameVolume = sourcePaths is null || sourcePaths.Count == 0 ||
                           BoxTransferPolicy.AreAllSameVolume(sourcePaths, targetDirectory);
        var effect = BoxTransferPolicy.Resolve(
            internalItems: false,
            sourceMapped: false,
            targetMapped: true,
            shiftPressed: shiftPressed,
            controlPressed: controlPressed,
            sourceMappedReadOnly: false,
            isSameVolume: isSameVolume);
        return effect == BoxTransferEffect.MoveFiles;
    }

    private BoxTransferEffect ResolveTransferEffect(Forms.DragEventArgs eventArgs, DesktopBox target)
    {
        if (target.MappedFolder?.IsReadOnly == true || eventArgs.Data is null)
        {
            return BoxTransferEffect.None;
        }
        var internalItems = eventArgs.Data.GetDataPresent(ItemKeysFormat);
        Guid? sourceId = null;
        var sourceMapped = false;
        var sourceMappedReadOnly = false;
        if (eventArgs.Data.GetDataPresent(SourceBoxFormat) &&
            eventArgs.Data.GetData(SourceBoxFormat) is string sourceValue &&
            Guid.TryParse(sourceValue, out var parsedSourceId))
        {
            sourceId = parsedSourceId;
            var source = _runtime.State.Boxes.FirstOrDefault(box => box.Id == parsedSourceId);
            sourceMapped = source?.IsMappedFolder == true;
            sourceMappedReadOnly = source?.MappedFolder?.IsReadOnly == true;
        }
        if (sourceId == target.Id)
        {
            return BoxTransferEffect.VirtualMove;
        }

        var destinationDir = target.IsMappedFolder
            ? target.MappedFolder?.Path ?? string.Empty
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var sourcePaths = ExtractSourcePaths(eventArgs);
        var isSameVolume = sourcePaths.Count == 0 || BoxTransferPolicy.AreAllSameVolume(sourcePaths, destinationDir);

        return BoxTransferPolicy.Resolve(
            internalItems,
            sourceMapped,
            target.IsMappedFolder,
            IsShiftPressed(eventArgs),
            IsControlPressed(eventArgs),
            sourceMappedReadOnly,
            isSameVolume);
    }

    private static Forms.DragDropEffects ToDragDropEffects(BoxTransferEffect effect) => effect switch
    {
        BoxTransferEffect.VirtualMove or BoxTransferEffect.MoveFiles => Forms.DragDropEffects.Move,
        BoxTransferEffect.CopyFiles => Forms.DragDropEffects.Copy,
        _ => Forms.DragDropEffects.None
    };

    private void OnGiveFeedback(object? sender, Forms.GiveFeedbackEventArgs eventArgs)
    {
        if (!_showVirtualDesktopDropCursor ||
            eventArgs.Effect != Forms.DragDropEffects.None ||
            IsPointerOverAnyBox(Forms.Cursor.Position))
        {
            return;
        }

        eventArgs.UseDefaultCursors = false;
        Forms.Cursor.Current = Forms.Cursors.SizeAll;
    }

}

