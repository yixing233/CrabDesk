using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

internal sealed class DisplayControlsPopup : ControlCenterPopup
{
    private readonly IDisplayControl _source;
    private readonly Dictionary<ControlDisplayMode, ModeRow> _modes = new();
    private readonly WpfControls.TextBlock _current, _count, _singleScreenHint;
    private readonly WpfControls.Button _cast, _settings;
    private ControlDisplaySnapshot _snapshot = new(null, 0);
    private bool _hasSnapshot, _busy;

    internal event Action<string>? ExternalActionFailed;
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;

    internal DisplayControlsPopup(IDisplayControl source, bool dark, Color accent, double scale)
        : base("投影", dark, accent, scale, 344, 330)
    {
        _source = source;
        var stack = new WpfControls.StackPanel();
        var summary = new WpfControls.Grid { Height = 22, Margin = new Wpf.Thickness(2, 0, 2, 8) };
        _current = TextLabel("正在读取显示配置…", 11);
        _count = TextLabel("", 11); _count.Foreground = SecondaryInk;
        _count.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        summary.Children.Add(_current); summary.Children.Add(_count);
        stack.Children.Add(summary);

        AddMode(stack, ControlDisplayMode.Internal, "仅主屏", "只在主屏幕上显示", LucideRuntimeIcon.Monitor);
        AddMode(stack, ControlDisplayMode.Clone, "复制", "两个屏幕显示相同内容", LucideRuntimeIcon.Copy);
        AddMode(stack, ControlDisplayMode.Extend, "扩展", "将桌面扩展到第二个屏幕", LucideRuntimeIcon.ArrowRight);
        AddMode(stack, ControlDisplayMode.External, "仅第二屏", "只在第二个屏幕上显示", LucideRuntimeIcon.Monitor);
        _singleScreenHint = TextLabel("连接第二台屏幕后可使用其他投影模式。", 10);
        _singleScreenHint.Foreground = SecondaryInk;
        _singleScreenHint.Margin = new Wpf.Thickness(2, 5, 2, 0);
        _singleScreenHint.TextWrapping = Wpf.TextWrapping.Wrap;
        _singleScreenHint.Visibility = Wpf.Visibility.Collapsed;
        stack.Children.Add(_singleScreenHint);
        Body.Children.Add(ScrollContent(stack));

        var actions = new WpfControls.Grid();
        actions.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(8) });
        actions.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        _cast = TextButton("无线投屏", "连接无线显示器");
        _settings = TextButton("显示设置", "打开显示设置");
        _cast.Background = HoverInk; _settings.Background = HoverInk;
        _cast.Click += (_, _) => PendingOperation = OpenExternalAsync(true);
        _settings.Click += (_, _) => PendingOperation = OpenExternalAsync(false);
        actions.Children.Add(_cast); WpfControls.Grid.SetColumn(_settings, 2); actions.Children.Add(_settings);
        Footer.Children.Add(actions);
        UpdateButtons();
        Shown += (_, _) => PendingOperation = RefreshAsync();
    }

    private void AddMode(WpfControls.StackPanel parent, ControlDisplayMode mode, string title,
        string description, LucideRuntimeIcon icon)
    {
        var content = new WpfControls.Grid();
        content.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(28) });
        content.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(22) });
        var symbol = Glyph(icon, 17); symbol.HorizontalAlignment = Wpf.HorizontalAlignment.Left;
        content.Children.Add(symbol);
        var labels = new WpfControls.StackPanel { VerticalAlignment = Wpf.VerticalAlignment.Center };
        var name = TextLabel(title, 12);
        var detail = TextLabel(description, 10); detail.Foreground = SecondaryInk;
        labels.Children.Add(name); labels.Children.Add(detail);
        WpfControls.Grid.SetColumn(labels, 1); content.Children.Add(labels);
        var check = Glyph(LucideRuntimeIcon.Check, 16); check.Foreground = AccentInk;
        check.Visibility = Wpf.Visibility.Hidden;
        WpfControls.Grid.SetColumn(check, 2); content.Children.Add(check);
        var button = TextButton(title);
        button.Height = 42;
        button.Margin = new Wpf.Thickness(0, 0, 0, 4);
        button.Content = content;
        button.Tag = mode;
        button.SizeChanged += (_, _) => content.Width = Math.Max(0, button.ActualWidth - 16);
        button.Click += (_, _) => PendingOperation = ChangeModeAsync(mode);
        Automation.SetHelpText(button, description);
        _modes.Add(mode, new(button, name, symbol, check));
        parent.Children.Add(button);
    }

    internal void ApplySnapshot(ControlDisplaySnapshot snapshot)
    {
        if (IsClosing) return;
        _snapshot = snapshot;
        _hasSnapshot = true;
        _current.Text = "当前：" + (snapshot.Mode is { } mode ? ModeName(mode) : "未知");
        _count.Text = snapshot.DisplayCount > 0 ? $"已连接 {snapshot.DisplayCount} 台屏幕" : "未检测到屏幕";
        _singleScreenHint.Visibility = snapshot.DisplayCount < 2 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        SetStatus("");
        UpdateButtons();
    }

    internal async Task RefreshAsync()
    {
        if (_busy || IsClosing) return;
        SetBusy(true, "正在读取显示配置…");
        try
        {
            var snapshot = await Task.Run(_source.Read);
            if (!IsClosing) ApplySnapshot(snapshot);
        }
        catch (Exception error)
        {
            if (!IsClosing) ReadFailed("读取显示配置失败：" + error.Message);
        }
        finally { if (!IsClosing) SetBusy(false); }
    }

    private async Task ChangeModeAsync(ControlDisplayMode mode)
    {
        if (_busy || IsClosing || !_hasSnapshot || !_modes.ContainsKey(mode) ||
            (_snapshot.DisplayCount < 2 && mode != ControlDisplayMode.Internal) || mode == _snapshot.Mode) return;
        SetBusy(true, "正在切换到" + ModeName(mode) + "…");
        string failure = "";
        try
        {
            try
            {
                var result = await Task.Run(() => _source.SetMode(mode));
                if (!result.Succeeded) failure = string.IsNullOrWhiteSpace(result.Error) ? "无法切换投影模式。" : result.Error;
            }
            catch (Exception error) { failure = "切换投影模式失败：" + error.Message; }
            if (IsClosing) return;
            try
            {
                var snapshot = await Task.Run(_source.Read);
                if (IsClosing) return;
                ApplySnapshot(snapshot);
                SetStatus(failure);
            }
            catch (Exception error)
            {
                if (!IsClosing) ReadFailed((failure.Length > 0 ? failure + " " : "") + "读取显示配置失败：" + error.Message);
            }
        }
        finally { if (!IsClosing) SetBusy(false); }
    }

    private async Task OpenExternalAsync(bool cast)
    {
        if (_busy || IsClosing) return;
        // Capture the error observer before closing; the host can report failure
        // after the shell action without trying to update this disposed card.
        var failed = ExternalActionFailed;
        Func<ControlOperationResult> open = cast ? _source.OpenCast : _source.OpenSettings;
        Dismiss();
        ControlOperationResult result;
        try { result = await Task.Run(open); }
        catch (Exception error) { result = new(false, error.Message); }
        if (!result.Succeeded) failed?.Invoke(string.IsNullOrWhiteSpace(result.Error)
            ? cast ? "无法打开无线投屏。" : "无法打开显示设置。"
            : result.Error);
    }

    private void ReadFailed(string message)
    {
        _hasSnapshot = false;
        _snapshot = new(null, 0);
        _current.Text = "无法读取显示配置";
        _count.Text = "";
        _singleScreenHint.Visibility = Wpf.Visibility.Collapsed;
        SetStatus(message);
        UpdateButtons();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (status is not null) SetStatus(status);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        foreach (var (mode, row) in _modes)
        {
            var selected = _hasSnapshot && _snapshot.Mode == mode;
            row.Button.IsEnabled = !_busy && _hasSnapshot && (_snapshot.DisplayCount >= 2 || mode == ControlDisplayMode.Internal);
            if (selected) row.Button.Background = HoverInk;
            else row.Button.ClearValue(WpfControls.Control.BackgroundProperty);
            row.Title.Foreground = row.Icon.Foreground = selected ? AccentInk : ForegroundInk;
            row.Check.Visibility = selected ? Wpf.Visibility.Visible : Wpf.Visibility.Hidden;
            Automation.SetItemStatus(row.Button, selected ? "已选中" : "");
        }
        _cast.IsEnabled = _settings.IsEnabled = !_busy;
    }

    private static string ModeName(ControlDisplayMode mode) => mode switch
    {
        ControlDisplayMode.Internal => "仅主屏",
        ControlDisplayMode.Clone => "复制",
        ControlDisplayMode.Extend => "扩展",
        ControlDisplayMode.External => "仅第二屏",
        _ => "未知"
    };

    private sealed record ModeRow(WpfControls.Button Button, WpfControls.TextBlock Title,
        WpfControls.TextBlock Icon, WpfControls.TextBlock Check);
}
