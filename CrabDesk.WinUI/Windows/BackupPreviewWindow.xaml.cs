using System.Runtime.InteropServices;
using CrabDesk.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace CrabDesk.WinUI.Windows;

public sealed partial class BackupPreviewWindow : Window
{
    private const int GwlpHwndParent = -8;
    private const uint MonitorDefaultToNearest = 2;
    private readonly IntPtr _ownerHandle;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Point? _panStart;
    private double _horizontalStart;
    private double _verticalStart;
    private bool _isPanning;
    private NativePoint? _windowDragStartCursor;
    private PointInt32? _windowDragStartPosition;
    private bool _isWindowDragging;

    private BackupPreviewWindow(IntPtr ownerHandle, LayoutBackupInfo backup, bool isDark)
    {
        _ownerHandle = ownerHandle;
        InitializeComponent();
        RootGrid.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(RootGrid_OnPointerWheelChanged),
            true);
        RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        DetailsText.Text = $"{backup.CreatedAtText} · {backup.BoxCount} 个盒子 · {backup.RuleCount} 条规则 · {backup.MonitorCount} 块屏幕";
        if (backup.HasDesktopPreview)
        {
            PreviewImage.Source = new BitmapImage(new Uri(backup.DesktopPreviewPath, UriKind.Absolute));
        }
        else
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            LayoutPreview.Visibility = Visibility.Visible;
            LayoutPreview.Snapshot = backup.Snapshot;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        Closed += (_, _) => _closed.TrySetResult();
    }

    internal static Task ShowAsync(IntPtr ownerHandle, LayoutBackupInfo backup, bool isDark)
    {
        var window = new BackupPreviewWindow(ownerHandle, backup, isDark);
        window.ConfigureWindow();
        window.Activate();
        return window._closed.Task;
    }

    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (_ownerHandle != IntPtr.Zero)
        {
            var owner = GetAncestor(_ownerHandle, 2);
            _ = SetWindowLongPtr(hwnd, GwlpHwndParent, owner == IntPtr.Zero ? _ownerHandle : owner);
            if (GetWindowRect(_ownerHandle, out var ownerRect))
            {
                AppWindow.Move(new PointInt32(ownerRect.Left, ownerRect.Top));
                AppWindow.Resize(new SizeInt32(ownerRect.Right - ownerRect.Left, ownerRect.Bottom - ownerRect.Top));
                return;
            }
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            AppWindow.Move(new PointInt32(info.WorkArea.Left, info.WorkArea.Top));
            AppWindow.Resize(new SizeInt32(info.WorkArea.Right - info.WorkArea.Left, info.WorkArea.Bottom - info.WorkArea.Top));
        }
    }

    private void RootGrid_OnSizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        ImageStage.Width = Math.Max(1, eventArgs.NewSize.Width);
        ImageStage.Height = Math.Max(1, eventArgs.NewSize.Height);
    }

    private void RootGrid_OnKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        if (eventArgs.Key == VirtualKey.Escape)
        {
            Close();
            eventArgs.Handled = true;
        }
    }

    private void Backdrop_OnTapped(object sender, TappedRoutedEventArgs eventArgs) => Close();

    internal static bool CanStartWindowDrag(PointerDeviceType pointerDeviceType, bool isLeftButtonPressed) =>
        pointerDeviceType.Equals(PointerDeviceType.Mouse) && isLeftButtonPressed;

    private void WindowDragRegion_OnPointerPressed(object sender, PointerRoutedEventArgs eventArgs)
    {
        var point = eventArgs.GetCurrentPoint(WindowDragRegion);
        if (!CanStartWindowDrag(eventArgs.Pointer.PointerDeviceType, point.Properties.IsLeftButtonPressed))
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        _windowDragStartCursor = cursor;
        _windowDragStartPosition = AppWindow.Position;
        _isWindowDragging = WindowDragRegion.CapturePointer(eventArgs.Pointer);
        eventArgs.Handled = _isWindowDragging;
    }

    private void WindowDragRegion_OnPointerMoved(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (!_isWindowDragging || _windowDragStartCursor is not { } dragStart || _windowDragStartPosition is not { } windowStart)
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var position = BackupPreviewWindowDragMath.CalculatePositionFromScreenCursor(
            windowStart.X,
            windowStart.Y,
            dragStart.X,
            dragStart.Y,
            cursor.X,
            cursor.Y);
        AppWindow.Move(new PointInt32(position.X, position.Y));
        eventArgs.Handled = true;
    }

    private void WindowDragRegion_OnPointerReleased(object sender, PointerRoutedEventArgs eventArgs)
    {
        eventArgs.Handled = _isWindowDragging;
        _windowDragStartCursor = null;
        _windowDragStartPosition = null;
        _isWindowDragging = false;
        WindowDragRegion.ReleasePointerCaptures();
    }

    private void ZoomOut_OnClick(object sender, RoutedEventArgs eventArgs) => ChangeZoom(ImageViewer.ZoomFactor / 1.25f);

    private void ZoomIn_OnClick(object sender, RoutedEventArgs eventArgs) => ChangeZoom(ImageViewer.ZoomFactor * 1.25f);

    private void Reset_OnClick(object sender, RoutedEventArgs eventArgs) => ImageViewer.ChangeView(0, 0, 1, true);

    private void Close_OnClick(object sender, RoutedEventArgs eventArgs) => Close();

    private void ImageViewer_OnViewChanged(object sender, ScrollViewerViewChangedEventArgs eventArgs) =>
        ZoomText.Text = $"{Math.Round(ImageViewer.ZoomFactor * 100)}%";

    private void ImageViewer_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs eventArgs) =>
        ChangeZoom(ImageViewer.ZoomFactor > 1.01f ? 1 : 2);

    private void ImageViewer_OnPointerWheelChanged(object sender, PointerRoutedEventArgs eventArgs)
    {
        var point = eventArgs.GetCurrentPoint(ImageViewer);
        var targetZoom = ImageViewer.ZoomFactor * (point.Properties.MouseWheelDelta > 0 ? 1.2f : 1 / 1.2f);
        ChangeZoomAt(point.Position, targetZoom);
        eventArgs.Handled = true;
    }

    private void RootGrid_OnPointerWheelChanged(object sender, PointerRoutedEventArgs eventArgs) =>
        eventArgs.Handled = true;

    private void ImageViewer_OnPointerPressed(object sender, PointerRoutedEventArgs eventArgs)
    {
        var point = eventArgs.GetCurrentPoint(ImageViewer);
        if (!eventArgs.Pointer.PointerDeviceType.Equals(PointerDeviceType.Mouse) || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _panStart = point.Position;
        _horizontalStart = ImageViewer.HorizontalOffset;
        _verticalStart = ImageViewer.VerticalOffset;
        _isPanning = ImageViewer.CapturePointer(eventArgs.Pointer);
        eventArgs.Handled = _isPanning;
    }

    private void ImageViewer_OnPointerMoved(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (!_isPanning || _panStart is not { } panStart)
        {
            return;
        }

        var point = eventArgs.GetCurrentPoint(ImageViewer).Position;
        ImageViewer.ChangeView(
            Math.Max(0, _horizontalStart - (point.X - panStart.X)),
            Math.Max(0, _verticalStart - (point.Y - panStart.Y)),
            null,
            true);
        eventArgs.Handled = true;
    }

    private void ImageViewer_OnPointerReleased(object sender, PointerRoutedEventArgs eventArgs)
    {
        _panStart = null;
        _isPanning = false;
        ImageViewer.ReleasePointerCaptures();
    }

    private void ChangeZoom(float zoomFactor)
    {
        var target = Math.Clamp(zoomFactor, ImageViewer.MinZoomFactor, ImageViewer.MaxZoomFactor);
        ImageViewer.ChangeView(null, null, target, true);
    }

    private void ChangeZoomAt(Point pointerPosition, float zoomFactor)
    {
        var target = Math.Clamp(zoomFactor, ImageViewer.MinZoomFactor, ImageViewer.MaxZoomFactor);
        if (Math.Abs(target - ImageViewer.ZoomFactor) < float.Epsilon)
        {
            return;
        }

        var offsets = BackupPreviewZoomMath.CalculateAnchoredOffsets(
            ImageViewer.HorizontalOffset,
            ImageViewer.VerticalOffset,
            pointerPosition.X,
            pointerPosition.Y,
            ImageViewer.ZoomFactor,
            target);
        ImageViewer.ChangeView(offsets.Horizontal, offsets.Vertical, target, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
