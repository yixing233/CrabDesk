using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;
using WinRT.Interop;

namespace CrabDesk.WinUI.Windows;

public sealed partial class FileShelfWindow : Window
{
    private readonly CrabDeskRuntime _runtime;
    public DesktopWidget Widget { get; }
    private readonly IntPtr _hwnd;
    private readonly Grid _frame = (Grid)XamlReader.Load("""
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"/>
        """);
    private readonly Border _shell = (Border)XamlReader.Load("""
        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" CornerRadius="12" BorderThickness="1"
            BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"/>
        """);
    private readonly Grid _panel = new() { Padding = new Thickness(14), RowSpacing = 12 };
    private readonly Grid _dragHandle = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), ColumnSpacing = 8 };
    private readonly TextBlock _heading = new() { FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _count = new() { FontSize = 11, Opacity = .65 };
    private readonly TextBlock _status = new() { FontSize = 11, Opacity = .8, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
    private readonly ComboBox _mode = new() { MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _add = new() { Content = "添加文件", MinWidth = 0 };
    private readonly ToggleButton _pin = new() { Content = new LucideIcon { Icon = LucideIconName.Pin, FontSize = 15 }, Width = 30, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0) };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended, CanDragItems = true, AllowDrop = false, IsItemClickEnabled = false, Padding = new Thickness(0) };
    private readonly Grid _empty = new();
    private readonly HyperlinkButton _emptyAdd = new() { Content = "添加文件", HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _dropHint = (Border)XamlReader.Load("""
        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" IsHitTestVisible="False" Visibility="Collapsed" CornerRadius="10" BorderThickness="2"
            Background="{ThemeResource SolidBackgroundFillColorSecondaryBrush}" BorderBrush="{ThemeResource SystemControlHighlightAccentBrush}"/>
        """);
    private readonly TextBlock _dropText = new() { TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 16 };
    private readonly Button _tab = new() { MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(0), Visibility = Visibility.Collapsed };
    private readonly WidgetColorIcon _tabIcon = new() { AssetName = "file-shelf", Width = 20, Height = 20 };
    private readonly ObservableCollection<FileShelfRow> _rows = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _iconGate = new(4);
    private FileShelfNativeDropTarget? _nativeDrop;
    private readonly DispatcherQueueTimer _timer;
    private bool _updating, _closed, _collapsed, _outgoingDrag, _incomingDrag, _menuOpen;
    private int _interactionDepth;
    private long _outsideSince, _insideSince, _lastAvailability;
    private LayoutRect _expanded;
    private FileShelfDockEdge _edge;
    private LayoutRect? _lastSavedBounds;
    internal bool IsCollapsed => _collapsed;

    public FileShelfWindow(CrabDeskRuntime runtime, DesktopWidget widget)
    {
        _runtime = runtime; Widget = widget;
        Title = "文件中转站 - CrabDesk";
        _hwnd = WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false;
            presenter.IsMinimizable = false; presenter.IsMaximizable = false; presenter.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        var style = GetWindowLongPtr(_hwnd, -16).ToInt64();
        SetWindowLongPtr(_hwnd, -16, new IntPtr(style & ~0x00400000L));
        // Hiding from switchers alone does not exclude this HWND from Show Desktop.
        var extendedStyle = GetWindowLongPtr(_hwnd, -20).ToInt64();
        SetWindowLongPtr(_hwnd, -20, new IntPtr((extendedStyle | 0x00000080L) & ~0x00040000L));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
        DesktopPopupWindow.Track(_hwnd);
        BuildContent();
        _shell.RenderTransform = _shellOffset;
        var surface = new Canvas(); surface.Children.Add(_shell);
        _frame.Children.Add(surface); Content = _frame;
        AppWindow.Changed += (_, args) => { if (args.DidSizeChange) UpdateShape(); };
        _frame.SizeChanged += (_, _) => { UpdateMotionContent(_surfaceBounds); UpdateShape(); };
        _shell.Loaded += (_, _) => _nativeDrop ??= new FileShelfNativeDropTarget(_hwnd,
            (_, source) => !_closed && !Widget.FileShelfBusy && !_outgoingDrag && source != Widget.Id.ToString("D"),
            NativeDropHover, NativeDropFiles);
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += Tick;
        runtime.Changed += RuntimeChanged;
        Closed += (_, _) =>
        {
            _closed = true; _timer.Stop(); StopMotion(); runtime.Changed -= RuntimeChanged; _nativeDrop?.Dispose();
            _lifetime.Cancel(); _lifetime.Dispose();
        };
        Refresh(); RestorePosition();
    }

    public void Present()
    {
        AppWindow.Show(false);
        _timer.Start();
    }

    private void BuildContent()
    {
        for (var index = 0; index < 4; index++) _panel.RowDefinitions.Add(new() { Height = index == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _dragHandle.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); _dragHandle.ColumnDefinitions.Add(new());
        _dragHandle.Children.Add(new WidgetColorIcon { AssetName = "file-shelf", Width = 28, Height = 28 });
        var titles = new StackPanel { Spacing = 2 }; titles.Children.Add(_heading); titles.Children.Add(_count);
        Grid.SetColumn(titles, 1); _dragHandle.Children.Add(titles); header.Children.Add(_dragHandle);
        AttachDragHandle(_dragHandle);
        ToolTipService.SetToolTip(_dragHandle, "拖动调整位置，靠近屏幕边缘可自动收起");
        header.Children.Add(_pin); Grid.SetColumn(_pin, 1);
        ToolTipService.SetToolTip(_pin, "保持展开"); AutomationProperties.SetName(_pin, "保持中转站展开");
        _pin.Click += (_, _) => { if (!_updating) _runtime.SetFileShelfFloating(Widget, true, _pin.IsChecked != true); };
        var close = new Button { Content = new LucideIcon { Icon = LucideIconName.X, FontSize = 15 }, Width = 30, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0) };
        close.Style = (Style)Application.Current.Resources["SubtleButtonStyle"];
        ToolTipService.SetToolTip(close, "关闭全局悬浮"); AutomationProperties.SetName(close, "关闭全局文件中转站");
        close.Click += (_, _) => _runtime.SetFileShelfFloating(Widget, false, Widget.FileShelfFloating.AutoHide);
        header.Children.Add(close); Grid.SetColumn(close, 2); _panel.Children.Add(header);
        var toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new()); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _mode.Items.Add("引用原文件"); _mode.Items.Add("复制到中转站");
        AutomationProperties.SetName(_mode, "新文件的收集方式");
        _mode.SelectionChanged += (_, _) => { if (!_updating && _mode.SelectedIndex >= 0) _runtime.SetFileShelfMode(Widget, _mode.SelectedIndex == 1 ? WidgetFileShelfMode.Copy : WidgetFileShelfMode.Reference); };
        _mode.DropDownOpened += (_, _) => _menuOpen = true; _mode.DropDownClosed += (_, _) => { _menuOpen = false; _outsideSince = 0; };
        _add.Click += async (_, _) => await PickFilesAsync(); toolbar.Children.Add(_mode); toolbar.Children.Add(_add); Grid.SetColumn(_add, 1);
        Grid.SetRow(toolbar, 1); _panel.Children.Add(toolbar);
        var body = new Grid(); Grid.SetRow(body, 2); _panel.Children.Add(body);
        _list.ItemsSource = _rows;
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid Padding="2,6" ColumnSpacing="10" Opacity="{Binding Opacity}">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="28"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <Grid Width="28" Height="28">
                        <FontIcon Glyph="&#xE8A5;" FontSize="24" Visibility="{Binding FallbackVisibility}"/>
                        <Image Source="{Binding Icon}" Stretch="Uniform"/>
                    </Grid>
                    <StackPanel Grid.Column="1" Spacing="3">
                        <TextBlock Text="{Binding Name}" FontSize="13" TextTrimming="CharacterEllipsis"/>
                        <TextBlock Text="{Binding Detail}" FontSize="11" Opacity="0.65" TextTrimming="CharacterEllipsis"/>
                    </StackPanel>
                </Grid>
            </DataTemplate>
            """);
        _list.ItemContainerStyle = new Style(typeof(ListViewItem));
        _list.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _list.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
        _list.SelectionChanged += (_, _) => UpdateStatus();
        _list.DoubleTapped += async (_, args) => { if (RowAt(args.OriginalSource) is { } row) { args.Handled = true; await OpenAsync(row.Item, false); } };
        _list.ContextRequested += ShowFileMenu;
        _list.DragItemsStarting += StartFileDrag;
        _list.DragItemsCompleted += (_, _) => { _outgoingDrag = false; _outsideSince = 0; };
        body.Children.Add(_list);
        var emptyContent = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        emptyContent.Children.Add(new WidgetColorIcon { AssetName = "file-shelf", Width = 40, Height = 40 });
        emptyContent.Children.Add(new TextBlock { Text = "拖入文件，随时中转", FontSize = 15, TextAlignment = TextAlignment.Center });
        emptyContent.Children.Add(_emptyAdd);
        _empty.Children.Add(emptyContent); _emptyAdd.Click += async (_, _) => await PickFilesAsync(); body.Children.Add(_empty);
        _dropHint.Child = _dropText; body.Children.Add(_dropHint);
        Grid.SetRow(_status, 3); _panel.Children.Add(_status);
        _panel.RenderTransform = _panelOffset; _tab.RenderTransform = _tabOffset;
        var surface = new Canvas(); surface.Children.Add(_panel); surface.Children.Add(_tab); _shell.Child = surface;
        _tabIcon.RenderTransform = _tabIconOffset;
        _tab.Content = _tabIcon;
        // The frame supplies the surface; the icon button only handles input and focus.
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled",
            "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" })
            _tab.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _tab.Click += (_, _) => Expand(); AttachDragHandle(_tab);
        AutomationProperties.SetName(_tab, "展开文件中转站，也可拖入文件");
        _shell.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Escape && !_menuOpen && _interactionDepth == 0 && _edge != FileShelfDockEdge.None) { Collapse(); args.Handled = true; }
            else if (args.Key == VirtualKey.Delete && _list.SelectedItems.Count > 0) { args.Handled = true; await RemoveSelectedAsync(); }
            else if (args.Key == VirtualKey.Enter && _list.SelectedItem is FileShelfRow row) { args.Handled = true; await OpenAsync(row.Item, false); }
        };
    }

    private void RuntimeChanged(object? sender, EventArgs args)
    {
        if (_closed) return;
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(() => RuntimeChanged(sender, args)); return; }
        if (!_runtime.State.Widgets.Contains(Widget) || !Widget.FileShelfFloating.Enabled) { Close(); return; }
        Refresh();
        if (_lastSavedBounds != Widget.FileShelfFloating.Bounds) RestorePosition();
        if (!Widget.FileShelfFloating.AutoHide) Expand();
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            _heading.Text = Widget.DisplayTitle; _count.Text = $"{Widget.FileShelfItems.Count} 个文件 · 全局悬浮";
            _mode.SelectedIndex = Widget.FileShelfMode == WidgetFileShelfMode.Copy ? 1 : 0;
            _mode.IsEnabled = _add.IsEnabled = _emptyAdd.IsEnabled = !Widget.FileShelfBusy;
            _pin.IsChecked = !Widget.FileShelfFloating.AutoHide;
            _frame.RequestedTheme = _runtime.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
            ToolTipService.SetToolTip(_tab, Widget.DisplayTitle);
            SyncRows(); UpdateStatus();
        }
        finally { _updating = false; }
    }

    private void UpdateStatus()
    {
        if (_closed) return;
        _status.Text = WidgetFileShelfPresentation.Footer(Widget, _list.SelectedItems.Count);
        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Report(string message)
    {
        if (_closed) return;
        Widget.FileShelfStatus = message; UpdateStatus();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
