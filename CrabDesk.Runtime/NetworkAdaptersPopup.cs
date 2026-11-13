using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

internal sealed class NetworkAdaptersPopup : ControlCenterPopup
{
    private readonly INetworkAdapterControl _source;
    private readonly Forms.Timer _refresh = new() { Interval = 5000 };
    private readonly WpfControls.StackPanel _items = new();
    private readonly WpfControls.TextBlock _summary, _empty;
    private readonly WpfControls.Button _scan, _settings;
    private readonly Dictionary<Guid, AdapterRow> _rows = new();
    private IReadOnlyList<ControlNetworkAdapter> _snapshot = [];
    private bool _busy, _changing, _hasSnapshot;

    private sealed record AdapterRow(WpfControls.Border Root, WpfControls.TextBlock Icon, WpfControls.TextBlock Name,
        WpfControls.TextBlock Detail, WpfControls.Button Button)
    {
        public ControlNetworkAdapter? Adapter { get; set; }
    }

    protected override bool CanDismissOnDeactivate => !_changing;
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;

    internal NetworkAdaptersPopup(INetworkAdapterControl source, bool dark, Color accent, double scale,
        Action<string> openSettings, bool animationsEnabled = true, Color? background = null)
        : base("网卡", dark, accent, scale, 360, 408, TablerRuntimeIcon.Network,
            detailCard: true, animationsEnabled: animationsEnabled, background: background, skeleton: PopupSkeletonLayout.NetworkAdapters)
    {
        _source = source;
        Body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        Body.RowDefinitions.Add(new() { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        _summary = TextLabel("正在读取网卡…", 12);
        _summary.Foreground = SecondaryInk;
        _summary.Margin = new Wpf.Thickness(0, 0, 0, 12);
        Body.Children.Add(_summary);
        var scroll = ScrollContent(_items);
        scroll.Padding = new Wpf.Thickness(0);
        scroll.ClipToBounds = false;
        scroll.Template = (WpfControls.ControlTemplate)Wpf.Markup.XamlReader.Parse(AdapterScrollTemplate);
        WpfControls.Grid.SetRow(scroll, 1);
        Body.Children.Add(scroll);
        _empty = TextLabel("未找到可用网卡", 12);
        _empty.Foreground = SecondaryInk;
        _empty.Margin = new Wpf.Thickness(2, 10, 2, 10);

        var footer = new WpfControls.Grid();
        _settings = TextButton("网络设置", icon: TablerRuntimeIcon.Cog);
        _settings.HorizontalAlignment = Wpf.HorizontalAlignment.Left;
        _settings.Click += (_, _) => { Dismiss(); openSettings("ms-settings:network-advancedsettings"); };
        footer.Children.Add(_settings);
        _scan = TextButton("刷新", "刷新网卡列表", TablerRuntimeIcon.RefreshCw);
        _scan.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        _scan.Click += (_, _) => _ = RefreshAsync();
        footer.Children.Add(_scan);
        Footer.Children.Add(footer);
        _refresh.Tick += (_, _) => _ = RefreshAsync();
        Shown += (_, _) => { _refresh.Start(); _ = RefreshAsync(); };
        ApplySnapshot();
    }

    internal Task RefreshAsync() => IsClosing || _busy || _changing ? PendingOperation : PendingOperation = RunAsync();

    private async Task RunAsync(Guid? id = null, bool enabled = false)
    {
        _busy = true;
        _changing |= id.HasValue;
        ApplySnapshot();
        var snapshot = _snapshot;
        var hasSnapshot = _hasSnapshot;
        var error = "";
        try
        {
            await Task.Run(async () =>
            {
                ControlOperationResult? result = null;
                if (id is { } target)
                {
                    result = await _source.SetEnabledAsync(target, enabled).ConfigureAwait(false);
                    if (!result.Succeeded) error = result.Error;
                }
                snapshot = _source.Read();
                hasSnapshot = true;
                if (id is { } changed && result?.Succeeded is true)
                {
                    for (var attempt = 0; attempt < 8 && snapshot.FirstOrDefault(adapter => adapter.Id == changed)?.Enabled != enabled; attempt++)
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                        snapshot = _source.Read();
                    }
                    if (snapshot.FirstOrDefault(adapter => adapter.Id == changed)?.Enabled != enabled)
                        error = "网卡状态尚未更新，请刷新后重试。";
                }
            }).ConfigureAwait(false);
        }
        catch (Exception exception) { error = "无法读取或更改网卡：" + exception.Message; }
        if (ContentView.Dispatcher.HasShutdownStarted) return;
        await ContentView.Dispatcher.InvokeAsync(() =>
        {
            if (IsClosing || IsDisposed) return;
            _snapshot = snapshot;
            _hasSnapshot = hasSnapshot;
            _busy = false;
            if (id.HasValue) _changing = false;
            ApplySnapshot();
            SetStatus(error);
            Loading!.ShowContent();
        });
    }

    private void ApplySnapshot()
    {
        _summary.Text = _changing ? "正在更改网卡…" : _hasSnapshot
            ? $"{_snapshot.Count} 张网卡 · {_snapshot.Count(adapter => adapter.Enabled is true)} 张已启用" :
            _busy ? "正在读取网卡…" : "网卡";
        _scan.IsEnabled = _settings.IsEnabled = !_changing;
        var ids = _snapshot.Select(adapter => adapter.Id).ToHashSet();
        foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _items.Children.Remove(_rows[id].Root);
            _rows.Remove(id);
        }
        if (_snapshot.Count > 0) _items.Children.Remove(_empty);
        for (var index = 0; index < _snapshot.Count; index++)
        {
            var adapter = _snapshot[index];
            if (!_rows.TryGetValue(adapter.Id, out var row)) _rows.Add(adapter.Id, row = CreateRow());
            row.Button.IsEnabled = !_changing && adapter.Enabled is not null;
            if (row.Adapter != adapter)
            {
                var updateButton = row.Adapter is null || row.Adapter.Enabled != adapter.Enabled;
                row.Adapter = adapter;
                row.Icon.Text = TablerRuntimeIcons.GetGlyph(adapter.Wireless ? TablerRuntimeIcon.Wifi : TablerRuntimeIcon.Network);
                row.Icon.Foreground = adapter.Enabled is true ? AccentInk : SecondaryInk;
                row.Name.Text = adapter.Name;
                row.Name.ToolTip = adapter.Description;
                row.Detail.Text = adapter.Enabled switch
                {
                    true => adapter.Connected ? "已启用 · 已连接" : "已启用 · 未连接",
                    false => "已禁用", _ => "不可用"
                };
                if (updateButton)
                    row.Button.Content = ButtonContent(adapter.Enabled is true ? "禁用" : "启用", TablerRuntimeIcon.Power);
                Automation.SetName(row.Button, (adapter.Enabled is true ? "禁用 " : "启用 ") + adapter.Name);
            }
            if (index < _items.Children.Count && ReferenceEquals(_items.Children[index], row.Root)) continue;
            _items.Children.Remove(row.Root);
            _items.Children.Insert(index, row.Root);
        }
        if (_snapshot.Count == 0 && _hasSnapshot && !_items.Children.Contains(_empty)) _items.Children.Add(_empty);
    }

    private AdapterRow CreateRow()
    {
        var grid = new WpfControls.Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(26) });
        grid.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(68) });
        var icon = Glyph(TablerRuntimeIcon.Network, 18);
        grid.Children.Add(icon);
        var labels = new WpfControls.StackPanel { Margin = new Wpf.Thickness(8, 0, 8, 0), VerticalAlignment = Wpf.VerticalAlignment.Center };
        var name = TextLabel("", 13);
        var detail = TextLabel("", 11);
        detail.Foreground = SecondaryInk;
        detail.Margin = new Wpf.Thickness(0, 3, 0, 0);
        labels.Children.Add(name);
        labels.Children.Add(detail);
        WpfControls.Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        var button = TextButton("");
        button.Height = 30;
        WpfControls.Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        var root = new WpfControls.Border { Child = grid, CornerRadius = new Wpf.CornerRadius(8), Background = HoverInk,
            Padding = new Wpf.Thickness(8), Margin = new Wpf.Thickness(0, 0, 0, 8) };
        var row = new AdapterRow(root, icon, name, detail, button);
        button.Click += (_, _) =>
        {
            if (IsClosing || _changing || row.Adapter is not { Enabled: { } enabled } adapter) return;
            if (_busy)
            {
                _changing = true;
                ApplySnapshot();
                PendingOperation = ChangeAfterRefreshAsync(PendingOperation, adapter.Id, !enabled);
            }
            else PendingOperation = RunAsync(adapter.Id, !enabled);
        };
        return row;
    }

    private async Task ChangeAfterRefreshAsync(Task refresh, Guid id, bool enabled)
    {
        await refresh.ConfigureAwait(false);
        if (ContentView.Dispatcher.HasShutdownStarted) return;
        var command = await ContentView.Dispatcher.InvokeAsync(() => IsClosing ? Task.CompletedTask : RunAsync(id, enabled));
        await command.ConfigureAwait(false);
    }

    private const string AdapterScrollTemplate = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollViewer">
          <Grid>
            <ScrollContentPresenter x:Name="PART_ScrollContentPresenter"
                                    Content="{TemplateBinding Content}"
                                    ContentTemplate="{TemplateBinding ContentTemplate}"
                                    CanContentScroll="{TemplateBinding CanContentScroll}"/>
            <ScrollBar x:Name="PART_VerticalScrollBar" Orientation="Vertical"
                       HorizontalAlignment="Right" MinWidth="0" Width="8" MaxWidth="8" Margin="0,0,-12,0"
                       Minimum="0" Maximum="{TemplateBinding ScrollableHeight}"
                       ViewportSize="{TemplateBinding ViewportHeight}"
                       Value="{Binding VerticalOffset, RelativeSource={RelativeSource TemplatedParent}, Mode=OneWay}"
                       Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
          </Grid>
        </ControlTemplate>
        """;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refresh.Dispose();
        base.Dispose(disposing);
    }
}
