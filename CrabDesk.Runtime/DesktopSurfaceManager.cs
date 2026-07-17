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
                    surface.Show();
                    DesktopWindowTools.AttachAsDesktopChild(surface.Handle, parentHandle);
                    DesktopWindowTools.PositionAboveDesktop(
                        surface.Handle,
                        host.DesktopListView,
                        (int)(monitor.PixelBounds.X - parentBounds.X),
                        (int)(monitor.PixelBounds.Y - parentBounds.Y),
                        (int)monitor.PixelBounds.Width,
                        (int)monitor.PixelBounds.Height);
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
            surface.RefreshWorkspace();
        }
    }

    internal void EnsureReady()
    {
        foreach (var surface in _surfaces)
        {
            DesktopWindowTools.NormalizeDesktopSurfaceStyles(surface.Handle);
            var rendered = surface.EnsureRendered();
            var ready = DesktopWindowTools.IsDesktopSurfaceReady(surface.Handle, _host.DesktopListView);
            if (!ready || !rendered)
            {
                throw new InvalidOperationException(
                    $"The CrabDesk desktop surface is not ready. rendered={rendered} paints={surface.PaintCount} " +
                    DesktopWindowTools.GetDesktopSurfaceDiagnostics(surface.Handle, _host.DesktopListView));
            }
        }
    }

    internal void UpdateRegions()
    {
        foreach (var surface in _surfaces)
        {
            surface.UpdateInteractionRegion();
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
