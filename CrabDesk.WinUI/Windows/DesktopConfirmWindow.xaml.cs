using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace CrabDesk.WinUI.Windows;

public sealed partial class DesktopConfirmWindow : Window
{
    private const int DialogWidthDip = 440;
    private const int DialogHeightDip = 208;
    private const int GwlpHwndParent = -8;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const uint MonitorDefaultToNearest = 2;

    // A single window serves every confirmation. Creating a WinUI window
    // stalls the UI thread the desktop surfaces share, so the dialog is built
    // once (see Prewarm) and hidden between requests with its content intact.
    private static DesktopConfirmWindow? _shared;

    private TaskCompletionSource<bool>? _pending;
    private long _presentStarted;
    private bool _presentedBefore;

    private DesktopConfirmWindow()
    {
        InitializeComponent();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        RootGrid.KeyDown += OnRootKeyDown;
        Activated += (_, _) => CancelButton.Focus(FocusState.Programmatic);
        // Alt+F4 or a stray close request dismisses the current confirmation
        // but keeps the window for the next one.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            Complete(false);
        };
        Closed += (_, _) =>
        {
            Resolve(false);
            if (ReferenceEquals(_shared, this))
            {
                _shared = null;
            }
        };
        var corner = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(
            WindowNative.GetWindowHandle(this),
            DwmwaWindowCornerPreference,
            ref corner,
            sizeof(int));
    }

    internal static Task<bool> ShowAsync(
        IntPtr ownerHandle,
        string title,
        string message,
        string primaryText,
        bool isDark)
    {
        return GetOrCreate().Present(ownerHandle, title, message, primaryText, isDark);
    }

    internal static void Prewarm()
    {
        try
        {
            _ = GetOrCreate();
        }
        catch (Exception exception)
        {
            AppDiagnostic.Error("Desktop confirmation window prewarm failed", exception);
        }
    }

    private static DesktopConfirmWindow GetOrCreate()
    {
        if (_shared is { } existing)
        {
            return existing;
        }
        var started = Stopwatch.GetTimestamp();
        _shared = new DesktopConfirmWindow();
        AppDiagnostic.Info(
            $"Desktop confirmation window created in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        return _shared;
    }

    private Task<bool> Present(
        IntPtr ownerHandle,
        string title,
        string message,
        string primaryText,
        bool isDark)
    {
        _presentStarted = Stopwatch.GetTimestamp();
        // A request arriving while the dialog is still up replaces it; the
        // earlier caller sees a cancel, as if the user had dismissed it.
        Resolve(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;

        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyPalette(isDark);
        PlaceOverOwner(ownerHandle);
        CompositionTarget.Rendered += OnFirstFrameRendered;
        AppWindow.Show();
        Activate();
        return completion.Task;
    }

    private void OnFirstFrameRendered(object? sender, RenderedEventArgs eventArgs)
    {
        CompositionTarget.Rendered -= OnFirstFrameRendered;
        AppDiagnostic.Info(
            $"Desktop confirmation presented reused={_presentedBefore} " +
            $"firstFrameMs={Stopwatch.GetElapsedTime(_presentStarted).TotalMilliseconds:0}");
        _presentedBefore = true;
    }

    private void PlaceOverOwner(IntPtr ownerHandle)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var rootOwner = ownerHandle != IntPtr.Zero ? GetAncestor(ownerHandle, 2) : IntPtr.Zero;
        if (rootOwner != IntPtr.Zero)
        {
            _ = SetWindowLongPtr(hwnd, GwlpHwndParent, rootOwner);
        }

        var anchor = ownerHandle != IntPtr.Zero ? ownerHandle : hwnd;
        var ownerDpi = GetDpiForWindow(anchor);
        var dpi = ownerDpi != 0 ? (int)ownerDpi : 96;
        var monitor = MonitorFromWindow(anchor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var work = info.WorkArea;
            AppWindow.MoveAndResize(CalculateBounds(work.Left, work.Top, work.Right, work.Bottom, dpi));
        }
        else
        {
            var fallback = CalculateBounds(0, 0, 0, 0, dpi);
            AppWindow.Resize(new SizeInt32(fallback.Width, fallback.Height));
        }
    }

    // Centres the dialog on the owner's work area, sized at the owner's DPI.
    internal static RectInt32 CalculateBounds(int workLeft, int workTop, int workRight, int workBottom, int dpi)
    {
        var scale = Math.Max(dpi, 1) / 96.0;
        var width = (int)Math.Round(DialogWidthDip * scale);
        var height = (int)Math.Round(DialogHeightDip * scale);
        return new RectInt32(
            workLeft + (workRight - workLeft - width) / 2,
            workTop + (workBottom - workTop - height) / 2,
            width,
            height);
    }

    private void ApplyPalette(bool isDark)
    {
        var background = isDark ? Argb(37, 40, 45) : Argb(255, 255, 255);
        var title = isDark ? Argb(242, 244, 247) : Argb(28, 32, 38);
        var body = isDark ? Argb(176, 182, 191) : Argb(92, 99, 108);
        var secondarySurface = isDark ? Argb(47, 51, 57) : Argb(255, 255, 255);
        var secondaryHover = isDark ? Argb(56, 60, 67) : Argb(244, 246, 248);
        var secondaryPressed = isDark ? Argb(64, 69, 76) : Argb(235, 238, 241);
        var secondaryText = isDark ? Argb(232, 235, 240) : Argb(36, 41, 47);
        var secondaryBorder = isDark ? Argb(72, 77, 85) : Argb(217, 221, 226);
        var danger = isDark ? Argb(219, 80, 86) : Argb(198, 45, 36);
        var dangerHover = isDark ? Argb(232, 94, 100) : Argb(180, 39, 31);
        var dangerPressed = isDark ? Argb(193, 63, 69) : Argb(152, 33, 26);
        var dangerTint = isDark ? Argb(44, 219, 80, 86) : Argb(30, 198, 45, 36);

        RootGrid.Background = Brush(background);
        SurfaceBorder.Background = Brush(background);
        BadgeFill.Fill = Brush(dangerTint);
        BadgeGlyph.Foreground = Brush(danger);
        TitleText.Foreground = Brush(title);
        MessageText.Foreground = Brush(body);

        CancelButton.Background = Brush(secondarySurface);
        CancelButton.Foreground = Brush(secondaryText);
        CancelButton.BorderBrush = Brush(secondaryBorder);
        SetButtonThemeColors(CancelButton, secondaryHover, secondaryPressed, secondaryText, secondaryBorder);

        PrimaryButton.Background = Brush(danger);
        PrimaryButton.Foreground = Brush(Argb(255, 255, 255));
        PrimaryButton.BorderBrush = Brush(danger);
        SetButtonThemeColors(PrimaryButton, dangerHover, dangerPressed, Argb(255, 255, 255), danger);
    }

    private static void SetButtonThemeColors(
        Button button,
        Color hoverBackground,
        Color pressedBackground,
        Color foreground,
        Color border)
    {
        button.Resources["ButtonBackgroundPointerOver"] = Brush(hoverBackground);
        button.Resources["ButtonBackgroundPressed"] = Brush(pressedBackground);
        button.Resources["ButtonForegroundPointerOver"] = Brush(foreground);
        button.Resources["ButtonForegroundPressed"] = Brush(foreground);
        button.Resources["ButtonBorderBrushPointerOver"] = Brush(border);
        button.Resources["ButtonBorderBrushPressed"] = Brush(border);
    }

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
    private static Color Argb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
    private static SolidColorBrush Brush(Color color) => new(color);

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        if (eventArgs.Key is VirtualKey.Escape or VirtualKey.Enter)
        {
            Complete(false);
            eventArgs.Handled = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs) => Complete(false);

    private void OnPrimaryClick(object sender, RoutedEventArgs eventArgs) => Complete(true);

    private void Complete(bool accepted)
    {
        if (!Resolve(accepted))
        {
            return;
        }
        try
        {
            AppWindow.Hide();
        }
        catch
        {
        }
    }

    private bool Resolve(bool accepted)
    {
        var pending = _pending;
        _pending = null;
        return pending?.TrySetResult(accepted) == true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RECT Monitor;
        public RECT WorkArea;
        public uint Flags;
    }
}
