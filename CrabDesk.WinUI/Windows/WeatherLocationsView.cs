using CrabDesk.Core;
using CrabDesk.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Windows;

public sealed class WeatherLocationsView : UserControl
{
    public AutoSuggestBox Search { get; } = new() { Header = "添加位置", PlaceholderText = "输入城市名，按 Enter 搜索", QueryIcon = new SymbolIcon(Symbol.Find), TextMemberPath = nameof(WeatherLocation.Name) };
    public ListView Saved { get; } = new() { DisplayMemberPath = nameof(WidgetWeatherCity.DisplayName), SelectionMode = ListViewSelectionMode.Single, MaxHeight = 220, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    public TextBlock Status { get; } = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = .75, Visibility = Visibility.Collapsed };
    public Button Locate { get; } = new() { Content = "使用当前位置", HorizontalAlignment = HorizontalAlignment.Stretch };
    public HyperlinkButton MoreSettings { get; } = new() { Content = "更多位置设置", Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
    public Button CloseButton { get; } = new() { Content = new TablerIcon { Icon = TablerIconName.X, FontSize = 14 },
        Width = 32, Height = 32, MinWidth = 32, MinHeight = 32, Padding = new Thickness(0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _heading = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private string[] _labels = [];

    public WeatherLocationsView()
    {
        var content = new StackPanel { Width = 280, Spacing = 12 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "位置管理", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(CloseButton); Grid.SetColumn(CloseButton, 1);
        content.Children.Add(header);
        content.Children.Add(Search); content.Children.Add(Status); content.Children.Add(_heading); content.Children.Add(Saved);
        content.Children.Add(Locate); content.Children.Add(MoreSettings); Content = content;
        AutomationProperties.SetName(Search, "搜索并添加天气位置");
        AutomationProperties.SetName(Saved, "已保存的天气位置");
        AutomationProperties.SetName(CloseButton, "关闭位置管理");
        ToolTipService.SetToolTip(CloseButton, "关闭");
    }

    public void Update(DesktopWidget widget, ElementTheme theme)
    {
        RequestedTheme = theme;
        var labels = widget.Cities.Select(city => city.DisplayName).ToArray();
        if (!Saved.Items.OfType<WidgetWeatherCity>().SequenceEqual(widget.Cities) || !_labels.SequenceEqual(labels))
        { Saved.ItemsSource = widget.Cities.ToArray(); _labels = labels; }
        Saved.SelectedItem = widget.ActiveCity;
        _heading.Text = widget.Cities.Count == 0 ? "尚未添加位置" : $"已保存位置 · {widget.Cities.Count}";
        Locate.IsEnabled = !widget.IsLocating;
        Locate.Content = widget.IsLocating ? "正在定位…" : "使用当前位置";
    }

    public void SetStatus(string text)
    {
        Status.Text = text;
        Status.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
