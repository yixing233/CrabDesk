using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

internal sealed partial class ConnectionDetailsPopup : ControlCenterPopup
{
    private readonly ConnectionDetailsController _controller;
    private readonly Action<string> _openSettings;
    private readonly Forms.Timer _refresh = new() { Interval = 5000 };
    private readonly WpfControls.StackPanel _items = new();
    private readonly Dictionary<string, ConnectionRow> _rows = new(StringComparer.Ordinal);
    private readonly WpfControls.TextBlock _empty;
    private readonly WpfControls.TextBlock _summary, _passwordTitle;
    private readonly WpfControls.Primitives.ToggleButton _radio;
    private readonly WpfControls.Button _scan, _settings, _connect;
    private readonly WpfControls.StackPanel _passwordPanel;
    private readonly WpfControls.PasswordBox _password;
    private readonly Media.Brush _rowFill, _rowBorder, _connectedFill, _connectedBorder;
    private string? _passwordNetwork;
    private bool _commandPending, _updatingRadio;
    private bool? _requestedRadioState;
    private string _settingsLabel = "";

    private sealed record ConnectionRow(WpfControls.Border Root, WpfControls.Border IconSurface, WpfControls.TextBlock Glyph,
        WpfControls.TextBlock Name, WpfControls.TextBlock Detail, WpfControls.Button Button, WpfControls.TextBlock Action)
    {
        public ControlConnectionItem? Item { get; set; }
    }

    private bool CommandBusy => _commandPending || _controller.Busy && !_controller.Refreshing;

    internal ConnectionDetailsPopup(ConnectionDetailsController controller, bool dark, Color accent, double scale, Action<string> openSettings,
        bool animationsEnabled = true, Color? background = null)
        : base(controller.Kind == ControlConnectionKind.WiFi ? "Wi-Fi" : "蓝牙", dark, accent, scale, 344, 408,
            controller.Kind == ControlConnectionKind.WiFi ? TablerRuntimeIcon.Wifi : TablerRuntimeIcon.Bluetooth,
            detailCard: true, animationsEnabled: animationsEnabled, background: background, skeleton: PopupSkeletonLayout.Connections)
    {
        _controller = controller;
        _openSettings = openSettings;
        _rowFill = Brush(dark ? Color.FromArgb(10, 255, 255, 255) : Color.FromArgb(160, 255, 255, 255));
        _rowBorder = Brush(dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(14, 31, 35, 41));
        _connectedFill = Brush(Color.FromArgb(dark ? 42 : 22, accent));
        _connectedBorder = Brush(Color.FromArgb(dark ? 100 : 72, accent));
        Body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        Body.RowDefinitions.Add(new() { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        Body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        var header = new WpfControls.Grid { Height = 34, Margin = new Wpf.Thickness(0, 0, 0, 12) };
        _summary = TextLabel("正在读取…", 12); _summary.Foreground = SecondaryInk;
        _summary.Margin = new Wpf.Thickness(0, 0, 60, 0);
        header.Children.Add(_summary);
        _radio = CreateRadioSwitch(animationsEnabled);
        _radio.Checked += OnRadioToggled;
        _radio.Unchecked += OnRadioToggled;
        header.Children.Add(_radio);
        Body.Children.Add(header);
        var scroll = ScrollContent(_items);
        WpfControls.Grid.SetRow(scroll, 1); Body.Children.Add(scroll);
        // The default ScrollViewer template reserves the system scrollbar
        // width even though our indicator is narrow. Keep its gutter compact.
        scroll.Padding = new Wpf.Thickness(0);
        scroll.Resources[Wpf.SystemParameters.VerticalScrollBarWidthKey] = 8.0;
        var scrollbar = new Wpf.Style(typeof(WpfControls.Primitives.ScrollBar),
            (Wpf.Style)scroll.FindResource(typeof(WpfControls.Primitives.ScrollBar)));
        scrollbar.Setters.Add(new Wpf.Setter(Wpf.FrameworkElement.MarginProperty, new Wpf.Thickness(2, 0, 0, 0)));
        scroll.Resources[typeof(WpfControls.Primitives.ScrollBar)] = scrollbar;
        _empty = TextLabel("", 12);
        _empty.Foreground = SecondaryInk;
        _empty.Margin = new Wpf.Thickness(12, 24, 12, 24);
        _empty.TextWrapping = Wpf.TextWrapping.Wrap;
        _empty.TextAlignment = Wpf.TextAlignment.Center;

        _passwordPanel = new WpfControls.StackPanel { Margin = new Wpf.Thickness(0, 8, 0, 0), Visibility = Wpf.Visibility.Collapsed };
        _passwordTitle = TextLabel("网络密码", 11);
        _passwordTitle.Margin = new Wpf.Thickness(0, 0, 0, 5);
        _passwordPanel.Children.Add(_passwordTitle);
        var passwordRow = new WpfControls.Grid();
        passwordRow.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        passwordRow.ColumnDefinitions.Add(new() { Width = Wpf.GridLength.Auto });
        passwordRow.ColumnDefinitions.Add(new() { Width = Wpf.GridLength.Auto });
        _password = new WpfControls.PasswordBox { Height = 28, Padding = new Wpf.Thickness(6, 2, 6, 2),
            Background = HoverInk, Foreground = ForegroundInk, BorderBrush = TrackInk, CaretBrush = ForegroundInk };
        Automation.SetName(_password, "Wi-Fi 网络密码");
        passwordRow.Children.Add(_password);
        _connect = TextButton("连接", "使用密码连接网络");
        _connect.Margin = new Wpf.Thickness(4, 0, 0, 0);
        _connect.Click += async (_, _) => await RunCommandAsync(SubmitPasswordAsync);
        WpfControls.Grid.SetColumn(_connect, 1); passwordRow.Children.Add(_connect);
        var cancel = IconButton(TablerRuntimeIcon.X, "取消输入密码");
        cancel.Click += (_, _) => HidePassword();
        WpfControls.Grid.SetColumn(cancel, 2); passwordRow.Children.Add(cancel);
        _password.PasswordChanged += (_, _) => _connect.IsEnabled = !CommandBusy && _password.Password.Length > 0;
        _password.KeyDown += async (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter && _connect.IsEnabled) { args.Handled = true; await RunCommandAsync(SubmitPasswordAsync); }
        };
        _passwordPanel.Children.Add(passwordRow);
        WpfControls.Grid.SetRow(_passwordPanel, 2); Body.Children.Add(_passwordPanel);

        var footer = new WpfControls.Grid();
        _settings = TextButton(controller.Kind == ControlConnectionKind.WiFi ? "网络设置" : "添加 / 管理设备",
            icon: controller.Kind == ControlConnectionKind.WiFi ? TablerRuntimeIcon.Cog : TablerRuntimeIcon.Plus);
        _settings.HorizontalAlignment = Wpf.HorizontalAlignment.Left;
        _settings.Click += (_, _) => OpenSettings(_controller.Snapshot.SettingsUri ?? DefaultSettings);
        footer.Children.Add(_settings);
        _scan = TextButton("刷新", "刷新" + Text + "列表", TablerRuntimeIcon.RefreshCw);
        _scan.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        _scan.Click += async (_, _) => await _controller.RefreshAsync(scan: true);
        footer.Children.Add(_scan);
        Footer.Children.Add(new WpfControls.Border { Child = footer, BorderBrush = _rowBorder,
            BorderThickness = new Wpf.Thickness(0, 1, 0, 0), Padding = new Wpf.Thickness(0, 10, 0, 0) });
        _controller.Changed += OnChanged;
        _refresh.Tick += async (_, _) => { if (!CommandBusy) await _controller.RefreshAsync(); };
        Shown += async (_, _) => { _refresh.Start(); await _controller.RefreshAsync(scan: true); };
        ApplySnapshot();
    }

    private string DefaultSettings => _controller.Kind == ControlConnectionKind.WiFi ? "ms-settings:network-wifi" : "ms-settings:bluetooth";
    private void OnChanged() => Post(ApplySnapshot);

    private async void OnRadioToggled(object sender, Wpf.RoutedEventArgs args)
    {
        if (_updatingRadio || IsClosing) return;
        if (CommandBusy || _controller.Snapshot.Enabled is null) { ApplySnapshot(); return; }
        var enabled = _radio.IsChecked is true;
        _requestedRadioState = enabled;
        try
        {
            await RunCommandAsync(async () =>
            {
                var result = await _controller.SetEnabledAsync(enabled);
                if (!IsClosing && result.Succeeded && enabled && _controller.Kind == ControlConnectionKind.WiFi)
                    await _controller.RefreshAsync(scan: true);
            });
        }
        finally
        {
            _requestedRadioState = null;
            if (!IsClosing) ApplySnapshot();
        }
    }

    internal void ApplySnapshot()
    {
        var snapshot = _controller.Snapshot;
        var busy = CommandBusy;
        _summary.Text = busy ? "正在更新连接…" : !_controller.HasSnapshot && string.IsNullOrEmpty(_controller.Error) ? "正在读取…" : snapshot.Enabled switch
        {
            true => snapshot.Items.Any(item => item.Connected) ? $"已连接 {snapshot.Items.Count(item => item.Connected)} 个" : "已开启",
            false => "已关闭", _ => "暂不可用"
        };
        _updatingRadio = true;
        try { _radio.IsChecked = _requestedRadioState ?? (snapshot.Enabled is true); }
        finally { _updatingRadio = false; }
        _radio.IsEnabled = snapshot.Enabled is not null && !busy;
        _radio.ToolTip = snapshot.Enabled is null ? Text + "暂不可用" : (_radio.IsChecked is true ? "关闭 " : "开启 ") + Text;
        Automation.SetItemStatus(_radio, snapshot.Enabled is null ? "暂不可用" : _radio.IsChecked is true ? "已开启" : "已关闭");
        _scan.IsEnabled = !_controller.Busy && !busy;
        _password.IsEnabled = !busy;
        _connect.IsEnabled = !busy && _password.Password.Length > 0;
        var settingsLabel = snapshot.SettingsUri == "ms-settings:privacy-location" ? "位置权限" :
            _controller.Kind == ControlConnectionKind.WiFi ? "网络设置" : "添加 / 管理设备";
        if (_settingsLabel != settingsLabel)
        {
            _settings.Content = ButtonContent(settingsLabel, snapshot.SettingsUri == "ms-settings:privacy-location"
                ? TablerRuntimeIcon.MapPin : _controller.Kind == ControlConnectionKind.WiFi ? TablerRuntimeIcon.Cog : TablerRuntimeIcon.Plus);
            _settingsLabel = settingsLabel;
        }
        Automation.SetName(_settings, settingsLabel);
        UpdateRows(snapshot.Items, busy);
        if (snapshot.Items.Count == 0)
        {
            _empty.Text = !_controller.HasSnapshot && string.IsNullOrEmpty(_controller.Error) ? "正在读取连接信息…" :
                _controller.Kind == ControlConnectionKind.WiFi ? "暂无可用网络" : "暂无已配对设备";
            if (!_items.Children.Contains(_empty)) _items.Children.Add(_empty);
        }
        if (_passwordNetwork is not null && !snapshot.Items.Any(item => item.Id == _passwordNetwork && !item.Connected)) HidePassword();
        SetStatus(string.IsNullOrEmpty(_controller.Error) ? snapshot.Status : _controller.Error);
        if (_controller.HasSnapshot || !string.IsNullOrEmpty(_controller.Error)) Loading!.ShowContent();
    }

    private void UpdateRows(IReadOnlyList<ControlConnectionItem> items, bool busy)
    {
        var ids = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _items.Children.Remove(_rows[id].Root);
            _rows.Remove(id);
        }
        if (items.Count > 0) _items.Children.Remove(_empty);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!_rows.TryGetValue(item.Id, out var row)) _rows[item.Id] = row = CreateRow();
            UpdateRow(row, item, busy);
            if (index < _items.Children.Count && ReferenceEquals(_items.Children[index], row.Root)) continue;
            _items.Children.Remove(row.Root);
            _items.Children.Insert(index, row.Root);
        }
    }

    private ConnectionRow CreateRow()
    {
        var row = new WpfControls.Grid { MinHeight = 42 };
        row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(32) });
        row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = Wpf.GridLength.Auto });
        var glyph = Glyph(TablerRuntimeIcon.Bluetooth, 17);
        var iconSurface = new WpfControls.Border { Child = glyph, Width = 32, Height = 32,
            CornerRadius = new Wpf.CornerRadius(8), VerticalAlignment = Wpf.VerticalAlignment.Center };
        row.Children.Add(iconSurface);
        var text = new WpfControls.StackPanel { VerticalAlignment = Wpf.VerticalAlignment.Center, Margin = new Wpf.Thickness(10, 0, 6, 0) };
        var name = TextLabel("", 13);
        text.Children.Add(name);
        var detail = TextLabel("", 11);
        detail.Margin = new Wpf.Thickness(0, 3, 0, 0);
        detail.Foreground = SecondaryInk;
        text.Children.Add(detail);
        WpfControls.Grid.SetColumn(text, 1); row.Children.Add(text);
        var button = TextButton("");
        WpfControls.Grid.SetColumn(button, 2); row.Children.Add(button);
        button.Height = 30;
        var root = new WpfControls.Border { Child = row, CornerRadius = new Wpf.CornerRadius(10), BorderThickness = new Wpf.Thickness(1),
            Padding = new Wpf.Thickness(8, 7, 6, 7), Margin = new Wpf.Thickness(0, 0, 0, 8) };
        var entry = new ConnectionRow(root, iconSurface, glyph, name, detail, button, (WpfControls.TextBlock)button.Content);
        root.MouseEnter += (_, _) => { if (entry.Item?.Connected is not true) root.Background = HoverInk; };
        root.MouseLeave += (_, _) => root.Background = entry.Item?.Connected is true ? _connectedFill : _rowFill;
        button.Click += async (_, _) =>
        {
            if (entry.Item is not { } selected) return;
            var disconnect = selected.Connected && selected.CanDisconnect;
            var connect = selected.CanConnect;
            await RunCommandAsync(async () =>
            {
                if (entry.Item is not { } item || !_rows.TryGetValue(item.Id, out var current) || !ReferenceEquals(current, entry)) return;
                if (disconnect)
                {
                    if (item.Connected && item.CanDisconnect) await _controller.DisconnectAsync(item.Id);
                    return;
                }
                if (!connect) { OpenSettings(DefaultSettings); return; }
                if (item.Connected || !item.CanConnect) return;
                if (item.RequiresPassword) { ShowPassword(item); return; }
                var result = await _controller.ConnectAsync(item.Id, null);
                if (!IsClosing && result.PasswordRequired) ShowPassword(item);
            });
        };
        return entry;
    }

    private void UpdateRow(ConnectionRow row, ControlConnectionItem item, bool busy)
    {
        row.Button.IsEnabled = !busy;
        if (row.Item == item) return;
        row.Item = item;
        var icon = _controller.Kind == ControlConnectionKind.WiFi ? TablerRuntimeIcon.Wifi : item.DeviceKind switch
        {
            ControlConnectionDeviceKind.Computer => TablerRuntimeIcon.Monitor,
            ControlConnectionDeviceKind.Phone => TablerRuntimeIcon.Smartphone,
            ControlConnectionDeviceKind.Audio => TablerRuntimeIcon.Headphones,
            ControlConnectionDeviceKind.Mouse => TablerRuntimeIcon.Mouse,
            ControlConnectionDeviceKind.Keyboard or ControlConnectionDeviceKind.Input => TablerRuntimeIcon.Keyboard,
            _ => TablerRuntimeIcon.Bluetooth
        };
        row.Glyph.Text = TablerRuntimeIcons.GetGlyph(icon);
        row.Glyph.Foreground = item.Connected ? AccentInk : SecondaryInk;
        row.IconSurface.Background = item.Connected ? _connectedFill : _rowBorder;
        row.Name.Text = item.Name; row.Name.ToolTip = item.Name;
        row.Name.FontWeight = item.Connected ? Wpf.FontWeights.SemiBold : Wpf.FontWeights.Normal;
        row.Detail.Text = item.Detail + (item.SignalPercent is { } signal ? $" · {signal}%" : "");
        row.Detail.ToolTip = row.Detail.Text;
        var label = item.Connected && item.CanDisconnect ? "断开" : item.CanConnect ? "连接" : "管理";
        row.Action.Text = label;
        Automation.SetName(row.Button, label + " " + item.Name);
        row.Root.Background = item.Connected ? _connectedFill : row.Root.IsMouseOver ? HoverInk : _rowFill;
        row.Root.BorderBrush = item.Connected ? _connectedBorder : _rowBorder;
    }

    private async Task RunCommandAsync(Func<Task> command)
    {
        if (IsClosing || CommandBusy) return;
        _commandPending = true;
        ApplySnapshot();
        try
        {
            // Keep polling visually quiet while honoring a click made during a read.
            await _controller.WaitForRefreshAsync();
            if (!IsClosing) await command();
        }
        finally
        {
            _commandPending = false;
            if (!IsClosing) ApplySnapshot();
        }
    }

    private void ShowPassword(ControlConnectionItem item)
    {
        _passwordNetwork = item.Id;
        _passwordTitle.Text = item.Name + " · 网络密码";
        _password.Clear();
        _passwordPanel.Visibility = Wpf.Visibility.Visible;
        _password.Focus();
    }

    private async Task SubmitPasswordAsync()
    {
        if (_passwordNetwork is not { } id || _controller.Busy || _password.Password.Length == 0) return;
        var password = _password.Password;
        _password.Clear();
        var result = await _controller.ConnectAsync(id, password);
        if (!IsClosing && result.Succeeded) HidePassword();
    }

    private void HidePassword()
    {
        _passwordNetwork = null;
        _password.Clear();
        _passwordPanel.Visibility = Wpf.Visibility.Collapsed;
    }

    private void OpenSettings(string uri)
    {
        Dismiss();
        _openSettings(uri);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _controller.Changed -= OnChanged;
            _controller.Dispose();
            _password.Clear();
        }
        base.Dispose(disposing);
    }
}
