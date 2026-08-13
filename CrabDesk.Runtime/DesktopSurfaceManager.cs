using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

internal sealed class DesktopSurfaceManager : IDisposable
{
    private readonly List<DesktopBoxForm> _surfaces = [];
    private readonly List<DesktopIconSurface> _iconSurfaces = [];
    private readonly DesktopHostService _host;
    private readonly IntPtr _desktopListView;
    private bool _desktopIconViewWasVisible;
    private bool _desktopIconViewHidden;
    private bool _desktopIconsVisible = true;

    internal int SurfaceCount => _surfaces.Count;

    internal DesktopSurfaceManager(
        CrabDeskRuntime runtime,
        DesktopHostService host,
        IReadOnlyList<MonitorLayout> monitors)
    {
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

    internal void Refresh()
    {
        foreach (var iconSurface in _iconSurfaces)
        {
            if (!_desktopIconsVisible)
            {
                continue;
            }
            if (!iconSurface.RefreshWorkspace() || !iconSurface.IsLayerReady)
            {
                throw new InvalidOperationException(
                    $"The desktop icon surface could not be refreshed: {iconSurface.LayerDiagnostic}");
            }
            // Explorer may raise its ListView after an unrelated shell change.
            // Reassert the icon layer first; box surfaces are restored below so
            // they remain the topmost interactive children.
            if (!DesktopWindowTools.RestoreAboveDesktop(iconSurface.Handle, _host.DesktopListView))
            {
                throw new InvalidOperationException("The desktop icon surface could not be restored above Explorer.");
            }
        }
        foreach (var surface in _surfaces)
        {
            if (!surface.RefreshWorkspace() || !surface.IsLayerReady || !surface.ValidateWindowRegion())
            {
                throw new InvalidOperationException("The CrabDesk desktop surface region could not be verified.");
            }
        }
        EnsureBoxesAboveDesktopIcons();
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
                    boxSurface.RenderOnIconLayer(graphics, clipBounds);
                }
            });
            foreach (var boxSurface in monitorBoxes)
            {
                boxSurface.SetIconLayerRenderRequest(() => _ = iconSurface.RequestRender());
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
                throw new InvalidOperationException("The CrabDesk desktop surface region could not be updated.");
            }
        }
    }

    internal int ClearIconCaches()
    {
        var cleared = 0;
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

    internal void ClearBoxSelection()
    {
        foreach (var surface in _surfaces)
        {
            surface.ClearSelection();
        }
    }

    internal bool IsDesktopIconPointerInteractionActive =>
        _iconSurfaces.Any(surface => surface.IsPointerInteractionActive);

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
