using System.Diagnostics;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace CrabDesk.WinUI.Windows;

public sealed partial class FileShelfWindow
{
    private uint? _dragPointer;
    private NativePoint _dragStart;
    private LayoutRect _dragBounds;
    private bool _dragEventsAttached;
    private LayoutRect _workArea;
    private LayoutRect _visibleBounds;
    private LayoutRect _surfaceBounds;

    private double DpiScale => Math.Max(1, GetDpiForWindow(_hwnd)) / 96d;
    private LayoutRect CurrentBounds => _visibleBounds;
    private LayoutRect WindowBounds => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
    private LayoutRect CurrentWorkArea()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        return new(area.X, area.Y, area.Width, area.Height);
    }

    private void Move(LayoutRect bounds)
    {
        _surfaceBounds = RoundBounds(bounds);
        // Keep the compositor's surface at full size while the region reveals
        // the shelf. Its swap chain remains stable throughout the transition.
        var host = RoundBounds(_transition.Progress < 1 ? _expanded : bounds);
        _visibleBounds = FileShelfPlacement.Visible(_surfaceBounds, host);
        if (WindowBounds != host)
            AppWindow.MoveAndResize(new RectInt32((int)host.X, (int)host.Y, (int)host.Width, (int)host.Height));
        UpdateMotionContent(_surfaceBounds);
        UpdateShape();
    }

    private static LayoutRect RoundBounds(LayoutRect bounds)
    {
        var left = (int)Math.Round(bounds.X); var top = (int)Math.Round(bounds.Y);
        var width = Math.Max(1, (int)Math.Round(bounds.X + bounds.Width) - left);
        var height = Math.Max(1, (int)Math.Round(bounds.Y + bounds.Height) - top);
        return new(left, top, width, height);
    }

    private void RestorePosition()
    {
        var settings = Widget.FileShelfFloating;
        var monitor = _runtime.Monitors.FirstOrDefault(m => m.Id == settings.MonitorId)
            ?? _runtime.Monitors.FirstOrDefault(m => m.Id == Widget.MonitorId) ?? _runtime.Monitors.FirstOrDefault(m => m.IsPrimary);
        var area = monitor?.PixelWorkArea ?? CurrentWorkArea();
        var scale = monitor?.DpiScale ?? DpiScale;
        var saved = settings.Bounds;
        _expanded = saved is { } b ? new(area.X + b.X * scale, area.Y + b.Y * scale, b.Width * scale, b.Height * scale)
            : new(area.X + Math.Max(0, area.Width - 404 * scale), area.Y + Math.Max(0, (area.Height - 420 * scale) / 2), 380 * scale, 420 * scale);
        _edge = settings.DockEdge; _workArea = area;
        _expanded = FileShelfPlacement.Dock(_expanded, area, _edge);
        _lastSavedBounds = saved;
        StopMotion(); _transition.Reset(true); _retraction.Reset(true);
        _collapsed = _retracted = false; _collapsedIdleSince = 0;
        Move(_expanded); _outsideSince = 0;
    }

    private void SavePosition()
    {
        var area = CurrentWorkArea(); var scale = DpiScale;
        var centerX = _expanded.X + _expanded.Width / 2; var centerY = _expanded.Y + _expanded.Height / 2;
        var monitor = _runtime.Monitors.FirstOrDefault(m => m.PixelWorkArea.Contains(centerX, centerY));
        var bounds = new LayoutRect((_expanded.X - area.X) / scale, (_expanded.Y - area.Y) / scale, _expanded.Width / scale, _expanded.Height / scale);
        _runtime.SaveFileShelfFloatingPosition(Widget, monitor?.Id ?? string.Empty, bounds, _edge);
        _lastSavedBounds = Widget.FileShelfFloating.Bounds;
    }

    private void AttachDragHandle(UIElement handle)
    {
        handle.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || _interactionDepth > 0 || !GetCursorPos(out _dragStart)) return;
            _dragPointer = args.Pointer.PointerId;
            Expand(animate: false); _dragBounds = CurrentBounds;
            _shell.CapturePointer(args.Pointer); args.Handled = true;
        };
        if (_dragEventsAttached) return;
        _dragEventsAttached = true;
        _shell.PointerMoved += (_, args) =>
        {
            if (_dragPointer != args.Pointer.PointerId || !GetCursorPos(out var cursor)) return;
            var x = cursor.X - _dragStart.X; var y = cursor.Y - _dragStart.Y;
            if (Math.Abs(x) + Math.Abs(y) < 3) return;
            Move(_dragBounds with { X = _dragBounds.X + x, Y = _dragBounds.Y + y });
            args.Handled = true;
        };
        _shell.PointerReleased += (_, args) =>
        {
            if (_dragPointer != args.Pointer.PointerId) return;
            FinishMove(); _shell.ReleasePointerCapture(args.Pointer); args.Handled = true;
        };
        _shell.PointerCaptureLost += (_, _) => FinishMove();
        _shell.PointerCanceled += (_, _) => FinishMove();
    }

    private void FinishMove()
    {
        if (_dragPointer is null || _closed) return;
        _dragPointer = null;
        _workArea = CurrentWorkArea();
        (_expanded, _edge) = FileShelfPlacement.Snap(CurrentBounds, _workArea, 20 * DpiScale);
        Move(_expanded); SavePosition(); _outsideSince = 0;
    }

    internal void Expand(bool animate = true)
    {
        if (_closed) return;
        _outsideSince = 0;
        if (!_collapsed && (animate || !IsMotionRunning)) return;
        _collapsed = false; _insideSince = 0;
        _workArea = CurrentWorkArea();
        _expanded = FileShelfPlacement.Dock(_expanded, _workArea, _edge);
        TransitionTo(expanded: true, animate: animate);
    }

    internal void Collapse()
    {
        if (_closed || _collapsed || _edge == FileShelfDockEdge.None || !Widget.FileShelfFloating.AutoHide ||
            _dragPointer is not null || _outgoingDrag || _incomingDrag || _interactionDepth > 0 || _menuOpen || Widget.FileShelfBusy) return;
        _collapsed = true; _insideSince = 0;
        TransitionTo(expanded: false);
    }

    private void Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_closed) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastAvailability == 0 || Stopwatch.GetElapsedTime(_lastAvailability, now).TotalSeconds >= 10)
        { _lastAvailability = now; _ = _runtime.RefreshFileShelfAvailabilityAsync(); }
        if (_dragPointer is not null || _outgoingDrag || _interactionDepth > 0 || _menuOpen || Widget.FileShelfBusy)
        { _outsideSince = _collapsedIdleSince = 0; return; }
        var area = CurrentWorkArea();
        if (area != _workArea)
        {
            _workArea = area; _expanded = FileShelfPlacement.Dock(_expanded, area, _edge);
            TransitionTo(expanded: !_collapsed, animate: false);
            SavePosition();
        }
        if (!GetCursorPos(out var cursor)) return;
        var hoverBounds = _collapsed
            ? FileShelfPlacement.Visible(CollapsedTabBounds, _workArea)
            : CurrentBounds;
        if (hoverBounds.Contains(cursor.X, cursor.Y))
        {
            _outsideSince = _collapsedIdleSince = 0;
            Retract(false);
            if (_insideSince == 0) _insideSince = now;
            if (_collapsed && Stopwatch.GetElapsedTime(_insideSince, now).TotalMilliseconds >= 180) Expand();
        }
        else
        {
            _insideSince = 0;
            _incomingDrag = false;
            _dropHint.Visibility = Visibility.Collapsed;
            if (_outsideSince == 0) _outsideSince = now;
            if (Stopwatch.GetElapsedTime(_outsideSince, now).TotalMilliseconds >= 650) Collapse();
            if (_collapsed && !_transition.IsRunning && !_incomingDrag && Widget.FileShelfFloating.AutoHide)
            {
                if (_collapsedIdleSince == 0) _collapsedIdleSince = now;
                if (Stopwatch.GetElapsedTime(_collapsedIdleSince, now).TotalSeconds >= 2) Retract(true);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
