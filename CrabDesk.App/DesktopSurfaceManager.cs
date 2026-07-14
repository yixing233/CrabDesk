using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.App;

internal sealed class DesktopSurfaceManager : IDisposable
{
    private readonly List<DesktopBoxForm> _surfaces = [];

    internal int SurfaceCount => _surfaces.Count;

    internal DesktopSurfaceManager(
        CrabDeskRuntime runtime,
        DesktopHostService host,
        IReadOnlyList<MonitorLayout> monitors)
    {
        var parentHandle = host.DesktopParent;
        var parentBounds = DesktopWindowTools.GetWindowBounds(parentHandle);
        foreach (var monitor in monitors)
        {
            var surface = new DesktopBoxForm(runtime, host, monitor);
            DesktopWindowTools.AttachAsDesktopChild(surface.Handle, parentHandle);
            surface.Show();
            DesktopWindowTools.PositionAboveDesktop(
                surface.Handle,
                host.DesktopView,
                (int)(monitor.PixelBounds.X - parentBounds.X),
                (int)(monitor.PixelBounds.Y - parentBounds.Y),
                (int)monitor.PixelBounds.Width,
                (int)monitor.PixelBounds.Height);
            _surfaces.Add(surface);
        }
        Refresh();
    }

    internal void Refresh()
    {
        foreach (var surface in _surfaces)
        {
            surface.RefreshWorkspace();
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

    public void Dispose()
    {
        foreach (var surface in _surfaces)
        {
            surface.Close();
            surface.Dispose();
        }
        _surfaces.Clear();
    }
}
