using System.Diagnostics;
using CrabDesk.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace CrabDesk.WinUI.Windows;

public sealed partial class FileShelfWindow
{
    private readonly FileShelfTransition _transition = new();
    private readonly FileShelfTransition _retraction = new();
    private readonly TranslateTransform _shellOffset = new();
    private readonly TranslateTransform _panelOffset = new();
    private readonly TranslateTransform _tabOffset = new();
    private readonly TranslateTransform _tabIconOffset = new();
    private long _lastMotionFrame;
    private long _collapsedIdleSince;
    private bool _retracted;
    private bool IsMotionRunning => _transition.IsRunning || _retraction.IsRunning;
    private LayoutRect CollapsedTabBounds => FileShelfPlacement.Retracted(
        FileShelfPlacement.Collapsed(_expanded, _workArea, _edge, DpiScale), _edge, 1 - _retraction.Progress);

    private void TransitionTo(bool expanded, bool animate = true)
    {
        _transition.SetTarget(expanded);
        _retracted = false; _collapsedIdleSince = 0;
        _retraction.SetTarget(true);
        StartMotion(animate);
    }

    private void Retract(bool retracted)
    {
        if (_retracted == retracted) return;
        _retracted = retracted;
        _retraction.SetTarget(!retracted);
        StartMotion(animate: true);
    }

    private void StartMotion(bool animate)
    {
        if (!animate || !_runtime.State.Settings.Appearance.AnimationEnabled || !new UISettings().AnimationsEnabled)
        {
            StopMotion();
            _transition.Reset(!_collapsed);
            _retraction.Reset(!_retracted);
            ApplyMotion();
            return;
        }
        if (!IsMotionRunning || _lastMotionFrame != 0) return;
        _lastMotionFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += MotionFrame;
        ApplyMotion();
    }

    private void MotionFrame(object? sender, object args)
    {
        if (_closed) { StopMotion(); return; }
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_lastMotionFrame, now).TotalSeconds;
        _transition.Advance(seconds);
        _retraction.Advance(seconds);
        _lastMotionFrame = now;
        ApplyMotion();
        if (!IsMotionRunning)
        {
            StopMotion();
            _insideSince = _outsideSince = 0;
        }
    }

    private void StopMotion()
    {
        if (_lastMotionFrame == 0) return;
        CompositionTarget.Rendering -= MotionFrame;
        _lastMotionFrame = 0;
    }

    private void ApplyMotion()
    {
        Move(FileShelfTransition.Interpolate(CollapsedTabBounds, _expanded, _transition.Progress));
    }

    private void UpdateMotionContent(LayoutRect bounds)
    {
        var progress = _transition.Progress;
        var scale = DpiScale;
        var tab = CollapsedTabBounds;
        _shell.Width = bounds.Width / scale;
        _shell.Height = bounds.Height / scale;
        _shellOffset.X = (bounds.X - WindowBounds.X) / scale;
        _shellOffset.Y = (bounds.Y - WindowBounds.Y) / scale;
        // Canvas keeps the full-size file layout stable while the HWND reveals
        // or clips it. Neither the file names nor the toolbar reflow per frame.
        _panel.Width = Math.Max(1, _expanded.Width / scale - 2);
        _panel.Height = Math.Max(1, _expanded.Height / scale - 2);
        _panelOffset.X = progress == 1 ? 0 : (_expanded.X - bounds.X) / scale + 1 - progress;
        _panelOffset.Y = progress == 1 ? 0 : (_expanded.Y - bounds.Y) / scale + 1 - progress;
        _tab.Width = tab.Width / scale;
        _tab.Height = tab.Height / scale;
        _tabOffset.X = (tab.X - bounds.X) / scale - progress;
        _tabOffset.Y = (tab.Y - bounds.Y) / scale - progress;
        // Keep the complete icon centered in the exposed part as the tab slides.
        var visibleTab = FileShelfPlacement.Visible(tab, _workArea);
        _tabIconOffset.X = (visibleTab.X + visibleTab.Width / 2 - tab.X - tab.Width / 2) / scale;
        _tabIconOffset.Y = (visibleTab.Y + visibleTab.Height / 2 - tab.Y - tab.Height / 2) / scale;
        var blend = FadeRange(progress, .08, .92);
        _panel.Opacity = blend;
        _tab.Opacity = 1 - blend;
        _panel.Visibility = progress > 0 ? Visibility.Visible : Visibility.Collapsed;
        _tab.Visibility = progress < 1 ? Visibility.Visible : Visibility.Collapsed;
        _panel.IsHitTestVisible = !_collapsed && progress >= .85;
        _tab.IsHitTestVisible = _collapsed;
    }

    private static double FadeRange(double value, double start, double end)
    {
        var progress = Math.Clamp((value - start) / (end - start), 0, 1);
        return progress * progress * (3 - 2 * progress);
    }
}
