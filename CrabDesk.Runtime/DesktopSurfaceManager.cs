using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

internal sealed class DesktopSurfaceManager : IDisposable
{
    private readonly List<DesktopBoxForm> _surfaces = [];
    private readonly DesktopHostService _host;

    internal int SurfaceCount => _surfaces.Count;

    internal DesktopSurfaceManager(
        CrabDeskRuntime runtime,
        DesktopHostService host,
        IReadOnlyList<MonitorLayout> monitors)
    {
        _host = host;
        try
        {
            var parentHandle = host.DesktopView;
            var parentBounds = DesktopWindowTools.GetWindowBounds(parentHandle);
            foreach (var monitor in monitors)
            {
                var surface = new DesktopBoxForm(runtime, host, monitor);
                try
                {
                    DesktopWindowTools.AttachAsDesktopChild(surface.Handle, parentHandle);
                    DesktopWindowTools.PositionAboveDesktop(
                        surface.Handle,
                        host.DesktopListView,
                        (int)(monitor.PixelBounds.X - parentBounds.X),
                        (int)(monitor.PixelBounds.Y - parentBounds.Y),
                        (int)monitor.PixelBounds.Width,
                        (int)monitor.PixelBounds.Height);
                    if (!surface.RefreshWorkspace() || !surface.ValidateWindowRegion())
                    {
                        throw new InvalidOperationException("The CrabDesk desktop surface region could not be verified.");
                    }
                    surface.Show();
                    if (!surface.UpdateInteractionRegion() || !surface.ValidateWindowRegion())
                    {
                        throw new InvalidOperationException("The CrabDesk desktop surface region was lost while showing.");
                    }
                    _surfaces.Add(surface);
                }
                catch
                {
                    surface.Dispose();
                    throw;
                }
            }
            Refresh();
            EnsureReady();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Refresh()
    {
        foreach (var surface in _surfaces)
        {
            if (!surface.RefreshWorkspace() || !surface.ValidateWindowRegion())
            {
                throw new InvalidOperationException("The CrabDesk desktop surface region could not be verified.");
            }
            DesktopWindowTools.RestoreAboveDesktop(surface.Handle, _host.DesktopListView);
        }
    }

    internal void SetVisible(bool visible)
    {
        foreach (var surface in _surfaces)
        {
            if (visible)
            {
                if (!surface.UpdateInteractionRegion() || !surface.ValidateWindowRegion())
                {
                    throw new InvalidOperationException("The CrabDesk desktop surface region could not be verified before showing.");
                }
                surface.Show();
                if (!surface.ValidateWindowRegion())
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
    }

    internal void EnsureReady()
    {
        foreach (var surface in _surfaces)
        {
            DesktopWindowTools.NormalizeDesktopSurfaceStyles(surface.Handle);
            var rendered = surface.EnsureRendered();
            var ready = DesktopWindowTools.IsDesktopSurfaceReady(surface.Handle, _host.DesktopListView);
            var regionValid = surface.ValidateWindowRegion();
            if (!ready || !rendered || !regionValid)
            {
                throw new InvalidOperationException(
                    $"The CrabDesk desktop surface is not ready. rendered={rendered} regionValid={regionValid} paints={surface.PaintCount} " +
                    DesktopWindowTools.GetDesktopSurfaceDiagnostics(surface.Handle, _host.DesktopListView));
            }
        }
    }

    internal void UpdateRegions()
    {
        foreach (var surface in _surfaces)
        {
            if (!surface.UpdateInteractionRegion() || !surface.ValidateWindowRegion())
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
        foreach (var surface in _surfaces)
        {
            surface.ClearSelection();
        }
    }

    public void Dispose()
    {
        foreach (var surface in _surfaces)
        {
            surface.Close();
        }
        _surfaces.Clear();
    }
}
