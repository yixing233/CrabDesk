using System.Diagnostics;
using System.Drawing;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Threading = System.Windows.Threading;

namespace CrabDesk.Runtime;

internal abstract partial class DesktopWidgetInputForm
{
    private Wpf.FrameworkElement? _entranceSurface;
    private bool _animateEntrance;
    private Threading.DispatcherOperation? _revealOperation;
    private Point _entranceTarget;
    private int _entranceDistance;
    private int _entrancePaintFrames;
    private long _entranceStarted;

    protected void InitializePopupEntrance(Wpf.FrameworkElement surface, bool animationsEnabled)
    {
        _entranceSurface = surface;
        _animateEntrance = animationsEnabled && Wpf.SystemParameters.ClientAreaAnimation;
        // Layer the complete HWND, including the ElementHost and native frame.
        // Fading only its WPF child leaves the opaque card flashing behind it.
        Opacity = _animateEntrance ? .01 : 1;
    }

    protected override void OnShown(EventArgs e)
    {
        if (_entranceSurface is { } surface)
        {
            surface.UpdateLayout();
            Media.CompositionTarget.Rendering += PrepareFirstFrame;
        }
        base.OnShown(e);
    }

    private void PrepareFirstFrame(object? sender, EventArgs args)
    {
        Media.CompositionTarget.Rendering -= PrepareFirstFrame;
        // Rendering fires before WPF submits its frame. Reveal at the next
        // lower dispatcher priority, after that render pass has completed.
        _revealOperation = _entranceSurface!.Dispatcher.BeginInvoke(Threading.DispatcherPriority.Loaded, (Action)ShowFirstFrame);
    }

    private void ShowFirstFrame()
    {
        _revealOperation = null;
        if (IsDisposed || Disposing || !Visible) return;
        if (_animateEntrance)
        {
            _entranceTarget = Location;
            _entranceDistance = Math.Max(1, (int)Math.Round(10 * DeviceDpi / 96.0));
            var area = System.Windows.Forms.Screen.FromRectangle(Bounds).WorkingArea;
            if (Bottom + _entranceDistance > area.Bottom) _entranceDistance = -_entranceDistance;
            MoveEntrance(_entranceDistance);
            _entrancePaintFrames = 0;
            Media.CompositionTarget.Rendering += WaitForEntrancePaint;
        }
        SetEntranceCloak(false);
    }

    private void WaitForEntrancePaint(object? sender, EventArgs args)
    {
        // WPF's Rendering event precedes presentation. Keep the native surface
        // nearly transparent while its first frame crosses the render thread.
        if (++_entrancePaintFrames < 2) return;
        Media.CompositionTarget.Rendering -= WaitForEntrancePaint;
        _entranceStarted = Stopwatch.GetTimestamp();
        Media.CompositionTarget.Rendering += AnimateEntrance;
    }

    private void AnimateEntrance(object? sender, EventArgs args)
    {
        if (IsDisposed || Disposing || !Visible)
        {
            StopPopupEntrance();
            return;
        }
        var elapsed = Stopwatch.GetElapsedTime(_entranceStarted).TotalMilliseconds;
        var fade = Math.Clamp(elapsed / 240, 0, 1);
        Opacity = .01 + .99 * fade * fade * (3 - 2 * fade);
        var travel = Math.Clamp(elapsed / 280, 0, 1);
        MoveEntrance((int)Math.Round(_entranceDistance * Math.Pow(1 - travel, 3)));
        if (travel == 1) StopPopupEntrance();
    }

    private void MoveEntrance(int offset) =>
        SetWindowPos(Handle, IntPtr.Zero, _entranceTarget.X, _entranceTarget.Y + offset, 0, 0, 0x0215);

    private void SetEntranceCloak(bool hidden)
    {
        if (!IsHandleCreated) return;
        var cloak = hidden ? 1 : 0;
        _ = DwmSetPopupAttribute(Handle, 13, ref cloak, sizeof(int));
    }

    private void StopPopupEntrance()
    {
        Media.CompositionTarget.Rendering -= PrepareFirstFrame;
        Media.CompositionTarget.Rendering -= WaitForEntrancePaint;
        Media.CompositionTarget.Rendering -= AnimateEntrance;
        _revealOperation?.Abort();
        _revealOperation = null;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetPopupAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
