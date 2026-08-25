using System.Runtime.InteropServices;
using CrabDesk.WinUI.Services;
using CrabDesk.WinUI.ViewModels;
using CrabDesk.WinUI.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace CrabDesk.WinUI.Windows;

public sealed partial class AiOrganizationWindow : Window
{
    private const double InitialWidthDips = 1260d;
    private const double InitialHeightDips = 820d;
    private const double MinimumWidthDips = 900d;
    private const double MinimumHeightDips = 600d;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const int GwlWndProc = -4;

    private readonly DialogService _dialogService = new();
    private readonly InfoBarService _infoBarService = new();
    private readonly AiClassificationViewModel _viewModel;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _notificationTimer;
    private WindowProcDelegate? _windowProcDelegate;
    private IntPtr _originalWndProc;
    private bool _enforcingMinimumSize;
    private bool _disposed;

    public AiOrganizationWindow()
    {
        InitializeComponent();
        Title = "AI 整理 - CrabDesk";
        AppWindow.IsShownInSwitchers = true;
        ConfigureWindowIcon();
        InstallMinimumSizeTracking();
        AppWindow.Changed += AppWindow_OnChanged;

        RootGrid.RequestedTheme = App.GetService<IThemeService>().CurrentMode switch
        {
            CrabDesk.Core.ApplicationThemeMode.Light => ElementTheme.Light,
            CrabDesk.Core.ApplicationThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        var backdropService = App.GetService<IBackdropService>();
        backdropService.Apply(this, backdropService.Current);

        _viewModel = new AiClassificationViewModel(
            App.GetService<ICrabDeskService>(),
            _infoBarService,
            _dialogService,
            App.GetService<DesktopItemIconSourceFactory>());
        WorkbenchHost.Content = new AiClassificationPage(_viewModel);

        _notificationTimer = DispatcherQueue.CreateTimer();
        _notificationTimer.IsRepeating = false;
        _notificationTimer.Tick += NotificationTimer_OnTick;
        _infoBarService.Requested += InfoBarService_OnRequested;
        RootGrid.Loaded += RootGrid_OnLoaded;
        Closed += Window_OnClosed;

        ConfigureInitialBounds();
    }

    private void RootGrid_OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        _dialogService.RegisterXamlRoot(RootGrid.XamlRoot, WindowNative.GetWindowHandle(this));
    }

    private void InfoBarService_OnRequested(object? sender, InfoBarNotification notification)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            NotificationBar.Message = notification.Message;
            NotificationBar.Severity = notification.Severity;
            NotificationBar.IsOpen = true;
            _notificationTimer.Stop();
            _notificationTimer.Interval = notification.Duration ?? TimeSpan.FromSeconds(5);
            _notificationTimer.Start();
        });
    }

    private void NotificationTimer_OnTick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        NotificationBar.IsOpen = false;
    }

    private void ConfigureInitialBounds()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = hwnd == IntPtr.Zero ? 96u : GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        var width = (int)Math.Round(InitialWidthDips * scale);
        var height = (int)Math.Round(InitialHeightDips * scale);
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);
        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width,
            height));
    }

    private void Window_OnClosed(object sender, WindowEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notificationTimer.Stop();
        _notificationTimer.Tick -= NotificationTimer_OnTick;
        _infoBarService.Requested -= InfoBarService_OnRequested;
        AppWindow.Changed -= AppWindow_OnChanged;
        RootGrid.Loaded -= RootGrid_OnLoaded;
        WorkbenchHost.Content = null;
        _viewModel.Dispose();
    }

    private void ConfigureWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CrabDesk.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private void AppWindow_OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _enforcingMinimumSize)
        {
            return;
        }

        var scale = GetWindowScale();
        var minimumWidth = (int)Math.Ceiling(MinimumWidthDips * scale);
        var minimumHeight = (int)Math.Ceiling(MinimumHeightDips * scale);
        var size = sender.Size;
        if (size.Width >= minimumWidth && size.Height >= minimumHeight)
        {
            return;
        }

        _enforcingMinimumSize = true;
        try
        {
            sender.Resize(new SizeInt32(
                Math.Max(size.Width, minimumWidth),
                Math.Max(size.Height, minimumHeight)));
        }
        finally
        {
            _enforcingMinimumSize = false;
        }
    }

    private void InstallMinimumSizeTracking()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        _windowProcDelegate = WindowProc;
        _originalWndProc = SetWindowLongPtr(
            hwnd,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProcDelegate));
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmGetMinMaxInfo)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var scale = GetWindowScale(hwnd);
            info.MinTrackSize = new NativePoint(
                (int)Math.Ceiling(MinimumWidthDips * scale),
                (int)Math.Ceiling(MinimumHeightDips * scale));
            Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }

        return CallWindowProc(_originalWndProc, hwnd, message, wParam, lParam);
    }

    private double GetWindowScale() => GetWindowScale(WindowNative.GetWindowHandle(this));

    private static double GetWindowScale(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 96u : GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96d : 1d;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newProc);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(
        IntPtr previousProc,
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcDelegate(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
