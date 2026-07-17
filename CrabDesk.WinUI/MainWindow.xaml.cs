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
    private bool? _validationPaneOpen;
    private bool? _paneTransitionOpen;

    public MainWindow(
        IThemeService themeService,
        IDialogService dialogService,
        IBackdropService backdropService,
        IFilePickerService filePickerService,
        CrabDeskRuntime runtime)
    {
        _themeService = themeService;
        _dialogService = dialogService;
        _backdropService = backdropService;
        _filePickerService = filePickerService;
        _runtime = runtime;
        InitializeComponent();

        Title = "CrabDesk 设置";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1040, 720));
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnAppWindowClosing;
        _backdropService.Apply(this, BackdropKind.Mica);

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
        var animationsEnabled = new UISettings().AnimationsEnabled;
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
