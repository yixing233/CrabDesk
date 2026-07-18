using CrabDesk.Runtime;
using CrabDesk.WinUI.Services;
using CrabDesk.WinUI.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace CrabDesk.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IBackdropService _backdropService;
    private readonly CrabDeskRuntime _runtime;
    private readonly IFilePickerService _filePickerService;
    private readonly IInfoBarService _infoBarService;
    private CancellationTokenSource? _infoBarDismissal;
    private Storyboard? _infoBarAnimation;
    private bool? _validationPaneOpen;
    private bool? _paneTransitionOpen;

    public MainWindow(
        IThemeService themeService,
        IDialogService dialogService,
        IBackdropService backdropService,
        IFilePickerService filePickerService,
        CrabDeskRuntime runtime,
        IInfoBarService infoBarService)
    {
        _themeService = themeService;
        _dialogService = dialogService;
        _backdropService = backdropService;
        _filePickerService = filePickerService;
        _runtime = runtime;
        _infoBarService = infoBarService;
        InitializeComponent();
        _infoBarService.Requested += OnInfoBarRequested;

        Title = "CrabDesk 设置";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1040, 720));
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnAppWindowClosing;
        var backdrop = Enum.TryParse<BackdropKind>(_runtime.State.Settings.WindowBackdrop, true, out var configuredBackdrop)
            ? configuredBackdrop
            : BackdropKind.Mica;
        _backdropService.Apply(this, backdrop);

        RootGrid.Loaded += OnRootLoaded;
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    private void OnRootLoaded(object sender, RoutedEventArgs eventArgs)
    {
        _themeService.RegisterRoot(RootGrid);
        _dialogService.RegisterXamlRoot(RootGrid.XamlRoot);
        _filePickerService.RegisterWindow(this);
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        UpdateNavigationMode(AppWindow.Size.Width / scale);
        UpdateContentFrameLayout();
        ApplyValidationPaneState();
        if (ContentFrame.CurrentSourcePageType is null)
        {
            Navigate(typeof(GeneralPage));
        }
    }

    private void OnInfoBarRequested(object? sender, InfoBarNotification notification)
    {
        DispatcherQueue.TryEnqueue(() => ShowInfoBar(notification));
    }

    private void ShowInfoBar(InfoBarNotification notification)
    {
        _infoBarDismissal?.Cancel();
        _infoBarDismissal?.Dispose();
        _infoBarDismissal = new CancellationTokenSource();
        _infoBarAnimation?.Stop();
        GlobalInfoBarHost.Visibility = Visibility.Visible;
        GlobalInfoBar.Message = notification.Message;
        GlobalInfoBar.Severity = notification.Severity;
        GlobalInfoBar.IsOpen = true;
        AnimateInfoBar(32, 0, 0, 1);

        var duration = notification.Duration ??
            (notification.Severity == InfoBarSeverity.Error
                ? TimeSpan.FromSeconds(6)
                : TimeSpan.FromSeconds(4));
        _ = DismissInfoBarAsync(duration, _infoBarDismissal.Token);
    }

    private async Task DismissInfoBarAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    CloseInfoBar();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void GlobalInfoBar_OnClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        _infoBarDismissal?.Cancel();
        GlobalInfoBarHost.Visibility = Visibility.Collapsed;
        GlobalInfoBarHost.Opacity = 0;
    }

    private void CloseInfoBar()
    {
        if (!GlobalInfoBar.IsOpen)
        {
            return;
        }

        _infoBarAnimation?.Stop();
        AnimateInfoBar(0, 32, 1, 0, () => GlobalInfoBar.IsOpen = false);
    }

    private void AnimateInfoBar(
        double fromX,
        double toX,
        double fromOpacity,
        double toOpacity,
        Action? completed = null)
    {
        _infoBarAnimation?.Stop();
        GlobalInfoBarTransform.X = fromX;
        GlobalInfoBarHost.Opacity = fromOpacity;
        var storyboard = new Storyboard();
        var slide = new DoubleAnimation
        {
            From = fromX,
            To = toX,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true
        };
        var fade = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(180))
        };
        Storyboard.SetTarget(slide, GlobalInfoBarTransform);
        Storyboard.SetTargetProperty(slide, "X");
        Storyboard.SetTarget(fade, GlobalInfoBarHost);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        if (completed is not null)
        {
            storyboard.Completed += (_, _) => completed();
        }
        _infoBarAnimation = storyboard;
        storyboard.Begin();
    }

    private void Navigation_OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }
        Navigate(tag switch
        {
            "hotkeys" => typeof(HotkeysPage),
            "backup" => typeof(BackupPage),
            "organization" => typeof(OrganizationPage),
            "appearance" => typeof(AppearancePage),
            "boxes" => typeof(BoxesPage),
            "about" => typeof(AboutPage),
            _ => typeof(GeneralPage)
        });
    }

    private void Navigation_OnPaneChanged(NavigationView sender, object args) =>
        CompletePaneTransition();

    private void Navigation_OnPaneOpening(NavigationView sender, object args)
    {
        _paneTransitionOpen = true;
        UpdateContentFrameLayout();
    }

    private void Navigation_OnPaneClosing(
        NavigationView sender,
        NavigationViewPaneClosingEventArgs args)
    {
        _paneTransitionOpen = false;
        UpdateContentFrameLayout();
    }

    private void CompletePaneTransition()
    {
        _paneTransitionOpen = null;
        UpdateContentFrameLayout();
    }

    private void Navigation_OnSizeChanged(object sender, SizeChangedEventArgs args) =>
        UpdateContentFrameLayout();

    private void Navigate(Type pageType)
    {
        var animationsEnabled = _runtime.State.Settings.Appearance.AnimationEnabled &&
                                new UISettings().AnimationsEnabled;
        NavigationTransitionInfo transition = animationsEnabled
            ? new EntranceNavigationTransitionInfo()
            : new SuppressNavigationTransitionInfo();
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, transition);
        }
    }

    internal void NavigateTo(string tag)
    {
        var item = Navigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            Navigation.SelectedItem = item;
        }
    }

    internal void ApplyValidationViewport(double width, double height, double scale)
    {
        if (width <= 0 || height <= 0 || scale <= 0)
        {
            return;
        }
        RootGrid.Width = width;
        RootGrid.Height = height;
        RootGrid.HorizontalAlignment = HorizontalAlignment.Left;
        RootGrid.VerticalAlignment = VerticalAlignment.Top;
        RootGrid.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        UpdateNavigationMode(width);
        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(height * scale)));
    }

    internal void ApplyValidationPane(string? paneState)
    {
        if (string.Equals(paneState, "closed", StringComparison.OrdinalIgnoreCase))
        {
            _validationPaneOpen = false;
        }
        else if (string.Equals(paneState, "open", StringComparison.OrdinalIgnoreCase))
        {
            _validationPaneOpen = true;
        }
        if (RootGrid.IsLoaded)
        {
            DispatcherQueue.TryEnqueue(ApplyValidationPaneState);
        }
    }

    private void ApplyValidationPaneState()
    {
        if (_validationPaneOpen is not bool isOpen)
        {
            return;
        }
        Navigation.IsPaneOpen = isOpen;
        UpdateContentFrameLayout();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        var minimumWidth = (int)Math.Ceiling(760 * scale);
        var minimumHeight = (int)Math.Ceiling(520 * scale);
        var size = sender.Size;
        var effectiveWidth = Math.Max(size.Width, minimumWidth);
        UpdateNavigationMode(effectiveWidth / scale);
        if (size.Width < minimumWidth || size.Height < minimumHeight)
        {
            sender.Resize(new SizeInt32(Math.Max(size.Width, minimumWidth), Math.Max(size.Height, minimumHeight)));
        }
    }

    private void UpdateNavigationMode(double logicalWidth)
    {
        Navigation.PaneDisplayMode = logicalWidth < 860
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;
        UpdateContentFrameLayout();
    }

    private void UpdateContentFrameLayout()
    {
        var paneIsOpen = _paneTransitionOpen ?? Navigation.IsPaneOpen;
        var usesExpandedPane = Navigation.PaneDisplayMode == NavigationViewPaneDisplayMode.Left &&
                               paneIsOpen;
        var paneWidth = usesExpandedPane ? Navigation.OpenPaneLength : Navigation.CompactPaneLength;
        var navigationWidth = Navigation.ActualWidth;
        if (navigationWidth <= 0)
        {
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
            navigationWidth = AppWindow.Size.Width / scale;
        }
        var pageMaxWidth = (double)Application.Current.Resources["PageMaxWidth"];
        var availableWidth = Math.Max(0, navigationWidth - paneWidth);
        var targetMaxWidth = usesExpandedPane ? pageMaxWidth : double.PositiveInfinity;
        var targetWidth = usesExpandedPane ? Math.Min(pageMaxWidth, availableWidth) : availableWidth;
        var targetAlignment = usesExpandedPane
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Left;
        if (ContentFrame.MaxWidth != targetMaxWidth)
        {
            ContentFrame.MaxWidth = targetMaxWidth;
        }
        if (Math.Abs(ContentFrame.Width - targetWidth) > 0.5)
        {
            ContentFrame.Width = targetWidth;
        }
        if (ContentFrame.HorizontalAlignment != targetAlignment)
        {
            ContentFrame.HorizontalAlignment = targetAlignment;
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_runtime.State.Settings.DesktopBehavior.LaunchToTray)
        {
            sender.Hide();
        }
        else
        {
            App.CurrentApp.Shutdown();
        }
    }
}
