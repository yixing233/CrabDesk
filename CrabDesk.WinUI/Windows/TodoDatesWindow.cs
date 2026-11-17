using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Controls;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace CrabDesk.WinUI.Windows;

/// <summary>Fluent date range editor anchored to the desktop widget.</summary>
public sealed class TodoDatesWindow : Window
{
    private const uint KeepPopupPositionAndFocus = 0x0213; // NOMOVE | NOSIZE | NOACTIVATE | NOOWNERZORDER
    private readonly CrabDeskRuntime _runtime;
    private readonly IntPtr _hwnd;
    private readonly Border _shell;
    private readonly StackPanel _content;
    private readonly TextBlock _taskText = new() { FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { FontSize = 12, MinHeight = 20, TextWrapping = TextWrapping.Wrap };
    private readonly CalendarDatePicker _start = new() { Header = "开始日期（可选）", PlaceholderText = "选择开始日期", HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    private readonly CalendarDatePicker _end = new() { Header = "截止日期（可选）", PlaceholderText = "选择截止日期", HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    private readonly Button _save = new() { Content = "保存", MinWidth = 80 };
    private DesktopWidget? _widget;
    private WidgetTodoItem? _task;
    private bool _visible;
    private bool _presenting;
    private bool _openedFromCenter;
    private readonly DetailPopupEntrance _entrance;

    public TodoDatesWindow(CrabDeskRuntime runtime)
    {
        _runtime = runtime;
        Title = "待办日期 - CrabDesk";
        _content = new StackPanel { Spacing = 16 };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(new TextBlock { Text = "日期范围", FontSize = 18, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(_taskText); header.Children.Add(heading);
        var close = new Button { Content = new TablerIcon { Icon = TablerIconName.X, FontSize = 12 }, Width = 28, Height = 28,
            MinWidth = 28, MinHeight = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, _) => Dismiss(); AutomationProperties.SetName(close, "关闭日期设置");
        header.Children.Add(close); Grid.SetColumn(close, 1);
        _content.Children.Add(header); _content.Children.Add(_start); _content.Children.Add(_end); _content.Children.Add(_status);
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var clear = new HyperlinkButton { Content = "清除日期", Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) => { _start.Date = null; _end.Date = null; ShowStatus("保存后移除日期限制。"); };
        footer.Children.Add(clear);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var cancel = new Button { Content = "取消", MinWidth = 80 }; cancel.Click += (_, _) => Dismiss();
        actions.Children.Add(cancel); actions.Children.Add(_save); footer.Children.Add(actions); Grid.SetColumn(actions, 1);
        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _save.Click += (_, _) => SaveDates(); _content.Children.Add(footer);
        _shell = new Border { Padding = new Thickness(20), CornerRadius = new CornerRadius(8), Child = _content };
        Content = _shell;
        AutomationProperties.SetName(_start, "开始日期"); AutomationProperties.SetAutomationId(_start, "TodoStartDate");
        AutomationProperties.SetName(_end, "截止日期"); AutomationProperties.SetAutomationId(_end, "TodoEndDate");
        AutomationProperties.SetAutomationId(_save, "SaveTodoDates");
        _start.DateChanged += (_, _) => ShowStatus("日期可留空，点击保存后生效。");
        _end.DateChanged += (_, _) => ShowStatus("日期可留空，点击保存后生效。");
        _shell.KeyDown += (_, args) => { if (args.Key == VirtualKey.Escape && !_start.IsCalendarOpen && !_end.IsCalendarOpen) { args.Handled = true; Dismiss(); } };
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        { presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; presenter.IsMinimizable = false; presenter.IsMaximizable = false; }
        AppWindow.IsShownInSwitchers = false;
        _hwnd = WindowNative.GetWindowHandle(this);
        DesktopPopupWindow.Track(_hwnd);
        SetWindowLongPtr(_hwnd, -16, new IntPtr(GetWindowLongPtr(_hwnd, -16).ToInt64() & ~0x00400000L));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
        DesktopInputProxy.Register(_hwnd);
        _entrance = new DetailPopupEntrance(this, _shell, () => _runtime.State.Settings.Appearance.AnimationEnabled);
        Activated += (_, args) =>
        {
            if (!_presenting && _visible && args.WindowActivationState == WindowActivationState.Deactivated && !_start.IsCalendarOpen && !_end.IsCalendarOpen) Dismiss();
        };
        _runtime.Changed += RuntimeChanged;
        Closed += (_, _) => { _runtime.Changed -= RuntimeChanged; _entrance.Dispose(); DesktopInputProxy.Unregister(_hwnd); };
    }

    public void Present(DesktopWidget widget, WidgetTodoItem task, WidgetTodoDateAnchor? anchor = null)
    {
        _presenting = true;
        try
        {
            _start.IsCalendarOpen = _end.IsCalendarOpen = false;
            _widget = widget; _task = task; _taskText.Text = task.Text;
            _start.Date = ToOffset(task.StartDate); _end.Date = ToOffset(task.EndDate);
            ShowStatus("日期可留空，点击保存后生效。");
            _openedFromCenter = anchor is not null && anchor.OwnerHandle != IntPtr.Zero;
            _ = SetWindowLongPtr(_hwnd, -8, anchor?.OwnerHandle ?? IntPtr.Zero); // GWLP_HWNDPARENT on a top-level window sets its owner.
            ApplyAppearance(anchor?.Appearance ?? ResolveAppearance(widget));
            var monitor = anchor?.Monitor ?? _runtime.Monitors.FirstOrDefault(m => m.Id == widget.MonitorId) ?? _runtime.Monitors.First();
            var scale = monitor.DpiScale; var area = monitor.PixelWorkArea;
            var width = Math.Max(1, Math.Min(360 * scale, area.Width - 24));
            _shell.Measure(new global::Windows.Foundation.Size(width / scale, double.PositiveInfinity));
            var height = Math.Max(1, Math.Min(Math.Max(320, Math.Ceiling(_shell.DesiredSize.Height)) * scale, area.Height - 24));
            if (anchor is not null)
            {
                AppWindow.MoveAndResize(PlaceByButton(anchor.ButtonBounds, area, (int)width, (int)height, (int)Math.Round(8 * scale)));
            }
            else
            {
                var x = monitor.PixelBounds.X + (widget.Bounds.X + widget.Bounds.Width) * scale + 12;
                if (x + width > area.X + area.Width) x = monitor.PixelBounds.X + widget.Bounds.X * scale - width - 12;
                var y = monitor.PixelBounds.Y + widget.Bounds.Y * scale;
                AppWindow.MoveAndResize(new RectInt32((int)Math.Clamp(x, area.X, area.X + Math.Max(0, area.Width - width)),
                    (int)Math.Clamp(y, area.Y, area.Y + Math.Max(0, area.Height - height)), (int)width, (int)height));
            }
            _entrance.Prepare();
            _visible = true;
            AppWindow.Show();
            Activate();
            SetPopupTopmost(_hwnd, _openedFromCenter);
        }
        finally { _presenting = false; }
    }

    internal static RectInt32 PlaceByButton(System.Drawing.Rectangle button, LayoutRect area, int width, int height, int gap)
    {
        var right = area.X + area.Width;
        var bottom = area.Y + area.Height;
        var x = button.Right - width;
        var y = button.Bottom + gap;
        if (y + height > bottom)
        {
            y = button.Top - height - gap;
            if (y < area.Y)
            {
                // Leave the date button clear when the card is too tall to fit above or below it.
                if (button.Right + gap + width <= right) x = button.Right + gap;
                else if (button.Left - gap - width >= area.X) x = button.Left - gap - width;
                y = button.Top;
            }
        }
        return new((int)Math.Clamp(x, area.X, Math.Max(area.X, right - width)),
            (int)Math.Clamp(y, area.Y, Math.Max(area.Y, bottom - height)), width, height);
    }

    private WidgetSurfaceAppearance ResolveAppearance(DesktopWidget widget)
    {
        var inherited = _runtime.State.Boxes.FirstOrDefault()?.Appearance ?? _runtime.State.Settings.DefaultBoxAppearance;
        var settings = _runtime.State.Settings.Appearance;
        var inCenter = widget.Placement == WidgetPlacement.Center;
        var centerTheme = _runtime.IsWidgetCenterDarkTheme ? WidgetColorTheme.Dark : WidgetColorTheme.Light;
        return WidgetAppearance.Resolve(widget, inherited, WidgetAppearance.UsesAcrylic(widget, settings.UseAcrylicBoxes, settings.WidgetAcrylic),
            widgetAcrylic: settings.WidgetAcrylic, forceSolid: inCenter, colorTheme: inCenter ? centerTheme : null);
    }

    private void ApplyAppearance(WidgetSurfaceAppearance appearance)
    {
        var settings = _runtime.State.Settings.Appearance;
        var color = Converters.HexColor.TryParse(appearance.Background, out var parsed) ? parsed : global::Windows.UI.Color.FromArgb(255, 42, 45, 50);
        var dark = color.R * .299 + color.G * .587 + color.B * .114 < 150;
        _shell.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        color.A = appearance.Acrylic ? (byte)Math.Round(255 * appearance.Opacity) : (byte)255;
        _shell.Background = new SolidColorBrush(color);
        if (Converters.HexColor.TryParse(appearance.Accent, out var accent))
        {
            _save.Background = new SolidColorBrush(accent);
            _save.Foreground = new SolidColorBrush(accent.R * .299 + accent.G * .587 + accent.B * .114 < 150 ? Colors.White : Colors.Black);
        }
        else
        {
            _save.ClearValue(Control.BackgroundProperty);
            _save.ClearValue(Control.ForegroundProperty);
        }
        var rounded = settings.ResolveWidgetCornerRadius() > 0;
        _shell.CornerRadius = new CornerRadius(rounded ? 8 : 0);
        SystemBackdrop = appearance.Acrylic ? new DesktopAcrylicBackdrop() : null;
        var nativeDark = dark ? 1 : 0; var corner = rounded ? 2 : 1; var noBorder = unchecked((int)0xFFFFFFFE);
        _ = DwmSetWindowAttribute(_hwnd, 20, ref nativeDark, 4);
        _ = DwmSetWindowAttribute(_hwnd, 33, ref corner, 4);
        _ = DwmSetWindowAttribute(_hwnd, 34, ref noBorder, 4);
    }

    private void SaveDates()
    {
        if (_widget is not { } widget || _task is not { } task) return;
        var start = ToDate(_start.Date); var end = ToDate(_end.Date);
        if (start > end) { ShowStatus("截止日期不能早于开始日期。", error: true); return; }
        _runtime.SetWidgetTaskDates(widget, task.Id, start, end); Dismiss();
    }

    private void ShowStatus(string text, bool error = false)
    {
        _status.Text = text; _status.Opacity = error ? 1 : .65;
        if (error) _status.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        else _status.ClearValue(TextBlock.ForegroundProperty);
    }

    private void Dismiss()
    {
        _visible = false; _start.IsCalendarOpen = _end.IsCalendarOpen = false;
        _save.Focus(FocusState.Programmatic); _ = SetFocus(IntPtr.Zero);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0297);
        AppWindow.Hide(); _widget = null; _task = null;
        _ = SetWindowLongPtr(_hwnd, -8, IntPtr.Zero);
        if (_openedFromCenter) SetPopupTopmost(_hwnd, false);
        _openedFromCenter = false;
        _entrance.Cancel();
    }

    private void RuntimeChanged(object? sender, EventArgs args)
    {
        if (_visible && (_widget is null || !_runtime.State.Widgets.Contains(_widget) || _task is null ||
            !_widget.Tasks.Contains(_task) || _runtime.IsPaused ||
            _openedFromCenter && (_widget.Placement != WidgetPlacement.Center || !_runtime.IsWidgetCenterVisible))) Dismiss();
    }

    internal static void SetPopupTopmost(IntPtr window, bool topmost) =>
        _ = SetWindowPos(window, new IntPtr(topmost ? -1 : -2), 0, 0, 0, 0, KeepPopupPositionAndFocus);

    private static DateTimeOffset? ToOffset(DateOnly? date) => date is { } value ? new DateTimeOffset(value.ToDateTime(TimeOnly.MinValue)) : null;
    private static DateOnly? ToDate(DateTimeOffset? date) => date is { } value ? DateOnly.FromDateTime(value.DateTime) : null;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
