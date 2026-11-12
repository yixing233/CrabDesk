using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace CrabDesk.WinUI.Windows;

internal sealed class DetailPopupEntrance : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _surface;
    private readonly Func<bool> _animationsEnabled;
    private readonly IntPtr _hwnd;
    private bool _animate, _pending, _running;
    private PointInt32 _target;
    private int _distance;
    private long _started;

    internal DetailPopupEntrance(Window window, FrameworkElement surface, Func<bool> animationsEnabled)
    {
        _window = window;
        _surface = surface;
        _animationsEnabled = animationsEnabled;
        _hwnd = WindowNative.GetWindowHandle(window);
    }

    internal void Prepare()
    {
        Cancel();
        _pending = true;
        _animate = _animationsEnabled() && new UISettings().AnimationsEnabled;
        _surface.Opacity = 1;
        SetOpacity(_animate ? 0 : 1);
        SetCloak(true);
        if (_surface.IsLoaded) QueueReveal();
        else _surface.Loaded += SurfaceLoaded;
    }

    private void SurfaceLoaded(object sender, RoutedEventArgs args)
    {
        _surface.Loaded -= SurfaceLoaded;
        QueueReveal();
    }

    private void QueueReveal()
    {
        _surface.UpdateLayout();
        CompositionTarget.Rendered += Reveal;
    }

    private void Reveal(object? sender, RenderedEventArgs args)
    {
        CompositionTarget.Rendered -= Reveal;
        if (!_pending) return;
        _pending = false;
        if (_animate)
        {
            _target = _window.AppWindow.Position;
            _distance = Math.Max(1, (int)Math.Round(10 * GetDpiForWindow(_hwnd) / 96.0));
            var area = System.Windows.Forms.Screen.FromHandle(_hwnd).WorkingArea;
            if (_target.Y + _window.AppWindow.Size.Height + _distance > area.Bottom) _distance = -_distance;
            _running = true;
            ApplyFrame(0);
            _started = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += Animate;
        }
        SetCloak(false);
    }

    private void Animate(object? sender, object args)
    {
        var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        ApplyFrame(elapsed);
        if (elapsed >= 280)
        {
            _running = false;
            CompositionTarget.Rendering -= Animate;
        }
    }

    private void ApplyFrame(double elapsed)
    {
        var fade = Math.Clamp(elapsed / 240, 0, 1);
        SetOpacity(fade * fade * (3 - 2 * fade));
        var travel = Math.Clamp(elapsed / 280, 0, 1);
        // Move the native surface as well as its XAML, so the acrylic backdrop
        // and outline enter together without exposing an unpainted edge.
        _window.AppWindow.Move(new PointInt32(_target.X, _target.Y +
            (int)Math.Round(_distance * Math.Pow(1 - travel, 3))));
    }

    internal void Cancel() => Stop(restoreSurface: true);

    private void Stop(bool restoreSurface)
    {
        _pending = false;
        _surface.Loaded -= SurfaceLoaded;
        CompositionTarget.Rendered -= Reveal;
        CompositionTarget.Rendering -= Animate;
        if (_running)
        {
            _running = false;
            if (restoreSurface) _window.AppWindow.Move(_target);
        }
        if (restoreSurface) { _surface.Opacity = 1; SetOpacity(1); }
    }

    public void Dispose() => Stop(restoreSurface: false);

    private void SetCloak(bool hidden)
    {
        var value = hidden ? 1 : 0;
        _ = DwmSetWindowAttribute(_hwnd, 13, ref value, sizeof(int));
    }

    private void SetOpacity(double opacity)
    {
        var style = GetWindowLongPtr(_hwnd, -20).ToInt64();
        if ((style & 0x00080000) == 0) SetWindowLongPtr(_hwnd, -20, new IntPtr(style | 0x00080000));
        _ = SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), 2);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
