using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

internal sealed class ConnectionDetailsPopup : ControlCenterPopup
{
    private readonly ConnectionDetailsController _controller;
    private readonly Action<string> _openSettings;
    private readonly Forms.Timer _refresh = new() { Interval = 5000 };
    private readonly WpfControls.StackPanel _items = new();
    private readonly Dictionary<string, ConnectionRow> _rows = new(StringComparer.Ordinal);
    private readonly WpfControls.TextBlock _empty;
    private readonly WpfControls.TextBlock _summary, _passwordTitle;
    private readonly WpfControls.Button _radio, _scan, _settings, _connect;
    private readonly WpfControls.StackPanel _passwordPanel;
    private readonly WpfControls.PasswordBox _password;
    private string? _passwordNetwork;
    private bool _commandPending;
    private bool? _radioState;
    private string _settingsLabel = "";

    private sealed record ConnectionRow(WpfControls.Border Root, WpfControls.TextBlock Glyph,
        WpfControls.TextBlock Name, WpfControls.TextBlock Detail, WpfControls.Button Button, WpfControls.TextBlock Action)
    {
        public ControlConnectionItem? Item { get; set; }
    }

    private bool CommandBusy => _commandPending || _controller.Busy && !_controller.Refreshing;

    internal ConnectionDetailsPopup(ConnectionDetailsController controller, bool dark, Color accent, double scale, Action<string> openSettings)
        : base(controller.Kind == ControlConnectionKind.WiFi ? "Wi-Fi" : "蓝牙", dark, accent, scale, 344, 408,
            controller.Kind == ControlConnectionKind.WiFi ? LucideRuntimeIcon.Wifi : LucideRuntimeIcon.Bluetooth)
    {
        _controller = controller;
        _openSettings = openSettings;
        Body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        Body.RowDefinitions.Add(new() { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        Body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        var header = new WpfControls.Grid { Height = 32, Margin = new Wpf.Thickness(0, 0, 0, 6) };
        _summary = TextLabel("正在读取…", 11); _summary.Foreground = SecondaryInk;
        _summary.Margin = new Wpf.Thickness(0, 0, 84, 0);
        header.Children.Add(_summary);
        _radio = TextButton("开启", "切换" + Text + "开关", LucideRuntimeIcon.Power);
        _radio.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        _radio.Click += async (_, _) =>
        {
            var enabled = _controller.Snapshot.Enabled is not true;
            await RunCommandAsync(async () =>
            {
                var result = await _controller.SetEnabledAsync(enabled);
                if (!IsClosing && result.Succeeded && enabled && _controller.Kind == ControlConnectionKind.WiFi)
                    await _controller.RefreshAsync(scan: true);
            });
        };
        header.Children.Add(_radio);
        Body.Children.Add(header);
        var scroll = ScrollContent(_items);
        WpfControls.Grid.SetRow(scroll, 1); Body.Children.Add(scroll);
        _empty = TextLabel("", 12);
        _empty.Foreground = SecondaryInk;
        _empty.Margin = new Wpf.Thickness(6, 16, 6, 16);

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
        var cancel = IconButton(LucideRuntimeIcon.X, "取消输入密码");
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
            icon: controller.Kind == ControlConnectionKind.WiFi ? LucideRuntimeIcon.Cog : LucideRuntimeIcon.Plus);
        _settings.HorizontalAlignment = Wpf.HorizontalAlignment.Left;
        _settings.Click += (_, _) => OpenSettings(_controller.Snapshot.SettingsUri ?? DefaultSettings);
        footer.Children.Add(_settings);
        _scan = TextButton("刷新", "刷新" + Text + "列表", LucideRuntimeIcon.RefreshCw);
        _scan.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        _scan.Click += async (_, _) => await _controller.RefreshAsync(scan: true);
        footer.Children.Add(_scan);
        Footer.Children.Add(footer);
        _controller.Changed += OnChanged;
        _refresh.Tick += async (_, _) => { if (!CommandBusy) await _controller.RefreshAsync(); };
        Shown += async (_, _) => { _refresh.Start(); await _controller.RefreshAsync(scan: true); };
        ApplySnapshot();
    }

    private string DefaultSettings => _controller.Kind == ControlConnectionKind.WiFi ? "ms-settings:network-wifi" : "ms-settings:bluetooth";
    private void OnChanged() => Post(ApplySnapshot);

    internal void ApplySnapshot()
    {
        var snapshot = _controller.Snapshot;
        var busy = CommandBusy;
        _summary.Text = busy ? "正在更新连接…" : !_controller.HasSnapshot && _controller.Busy ? "正在读取…" : snapshot.Enabled switch
        {
            true => snapshot.Items.Any(item => item.Connected) ? $"已连接 {snapshot.Items.Count(item => item.Connected)} 个" : "已开启",
            false => "已关闭", _ => "暂不可用"
        };
        if (_radioState != snapshot.Enabled)
        {
            _radio.Content = ButtonContent(snapshot.Enabled is true ? "关闭" : "开启", LucideRuntimeIcon.Power);
            _radioState = snapshot.Enabled;
        }
        _radio.Width = 68;
        _radio.Background = snapshot.Enabled is true ? HoverInk : null;
        _radio.IsEnabled = snapshot.Enabled is not null && !busy;
        Automation.SetName(_radio, (snapshot.Enabled is true ? "关闭 " : "开启 ") + Text);
        _scan.IsEnabled = !_controller.Busy && !busy;
        _password.IsEnabled = !busy;
        _connect.IsEnabled = !busy && _password.Password.Length > 0;
        var settingsLabel = snapshot.SettingsUri == "ms-settings:privacy-location" ? "位置权限" :
            _controller.Kind == ControlConnectionKind.WiFi ? "网络设置" : "添加 / 管理设备";
        if (_settingsLabel != settingsLabel)
        {
            _settings.Content = ButtonContent(settingsLabel, snapshot.SettingsUri == "ms-settings:privacy-location"
                ? LucideRuntimeIcon.MapPin : _controller.Kind == ControlConnectionKind.WiFi ? LucideRuntimeIcon.Cog : LucideRuntimeIcon.Plus);
            _settingsLabel = settingsLabel;
        }
        Automation.SetName(_settings, settingsLabel);
        UpdateRows(snapshot.Items, busy);
        if (snapshot.Items.Count == 0)
        {
            _empty.Text = !_controller.HasSnapshot && _controller.Busy ? "正在读取连接信息…" :
                _controller.Kind == ControlConnectionKind.WiFi ? "暂无可用网络" : "暂无已配对设备";
            if (!_items.Children.Contains(_empty)) _items.Children.Add(_empty);
        }
        if (_passwordNetwork is not null && !snapshot.Items.Any(item => item.Id == _passwordNetwork && !item.Connected)) HidePassword();
        SetStatus(string.IsNullOrEmpty(_controller.Error) ? snapshot.Status : _controller.Error);
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
        row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(25) });
        row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = Wpf.GridLength.Auto });
        var glyph = Glyph(LucideRuntimeIcon.Bluetooth, 17);
        row.Children.Add(glyph);
        var text = new WpfControls.StackPanel { VerticalAlignment = Wpf.VerticalAlignment.Center, Margin = new Wpf.Thickness(2, 0, 6, 0) };
        var name = TextLabel("", 12);
        text.Children.Add(name);
        var detail = TextLabel("", 10);
        detail.Foreground = SecondaryInk;
        text.Children.Add(detail);
        WpfControls.Grid.SetColumn(text, 1); row.Children.Add(text);
        var button = TextButton("");
        WpfControls.Grid.SetColumn(button, 2); row.Children.Add(button);
        var root = new WpfControls.Border { Child = row, CornerRadius = new Wpf.CornerRadius(6),
            Padding = new Wpf.Thickness(5, 3, 3, 3), Margin = new Wpf.Thickness(0, 0, 0, 3) };
        var entry = new ConnectionRow(root, glyph, name, detail, button, (WpfControls.TextBlock)button.Content);
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
        var icon = _controller.Kind == ControlConnectionKind.WiFi ? LucideRuntimeIcon.Wifi : item.DeviceKind switch
        {
            ControlConnectionDeviceKind.Computer => LucideRuntimeIcon.Monitor,
            ControlConnectionDeviceKind.Phone => LucideRuntimeIcon.Smartphone,
            ControlConnectionDeviceKind.Audio => LucideRuntimeIcon.Headphones,
            ControlConnectionDeviceKind.Mouse => LucideRuntimeIcon.Mouse,
            ControlConnectionDeviceKind.Keyboard or ControlConnectionDeviceKind.Input => LucideRuntimeIcon.Keyboard,
            _ => LucideRuntimeIcon.Bluetooth
        };
        row.Glyph.Text = LucideRuntimeIcons.GetGlyph(icon);
        row.Glyph.Foreground = item.Connected ? AccentInk : SecondaryInk;
        row.Name.Text = item.Name; row.Name.ToolTip = item.Name;
        row.Name.FontWeight = item.Connected ? Wpf.FontWeights.SemiBold : Wpf.FontWeights.Normal;
        row.Detail.Text = item.Detail + (item.SignalPercent is { } signal ? $" · {signal}%" : "");
        row.Detail.ToolTip = row.Detail.Text;
        var label = item.Connected && item.CanDisconnect ? "断开" : item.CanConnect ? "连接" : "管理";
        row.Action.Text = label;
        Automation.SetName(row.Button, label + " " + item.Name);
        row.Root.Background = item.Connected ? HoverInk : null;
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
