using System.Drawing;
using CrabDesk.Core;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Input = System.Windows.Input;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

internal sealed class QuickActionsEditorPopup : ControlCenterPopup
{
    private readonly List<ControlCenterQuickAction> _selected;
    private readonly Action<IReadOnlyList<ControlCenterQuickAction>> _changed;
    private readonly Dictionary<ControlCenterQuickAction, ActionRow> _rows = new();
    private readonly WpfControls.StackPanel _list = new();
    private readonly WpfControls.TextBlock _count;
    private readonly WpfControls.Button _reset;
    private bool _updating;

    internal QuickActionsEditorPopup(IReadOnlyList<ControlCenterQuickAction> selected,
        Action<IReadOnlyList<ControlCenterQuickAction>> changed, bool dark, Color accent, double scale, Color? background = null,
        bool animationsEnabled = true)
        : base("编辑快捷操作", dark, accent, scale, 344, 430, animationsEnabled: animationsEnabled, background: background)
    {
        _selected = ControlQuickActions.Normalize(selected);
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        Body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        Body.RowDefinitions.Add(new() { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        var summary = new WpfControls.Grid { Height = 22, Margin = new Wpf.Thickness(2, 0, 2, 6) };
        var hint = TextLabel("选择 1–6 项，使用箭头调整顺序", 11);
        hint.Foreground = SecondaryInk;
        summary.Children.Add(hint);
        _count = TextLabel("", 11);
        _count.Foreground = SecondaryInk;
        _count.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        summary.Children.Add(_count);
        Body.Children.Add(summary);
        var scroll = ScrollContent(_list);
        WpfControls.Grid.SetRow(scroll, 1);
        Body.Children.Add(scroll);

        var checkboxStyle = (Wpf.Style)Wpf.Markup.XamlReader.Parse(CheckBoxStyle);
        foreach (var action in Enum.GetValues<ControlCenterQuickAction>()) AddRow(action, checkboxStyle);

        _reset = TextButton("恢复默认");
        _reset.Background = HoverInk;
        _reset.HorizontalAlignment = Wpf.HorizontalAlignment.Left;
        _reset.Click += (_, _) => RestoreDefaults();
        Footer.Children.Add(_reset);
        RefreshRows();
    }

    private void AddRow(ControlCenterQuickAction action, Wpf.Style style)
    {
        var name = ControlQuickActions.Label(action);
        var row = new WpfControls.Grid { Height = 32, Margin = new Wpf.Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(28) });
        row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(28) });
        var check = new WpfControls.CheckBox
        {
            Content = TextLabel(name), Style = style, Foreground = ForegroundInk,
            BorderBrush = TrackInk, VerticalContentAlignment = Wpf.VerticalAlignment.Center,
            HorizontalContentAlignment = Wpf.HorizontalAlignment.Stretch, Cursor = Input.Cursors.Hand
        };
        Automation.SetName(check, name);
        check.Checked += (_, _) => ChangeSelection(action, true);
        check.Unchecked += (_, _) => ChangeSelection(action, false);
        row.Children.Add(check);
        var up = IconButton(LucideRuntimeIcon.ArrowUp, "上移 " + name, 26);
        var down = IconButton(LucideRuntimeIcon.ArrowDown, "下移 " + name, 26);
        up.VerticalAlignment = down.VerticalAlignment = Wpf.VerticalAlignment.Center;
        up.Click += (_, _) => MoveAction(action, -1);
        down.Click += (_, _) => MoveAction(action, 1);
        WpfControls.Grid.SetColumn(up, 1); row.Children.Add(up);
        WpfControls.Grid.SetColumn(down, 2); row.Children.Add(down);
        _rows.Add(action, new(row, check, up, down));
    }

    private void ChangeSelection(ControlCenterQuickAction action, bool include)
    {
        if (_updating || IsClosing) return;
        if (include == _selected.Contains(action)) return;
        if (include && _selected.Count >= ControlQuickActions.MaximumCount)
        {
            RefreshRows();
            SetStatus("最多选择 6 项，请先取消其他项目。");
            return;
        }
        if (!include && _selected.Count == 1)
        {
            RefreshRows();
            SetStatus("请至少保留 1 项快捷操作。");
            return;
        }
        if (include) _selected.Add(action);
        else _selected.Remove(action);
        PublishChange();
    }

    private void MoveAction(ControlCenterQuickAction action, int direction)
    {
        if (IsClosing) return;
        var index = _selected.IndexOf(action);
        var next = index + direction;
        if (index < 0 || next < 0 || next >= _selected.Count) return;
        (_selected[index], _selected[next]) = (_selected[next], _selected[index]);
        PublishChange();
    }

    private void RestoreDefaults()
    {
        if (IsClosing || _selected.SequenceEqual(ControlQuickActions.Defaults)) return;
        _selected.Clear();
        _selected.AddRange(ControlQuickActions.Defaults);
        PublishChange();
    }

    private void PublishChange()
    {
        SetStatus("");
        RefreshRows();
        _changed(Array.AsReadOnly(_selected.ToArray()));
    }

    private void RefreshRows()
    {
        var focused = Input.Keyboard.FocusedElement as Wpf.FrameworkElement;
        _updating = true;
        try
        {
            _list.Children.Clear();
            foreach (var action in _selected.Concat(Enum.GetValues<ControlCenterQuickAction>().Where(action => !_selected.Contains(action))))
            {
                var row = _rows[action];
                var index = _selected.IndexOf(action);
                var selected = index >= 0;
                row.Check.IsChecked = selected;
                row.Up.Visibility = row.Down.Visibility = selected ? Wpf.Visibility.Visible : Wpf.Visibility.Hidden;
                row.Up.IsEnabled = index > 0;
                row.Down.IsEnabled = selected && index < _selected.Count - 1;
                Automation.SetItemStatus(row.Check, selected ? $"已选中，第 {index + 1} 项" : "未选中");
                _list.Children.Add(row.Container);
            }
            _count.Text = $"{_selected.Count} / {ControlQuickActions.MaximumCount}";
            _reset.IsEnabled = !_selected.SequenceEqual(ControlQuickActions.Defaults);
        }
        finally { _updating = false; }
        if (focused is { IsVisible: true, IsEnabled: true }) focused.Focus();
    }

    private sealed record ActionRow(WpfControls.Grid Container, WpfControls.CheckBox Check,
        WpfControls.Button Up, WpfControls.Button Down);

    private const string CheckBoxStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
          <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="CheckBox">
                <Border x:Name="Row" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="5" Padding="4,0">
                  <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="8"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <Border x:Name="Box" Width="18" Height="18" CornerRadius="4" BorderThickness="1" BorderBrush="{TemplateBinding BorderBrush}" VerticalAlignment="Center">
                      <Path x:Name="Tick" Data="M 3,8 L 6.5,11.5 L 13,4.5" Stroke="White" StrokeThickness="1.7" Visibility="Hidden"/>
                    </Border>
                    <ContentPresenter Grid.Column="2" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                  </Grid>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource MixerHover}"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource MixerAccent}"/></Trigger>
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="Box" Property="Background" Value="{DynamicResource MixerAccent}"/>
                    <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource MixerAccent}"/>
                    <Setter TargetName="Tick" Property="Visibility" Value="Visible"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Row" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;
}
