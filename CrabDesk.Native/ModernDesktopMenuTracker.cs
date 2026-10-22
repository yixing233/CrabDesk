using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace CrabDesk.Native;

internal sealed record ModernDesktopRefreshItem(IntPtr Window, RectangleF Bounds, bool UsesNativeHitTest = true)
{
    internal bool Contains(IntPtr window, int x, int y) =>
        (!UsesNativeHitTest || Window == window) && Bounds.Contains(x, y);
}

/// <summary>
/// Reads XAML menu accessibility on an MTA worker. The input hook only reads
/// immutable rectangles, never a UI Automation provider in explorer.exe.
/// </summary>
internal sealed class ModernDesktopMenuTracker : IDisposable
{
    private sealed record Snapshot(long ExpiresAt, ModernDesktopRefreshItem[] Items);

    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private readonly Func<uint, ModernDesktopRefreshItem[]> _capture;
    private Snapshot _snapshot = new(0, []);
    private long _expiresAt;
    private uint _explorerProcessId;
    private volatile bool _disposed;

    internal ModernDesktopMenuTracker(Func<uint, ModernDesktopRefreshItem[]>? capture = null)
    {
        _capture = capture ?? CaptureRefreshItems;
        _worker = new Thread(ReadMenus)
        {
            IsBackground = true,
            Name = "CrabDesk modern desktop menu"
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    internal void Track(IntPtr desktopListView, long expiresAt)
    {
        if (_disposed) return;
        NativeMethods.GetWindowThreadProcessId(desktopListView, out var processId);
        Volatile.Write(ref _explorerProcessId, processId);
        Volatile.Write(ref _snapshot, new Snapshot(0, []));
        Volatile.Write(ref _expiresAt, expiresAt);
        _wake.Set();
    }

    internal bool IsRefreshAt(IntPtr targetWindow, int x, int y)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (_disposed || Environment.TickCount64 >= snapshot.ExpiresAt) return false;
        var root = NativeMethods.GetAncestor(targetWindow, NativeMethods.GaRoot);
        return snapshot.Items.Any(item => item.Contains(root, x, y) &&
            NativeMethods.IsWindowVisible(item.Window));
    }

    internal void Stop()
    {
        Volatile.Write(ref _expiresAt, 0);
        Volatile.Write(ref _snapshot, new Snapshot(0, []));
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _wake.Set();
        // A provider may be busy; it must not delay shutdown of the UI or hook.
        if (_worker.Join(100)) _wake.Dispose();
    }

    private void ReadMenus()
    {
        while (!_disposed)
        {
            var expiresAt = Volatile.Read(ref _expiresAt);
            if (Environment.TickCount64 >= expiresAt)
            {
                _wake.WaitOne();
                continue;
            }

            try
            {
                var items = _capture(Volatile.Read(ref _explorerProcessId));
                // Discard a slow result belonging to an earlier menu session.
                if (!_disposed && expiresAt == Volatile.Read(ref _expiresAt))
                    Volatile.Write(ref _snapshot, new Snapshot(expiresAt, items));
            }
            catch (Exception)
            {
                // Menus can disappear between enumeration and accessibility reads.
                if (expiresAt == Volatile.Read(ref _expiresAt))
                    Volatile.Write(ref _snapshot, new Snapshot(0, []));
            }
            _wake.WaitOne(80);
        }
    }

    private static ModernDesktopRefreshItem[] CaptureRefreshItems(uint explorerProcessId)
    {
        if (explorerProcessId == 0) return [];
        var windows = new List<IntPtr>();
        // EnumWindows omits the immersive popup hosts used by Windows 11.
        // FindWindowEx includes them, even when WindowFromPoint sees the app
        // underneath the menu instead of its composition popup.
        for (var window = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, null);
             window != IntPtr.Zero;
             window = NativeMethods.FindWindowEx(IntPtr.Zero, window, null, null))
        {
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId != explorerProcessId || !NativeMethods.IsWindowVisible(window)) continue;
            if (!NativeMethods.GetWindowRect(window, out var rectangle) ||
                rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top) continue;
            var name = new StringBuilder(128);
            NativeMethods.GetClassName(window, name, name.Capacity);
            if (IsPopupWindowClass(name.ToString())) windows.Add(window);
        }

        return ReadRefreshItems(windows);
    }

    internal static ModernDesktopRefreshItem[] ReadRefreshItems(IReadOnlyList<IntPtr> windows)
    {
        var result = new List<ModernDesktopRefreshItem>();
        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        var menuItems = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
        foreach (var window in windows)
        {
            try
            {
                // FromHandle needs a live element for FindAll; only its children
                // use cache-only mode so their properties cost no extra RPCs.
                var root = AutomationElement.FromHandle(window);
                using (cache.Activate())
                {
                    foreach (AutomationElement item in root.FindAll(TreeScope.Descendants, menuItems))
                    {
                        var info = item.Cached;
                        var bounds = info.BoundingRectangle;
                        if (info.IsEnabled && !info.IsOffscreen && !bounds.IsEmpty &&
                            (info.AutomationId == "refresh" || IsRefreshLabel(info.Name)))
                            result.Add(new ModernDesktopRefreshItem(window, new RectangleF(
                                (float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height),
                                UsesNativeHitTest: false));
                    }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
            catch (InvalidOperationException) { }
        }
        return result.ToArray();
    }

    internal static bool IsPopupWindowClass(string name) =>
        name == "#32768" || name.Contains("Xaml", StringComparison.OrdinalIgnoreCase) ||
        name == "Microsoft.UI.Content.PopupWindowSiteBridge";

    internal static bool IsRefreshLabel(string label)
    {
        var text = label.Split('\t')[0].Replace("&", string.Empty).Trim();
        var accelerator = text.LastIndexOf('(');
        if (accelerator >= 0 && text.EndsWith(')')) text = text[..accelerator].TrimEnd();
        return text.Equals("Refresh", StringComparison.OrdinalIgnoreCase) || text == "刷新";
    }
}
