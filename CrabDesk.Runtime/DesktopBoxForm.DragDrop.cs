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
        if (string.Equals(_lastLoggedFolderDropTarget, folderTargetName, StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(folderTargetName))
        {
            return;
        }
        _lastLoggedFolderDropTarget = folderTargetName;
        DiagnosticLog.Info(
            $"FolderDropProbe source={source} box={_runtime.State.Boxes.FirstOrDefault(b => b.Id == _boxes.LastOrDefault(x => x.Bounds.Contains(point))?.Box.Id)?.IsMappedFolder} " +
            $"folderTarget={folderTargetName ?? "(none)"} point=({point.X:0},{point.Y:0}) items={_items.Count}");
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
        var assigned = _runtime.AssignItems(incoming, target.Box.Id);
        if (assigned > 0 && beforeKey is not null)
        {
            // A desktop drop is an insertion, not an append. ReorderItems also
            // promotes a sorted box to manual mode so the chosen position is
            // retained after the next refresh.
            _runtime.ReorderBoxItems(target.Box.Id, incoming, beforeKey);
        }

        var manualTab = GetManualBoxTabAtPoint(target, point);
        if (assigned > 0 && manualTab is not null)
        {
            _runtime.MoveItemsToManualTab(target.Box.Id, incoming, manualTab.Id);
        }
        return assigned;
    }

    private void SetDropPreview(DropPreviewState? preview, bool requestRender = true)
    {
        if (_dropPreview == preview)
        {
            return;
        }

        if (_dropPreview?.BoxId != preview?.BoxId)
        {
            _dynamicVisualVersion++;
        }
        _dropPreview = preview;
        if (requestRender)
        {
            RequestDragRender();
        }
    }

    private void ClearDropPreview()
    {
        _folderDropTargetName = null;
        _lastDesktopDropTargetKey = null;
        if (_dropPreview is null)
        {
            return;
        }

        _dropPreview = null;
        _dynamicVisualVersion++;
        RequestDragRender();
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

    private DragImage? CreateDragImage(
        IReadOnlyList<DesktopItemRef> selected,
        DesktopItemRef? pressedItem)
    {
        if (selected.Count == 0)
        {
            return null;
        }

        var sourceBox = _pressedBoxId is { } boxId
            ? _runtime.State.Boxes.FirstOrDefault(box => box.Id == boxId)
            : null;
        var iconSize = Math.Clamp(
            (int)Math.Round((sourceBox?.Appearance.IconSize ?? 40) * _scale),
            24,
            64);
        const int padding = 8;
        var stackCount = Math.Min(3, selected.Count);
        var offset = (stackCount - 1) * 4;
        var badgeDiameter = selected.Count > 1 ? 20 : 0;
        var width = iconSize + offset + padding * 2 + badgeDiameter / 2;
        var height = iconSize + offset + padding * 2;
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.Clear(Color.Transparent);
            var accent = sourceBox is null
                ? ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor)
                : ParseOpaqueColor(sourceBox.Appearance.Accent);
            for (var index = stackCount - 1; index >= 0; index--)
            {
                var tileOffset = index * 4;
                var tile = new RectangleF(
                    padding + tileOffset - 2,
                    padding + tileOffset - 2,
                    iconSize + 4,
                    iconSize + 4);
                using var tileFill = new SolidBrush(Color.FromArgb(32 + index * 8, accent));
                using var tileBorder = new Pen(Color.FromArgb(150, accent), 1);
                using var tilePath = RoundedRectangle(tile, 5);
                graphics.FillPath(tileFill, tilePath);
                graphics.DrawPath(tileBorder, tilePath);
            }

            var primary = pressedItem is null
                ? selected[0]
                : selected.FirstOrDefault(item => item.Key == pressedItem.Key) ?? selected[0];
            var icon = GetIconBitmap(primary, (float)(sourceBox?.Appearance.IconSize ?? 40)) ??
                       ShellIconProvider.GetGenericFileIcon();
            if (icon is not null)
            {
                graphics.DrawImage(icon, new Rectangle(padding, padding, iconSize, iconSize));
            }

            if (selected.Count > 1)
            {
                var badge = new RectangleF(width - badgeDiameter - 2, height - badgeDiameter - 2, badgeDiameter, badgeDiameter);
                using var badgeFill = new SolidBrush(Color.FromArgb(245, accent));
                using var badgePath = RoundedRectangle(badge, badgeDiameter / 2f);
                using var badgeText = new SolidBrush(Color.White);
                using var badgeFont = new Font("Segoe UI", 8, FontStyle.Bold, GraphicsUnit.Point);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                graphics.FillPath(badgeFill, badgePath);
                graphics.DrawString(selected.Count.ToString(), badgeFont, badgeText, badge, format);
            }

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
            var cursorOffset = sourceIconBounds.IsEmpty
                ? new Point(padding + iconSize / 2, padding + iconSize / 2)
                : new Point(
                    padding + (int)Math.Round(relativeCursorX * Math.Max(0, iconSize - 1)),
                    padding + (int)Math.Round(relativeCursorY * Math.Max(0, iconSize - 1)));
            cursorOffset = new Point(
                Math.Clamp(cursorOffset.X, 0, width - 1),
                Math.Clamp(cursorOffset.Y, 0, height - 1));
            return new DragImage(bitmap, cursorOffset);
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }
    }

    private void OnDragOver(object? sender, Forms.DragEventArgs eventArgs)
    {
        var point = ToDip(PointToClient(new Point(eventArgs.X, eventArgs.Y)));
        // Every frame recomputes the drop-target folder highlight; branches
        // that accept a folder item set it again before returning.
        _folderDropTargetName = null;
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
            _folderDropTargetName = deskDropFolderTarget?.DisplayName;
            LogFolderDropProbe("DeskIconDrag", deskDropFolderTarget?.DisplayName, point);
            var acceptsDrop = deskDropFolderTarget is not null ||
                              target.MappedFolder?.IsReadOnly != true;
            UpdateOleDropPreview(
                targetGeometry,
                point,
                desktopDrag.ItemKeys,
                desktopDrag.ItemKeys.Count,
                acceptsDrop,
                // No grid insertion projection: a box receives the drop into
                // its body (or a highlighted mapped subfolder), it never
                // reflows the item grid visually.
                DropPreviewKind.Assign,
                floatingCard: false);
            // A box body accepts the drag as a plain virtual assignment
            // (Copy). Entering a folder item under the pointer turns it into
            // a real filesystem move (Ctrl = copy), matching Explorer.
            eventArgs.Effect = deskDropFolderTarget is not null
                ? (IsControlPressed(eventArgs) ? Forms.DragDropEffects.Copy : Forms.DragDropEffects.Move)
                : acceptsDrop ? Forms.DragDropEffects.Copy : Forms.DragDropEffects.None;
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
                ? Forms.DragDropEffects.Copy
                : Forms.DragDropEffects.None;
            return;
        }

        var desktopVirtualAssignment = IsDesktopVirtualAssignment(eventArgs, target);
        if (target!.MappedFolder?.IsReadOnly == true)
        {
            UpdateOleDropPreview(
                targetGeometry,
                point,
                GetDragItemKeys(eventArgs),
                GetDragItemCount(eventArgs),
                false,
                DropPreviewKind.Assign,
                floatingCard: false);
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }
        var effect = ResolveTransferEffect(eventArgs, target);
        var mappedFolderTarget = GetFolderDropTarget(targetGeometry, point);
        _folderDropTargetName = mappedFolderTarget?.DisplayName;
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
                DropPreviewKind.Assign,
                floatingCard: false);
            eventArgs.Effect = IsControlPressed(eventArgs)
                ? Forms.DragDropEffects.Copy
                : Forms.DragDropEffects.Move;
            InvalidateDropPreview(target.Id);
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
                DropPreviewKind.Assign,
                floatingCard: false);
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }
        // A desktop file dropped into a normal box is a virtual assignment,
        // not a filesystem move. Advertising Move makes Explorer dim the
        // source icon as a cut operation until its delayed shell refresh.
        // External folder imports default to Move, matching Explorer; the
        // Ctrl key forces a Copy.
        eventArgs.Effect = desktopVirtualAssignment
            ? Forms.DragDropEffects.Copy
            : IsControlPressed(eventArgs)
                ? Forms.DragDropEffects.Copy
                : Forms.DragDropEffects.Move;
        // Grid insertion/Restock projections are intentionally omitted: the
        // drop lands in the box body and ordering is still resolved from the
        // pointer at drop time.
        var previewKind = DropPreviewKind.Assign;
        UpdateOleDropPreview(
            targetGeometry!,
            point,
            GetDragItemKeys(eventArgs),
            GetDragItemCount(eventArgs),
            eventArgs.Effect != Forms.DragDropEffects.None,
            previewKind,
            // Box-item drags carry no shell drag image, so the box draws the
            // shared ghost card itself. External file and desktop-icon drags
            // already have a following ghost and only need slot feedback.
            floatingCard: eventArgs.Data?.GetDataPresent(ItemKeysFormat) == true);
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
                forward(point, null, desktopDrag.ItemKeys);
                return;
            }

            // Box-item drags draw their own ghost card on this surface; the
            // surface must not paint a second external card (and leave a
            // stale one behind after the drop).
            if (eventArgs.Data.GetDataPresent(ItemKeysFormat))
            {
                forward(point, null, null);
                return;
            }

            IReadOnlyList<string>? externalPaths = null;
            if (eventArgs.Data.GetDataPresent(Forms.DataFormats.FileDrop) &&
                eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is string[] paths)
            {
                externalPaths = paths;
            }
            forward(point, externalPaths, null);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Box drag state forward failed", exception);
        }
    }

    private void OnDragLeave(object? sender, EventArgs eventArgs)
    {
        ClearDropPreview();
        _iconDragStateForward?.Invoke(PointF.Empty, null, null);
    }

    private void UpdateOleDropPreview(
        BoxGeometry target,
        PointF point,
        IReadOnlyList<string> itemKeys,
        int itemCount,
        bool acceptsDrop,
        DropPreviewKind kind,
        bool floatingCard = false)
    {
        var manualTabIndex = GetManualBoxTabIndex(target, point);
        SetDropPreview(new DropPreviewState(
            target.Box.Id,
            point,
            itemKeys,
            itemCount,
            acceptsDrop,
            kind,
            manualTabIndex,
            floatingCard));
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

        // Explorer's desktop drag exposes only FileDrop paths. Resolve those
        // paths back to the stable runtime keys so DesktopAssign can project
        // the exact destination slot before the drop is committed.
        if (eventArgs.Data?.GetDataPresent(Forms.DataFormats.FileDrop) == true &&
            eventArgs.Data.GetData(Forms.DataFormats.FileDrop) is string[] paths)
        {
            var desktopItemsByPath = _runtime.Items
                .Where(item => item.FileSystemPath is not null)
                .ToDictionary(
                    item => Path.GetFullPath(item.FileSystemPath!),
                    item => item.Key.ToString(),
                    StringComparer.OrdinalIgnoreCase);
            return paths
                .Select(path => Path.GetFullPath(path))
                .Where(desktopItemsByPath.ContainsKey)
                .Select(path => desktopItemsByPath[path])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
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
        _iconDragStateForward?.Invoke(PointF.Empty, null, null);
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
            var mappedFolderTarget = GetFolderDropTarget(box, point);
            DiagnosticLog.Info(
                $"FolderDrop point=({point.X:0},{point.Y:0}) boxMapped={box.Box.IsMappedFolder} " +
                $"target={mappedFolderTarget?.DisplayName ?? "(none)"} items={_items.Count}");
            if (mappedFolderTarget is not null)
            {
                // A drop on a real folder item (inside any box, or a mapped
                // box's subfolder) imports the payload into that folder:
                // external FileDrop, or a CrabDesk desktop-icon drag.
                // Default is Move; holding Ctrl forces a Copy.
                var move = !IsControlPressed(eventArgs);
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
                    var imported = await _runtime.ImportFilesToBoxAsync(
                        paths,
                        box.Box.Id,
                        !IsControlPressed(eventArgs));
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
                var imported = await _runtime.ImportFilesAsync(
                    external,
                    box.Box.Id,
                    !IsControlPressed(eventArgs));
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

    private static bool IsControlPressed(Forms.DragEventArgs eventArgs)
    {
        const int controlKeyState = 8;
        return (eventArgs.KeyState & controlKeyState) != 0;
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
        const int shiftKeyState = 4;
        const int controlKeyState = 8;
        return BoxTransferPolicy.Resolve(
            internalItems,
            sourceMapped,
            target.IsMappedFolder,
            (eventArgs.KeyState & shiftKeyState) != 0,
            (eventArgs.KeyState & controlKeyState) != 0,
            sourceMappedReadOnly);
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

