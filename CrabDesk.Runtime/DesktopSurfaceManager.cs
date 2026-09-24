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
    private readonly IntPtr _iconSurfaceAnchor;
    private IntPtr _boxSurfaceAnchor;
    private DesktopAcrylicHost? _acrylicHost;
    private bool _visible = true;
    private bool _desktopIconViewWasVisible;
    private bool _desktopIconViewHidden;
    private bool _desktopIconsVisible = true;
    private bool _deleteInProgress;
    private bool _boxHoverReconcilePending;

    internal int SurfaceCount => _surfaces.Count;

    /// <summary>
    /// The icon surface handle for a monitor, used as the "is CrabDesk still
    /// answering?" target by the stall diagnostics. Zero when no surface exists
    /// for that monitor or its handle was never created.
    /// </summary>
    internal IntPtr GetIconSurfaceHandle(string monitorId)
    {
        var surface = _iconSurfaces.FirstOrDefault(candidate =>
            string.Equals(candidate.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase));
        if (surface is null || surface.IsDisposed || !surface.IsHandleCreated)
        {
            return IntPtr.Zero;
        }

        return surface.Handle;
    }

    internal bool AcrylicRequested { get; }

    internal readonly record struct SurfaceVisibility(bool ShowIcons, bool ShowBoxes);

    internal static SurfaceVisibility ResolveVisibility(bool appVisible, bool desktopIconsVisible) =>
        new(appVisible && desktopIconsVisible, appVisible);

    internal DesktopSurfaceManager(
        CrabDeskRuntime runtime,
        DesktopHostService host,
        IReadOnlyList<MonitorLayout> monitors)
    {
        _runtime = runtime;
        AcrylicRequested = runtime.State.Settings.Appearance.UseAcrylicBoxes;
        _host = host;
        _desktopListView = host.DesktopListView;
        _iconSurfaceAnchor = host.DesktopListView;
        _boxSurfaceAnchor = host.DesktopListView;
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

            var iconParentHandle = host.DesktopView;
            var boxParentHandle = host.DesktopView;
            if (runtime.State.Settings.Appearance.UseAcrylicBoxes && DesktopAcrylicHost.IsSupported)
            {
                try
                {
                    _acrylicHost = new DesktopAcrylicHost(host.DesktopView, monitors);
                    _acrylicHost.InitializeComposition();
                    boxParentHandle = _acrylicHost.Handle;
                    _boxSurfaceAnchor = _acrylicHost.AnchorHandle;
                    _acrylicHost.EffectsChanged = () =>
                    {
                        foreach (var boxSurface in _surfaces)
                            boxSurface.SetAcrylicBackground(_acrylicHost.EffectsEnabled);
                        Refresh();
                    };
                    // An OLE drag from another process hit-tests this top-level
                    // host, not the icon layer beneath it (HTTRANSPARENT only
                    // defers within our thread). Accept drops on the host and
                    // route them to the icon surface for the monitor under the
                    // pointer; the list is filled in below and read at drag time.
                    _acrylicHost.SetDropForwarding(point =>
                        _iconSurfaces.FirstOrDefault(surface => surface.ContainsScreenPixel(point)));
                }
                catch (Exception exception)
                {
                    _acrylicHost?.Dispose();
                    _acrylicHost = null;
                    boxParentHandle = host.DesktopView;
                    _boxSurfaceAnchor = host.DesktopListView;
                    DiagnosticLog.Error("Acrylic initialization failed; retaining desktop surfaces", exception);
                }
            }
            var iconParentBounds = DesktopWindowTools.GetWindowBounds(iconParentHandle);
            var boxParentBounds = DesktopWindowTools.GetWindowBounds(boxParentHandle);
            foreach (var monitor in monitors)
            {
                var iconSurface = new DesktopIconSurface(runtime, monitor, host.DesktopListView);
                try
                {
                    DesktopWindowTools.AttachAsDesktopChild(iconSurface.Handle, iconParentHandle);
                    DesktopWindowTools.PositionAboveDesktop(
                        iconSurface.Handle,
                        _iconSurfaceAnchor,
                        (int)(monitor.PixelBounds.X - iconParentBounds.X),
                        (int)(monitor.PixelBounds.Y - iconParentBounds.Y),
                        (int)monitor.PixelBounds.Width,
                        (int)monitor.PixelBounds.Height);
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
                    if (_acrylicHost is null)
                    {
                        surface.PrepareIconLayerComposition();
                    }
                    surface.SetAcrylicBackground(_acrylicHost?.EffectsEnabled == true);
                    if (_acrylicHost is not null)
                    {
                        surface.SetAcrylicFramePresenter((bitmap, origin, regions) =>
                            _acrylicHost.PresentForeground(monitor.Id, bitmap, origin, regions));
                    }
                    surface.AttachToDesktop(boxParentHandle);
                    DesktopWindowTools.PositionAboveDesktop(
                        surface.Handle,
                        _boxSurfaceAnchor,
                        (int)(monitor.PixelBounds.X - boxParentBounds.X),
                        (int)(monitor.PixelBounds.Y - boxParentBounds.Y),
                        (int)monitor.PixelBounds.Width,
                        (int)monitor.PixelBounds.Height);
                    if (!surface.RefreshWorkspace() || !surface.IsLayerReady || !surface.ValidateWindowRegion())
                    {
                        throw new InvalidOperationException("The CrabDesk desktop surface region could not be verified.");
                    }
                    _surfaces.Add(surface);
                }
                catch
                {
                    surface.Dispose();
                    throw;
                }
            }
            if (_acrylicHost is null)
            {
                ConfigureBoxIconLayerComposition();
            }
            // Keep Explorer's native desktop fully visible and interactive
            // while the replacement windows are prepared. Render the final
            // icon/box composite once, then show every prepared child in one
            // short hand-off immediately before hiding Explorer's ListView.
            foreach (var iconSurface in _iconSurfaces)
            {
                if (!iconSurface.RefreshWorkspace() || !iconSurface.IsLayerReady)
                {
                    throw new InvalidOperationException(
                        $"The CrabDesk desktop icon surface could not be rendered: {iconSurface.LayerDiagnostic}");
                }
            }
            ShowPreparedSurfaces();
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

    private void ShowPreparedSurfaces()
    {
        _acrylicHost?.ShowAtDesktop();
        foreach (var iconSurface in _iconSurfaces)
        {
            iconSurface.Show();
            if (!DesktopWindowTools.ShowAboveDesktop(iconSurface.Handle, _iconSurfaceAnchor) ||
                !iconSurface.IsLayerReady)
            {
                throw new InvalidOperationException(
                    $"The CrabDesk desktop icon surface could not be shown: {iconSurface.LayerDiagnostic}");
            }
        }

        foreach (var surface in _surfaces)
        {
            surface.Show();
            var shown = DesktopWindowTools.ShowAboveDesktop(surface.Handle, _boxSurfaceAnchor);
            var regionUpdated = surface.UpdateInteractionRegion();
            var regionValid = surface.ValidateWindowRegion();
            if (!shown || !regionUpdated || !surface.IsLayerReady || !regionValid)
            {
                throw new InvalidOperationException(
                    "The CrabDesk desktop surface region was lost while showing. " +
                    $"shown={shown} regionUpdated={regionUpdated} regionValid={regionValid} " +
                    DesktopWindowTools.GetDesktopSurfaceDiagnostics(surface.Handle, _host.DesktopListView));
            }
        }

        EnsureBoxesAboveDesktopIcons();
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
            if (!DesktopWindowTools.RestoreAboveDesktop(iconSurface.Handle, _iconSurfaceAnchor))
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

    internal bool RefreshDesktopItemAssignment(Guid boxId) => RefreshBoxItems(boxId);

    internal bool RefreshBoxItems(Guid boxId)
    {
        return RefreshBoxItems([boxId]);
    }

    internal bool RefreshBox(Guid boxId) => RefreshBoxes([boxId]);

    internal bool RefreshBoxMonitorChanged(
        Guid boxId,
        string previousMonitorId,
        string currentMonitorId)
    {
        var refreshed = false;
        foreach (var surface in _surfaces.Where(surface =>
                     string.Equals(surface.MonitorId, previousMonitorId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(surface.MonitorId, currentMonitorId, StringComparison.OrdinalIgnoreCase)))
        {
            refreshed |= surface.RefreshWorkspace();
            if (!surface.UpdateInteractionRegion())
            {
                DiagnosticLog.Error(
                    "Targeted desktop box monitor refresh could not update the interaction region.",
                    new InvalidOperationException("The desktop box interaction region could not be updated."));
            }
        }

        foreach (var iconSurface in _iconSurfaces.Where(surface =>
                     string.Equals(surface.MonitorId, previousMonitorId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(surface.MonitorId, currentMonitorId, StringComparison.OrdinalIgnoreCase)))
        {
            refreshed |= iconSurface.RequestRender();
        }

        return refreshed;
    }

    internal bool RefreshBoxes(IReadOnlyCollection<Guid> boxIds)
    {
        if (boxIds.Count == 0)
        {
            return false;
        }

        var requestedIds = boxIds.ToHashSet();
        var refreshed = false;
        foreach (var surface in _surfaces)
        {
            var surfaceRefreshed = false;
            foreach (var boxId in requestedIds)
            {
                surfaceRefreshed |= surface.RefreshBoxItems(boxId);
            }

            if (!surfaceRefreshed)
            {
                continue;
            }

            refreshed = true;
            if (!surface.UpdateInteractionRegion())
            {
                DiagnosticLog.Error(
                    "Targeted desktop box refresh could not update the interaction region.",
                    new InvalidOperationException("The desktop box interaction region could not be updated."));
            }
        }

        return refreshed;
    }

    internal bool RefreshBoxItems(IReadOnlyCollection<Guid> boxIds)
    {
        if (boxIds.Count == 0)
        {
            return false;
        }

        var requestedIds = boxIds.ToHashSet();
        var refreshed = false;
        foreach (var surface in _surfaces)
        {
            foreach (var boxId in requestedIds)
            {
                refreshed |= surface.RefreshBoxItems(boxId);
            }
        }

        return refreshed;
    }

    internal bool RefreshBoxAdded(Guid boxId)
    {
        foreach (var surface in _surfaces)
        {
            if (!surface.RefreshBoxItems(boxId))
            {
                continue;
            }

            return surface.UpdateInteractionRegion();
        }

        return false;
    }

    internal bool RefreshDesktopItemRelease(
        IReadOnlyCollection<Guid> sourceBoxIds,
        IReadOnlyCollection<string> releasedItemKeys)
    {
        var refreshedAllSourceBoxes = true;
        foreach (var boxId in sourceBoxIds)
        {
            if (_surfaces.Any(surface => surface.RefreshBoxItems(boxId)))
            {
                continue;
            }

            refreshedAllSourceBoxes = false;
        }

        var refreshedDesktop = false;
        foreach (var surface in _iconSurfaces)
        {
            refreshedDesktop |= surface.RefreshReleasedItems(releasedItemKeys);
        }
        return refreshedAllSourceBoxes && refreshedDesktop;
    }

    internal bool RefreshDesktopItemsRemoved(IReadOnlyCollection<string> removedItemKeys)
    {
        var refreshed = false;
        foreach (var surface in _iconSurfaces)
        {
            refreshed |= surface.RefreshRemovedItems(removedItemKeys);
        }
        return refreshed;
    }

    internal bool RefreshDesktopItemsAdded(IReadOnlyCollection<string> addedItemKeys)
    {
        var refreshed = false;
        foreach (var surface in _iconSurfaces)
        {
            refreshed |= surface.RefreshReleasedItems(addedItemKeys);
        }
        return refreshed;
    }

    // Assigning desktop items to a box is the inverse of releasing them: the
    // box gains the items and the desktop cells they occupied are repainted.
    // Both sides are partial updates, so no other monitor or box is touched.
    internal bool RefreshDesktopItemsAssigned(
        Guid boxId,
        IReadOnlyCollection<string> assignedItemKeys)
    {
        if (assignedItemKeys.Count == 0)
        {
            return false;
        }

        var refreshedBox = false;
        foreach (var surface in _surfaces)
        {
            refreshedBox |= surface.RefreshBoxItems(boxId);
        }

        var refreshedDesktop = false;
        foreach (var iconSurface in _iconSurfaces)
        {
            refreshedDesktop |= iconSurface.RefreshReleasedItems(assignedItemKeys);
        }

        return refreshedBox && refreshedDesktop;
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

        _desktopIconsVisible = visible;
        SetVisible(_visible);
    }

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        var visibility = ResolveVisibility(visible, _desktopIconsVisible);
        if (_acrylicHost is not null)
        {
            if (visibility.ShowBoxes) _acrylicHost.ShowAtDesktop();
            else _acrylicHost.HideFromDesktop();
        }
        foreach (var iconSurface in _iconSurfaces)
        {
            if (visibility.ShowIcons)
            {
                if (!iconSurface.RefreshWorkspace() || !iconSurface.IsLayerReady)
                {
                    throw new InvalidOperationException(
                        $"The desktop icon surface could not be prepared: {iconSurface.LayerDiagnostic}");
                }
                iconSurface.Show();
                if (!DesktopWindowTools.ShowAboveDesktop(iconSurface.Handle, _iconSurfaceAnchor))
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
            if (visibility.ShowBoxes)
            {
                if (!surface.UpdateInteractionRegion() || !surface.IsLayerReady || !surface.ValidateWindowRegion())
                {
                    throw new InvalidOperationException("The CrabDesk desktop surface region could not be verified before showing.");
                }
                surface.Show();
                if (!DesktopWindowTools.ShowAboveDesktop(surface.Handle, _boxSurfaceAnchor) ||
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
        if (visibility.ShowBoxes)
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
            if (!DesktopWindowTools.RestoreAboveDesktop(surface.Handle, _boxSurfaceAnchor))
            {
                throw new InvalidOperationException("The desktop box surface could not be restored above Explorer.");
            }
        }

        if (_acrylicHost is not null)
        {
            return;
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
            var allBoxSurfaces = _surfaces.ToArray();
            iconSurface.SetBoxRenderer((graphics, clipBounds) =>
            {
                foreach (var boxSurface in monitorBoxes)
                {
                    boxSurface.RenderStaticOnIconLayer(graphics, clipBounds);
                }
            });
            iconSurface.SetDragBoxRenderer((graphics, clipBounds) =>
            {
                foreach (var boxSurface in allBoxSurfaces)
                {
                    boxSurface.RenderDragOnIconLayerForMonitor(
                        graphics,
                        clipBounds,
                        iconSurface.MonitorId);
                }
            });
            iconSurface.SetBoxTransformActive(() => allBoxSurfaces.Any(surface =>
                surface.HasDynamicVisualForMonitor(iconSurface.MonitorId)));
            // A box dragged across monitors joins the target monitor's dynamic
            // frame while its form still belongs to the source monitor. Scope
            // the ownership test to the rendered monitor so the target surface
            // keeps the box on its own parent layer instead of handing it to a
            // cold-started drag overlay on the first crossing.
            iconSurface.SetBoxVisualsInParent(() => allBoxSurfaces.Any(surface =>
                    DesktopBoxForm.ShouldCompositeBoxVisualsInParent(
                        surface.HasDynamicVisualForMonitor(iconSurface.MonitorId),
                        surface.IsPartialAnimationOnly)));
            iconSurface.SetBoxPointerHitTest(screenPoint =>
                monitorBoxes.Any(surface => surface.IsPointOverBox(screenPoint)));
            iconSurface.SetBoxDynamicBounds(() =>
            {
                RectangleF? bounds = null;
                foreach (var boxSurface in allBoxSurfaces)
                {
                    if (boxSurface.GetDynamicVisualBoundsForMonitor(iconSurface.MonitorId) is not { } candidate)
                    {
                        continue;
                    }
                    bounds = bounds is { } existing
                        ? RectangleF.Union(existing, candidate)
                        : candidate;
                }
                return bounds;
            });
            iconSurface.SetBoxDynamicDirtyBounds(() =>
            {
                RectangleF? bounds = null;
                foreach (var boxSurface in allBoxSurfaces)
                {
                    if (boxSurface.GetDynamicVisualDirtyBoundsForMonitor(iconSurface.MonitorId) is not { } candidate)
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
                foreach (var boxSurface in allBoxSurfaces)
                {
                    if (boxSurface.HasDynamicVisualForMonitor(iconSurface.MonitorId))
                    {
                        version = unchecked(version * 31 + boxSurface.DynamicVisualVersion);
                    }
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
            iconSurface.SetBoxPartialAnimationOnly(() =>
            {
                var dynamicBoxes = allBoxSurfaces
                    .Where(surface => surface.HasDynamicVisualForMonitor(iconSurface.MonitorId))
                    .ToArray();
                return dynamicBoxes.Length > 0 &&
                    dynamicBoxes.All(surface => surface.UsesPartialBoxAnimationComposition);
            });
            iconSurface.SetBoxDynamicAnimationActive(() =>
                allBoxSurfaces.Any(surface =>
                    surface.HasDynamicVisualForMonitor(iconSurface.MonitorId) &&
                    surface.HasDynamicAnimation));
            foreach (var boxSurface in monitorBoxes)
            {
                boxSurface.SetIconLayerRenderRequest(() =>
                    RequestIconDragFrames(boxSurface));
                boxSurface.SetIconLayerPartialRenderRequest(iconSurface.RequestBoxVisualFrame);
                boxSurface.SetIconDragStateForward((point, paths, keys, grabOffset) =>
                    iconSurface.ForwardDragFromBox(point, paths, keys, grabOffset));
            }
        }
    }

    internal void EnsureReady()
    {
        _acrylicHost?.EnsureDesktopPlacement();
        foreach (var iconSurface in _iconSurfaces)
        {
            if (!DesktopWindowTools.IsDesktopSurfaceReady(iconSurface.Handle, _iconSurfaceAnchor) ||
                !iconSurface.IsLayerReady)
            {
                throw new InvalidOperationException(
                    $"The desktop icon surface is not ready: {iconSurface.LayerDiagnostic}");
            }
        }
        foreach (var surface in _surfaces)
        {
            var ready = DesktopWindowTools.IsDesktopSurfaceReady(surface.Handle, _boxSurfaceAnchor);
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

    internal static IReadOnlySet<string> ResolveIconDragFrameMonitorIds(
        string sourceMonitorId,
        string? activeMonitorId,
        string? previousMonitorId)
    {
        var monitorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (activeMonitorId is not null)
        {
            monitorIds.Add(activeMonitorId);
        }
        if (previousMonitorId is not null)
        {
            monitorIds.Add(previousMonitorId);
        }
        else if (activeMonitorId is null)
        {
            monitorIds.Add(sourceMonitorId);
        }
        return monitorIds;
    }

    private void RequestIconDragFrames(DesktopBoxForm sourceSurface)
    {
        var previousMonitorId = sourceSurface.ConsumeMonitorTransferPreviousMonitorId();
        var transferRefreshPending = previousMonitorId is not null;
        var monitorIds = ResolveIconDragFrameMonitorIds(
            sourceSurface.MonitorId,
            sourceSurface.TransformMonitorId,
            previousMonitorId);

        foreach (var surface in _iconSurfaces)
        {
            if (monitorIds.Contains(surface.MonitorId))
            {
                var isPreviousMonitor = string.Equals(
                    surface.MonitorId,
                    previousMonitorId,
                    StringComparison.OrdinalIgnoreCase);
                surface.RequestDragFrame(
                    forceFullFrame: transferRefreshPending && isPreviousMonitor);
            }
        }
    }

    internal bool CommitActiveInlineRename()
    {
        foreach (var iconSurface in _iconSurfaces)
        {
            if (iconSurface.CommitActiveInlineRename())
            {
                DiagnosticLog.Verbose("Committed active desktop inline rename from a surface click.");
                return true;
            }
        }

        foreach (var surface in _surfaces)
        {
            if (surface.CommitActiveInlineRename())
            {
                _boxHoverReconcilePending = true;
                DiagnosticLog.Verbose("Committed active box inline rename from a surface click.");
                return true;
            }
        }

        return false;
    }

    internal bool TryScrollBoxAt(int x, int y, int delta)
    {
        var point = new Point(x, y);
        foreach (var surface in _surfaces)
        {
            if (surface.TryScrollBoxAt(point, delta))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool ShouldFlushPendingBoxHoverReconcile(
        bool reconcilePending,
        bool pointerInteractionActive) =>
        reconcilePending && !pointerInteractionActive;

    internal void CompleteDesktopPointerInteraction()
    {
        if (!ShouldFlushPendingBoxHoverReconcile(
                _boxHoverReconcilePending,
                IsDesktopIconPointerInteractionActive))
        {
            return;
        }

        _boxHoverReconcilePending = false;
        foreach (var surface in _surfaces)
        {
            surface.ReconcileHoverAfterDesktopPointerInteraction();
        }
        DiagnosticLog.Verbose(
            "Requeued box hover reconciliation after desktop pointer interaction completed.");
    }

    internal void PrepareSelection(
        DesktopIconSurface source,
        bool preserveExisting)
    {
        DiagnosticLog.Verbose(
            $"Global selection gesture source=desktop preserveExisting={preserveExisting}");
        if (preserveExisting)
        {
            return;
        }

        foreach (var iconSurface in _iconSurfaces.Where(surface => surface != source))
        {
            iconSurface.ClearSelection();
        }
        foreach (var surface in _surfaces)
        {
            surface.ClearSelection();
        }
    }

    internal void PrepareSelection(
        DesktopBoxForm source,
        bool preserveExisting)
    {
        DiagnosticLog.Verbose(
            $"Global selection gesture source=box preserveExisting={preserveExisting}");
        if (preserveExisting)
        {
            return;
        }

        foreach (var iconSurface in _iconSurfaces)
        {
            iconSurface.ClearSelection();
        }
        foreach (var surface in _surfaces.Where(surface => surface != source))
        {
            surface.ClearSelection();
        }
    }

    internal bool CanDeleteSelectedItems =>
        !_deleteInProgress &&
        !_surfaces.Any(surface => surface.IsTitleEditing) &&
        GetDeleteSelection().SelectedCount > 0;

    internal bool CanRenameSelectedItem =>
        !_deleteInProgress &&
        !_surfaces.Any(surface => surface.IsTitleEditing) &&
        GetRenameSelectionCount() == 1;

    /// <summary>
    /// Decides whether the low-level keyboard hook consumes a desktop command
    /// key. This runs on the hook's own thread, which holds the key event (and
    /// therefore all system input) until it returns, so it reads atomic state
    /// only: enumerating a selection, resolving box geometry, or opening the
    /// clipboard here would stall every application's input.
    /// <para>
    /// The answer is deliberately permissive. <see
    /// cref="ExecuteDesktopKeyboardCommandAsync"/> repeats the exact check on
    /// the UI thread before it acts, so an intercepted key that turns out to
    /// have no target is simply dropped rather than misapplied.
    /// </para>
    /// </summary>
    internal bool CanInterceptDesktopKeyboardCommand(DesktopKeyboardCommand command)
    {
        if (_deleteInProgress)
        {
            return false;
        }

        return command switch
        {
            // Only the offered clipboard formats are inspected; the paste
            // itself resolves the target box and reads the file list.
            DesktopKeyboardCommand.Paste => _runtime.CanPasteToDesktop(),
            // F5 acts on the whole icon layer, so an empty selection — the
            // normal state when the user reaches for it — must not veto it.
            DesktopKeyboardCommand.Refresh => true,
            _ => HasAnyDesktopSelection()
        };
    }

    // Count reads on the surfaces' own selection sets. A set that the UI
    // thread happens to be changing yields the count from either side of that
    // change, which is all this decision needs.
    private bool HasAnyDesktopSelection() =>
        _iconSurfaces.Any(surface => surface.HasSelection) ||
        _surfaces.Any(surface => surface.HasSelection);

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
            DesktopKeyboardCommand.Paste => GetPasteTargetSurface() is not null ||
                _runtime.CanPasteToDesktop(),
            DesktopKeyboardCommand.Open => GetSelectedItems().Count == 1,
            DesktopKeyboardCommand.Refresh => true,
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
                else
                {
                    await PasteToDesktopAsync();
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
            case DesktopKeyboardCommand.Refresh:
                // Shares the context-menu Refresh pipeline, so holding F5 down
                // coalesces into one pass instead of stacking rebuilds.
                _runtime.RequestDesktopRefresh();
                break;
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

        var selection = GetDeleteSelection();
        if (selection.SelectedCount == 0)
        {
            return;
        }

        _deleteInProgress = true;
        var deleteAttempted = false;
        var deletedPaths = Array.Empty<string>();
        try
        {
            if (selection.DeletableItems.Count == 0)
            {
                ShowDeleteMessage(
                    "无法删除所选项目",
                    "所选项目属于只读映射目录或系统桌面项目。",
                    DesktopDialogKind.Warning);
                return;
            }

            deleteAttempted = true;
            var result = await _runtime.FileOperations.DeleteAsync(selection.DeletableItems);
            // Only the items that actually reached the Recycle Bin vacate their
            // cells. A failed item is still on the desktop, so reconciling it as
            // removed would blank a cell that still holds a real file.
            deletedPaths = result.SucceededPaths.ToArray();
            ReportDeleteOutcome(result, selection.BlockedCount);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to delete selected desktop items.", exception);
            ShowDeleteMessage("删除失败", exception.Message, DesktopDialogKind.Error);
        }
        finally
        {
            if (deleteAttempted && deletedPaths.Length > 0)
            {
                try
                {
                    await _runtime.RefreshAfterDesktopItemsDeletedAsync(deletedPaths);
                }
                catch (Exception exception)
                {
                    DiagnosticLog.Error("Failed to refresh desktop items after deletion.", exception);
                }
            }
            _deleteInProgress = false;
            _runtime.ActivateDesktopKeyboardInput();
        }
    }

    // A selection can be partly removable: system icons and read-only mapped
    // folders are skipped, and an individual file can be held open by another
    // process. Reporting only when nothing at all was deletable left the user
    // believing a mixed selection had been fully removed.
    private void ReportDeleteOutcome(FileDeleteBatchResult result, int blockedCount)
    {
        if (result.HasFailures)
        {
            ShowDeleteMessage(
                "部分项目未能删除",
                DescribeDeleteOutcome(result, blockedCount),
                DesktopDialogKind.Error);
            return;
        }

        if (blockedCount > 0)
        {
            ShowDeleteMessage(
                "部分项目未删除",
                $"已删除 {result.SucceededCount} 项，{blockedCount} 项已跳过。" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "被跳过的项目属于系统桌面项目或只读映射目录，无法删除。",
                DesktopDialogKind.Warning);
        }
    }

    internal static string DescribeDeleteOutcome(FileDeleteBatchResult result, int blockedCount)
    {
        var details = string.Join(
            Environment.NewLine,
            result.FailedItems.Take(3).Select(item =>
                $"- {Path.GetFileName(item.Path)}: {item.ErrorMessage}"));
        if (result.FailedCount > 3)
        {
            details += Environment.NewLine + $"另有 {result.FailedCount - 3} 项未删除。";
        }

        var blocked = blockedCount > 0
            ? $"{Environment.NewLine}{blockedCount} 项系统桌面项目或只读映射目录已跳过。"
            : string.Empty;
        return $"已删除 {result.SucceededCount} 项，{result.FailedCount} 项失败。" +
            blocked +
            $"{Environment.NewLine}{Environment.NewLine}{details}";
    }

    // Ctrl+V on the replacement desktop is handled here instead of falling
    // through to Explorer: the shell's file-based paste refuses to copy an item
    // into the folder it already lives in, while the import service resolves the
    // collision with an automatic "_2" suffix.
    private async Task PasteToDesktopAsync()
    {
        try
        {
            var imported = await _runtime.PasteToDesktopAsync();
            if (imported.HasFailures)
            {
                ShowImportFailureMessage(imported);
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to paste clipboard files onto the desktop.", exception);
            ShowDeleteMessage("粘贴失败", exception.Message, DesktopDialogKind.Error);
        }
    }

    private void ShowImportFailureMessage(FileImportBatchResult result)
    {
        var details = string.Join(
            Environment.NewLine,
            result.FailedItems.Take(3).Select(item =>
                $"- {Path.GetFileName(item.SourcePath)}: {item.ErrorMessage}"));
        if (result.FailedCount > 3)
        {
            details += Environment.NewLine + $"另有 {result.FailedCount - 3} 项未粘贴。";
        }

        ShowDeleteMessage(
            "粘贴未完成",
            $"已粘贴 {result.SucceededCount} 项，{result.FailedCount} 项失败。" +
            $"{Environment.NewLine}{Environment.NewLine}{details}",
            DesktopDialogKind.Warning);
    }

    private void ShowDeleteMessage(string title, string message, DesktopDialogKind kind)
    {
        System.Windows.Forms.Form? owner = (System.Windows.Forms.Form?)_surfaces.FirstOrDefault() ??
            _iconSurfaces.FirstOrDefault();
        if (owner is not null)
        {
            DesktopConfirmationDialog.ShowMessage(
                owner,
                _runtime.IsDarkTheme,
                title,
                message,
                kind);
        }
    }

    private DesktopDeleteSelection GetDeleteSelection() =>
        DesktopSelectionPolicy.BuildDeleteSelection(
            GetSelectedItems(),
            GetSelectedFileSystemItems());

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

    internal bool IsBoxItemDragActive =>
        _surfaces.Any(surface => surface.IsItemDragActive);

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

    // Backdrop geometry is published only with its matching foreground frame.
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
        _acrylicHost?.Dispose();
        _acrylicHost = null;
    }
}
