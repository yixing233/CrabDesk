using System;
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

    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _ownerHandle;

    private DesktopConfirmWindow(
        IntPtr ownerHandle,
        string title,
        string message,
        string primaryText,
        bool isDark)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyPalette(isDark);
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
        AppWindow.Closing += (_, _) => _ = _completion.TrySetResult(false);
        Closed += (_, _) => _ = _completion.TrySetResult(false);
        _ownerHandle = ownerHandle;
    }

    internal static Task<bool> ShowAsync(
        IntPtr ownerHandle,
        string title,
        string message,
        string primaryText,
        bool isDark)
    {
        var window = new DesktopConfirmWindow(ownerHandle, title, message, primaryText, isDark);
        window.Activate();
        window.ConfigureWindow();
        return window._completion.Task;
    }

    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var ownerDpi = GetDpiForWindow(_ownerHandle);
        var dpi = ownerDpi != 0 ? (int)ownerDpi : 96;
        var scale = dpi / 96.0;
        var width = (int)Math.Round(DialogWidthDip * scale);
        var height = (int)Math.Round(DialogHeightDip * scale);
        AppWindow.Resize(new SizeInt32(width, height));

        var rootOwner = GetAncestor(_ownerHandle, 2);
        if (rootOwner != IntPtr.Zero)
        {
            _ = SetWindowLongPtr(hwnd, GwlpHwndParent, rootOwner);
        }

        var monitor = MonitorFromWindow(_ownerHandle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var work = info.WorkArea;
            var x = work.Left + (work.Right - work.Left - width) / 2;
            var y = work.Top + (work.Bottom - work.Top - height) / 2;
            AppWindow.Move(new PointInt32(x, y));
        }

        var corner = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
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
        if (!_completion.TrySetResult(accepted))
        {
            return;
        }
        try
        {
            Close();
        }
        catch
        {
        }
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
