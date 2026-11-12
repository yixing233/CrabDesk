using CrabDesk.Core;
using CrabDesk.Runtime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace CrabDesk.WinUI.Windows;

public sealed partial class WeatherDetailsWindow : Window
{
    private readonly CrabDeskRuntime _runtime;
    private readonly DesktopWidget _widget;
    private bool _updating;
    private bool? _acrylic;
    private (int Width, int Height, int Radius)? _shape;
    private readonly bool _locationsOnly;
    private WeatherDetailsView? _detailsView;
    private readonly FrameworkElement _root;
    private readonly Border? _locationsShell;
    public WeatherDetailsView View => _detailsView ??= new();
    public WeatherDetailsWindow(CrabDeskRuntime runtime, DesktopWidget widget, bool locationsOnly = false)
    {
        _runtime = runtime; _widget = widget; _locationsOnly = locationsOnly;
        Title = locationsOnly ? "天气位置 - CrabDesk" : "天气详情 - CrabDesk";
        if (locationsOnly)
        {
            Locations.VerticalAlignment = VerticalAlignment.Top;
            Locations.CloseButton.Visibility = Visibility.Visible;
            Locations.CloseButton.Click += (_, _) => Close();
            _locationsShell = new Border { Padding = new Thickness(16), Child = new ScrollViewer {
                Content = Locations, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled } };
            _root = _locationsShell;
            Locations.SizeChanged += (_, _) => ResizeLocations();
        }
        else _root = View;
        Content = _root;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        { presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; presenter.IsMinimizable = false; presenter.IsMaximizable = false; }
        var hwnd = WindowNative.GetWindowHandle(this);
        CrabDesk.Native.DesktopPopupWindow.Track(hwnd);
        // The presenter leaves WS_DLGFRAME behind even with HasBorder=false.
        // Remove that non-client frame so it cannot surround the XAML card.
        var style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, new IntPtr(style & ~0x00400000L));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037); // FRAMECHANGED, no move/size/activation/z-order change
        AppWindow.IsShownInSwitchers = false;
        InitializeEntrance();
        InitializeLocations();
        AppWindow.Changed += (_, args) => { if (args.DidSizeChange) UpdateWindowShape(); };
        _root.SizeChanged += (_, _) => UpdateWindowShape();
        if (!locationsOnly)
        {
            View.CloseButton.Click += (_, _) => Close();
            View.RefreshButton.Click += async (_, _) => { var task = runtime.RefreshWidgetWeatherAsync(widget); Update(); await task; };
            View.Cities.SelectionChanged += async (_, _) =>
            {
                if (_updating || View.Cities.SelectedItem is not WidgetWeatherCity city || city.Id == widget.ActiveCityId) return;
                runtime.SelectWidgetCity(widget, city.Id);
                await runtime.RefreshWidgetWeatherAsync(widget, force: false);
            };
        }
        _root.KeyDown += (_, args) => { if (args.Key == VirtualKey.Escape) { args.Handled = true; Close(); } };
        Activated += (_, args) => {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (locationsOnly) Locations.Search.Focus(FocusState.Programmatic);
            else if (!_locationsFlyout.IsOpen) View.CloseButton.Focus(FocusState.Programmatic);
        };
        if (locationsOnly) Locations.Loaded += (_, _) => Locations.Search.Focus(FocusState.Programmatic);
        runtime.Changed += RuntimeChanged;
        Closed += (_, _) => runtime.Changed -= RuntimeChanged;
        Update();
        var monitor = runtime.Monitors.FirstOrDefault(m => m.Id == widget.MonitorId);
        if (monitor is not null)
        {
            var scale = monitor.DpiScale;
            var area = monitor.PixelWorkArea;
            var width = (int)Math.Min((locationsOnly ? 314 : 420) * scale, area.Width - 24);
            var height = (int)Math.Min((locationsOnly ? 520 : 760) * scale, area.Height - 24);
            if (locationsOnly)
            {
                _root.Measure(new global::Windows.Foundation.Size(width / scale, double.PositiveInfinity));
                height = (int)Math.Min(Math.Ceiling(_root.DesiredSize.Height * scale), area.Height - 24);
            }
            var x = monitor.PixelBounds.X + (widget.Bounds.X + widget.Bounds.Width) * scale + 12;
            if (x + width > area.X + area.Width) x = monitor.PixelBounds.X + widget.Bounds.X * scale - width - 12;
            var y = monitor.PixelBounds.Y + widget.Bounds.Y * scale;
            AppWindow.MoveAndResize(new RectInt32((int)Math.Clamp(x, area.X, area.X + area.Width - width), (int)Math.Clamp(y, area.Y, area.Y + area.Height - height), width, height));
        }
    }
    private void RuntimeChanged(object? sender, EventArgs args)
    {
        if (!_runtime.State.Widgets.Contains(_widget) || _runtime.IsPaused) { Close(); return; }
        Update();
    }
    private void Update()
    {
        _updating = true;
        try
        {
            var appearance = _runtime.State.Boxes.FirstOrDefault()?.Appearance ?? _runtime.State.Settings.DefaultBoxAppearance;
            var settings = _runtime.State.Settings.Appearance;
            if (_locationsOnly)
            {
                var color = Converters.HexColor.TryParse(appearance.Background, out var parsed) ? parsed : global::Windows.UI.Color.FromArgb(255, 42, 45, 50);
                var isDark = color.R * .299 + color.G * .587 + color.B * .114 < 150;
                _root.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
                color.A = (byte)Math.Round(255 * appearance.ResolveBackgroundOpacity(settings.UseAcrylicBoxes));
                _locationsShell!.Background = new SolidColorBrush(color);
                _locationsShell.CornerRadius = new CornerRadius(WindowCornerRadius(settings.CornerRadius));
                _locationsShell.BorderThickness = new Thickness(0);
            }
            else View.Update(_widget, appearance, settings, WindowCornerRadius(settings.CornerRadius));
            UpdateLocations();
            ApplyLocationsMaterial(appearance, settings);
            if (_acrylic != settings.UseAcrylicBoxes)
            {
                _acrylic = settings.UseAcrylicBoxes;
                SystemBackdrop = settings.UseAcrylicBoxes ? new DesktopAcrylicBackdrop() : null;
            }
            var dark = _root.RequestedTheme == ElementTheme.Dark ? 1 : 0;
            var hwnd = WindowNative.GetWindowHandle(this);
            // Match the native frame to the card theme; the XAML shell draws
            // the shared border, so DWM must not add a second bright outline.
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            var border = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            _ = DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
            var corners = settings.CornerRadius > 0 ? 2 : 1;
            _ = DwmSetWindowAttribute(hwnd, 33, ref corners, sizeof(int));
            UpdateWindowShape();
        }
        finally { _updating = false; }
    }

    private void ResizeLocations()
    {
        var monitor = _runtime.Monitors.FirstOrDefault(m => m.Id == _widget.MonitorId);
        if (monitor is null || Locations.ActualHeight <= 0) return;
        var area = monitor.PixelWorkArea;
        var inset = _locationsShell!.Padding.Top + _locationsShell.Padding.Bottom +
            _locationsShell.BorderThickness.Top + _locationsShell.BorderThickness.Bottom;
        var height = (int)Math.Min(Math.Ceiling((Locations.ActualHeight + inset) * monitor.DpiScale), area.Height - 24);
        var position = AppWindow.Position;
        if (AppWindow.Size.Height == height) return;
        AppWindow.MoveAndResize(new RectInt32(position.X, (int)Math.Clamp(position.Y, area.Y, area.Y + area.Height - height), AppWindow.Size.Width, height));
    }

    private void UpdateWindowShape()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var size = AppWindow.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;
        var radius = (int)Math.Round(Math.Clamp(WindowCornerRadius(_runtime.State.Settings.Appearance.CornerRadius) * scale,
            0, Math.Min(size.Width, size.Height) / 2.0));
        var shape = (size.Width, size.Height, radius);
        if (_shape == shape) return;
        // Window regions disable DWM's antialiased corners. Let Windows 11
        // round the entire backdrop, and match the XAML shell to that radius.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            _ = SetWindowRgn(hwnd, IntPtr.Zero, true);
            _shape = shape;
            return;
        }
        // Retain the configured outline on systems without native rounding.
        if (radius == 0)
        {
            if (SetWindowRgn(hwnd, IntPtr.Zero, true) != 0) _shape = shape;
            return;
        }
        var region = CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(hwnd, region, true) != 0) _shape = shape; // Windows owns the applied region.
        else DeleteObject(region);
    }

    private static double WindowCornerRadius(double configured) => configured <= 0 ? 0 :
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? 8 : Math.Clamp(configured, 0, 20);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
