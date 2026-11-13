using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace CrabDesk.Runtime;

internal sealed partial class WidgetCenterForm
{
    private Point _panelTarget;
    private int _panelFrom;
    private int _panelTo;
    private long _panelStarted;
    private bool _panelShowing;
    private int _panelOffset;
    private double _panelDuration;
    private bool _panelFrameFrozen;

    internal int PanelOffset => _panelMotion.Enabled
        ? PanelMotionOffset(_panelFrom, _panelTo, Stopwatch.GetElapsedTime(_panelStarted).TotalMilliseconds / _panelDuration)
        : _panelOffset;

    internal static int PanelMotionOffset(int from, int to, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        return (int)Math.Round(from + (to - from) * eased);
    }

    private void StartPanelMotion(bool showing, bool animate)
    {
        var from = PanelOffset;
        _panelShowing = showing;
        _panelFrom = from;
        _panelTo = showing ? 0 : Width;
        _panelDuration = Math.Max(60, (showing ? 240d : 180d) * Math.Abs(_panelTo - from) / Math.Max(1, Width));
        _panelStarted = Stopwatch.GetTimestamp();
        if (!animate || _panelFrom == _panelTo)
        {
            FinishPanelMotion();
            return;
        }
        _widgetTimer?.Stop();
        _lyricsAnimationTimer?.Stop();
        _panelComposition?.AnimateOffset(_panelTo, TimeSpan.FromMilliseconds(_panelDuration));
        _panelMotion.RequestFrames();
    }

    private void AdvancePanelMotion(object? sender, EventArgs args)
    {
        if (IsDisposed || Disposing || !Visible) { StopPanelMotion(); return; }
        var progress = Stopwatch.GetElapsedTime(_panelStarted).TotalMilliseconds / _panelDuration;
        if (_panelComposition is null)
            MovePanel(PanelMotionOffset(_panelFrom, _panelTo, progress));
        if (progress >= 1) FinishPanelMotion();
    }

    private void FinishPanelMotion()
    {
        StopPanelMotion();
        if (!_panelShowing) Hide();
        MovePanel(0);
        _panelFrameFrozen = false;
        if (_panelShowing)
        {
            _widgetTimer?.Start();
            UpdateLyricsAnimationTimer();
            Invalidate(true);
        }
    }

    private void StopPanelMotion() => _panelMotion.StopWhenIdle(false);

    private void MovePanel(int offset)
    {
        _panelOffset = offset;
        if (_panelComposition is not null)
            _panelComposition.SetOffset(offset);
        else if (IsHandleCreated)
            _ = SetWindowPos(Handle, IntPtr.Zero, _panelTarget.X + offset, _panelTarget.Y, 0, 0, 0x0215);
        else Location = new(_panelTarget.X + offset, _panelTarget.Y);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
