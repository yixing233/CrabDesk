using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CrabDesk.Native;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class AcrylicHostWindowTests
{
    [Fact]
    public void HiddenHostIsRestoredAtDesktop()
    {
        RunInSta(() =>
        {
            using var desktop = new Form();
            using var host = new Form();
            using var child = new Panel { Dock = DockStyle.Fill };
            host.Controls.Add(child);
            desktop.Show();
            host.Show();
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));

            ShowWindow(host.Handle, SwHide);
            Assert.False(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));

            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.True(IsWindowVisible(host.Handle));
            Assert.True(IsWindowVisible(child.Handle));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
        });
    }

    [Fact]
    public void MinimizedHostRestoresBoundsAndChildrenWithoutActivationOrRaising()
    {
        RunInSta(() =>
        {
            using var desktop = new Form();
            using var host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(120, 140, 360, 240)
            };
            using var child = new Panel { Dock = DockStyle.Fill };
            using var spacer = new Form();
            using var ordinary = new Form();
            host.Controls.Add(child);
            desktop.Show();
            host.Show();
            spacer.Show();
            ordinary.Show();
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(spacer.Handle, desktop.Handle));
            Assert.False(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            var bounds = GetBounds(host.Handle);

            ShowWindow(host.Handle, SwMinimize);
            Assert.True(IsIconic(host.Handle));
            var active = GetActiveWindow();
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));

            Assert.False(IsIconic(host.Handle));
            Assert.Equal(bounds, GetBounds(host.Handle));
            Assert.True(IsWindowVisible(child.Handle));
            Assert.Equal(active, GetActiveWindow());
            Assert.True(IsAbove(ordinary.Handle, host.Handle));
            Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
        });
    }

    [Fact]
    public void AlreadyOrderedMinimizedHostIsRestored()
    {
        RunInSta(() =>
        {
            using var desktop = new Form();
            using var host = new Form();
            desktop.Show();
            host.Show();
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.Equal(host.Handle, GetWindow(desktop.Handle, GwHwndPrev));

            ShowWindow(host.Handle, SwMinimize);
            Assert.True(IsIconic(host.Handle));
            var previous = GetWindow(desktop.Handle, GwHwndPrev);
            Assert.True(SetWindowPos(host.Handle, previous, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize));
            Assert.Equal(host.Handle, GetWindow(desktop.Handle, GwHwndPrev));

            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.False(IsIconic(host.Handle));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
        });
    }

    [Fact]
    public void PlacementIsIdempotentAndInvalidHandlesAreRejected()
    {
        RunInSta(() =>
        {
            using var desktop = new Form();
            using var host = new Form();
            using var child = new Panel();
            host.Controls.Add(child);
            desktop.Show();
            host.Show();
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            Assert.False(DesktopAcrylicWindowTools.PlaceAtDesktop(IntPtr.Zero, desktop.Handle));
            Assert.False(DesktopAcrylicWindowTools.PlaceAtDesktop(new IntPtr(-1), desktop.Handle));
            Assert.False(DesktopAcrylicWindowTools.PlaceAtDesktop(child.Handle, desktop.Handle));
            Assert.False(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, IntPtr.Zero));
            Assert.False(DesktopAcrylicWindowTools.IsReadyAtDesktop(IntPtr.Zero, desktop.Handle));
        });
    }

    private static Rectangle GetBounds(IntPtr hwnd)
    {
        Assert.True(GetWindowRect(hwnd, out var rect));
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (var current = upper; current != IntPtr.Zero; current = GetWindow(current, GwHwndNext))
            if (current == lower) return true;
        return false;
    }

    private static void RunInSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA test thread timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private const int SwHide = 0, SwMinimize = 6, GwlExStyle = -20;
    private const uint GwHwndNext = 2, GwHwndPrev = 3;
    private const uint SwpNoSize = 1, SwpNoMove = 2, SwpNoActivate = 0x10;
    private static readonly IntPtr WsExTopmost = new(8);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
