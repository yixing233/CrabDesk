using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

internal sealed class DesktopSurfaceManager : IDisposable
{
    private readonly List<DesktopBoxForm> _surfaces = [];
    private readonly List<DesktopIconSurface> _iconSurfaces = [];
    private readonly CrabDeskRuntime _runtime;
    private readonly DesktopHostService _host;
    private readonly IntPtr _desktopListView;
    private bool _desktopIconViewWasVisible;
    private bool _desktopIconViewHidden;
    private bool _desktopIconsVisible = true;
    private bool _deleteInProgress;

    internal int SurfaceCount => _surfaces.Count;

    internal DesktopSurfaceManager(
        CrabDeskRuntime runtime,
        DesktopHostService host,
        IReadOnlyList<MonitorLayout> monitors)
    {
        _runtime = runtime;
        _host = host;
        _desktopListView = host.DesktopListView;
        try
        {
            // A forced process exit can leave Explorer's ListView hidden even
            // though the user still has "Show desktop icons" enabled. Restore
            // that native fallback before creating replacement windows, then
            // hide it again only after our visual icon layer is ready.
            var desktopIconsRequested = DesktopIconPositionService
                .GetDesktopViewState()
                .DesktopIconsVisible;
            if (desktopIconsRequested &&
                !DesktopWindowTools.EnsureDesktopIconViewVisible(_desktopListView))
            {
                throw new InvalidOperationException(
                    "The Explorer desktop icon view could not be restored before visual takeover.");
            }

            var parentHandle = host.DesktopView;
            var parentBounds = DesktopWindowTools.GetWindowBounds(parentHandle);
            foreach (var monitor in monitors)
            {
                var iconSurface = new DesktopIconSurface(runtime, monitor, host.DesktopListView);
                try
                {
                    DesktopWindowTools.AttachAsDesktopChild(iconSurface.Handle, parentHandle);
                    DesktopWindowTools.PositionAboveDesktop(
                        iconSurface.Handle,
                        host.DesktopListView,
                        (int)(monitor.PixelBounds.X - parentBounds.X),
                        (int)(monitor.PixelBounds.Y - parentBounds.Y),
                        (int)monitor.PixelBounds.Width,
                        (int)monitor.PixelBounds.Height);
                    if (!iconSurface.RefreshWorkspace() || !iconSurface.IsLayerReady)
                    {
                        throw new InvalidOperationException(
                            $"The CrabDesk desktop icon surface could not be rendered: {iconSurface.LayerDiagnostic}");
                    }
                    iconSurface.Show();
                    if (!DesktopWindowTools.ShowAboveDesktop(iconSurface.Handle, host.DesktopListView) ||
                        !iconSurface.IsLayerReady)
                    {
                        throw new InvalidOperationException(
                            $"The CrabDesk desktop icon surface could not be shown: {iconSurface.LayerDiagnostic}");
                    }
                    _iconSurfaces.Add(iconSurface);
                }
                catch
                {
                    iconSurface.Dispose();
                    throw;
                }
            }
            foreach (var monitor in monitors)
            {
                var surface = new DesktopBoxForm(runtime, monitor);
                try
                {
                    surface.PrepareIconLayerComposition();
                    DesktopWindowTools.AttachAsDesktopChild(surface.Handle, parentHandle);
                    DesktopWindowTools.PositionAboveDesktop(
                        surface.Handle,
                        host.DesktopListView,
                        (int)(monitor.PixelBounds.X - parentBounds.X),
                        (int)(monitor.PixelBounds.Y - parentBounds.Y),
                        (int)monitor.PixelBounds.Width,
                        (int)monitor.PixelBounds.Height);
                    if (!surface.RefreshWorkspace() || !surface.IsLayerReady || !surface.ValidateWindowRegion())
                    {
                        throw new InvalidOperationException("The CrabDesk desktop surface region could not be verified.");
                    }
                    surface.Show();
                    var shown = DesktopWindowTools.ShowAboveDesktop(surface.Handle, host.DesktopListView);
                    var regionUpdated = surface.UpdateInteractionRegion();
                    var regionValid = surface.ValidateWindowRegion();
                    if (!shown || !regionUpdated || !surface.IsLayerReady || !regionValid)
                    {
                        throw new InvalidOperationException(
                            "The CrabDesk desktop surface region was lost while showing. " +
                            $"shown={shown} regionUpdated={regionUpdated} regionValid={regionValid} " +
                            DesktopWindowTools.GetDesktopSurfaceDiagnostics(surface.Handle, host.DesktopListView));
                    }
                    _surfaces.Add(surface);
                }
                catch
                {
                    surface.Dispose();
                    throw;
                }
            }
            ConfigureBoxIconLayerComposition();
            Refresh();
            EnsureReady();
            if (!DesktopWindowTools.TryHideDesktopIconView(_desktopListView, out _desktopIconViewWasVisible))
            {
                throw new InvalidOperationException("The Explorer desktop icon view could not be hidden after the visual surfaces were ready.");
            }
            _desktopIconViewHidden = _desktopIconViewWasVisible;
            DiagnosticLog.Info(
                $"Visual desktop icon surface activated monitors={_iconSurfaces.Count} " +
                $"nativeViewWasVisible={_desktopIconViewWasVisible}");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Refreshes every surface after a workspace change (e.g. an appearance
    // setting was adjusted). Failures are recorded instead of thrown: an
    // exception here would propagate through the settings setter into the
    // WinUI message loop and take the whole process down with it.
    internal void Refresh()
    {
        var anyFailure = false;
        foreach (var iconSurface in _iconSurfaces)
        {
            if (!_desktopIconsVisible)
            {
                continue;
            }
            if (!iconSurface.RefreshWorkspace() || !iconSurface.IsLayerReady)
            {
                DiagnosticLog.Error(
                    $"Desktop icon surface refresh failed: {iconSurface.LayerDiagnostic}",
                    new InvalidOperationException("The desktop icon surface could not be refreshed."));
                anyFailure = true;
                continue;
            }
            // Explorer may raise its ListView after an unrelated shell change.
            // Reassert the icon layer first; box surfaces are restored below so
            // they remain the topmost interactive children.
            if (!DesktopWindowTools.RestoreAboveDesktop(iconSurface.Handle, _host.DesktopListView))
            {
                DiagnosticLog.Error(
                    "The desktop icon surface could not be restored above Explorer.",
                    new InvalidOperationException("RestoreAboveDesktop failed."));
                anyFailure = true;
            }
        }
        foreach (var surface in _surfaces)
        {
            if (!surface.RefreshWorkspace() || !surface.IsLayerReady || !surface.ValidateWindowRegion())
            {
                DiagnosticLog.Error(
                    $"CrabDesk desktop surface region could not be verified: {surface.LayerDiagnostic}",
                    new InvalidOperationException("The CrabDesk desktop surface region could not be verified."));
                anyFailure = true;
            }
        }
        EnsureBoxesAboveDesktopIcons();
        if (anyFailure)
        {
            DiagnosticLog.Info("Desktop surface refresh completed with failures.");
        }
    }

    internal void SetDesktopIconsVisible(bool visible)
    {
        if (_desktopIconsVisible == visible)
        {
            if (visible)
            {
                EnsureBoxesAboveDesktopIcons();
            }
            return;
        }

        foreach (var iconSurface in _iconSurfaces)
        {
            if (!visible)
            {
                iconSurface.Hide();
                continue;
            }

            if (!iconSurface.RefreshWorkspace() || !iconSurface.IsLayerReady)
            {
                throw new InvalidOperationException(
                    $"The desktop icon surface could not be prepared: {iconSurface.LayerDiagnostic}");
            }
            iconSurface.Show();
            if (!DesktopWindowTools.ShowAboveDesktop(iconSurface.Handle, _host.DesktopListView))
            {
                iconSurface.Hide();
                throw new InvalidOperationException("The desktop icon surface could not be shown.");
            }
        }
        _desktopIconsVisible = visible;
        if (visible)
        {
            EnsureBoxesAboveDesktopIcons();
        }
    }

    internal void SetVisible(bool visible)
    {
        foreach (var iconSurface in _iconSurfaces)
        {
            if (visible && _desktopIconsVisible)
            {
                if (!iconSurface.RefreshWorkspace() || !iconSurface.IsLayerReady)
                {
                    throw new InvalidOperationException(
                        $"The desktop icon surface could not be prepared: {iconSurface.LayerDiagnostic}");
                }
                iconSurface.Show();
                if (!DesktopWindowTools.ShowAboveDesktop(iconSurface.Handle, _host.DesktopListView))
                {
                    iconSurface.Hide();
                    throw new InvalidOperationException("The desktop icon surface could not be shown.");
                }
            }
            else
            {
                iconSurface.Hide();
            }
        }
        foreach (var surface in _surfaces)
        {
            if (visible)
            {
                if (!surface.UpdateInteractionRegion() || !surface.IsLayerReady || !surface.ValidateWindowRegion())
                {
                    throw new InvalidOperationException("The CrabDesk desktop surface region could not be verified before showing.");
                }
                surface.Show();
                if (!DesktopWindowTools.ShowAboveDesktop(surface.Handle, _host.DesktopListView) ||
                    !surface.IsLayerReady || !surface.ValidateWindowRegion())
                {
                    surface.Hide();
                    throw new InvalidOperationException("The CrabDesk desktop surface region was lost while showing.");
                }
            }
            else
            {
                surface.Hide();
            }
        }
        if (visible && _desktopIconsVisible)
        {
            EnsureBoxesAboveDesktopIcons();
        }
    }

    // Icon surfaces occupy the whole monitor, so being above Explorer alone
    // is not enough. Reassert every box as a sibling above the icon surfaces
    // whenever those full-screen layers are refreshed or shown.
    private void EnsureBoxesAboveDesktopIcons()
    {
        foreach (var surface in _surfaces)
        {
            if (!DesktopWindowTools.RestoreAboveDesktop(surface.Handle, _host.DesktopListView))
            {
                throw new InvalidOperationException("The desktop box surface could not be restored above Explorer.");
            }
        }

        foreach (var surface in _surfaces)
        {
            foreach (var iconSurface in _iconSurfaces)
            {
                if (!DesktopWindowTools.IsWindowAbove(surface.Handle, iconSurface.Handle))
                {
                    throw new InvalidOperationException(
                        "The desktop box surface is below the desktop icon surface.");
                }
            }
        }
    }

    private void ConfigureBoxIconLayerComposition()
    {
        foreach (var iconSurface in _iconSurfaces)
        {
            var monitorBoxes = _surfaces
                .Where(surface => string.Equals(
                    surface.MonitorId,
                    iconSurface.MonitorId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            iconSurface.SetBoxRenderer((graphics, clipBounds) =>
            {
                foreach (var boxSurface in monitorBoxes)
                {
                    boxSurface.RenderStaticOnIconLayer(graphics, clipBounds);
                }
            });
            iconSurface.SetDragBoxRenderer((graphics, clipBounds) =>
            {
                foreach (var boxSurface in monitorBoxes)
                {
                    boxSurface.RenderDragOnIconLayer(graphics, clipBounds);
                }
            });
            iconSurface.SetBoxTransformActive(() => monitorBoxes.Any(surface => surface.HasDynamicVisual));
            iconSurface.SetBoxVisualsInParent(() => monitorBoxes.Any(surface => surface.HasDynamicVisual));
            iconSurface.SetBoxPointerHitTest(screenPoint =>
                monitorBoxes.Any(surface => surface.IsPointOverBox(screenPoint)));
            iconSurface.SetBoxDynamicBounds(() =>
            {
                RectangleF? bounds = null;
                foreach (var boxSurface in monitorBoxes)
                {
                    if (boxSurface.GetDynamicVisualBounds() is not { } candidate)
                    {
                        continue;
                    }
                    bounds = bounds is { } existing
                        ? RectangleF.Union(existing, candidate)
                        : candidate;
                }
                return bounds;
            });
            iconSurface.SetBoxDynamicVersion(() =>
            {
                var version = 17;
                foreach (var boxSurface in monitorBoxes)
                {
                    version = unchecked(version * 31 + boxSurface.DynamicVisualVersion);
                }
                return version;
            });
            iconSurface.SetBoxDynamicStateUpdater(() =>
            {
                foreach (var boxSurface in monitorBoxes)
                {
                    boxSurface.UpdateDynamicSelectionAtCursor();
                }
            });
            iconSurface.SetBoxHeightAnimationOnly(() =>
            {
                var dynamicBoxes = monitorBoxes
                    .Where(surface => surface.HasDynamicVisual)
                    .ToArray();
                return dynamicBoxes.Length > 0 &&
                    dynamicBoxes.All(surface => surface.IsHeightAnimationOnly);
            });
            foreach (var boxSurface in monitorBoxes)
            {
                boxSurface.SetIconLayerRenderRequest(iconSurface.RequestDragFrame);
                boxSurface.SetIconDragStateForward((point, paths, keys) =>
                    iconSurface.ForwardDragFromBox(point, paths, keys));
            }
        }
    }

    internal void EnsureReady()
    {
        foreach (var iconSurface in _iconSurfaces)
        {
            if (!DesktopWindowTools.IsDesktopSurfaceReady(iconSurface.Handle, _host.DesktopListView) ||
                !iconSurface.IsLayerReady)
            {
                throw new InvalidOperationException(
                    $"The desktop icon surface is not ready: {iconSurface.LayerDiagnostic}");
            }
        }
        foreach (var surface in _surfaces)
        {
            var ready = DesktopWindowTools.IsDesktopSurfaceReady(surface.Handle, _host.DesktopListView);
            var regionValid = surface.ValidateWindowRegion();
            if (!ready || !surface.IsLayerReady || !regionValid)
            {
                throw new InvalidOperationException(
                    $"The CrabDesk desktop surface is not ready. layer={surface.LayerDiagnostic} regionValid={regionValid} paints={surface.PaintCount} " +
                    DesktopWindowTools.GetDesktopSurfaceDiagnostics(surface.Handle, _host.DesktopListView));
            }
        }
    }

    internal void UpdateRegions()
    {
        foreach (var surface in _surfaces)
        {
            if (!surface.UpdateInteractionRegion() || !surface.IsLayerReady || !surface.ValidateWindowRegion())
            {
                DiagnosticLog.Error(
                    $"CrabDesk desktop surface region could not be updated: {surface.LayerDiagnostic}",
                    new InvalidOperationException("The CrabDesk desktop surface region could not be updated."));
            }
        }
    }

    internal bool IsPointOverAnyBox(int x, int y)
    {
        var point = new Point(x, y);
        foreach (var surface in _surfaces)
        {
            if (surface.IsPointOverBox(point))
            {
                return true;
            }
        }
        return false;
    }

    internal bool TryZoomBoxIconsAt(int x, int y, int delta)
    {
        var point = new Point(x, y);
        foreach (var surface in _surfaces)
        {
            if (surface.TryZoomBoxIconsAt(point, delta))
            {
                return true;
            }
        }
        return false;
    }

    internal int ClearIconCaches()
    {
        var cleared = 0;
        foreach (var iconSurface in _iconSurfaces)
        {
            cleared += iconSurface.ClearIconCache();
        }
        foreach (var surface in _surfaces)
        {
            cleared += surface.ClearIconCache();
        }
        return cleared;
    }

    internal void ClearSelection()
    {
        foreach (var iconSurface in _iconSurfaces)
        {
            iconSurface.ClearSelection();
        }
        foreach (var surface in _surfaces)
        {
            surface.ClearSelection();
        }
    }

    internal bool CanDeleteSelectedItems =>
        !_deleteInProgress &&
        !_surfaces.Any(surface => surface.IsTitleEditing) &&
        GetSelectedFileSystemItems().Count > 0;

    internal bool CanRenameSelectedItem =>
        !_deleteInProgress &&
        !_surfaces.Any(surface => surface.IsTitleEditing) &&
        GetRenameSelectionCount() == 1;

    internal bool CanHandleDesktopKeyboardCommand(DesktopKeyboardCommand command)
    {
        if (_deleteInProgress || _surfaces.Any(surface => surface.IsTitleEditing))
        {
            return false;
        }

        return command switch
        {
            DesktopKeyboardCommand.Delete => CanDeleteSelectedItems,
            DesktopKeyboardCommand.Rename => CanRenameSelectedItem,
            DesktopKeyboardCommand.SelectAll => CanSelectAllItems(),
            DesktopKeyboardCommand.Copy => GetSelectedFileSystemItems(includeReadOnly: true).Count > 0,
            DesktopKeyboardCommand.Cut => CanCutSelectedItems(),
            DesktopKeyboardCommand.Paste => GetPasteTargetSurface() is not null,
            DesktopKeyboardCommand.Open => GetSelectedItems().Count == 1,
            _ => false
        };
    }

    internal async Task ExecuteDesktopKeyboardCommandAsync(DesktopKeyboardCommand command)
    {
        if (!CanHandleDesktopKeyboardCommand(command))
        {
            return;
        }

        switch (command)
        {
            case DesktopKeyboardCommand.Delete:
                await DeleteSelectedItemsAsync();
                break;
            case DesktopKeyboardCommand.Rename:
                BeginRenameSelectedItem();
                break;
            case DesktopKeyboardCommand.SelectAll:
                SelectAllItems();
                break;
            case DesktopKeyboardCommand.Copy:
                _runtime.FileOperations.SetClipboardFiles(
                    GetSelectedFileSystemItems(includeReadOnly: true),
                    move: false);
                break;
            case DesktopKeyboardCommand.Cut:
                _runtime.FileOperations.SetClipboardFiles(
                    GetSelectedFileSystemItems(),
                    move: true);
                break;
            case DesktopKeyboardCommand.Paste:
            {
                var target = GetPasteTargetSurface();
                if (target is not null)
                {
                    await target.PasteIntoSelectedOrHoveredBoxAsync(System.Windows.Forms.Cursor.Position);
                }
                break;
            }
            case DesktopKeyboardCommand.Open:
            {
                var item = GetSelectedItems().SingleOrDefault();
                if (item is not null)
                {
                    try
                    {
                        _runtime.FileOperations.Open(item);
                    }
                    catch (Exception exception)
                    {
                        DiagnosticLog.Error($"Failed to open selected desktop item '{item.DisplayName}'.", exception);
                    }
                }
                break;
            }
        }
    }

    internal bool BeginRenameSelectedItem()
    {
        if (!CanRenameSelectedItem)
        {
            return false;
        }

        var iconSurface = _iconSurfaces.FirstOrDefault(surface => surface.RenameSelectionCount == 1);
        if (iconSurface is not null)
        {
            return iconSurface.BeginRenameSelectedItem();
        }

        var boxSurface = _surfaces.FirstOrDefault(surface => surface.RenameSelectionCount == 1);
        return boxSurface?.BeginRenameSelectedItem() == true;
    }

    internal async Task DeleteSelectedItemsAsync()
    {
        if (_deleteInProgress || _surfaces.Any(surface => surface.IsTitleEditing))
        {
            return;
        }

        var selectedItems = GetSelectedFileSystemItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        _deleteInProgress = true;
        ClearSelection();
        try
        {
            await _runtime.FileOperations.DeleteAsync(selectedItems);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to delete selected desktop items.", exception);
        }
        finally
        {
            try
            {
                await _runtime.RefreshItemsAsync(false);
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error("Failed to refresh desktop items after deletion.", exception);
            }
            finally
            {
                _deleteInProgress = false;
            }
        }
    }

    private IReadOnlyList<DesktopItemRef> GetSelectedFileSystemItems(bool includeReadOnly = false) => _iconSurfaces
        .SelectMany(surface => surface.GetSelectedFileSystemItems())
        .Concat(_surfaces.SelectMany(surface => surface.GetSelectedFileSystemItems(includeReadOnly)))
        .GroupBy(item => item.FileSystemPath!, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();

    private IReadOnlyList<DesktopItemRef> GetSelectedItems() => _iconSurfaces
        .SelectMany(surface => surface.GetSelectedItems())
        .Concat(_surfaces.SelectMany(surface => surface.GetSelectedItems()))
        .GroupBy(item => item.FileSystemPath ?? item.Key.ToString(), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();

    private bool CanCutSelectedItems()
    {
        var selected = GetSelectedFileSystemItems(includeReadOnly: true);
        return selected.Count > 0 && selected.Count == GetSelectedFileSystemItems().Count;
    }

    private bool CanSelectAllItems()
    {
        var selectedBoxes = _surfaces.Where(surface => surface.HasSelection).ToArray();
        if (selectedBoxes.Length > 0)
        {
            return selectedBoxes.Length == 1 &&
                selectedBoxes[0].CanSelectAllSelectedOrHoveredItems(System.Windows.Forms.Cursor.Position);
        }

        return _iconSurfaces.Any(surface => surface.HasSelection);
    }

    private void SelectAllItems()
    {
        var selectedBoxes = _surfaces.Where(surface => surface.HasSelection).ToArray();
        if (selectedBoxes.Length == 1 &&
            selectedBoxes[0].SelectAllSelectedOrHoveredItems(System.Windows.Forms.Cursor.Position))
        {
            foreach (var iconSurface in _iconSurfaces)
            {
                iconSurface.ClearSelection();
            }
            foreach (var surface in _surfaces.Where(surface => surface != selectedBoxes[0]))
            {
                surface.ClearSelection();
            }
            return;
        }

        if (_iconSurfaces.Any(surface => surface.HasSelection))
        {
            ClearBoxSelection();
            foreach (var iconSurface in _iconSurfaces)
            {
                iconSurface.SelectAllItems();
            }
        }
    }

    private DesktopBoxForm? GetPasteTargetSurface()
    {
        var selectedSurfaces = _surfaces.Where(surface => surface.HasSelection).ToArray();
        if (selectedSurfaces.Length == 1)
        {
            return selectedSurfaces[0].CanPasteSelectedOrHoveredBox(System.Windows.Forms.Cursor.Position)
                ? selectedSurfaces[0]
                : null;
        }
        if (selectedSurfaces.Length > 1)
        {
            return null;
        }

        var pointer = System.Windows.Forms.Cursor.Position;
        return _surfaces.FirstOrDefault(surface => surface.CanPasteSelectedOrHoveredBox(pointer));
    }

    private int GetRenameSelectionCount() => _iconSurfaces.Sum(surface => surface.RenameSelectionCount) +
        _surfaces.Sum(surface => surface.RenameSelectionCount);

    internal void ClearBoxSelection()
    {
        foreach (var surface in _surfaces)
        {
            surface.ClearSelection();
        }
    }

    internal bool IsDesktopIconPointerInteractionActive =>
        _iconSurfaces.Any(surface => surface.IsPointerInteractionActive) ||
        _surfaces.Any(surface => surface.IsMarqueeSelectionActive);

    internal bool IsDesktopIconDragActive =>
        _iconSurfaces.Any(surface => surface.IsItemDragActive);

    internal void SetVirtualBoxDropTargetEnabled(bool enabled)
    {
        foreach (var surface in _iconSurfaces)
        {
            surface.SetVirtualBoxDropTargetEnabled(enabled);
        }
    }

    internal bool TryDropDesktopItemsIntoBox(
        System.Drawing.Point screenPoint,
        IReadOnlyList<string> itemKeys)
    {
        if (itemKeys.Count == 0)
        {
            return false;
        }

        foreach (var surface in _surfaces)
        {
            if (surface.TryDropDesktopItemsIntoBox(screenPoint, itemKeys))
            {
                return true;
            }
        }

        return false;
    }

    internal bool UpdateDesktopItemDropPreview(
        System.Drawing.Point screenPoint,
        IReadOnlyList<string> itemKeys,
        out bool pointerOverBox)
    {
        pointerOverBox = false;
        var acceptsDrop = false;
        foreach (var surface in _surfaces)
        {
            acceptsDrop |= surface.UpdateDesktopItemDropPreview(
                screenPoint,
                itemKeys,
                out var pointerOverSurfaceBox);
            pointerOverBox |= pointerOverSurfaceBox;
        }
        return acceptsDrop;
    }

    internal void ClearDesktopItemDropPreviews()
    {
        foreach (var surface in _surfaces)
        {
            surface.ClearDesktopItemDropPreview();
        }
    }

    public void Dispose()
    {
        if (_desktopIconViewHidden)
        {
            DesktopWindowTools.RestoreDesktopIconView(_desktopListView, _desktopIconViewWasVisible);
            _desktopIconViewHidden = false;
        }
        foreach (var surface in _surfaces)
        {
            surface.Close();
        }
        _surfaces.Clear();
        foreach (var iconSurface in _iconSurfaces)
        {
            iconSurface.Close();
        }
        _iconSurfaces.Clear();
    }
}
