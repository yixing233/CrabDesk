using CrabDesk.Core;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace CrabDesk.WinUI.Windows;

public sealed class WeatherDetailsView : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 12, Padding = new Thickness(16, 16, 16, 16) };
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _updated = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly Border _divider = new() { Height = 1 };
    private readonly WidgetColorIcon _summaryIcon = new() { Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Center };
    private WidgetWeatherSnapshot? _renderedWeather;
    private SurfaceStyle? _renderedStyle;
    private ScrollViewer? _hourlyScroll, _dailyScroll;
    private readonly HashSet<WidgetWeatherAlert> _expandedAlerts = [];
    private string[] _cityLabels = [];
    public ComboBox Cities { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = nameof(WidgetWeatherCity.DisplayName) };
    public Button RefreshButton { get; } = new() { Content = new TablerIcon { Icon = TablerIconName.Refresh, FontSize = 14 }, Width = 32, Height = 32, MinWidth = 32, MinHeight = 32, Padding = new Thickness(0) };
    public Button CloseButton { get; } = new() { Content = new TablerIcon { Icon = TablerIconName.X, FontSize = 14 }, Width = 32, Height = 32, MinWidth = 32, MinHeight = 32, Padding = new Thickness(0) };
    public Button LocationsButton { get; } = new() { Content = new TablerIcon { Icon = TablerIconName.MapPin, FontSize = 14 }, Width = 32, Height = 32, MinWidth = 32, MinHeight = 32, Padding = new Thickness(0) };
    public ScrollViewer Scroll { get; }
    internal FrameworkElement EntranceContent => (FrameworkElement)_shell.Child;
    private readonly Border _shell;
    private readonly SolidColorBrush _text = new(Colors.White), _muted = new(Colors.LightGray);
    private readonly SolidColorBrush _warm = new(Color.FromArgb(255, 242, 184, 75));
    private readonly SolidColorBrush _cool = new(Color.FromArgb(255, 78, 161, 211));
    private readonly SolidColorBrush _green = new(Color.FromArgb(255, 42, 161, 152));
    private readonly SolidColorBrush _cardFill = new(Colors.Transparent), _cardBorder = new(Colors.Transparent);

    private sealed record SurfaceStyle(string Background, double Opacity, bool Acrylic, double Radius, double WindowRadius, bool Border, string BorderColor, double BorderOpacity, double BorderWidth);

    public WeatherDetailsView()
    {
        Scroll = new ScrollViewer { Content = _body, Margin = new Thickness(0, 0, 2, 2), IsTabStop = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch, ZoomMode = ZoomMode.Disabled };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(_title); header.Children.Add(LocationsButton); header.Children.Add(RefreshButton); header.Children.Add(CloseButton);
        Grid.SetColumn(LocationsButton, 1); Grid.SetColumn(RefreshButton, 2); Grid.SetColumn(CloseButton, 3);
        var heading = new StackPanel { Spacing = 10, Padding = new Thickness(16, 16, 16, 14) };
        heading.Children.Add(header); heading.Children.Add(Cities); heading.Children.Add(_updated);
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new());
        layout.Children.Add(heading); layout.Children.Add(_divider); layout.Children.Add(Scroll);
        Grid.SetRow(_divider, 1); Grid.SetRow(Scroll, 2);
        _shell = new Border { Child = layout };
        Content = _shell;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Cities, "天气城市");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RefreshButton, "刷新天气");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CloseButton, "关闭天气详情");
        ToolTipService.SetToolTip(RefreshButton, "刷新天气");
        ToolTipService.SetToolTip(CloseButton, "关闭");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LocationsButton, "位置管理");
        ToolTipService.SetToolTip(LocationsButton, "添加和切换天气位置");
    }

    public void Update(DesktopWidget widget, BoxAppearance appearance, GlobalAppearanceSettings settings, double? windowCornerRadius = null)
    {
        var surface = new SurfaceStyle(appearance.Background, appearance.ResolveBackgroundOpacity(settings.UseAcrylicBoxes), settings.UseAcrylicBoxes,
            settings.CornerRadius, windowCornerRadius ?? settings.CornerRadius, settings.ShowBorder, settings.BorderColor, settings.BorderOpacity, settings.BorderWidth);
        if (surface != _renderedStyle)
        {
            var color = CrabDesk.WinUI.Converters.HexColor.TryParse(appearance.Background, out var parsed) ? parsed : Color.FromArgb(255, 42, 45, 50);
            var dark = color.R * .299 + color.G * .587 + color.B * .114 < 150;
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
            _text.Color = dark ? Color.FromArgb(255, 242, 244, 248) : Color.FromArgb(255, 31, 35, 41);
            _muted.Color = dark ? Color.FromArgb(255, 190, 198, 210) : Color.FromArgb(255, 82, 90, 103);
            _warm.Color = dark ? Color.FromArgb(255, 242, 184, 75) : Color.FromArgb(255, 156, 97, 0);
            _cool.Color = dark ? Color.FromArgb(255, 78, 161, 211) : Color.FromArgb(255, 23, 103, 156);
            _green.Color = dark ? Color.FromArgb(255, 92, 206, 181) : Color.FromArgb(255, 20, 118, 110);
            _cardFill.Color = dark ? Color.FromArgb(12, 255, 255, 255) : Color.FromArgb(112, 255, 255, 255);
            _cardBorder.Color = dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(18, 31, 35, 41);
            _divider.Background = _cardBorder;
            color.A = (byte)Math.Round(255 * surface.Opacity);
            _shell.Background = new SolidColorBrush(color);
            _shell.CornerRadius = new CornerRadius(surface.WindowRadius);
            _shell.BorderThickness = new Thickness(0);
        }
        _title.Text = widget.Location?.Name ?? "天气详情";
        _summaryIcon.AssetName = WidgetIconCatalog.GetWeatherName(widget.Weather, DateTimeOffset.Now);
        _title.Foreground = _text;
        _updated.Foreground = _muted; _error.Foreground = _muted; _empty.Foreground = _muted;
        // Avoid replacing the selection while the user is interacting with the city picker.
        var cityLabels = widget.Cities.Select(city => city.DisplayName).ToArray();
        if (!Cities.Items.OfType<WidgetWeatherCity>().SequenceEqual(widget.Cities) || !_cityLabels.SequenceEqual(cityLabels))
        { Cities.ItemsSource = widget.Cities.ToArray(); _cityLabels = cityLabels; }
        Cities.SelectedItem = widget.ActiveCity;
        Cities.Visibility = widget.Cities.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !widget.IsLoading && !widget.IsLocating && widget.Location is not null;
        _empty.Text = widget.IsLocating ? "正在获取当前位置…" : widget.IsLoading ? "正在获取天气…" : "暂无天气数据，可在桌面卡片定位或添加城市。";
        _updated.Text = widget.Weather is { } snapshot ? $"{snapshot.Provider} · {(snapshot.PublishedAt ?? snapshot.FetchedAt):MM/dd HH:mm} 更新" +
            (widget.IsLocating ? " · 正在定位…" : widget.IsLoading ? " · 正在刷新…" : "") : "";
        _updated.Visibility = widget.Weather is null ? Visibility.Collapsed : Visibility.Visible;
        _error.Text = widget.Error;
        _error.Visibility = string.IsNullOrWhiteSpace(widget.Error) ? Visibility.Collapsed : Visibility.Visible;
        if (ReferenceEquals(_renderedWeather, widget.Weather) && surface == _renderedStyle) return;
        var hourlyOffset = _hourlyScroll?.HorizontalOffset ?? 0;
        var dailyOffset = _dailyScroll?.HorizontalOffset ?? 0;
        _renderedWeather = widget.Weather; _renderedStyle = surface;
        _hourlyScroll = null; _dailyScroll = null;
        // Detach the reusable icon while its previous visual parent is still
        // attached. Clearing the ancestor card first can invalidate Parent.
        if (_summaryIcon.Parent is Panel previousSummary) previousSummary.Children.Remove(_summaryIcon);
        _body.Children.Clear();
        _body.Children.Add(_error);
        if (widget.Weather is not { } weather)
        { _body.Children.Add(_empty); return; }
        var summary = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        summary.ColumnDefinitions.Add(new()); summary.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        summary.RowDefinitions.Add(new() { Height = GridLength.Auto }); summary.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var current = new StackPanel { Spacing = 2 };
        current.Children.Add(Text($"{weather.Temperature:0.#}°", 52));
        current.Children.Add(Text(weather.Description, 15, true));
        summary.Children.Add(current);
        summary.Children.Add(_summaryIcon); Grid.SetColumn(_summaryIcon, 1);
        var range = Text($"今日  {weather.Minimum:0}° / {weather.Maximum:0}°", 12);
        summary.Children.Add(range); Grid.SetRow(range, 1);
        if (weather.AirQuality is { } aqi)
        {
            var quality = Text($"AQI {aqi:0}", 12, true); quality.Foreground = _green;
            quality.HorizontalAlignment = HorizontalAlignment.Right;
            summary.Children.Add(quality); Grid.SetColumn(quality, 1); Grid.SetRow(quality, 1);
        }
        _body.Children.Add(Card(summary, settings));
        var metrics = new Grid { ColumnSpacing = 8, RowSpacing = 12 };
        for (var i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new());
        for (var i = 0; i < 3; i++) metrics.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var values = new[] { ("体感", Unit(weather.FeelsLike, "°")), ("湿度", Unit(weather.Humidity, "%")), ("风速", Unit(weather.WindSpeed, " km/h")),
            ("气压", Unit(weather.Pressure, " hPa")), ("紫外线", Unit(weather.UvIndex, "")), ("日出 / 日落", $"{Time(weather.Sunrise)} / {Time(weather.Sunset)}"),
            ("PM2.5", Unit(weather.Pm25, " μg/m³")), ("风向", Wind(weather.WindDirection)), ("空气质量", Unit(weather.AirQuality, " AQI")) };
        for (var i = 0; i < values.Length; i++)
        {
            var cell = new StackPanel { Spacing = 5 }; cell.Children.Add(Text(values[i].Item1, 11)); cell.Children.Add(Text(values[i].Item2, 13, true));
            metrics.Children.Add(cell); Grid.SetRow(cell, i / 3); Grid.SetColumn(cell, i % 3);
        }
        _body.Children.Add(Card(metrics, settings));
        if (!string.IsNullOrWhiteSpace(weather.AirQualityAdvice)) _body.Children.Add(Text(weather.AirQualityAdvice, 12));
        if (weather.Hourly.Count > 0)
        {
            var forecast = new StackPanel { Spacing = 12 };
            forecast.Children.Add(ChartHeading("逐小时预报", ("气温 °C", _warm), ("AQI", _green)));
            _hourlyScroll = HourlyChart(weather.Hourly); RestoreOffset(_hourlyScroll, hourlyOffset);
            forecast.Children.Add(_hourlyScroll);
            _body.Children.Add(Card(forecast, settings));
        }
        if (weather.Daily.Count > 0)
        {
            var forecast = new StackPanel { Spacing = 12 };
            forecast.Children.Add(ChartHeading("未来七天", ("最高温", _warm), ("最低温", _cool)));
            _dailyScroll = DailyChart(weather.Daily); RestoreOffset(_dailyScroll, dailyOffset);
            forecast.Children.Add(_dailyScroll);
            _body.Children.Add(Card(forecast, settings));
        }
        _expandedAlerts.IntersectWith(weather.Alerts);
        if (weather.Alerts.Count > 0)
        {
            _body.Children.Add(Text($"气象预警 · {weather.Alerts.Count}", 14, true));
            var alerts = new StackPanel { Spacing = 8 };
            foreach (var alert in weather.Alerts)
            {
                var expander = new Expander
                {
                    Header = Text(alert.Title, 13, true), Content = Text(alert.Detail, 12),
                    IsExpanded = _expandedAlerts.Contains(alert), MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(Math.Min(12, settings.CornerRadius)),
                    Background = _cardFill, BorderBrush = _cardBorder
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, alert.Title);
                expander.Expanding += (_, _) => _expandedAlerts.Add(alert);
                expander.Collapsed += (_, _) => _expandedAlerts.Remove(alert);
                alerts.Children.Add(expander);
            }
            _body.Children.Add(alerts);
        }
        if (weather.Indices.Count > 0)
        {
            _body.Children.Add(Text("生活指数", 14, true));
            _body.Children.Add(LifeIndices(weather.Indices, settings));
        }
    }

    private Grid LifeIndices(IReadOnlyList<WidgetWeatherIndex> indices, GlobalAppearanceSettings settings)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        for (var i = 0; i < (indices.Count + 1) / 2; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var i = 0; i < indices.Count; i++)
        {
            var index = indices[i];
            // Each life index keeps its own semantic icon instead of a Segoe glyph.
            var icon = index.Type switch
            {
                "uvIndex" or "feelsLike" => TablerIconName.Temperature,
                "humidity" => TablerIconName.Droplets,
                "pressure" => TablerIconName.Gauge,
                "carWash" => TablerIconName.Car,
                "sports" => TablerIconName.BallBasketball,
                _ => TablerIconName.InfoCircle
            };
            var accent = index.Type switch { "uvIndex" or "feelsLike" => _warm, "humidity" or "pressure" => _cool, _ => _green };
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            heading.Children.Add(new TablerIcon { Icon = icon, FontSize = 16, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
            heading.Children.Add(Text(index.Name, 12));
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(heading); content.Children.Add(Text(index.Value, 20, true));
            var card = Card(content, settings);
            grid.Children.Add(card); Grid.SetRow(card, i / 2); Grid.SetColumn(card, i % 2);
        }
        return grid;
    }

    private ScrollViewer HourlyChart(IReadOnlyList<WidgetHourlyWeather> hours)
    {
        var canvas = new Canvas { Width = Math.Max(288, hours.Count * 56), Height = 164 };
        var min = hours.Min(h => h.Temperature); var max = hours.Max(h => h.Temperature);
        var aqis = hours.Where(h => h.AirQuality.HasValue).Select(h => h.AirQuality!.Value).ToArray();
        var minAqi = aqis.Length == 0 ? 0 : aqis.Min(); var maxAqi = aqis.Length == 0 ? 1 : aqis.Max();
        var temperature = new Polyline { Stroke = _warm, StrokeThickness = 2 };
        Polyline? air = null;
        for (var i = 0; i < hours.Count; i++)
        {
            var hour = hours[i]; var x = 28 + i * 56;
            var y = 26 + (max - hour.Temperature) / Math.Max(2, max - min) * 36;
            temperature.Points.Add(new(x, y)); Label(canvas, $"{hour.Temperature:0}°", x - 18, y - 24, _warm);
            if (hour.AirQuality is { } aqi)
            {
                if (air is null) { air = new Polyline { Stroke = _green, StrokeThickness = 2, StrokeDashArray = [3, 3] }; canvas.Children.Add(air); }
                var ay = 91 + (maxAqi - aqi) / Math.Max(10, maxAqi - minAqi) * 22;
                air.Points.Add(new(x, ay)); Label(canvas, $"{aqi:0}", x - 18, ay + 6, _green);
            }
            else air = null;
            Label(canvas, hour.Time.ToString("HH:mm"), x - 18, 132, _muted);
            ToolTipService.SetToolTip(temperature, "气温曲线；AQI 使用下方独立刻度");
        }
        canvas.Children.Add(temperature);
        return Horizontal(canvas);
    }

    private ScrollViewer DailyChart(IReadOnlyList<WidgetDailyWeather> days)
    {
        var canvas = new Canvas { Width = Math.Max(288, days.Count * 72), Height = 184 };
        var min = days.Min(d => d.Minimum); var max = days.Max(d => d.Maximum);
        var highs = new Polyline { Stroke = _warm, StrokeThickness = 2 }; var lows = new Polyline { Stroke = _cool, StrokeThickness = 2 };
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i]; var x = 36 + i * 72;
            Label(canvas, day.Date.ToString("MM/dd"), x - 20, 0, _text);
            Label(canvas, day.Condition, x - 30, 24, _muted);
            var high = 58 + (max - day.Maximum) / Math.Max(2, max - min) * 64;
            var low = 58 + (max - day.Minimum) / Math.Max(2, max - min) * 64;
            highs.Points.Add(new(x, high)); lows.Points.Add(new(x, low));
            Label(canvas, $"{day.Maximum:0}°", x - 18, high - 23, _warm); Label(canvas, $"{day.Minimum:0}°", x - 18, low + 5, _cool);
            if (day.PrecipitationProbability is { } rain) Label(canvas, $"降水 {rain:0}%", x - 29, 154, _muted);
        }
        canvas.Children.Add(highs); canvas.Children.Add(lows);
        return Horizontal(canvas);
    }
    private static ScrollViewer Horizontal(UIElement content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, ZoomMode = ZoomMode.Disabled };
    private Border Card(UIElement child, GlobalAppearanceSettings settings) => new() { Child = child, Padding = new Thickness(14), CornerRadius = new CornerRadius(Math.Min(12, settings.CornerRadius)), Background = _cardFill, BorderBrush = _cardBorder, BorderThickness = new Thickness(1) };
    private Grid ChartHeading(string title, params (string Label, Brush Color)[] entries)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(Text(title, 14, true));
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var entry in entries)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            item.Children.Add(new Border { Width = 12, Height = 2, Background = entry.Color, VerticalAlignment = VerticalAlignment.Center });
            var label = Text(entry.Label, 10); label.Foreground = entry.Color; item.Children.Add(label); legend.Children.Add(item);
        }
        row.Children.Add(legend); Grid.SetColumn(legend, 1);
        return row;
    }
    private static void RestoreOffset(ScrollViewer scroll, double offset)
    {
        if (offset <= 0) return;
        scroll.Loaded += (_, _) => scroll.DispatcherQueue.TryEnqueue(() => scroll.ChangeView(Math.Min(offset, scroll.ScrollableWidth), null, null, true));
    }
    private TextBlock Text(string value, double size, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = bold || size >= 18 ? _text : _muted, TextWrapping = TextWrapping.Wrap };
    private void Label(Canvas canvas, string value, double x, double y, Brush brush)
    { var label = Text(value, 11); label.Foreground = brush; canvas.Children.Add(label); Canvas.SetLeft(label, x); Canvas.SetTop(label, y); }
    private static string Unit(double? value, string unit) => value is { } number ? $"{number:0.#}{unit}" : "—";
    private static string Time(DateTimeOffset? value) => value is { } time ? time.ToString("HH:mm") : "—";
    private static string Wind(double? angle) => angle is { } degrees ? new[] { "北风", "东北风", "东风", "东南风", "南风", "西南风", "西风", "西北风" }[(int)Math.Round(((degrees % 360 + 360) % 360) / 45) % 8] : "—";
}
