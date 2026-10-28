using CrabDesk.Core;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Converters;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CrabDesk.WinUI.Controls;

public sealed record WidgetPreviewAppearance(BoxAppearance Surface, GlobalAppearanceSettings Settings);

public sealed partial class WidgetPreview : UserControl
{
    public static readonly DependencyProperty WidgetProperty = DependencyProperty.Register(nameof(Widget), typeof(DesktopWidget),
        typeof(WidgetPreview), new PropertyMetadata(null, OnPreviewChanged));
    public static readonly DependencyProperty PreviewAppearanceProperty = DependencyProperty.Register(nameof(PreviewAppearance), typeof(WidgetPreviewAppearance),
        typeof(WidgetPreview), new PropertyMetadata(null, OnPreviewChanged));
    public static readonly DependencyProperty PreviewRevisionProperty = DependencyProperty.Register(nameof(PreviewRevision), typeof(int),
        typeof(WidgetPreview), new PropertyMetadata(0, OnPreviewChanged));
    private SolidColorBrush _text = new(Colors.White);
    private SolidColorBrush _secondary = new(Colors.White);
    private SolidColorBrush _accent = new(Colors.CornflowerBlue);
    private readonly Grid _calendarHeader = new() { Height = 28, Margin = new Thickness(8, 0, 36, 0), ColumnSpacing = 2, Visibility = Visibility.Collapsed };
    private readonly LucideIcon _todoAdd = new() { Icon = LucideIconName.Plus, FontSize = 14, Width = 24, Height = 24,
        Margin = new Thickness(0, 4, 40, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly Grid _todoHeader = new() { Height = 24, Margin = new Thickness(12, 4, 76, 0), ColumnSpacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _todoTitle = new() { FontSize = 13.333, FontWeight = FontWeights.SemiBold,
        FontFamily = new FontFamily(BoxAppearance.DefaultFontFamily), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _todoSummary = new() { FontSize = 10.5,
        FontFamily = new FontFamily(BoxAppearance.DefaultFontFamily), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly LucideIcon _weatherLocations = new() { Icon = LucideIconName.MapPin, FontSize = 14, Width = 24, Height = 24,
        Margin = new Thickness(0, 2, 36, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };

    public DesktopWidget? Widget { get => (DesktopWidget?)GetValue(WidgetProperty); set => SetValue(WidgetProperty, value); }
    public WidgetPreviewAppearance? PreviewAppearance { get => (WidgetPreviewAppearance?)GetValue(PreviewAppearanceProperty); set => SetValue(PreviewAppearanceProperty, value); }
    public int PreviewRevision { get => (int)GetValue(PreviewRevisionProperty); set => SetValue(PreviewRevisionProperty, value); }

    public WidgetPreview()
    {
        InitializeComponent();
        _calendarHeader.ColumnDefinitions.Add(new());
        _calendarHeader.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
        _calendarHeader.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        _calendarHeader.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
        CardLayout.Children.Add(_calendarHeader);
        CardLayout.Children.Add(_todoAdd);
        _todoHeader.ColumnDefinitions.Add(new());
        _todoHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _todoHeader.Children.Add(_todoTitle); _todoHeader.Children.Add(_todoSummary); Grid.SetColumn(_todoSummary, 1);
        CardLayout.Children.Add(_todoHeader);
        CardLayout.Children.Add(_weatherLocations);
        Loaded += (_, _) => Refresh();
    }

    private static void OnPreviewChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((WidgetPreview)sender).Refresh();

    public void Refresh()
    {
        if (CardSurface is null || Widget is not { } widget) return;
        var appearance = PreviewAppearance ?? new WidgetPreviewAppearance(new(), new());
        var background = Parse(appearance.Surface.Background, Color.FromArgb(255, 42, 45, 50));
        var dark = background.R * .299 + background.G * .587 + background.B * .114 < 150;
        _text = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 31, 35, 41));
        _secondary = new SolidColorBrush(dark ? Color.FromArgb(255, 237, 240, 245) : Color.FromArgb(255, 52, 59, 69));
        _accent = new SolidColorBrush(Parse(appearance.Surface.Accent, Color.FromArgb(255, 78, 161, 211)));
        var alpha = Math.Clamp(appearance.Surface.Opacity, .35, 1) * (appearance.Settings.UseAcrylicBoxes ? .72 : 1);
        background.A = (byte)Math.Round(alpha * 255);
        CardSurface.Background = new SolidColorBrush(background);
        CardSurface.CornerRadius = new CornerRadius(Math.Clamp(appearance.Settings.CornerRadius, 0, 20));
        var stroke = BoxBorderStyle.ApplyTint(BoxBorderStyle.ResolveStroke(System.Drawing.Color.FromArgb(background.R, background.G, background.B),
            dark, appearance.Settings.BorderColor, appearance.Settings.BorderOpacity), alpha);
        CardSurface.BorderBrush = new SolidColorBrush(Color.FromArgb(stroke.A, stroke.R, stroke.G, stroke.B));
        CardSurface.BorderThickness = new Thickness(appearance.Settings.ShowBorder ? Math.Clamp(appearance.Settings.BorderWidth, 1, 6) : 0);
        var (width, height) = DesktopWidgets.Dimensions(widget.Size);
        CardSurface.Width = width;
        CardSurface.Height = height;
        CardTitle.Visibility = widget.Kind is DesktopWidgetKind.Clock or DesktopWidgetKind.Calendar or DesktopWidgetKind.Todo ? Visibility.Collapsed : Visibility.Visible;
        _calendarHeader.Visibility = widget.Kind == DesktopWidgetKind.Calendar ? Visibility.Visible : Visibility.Collapsed;
        _todoAdd.Visibility = widget.Kind == DesktopWidgetKind.Todo ? Visibility.Visible : Visibility.Collapsed;
        _todoAdd.Foreground = _accent;
        _todoHeader.Visibility = widget.Kind == DesktopWidgetKind.Todo ? Visibility.Visible : Visibility.Collapsed;
        _todoTitle.Text = widget.DisplayTitle; _todoTitle.Foreground = _text;
        _todoSummary.Text = $"{widget.Tasks.Count(task => !task.IsCompleted)} · {widget.Tasks.Count(task => task.IsCompleted)} 完成";
        _todoSummary.Foreground = _secondary; _todoSummary.MaxWidth = Math.Max(0, width - 128);
        _weatherLocations.Visibility = widget.Kind == DesktopWidgetKind.Weather ? Visibility.Visible : Visibility.Collapsed;
        _weatherLocations.Foreground = _accent;
        CardTitle.Margin = widget.Kind is DesktopWidgetKind.Todo or DesktopWidgetKind.Weather ? new Thickness(60, 0, 60, 0) : new Thickness(28, 0, 28, 0);
        CardTitle.Text = widget.Kind == DesktopWidgetKind.Calendar ? DateTime.Today.ToString("yyyy.MM") : widget.DisplayTitle;
        CardTitle.Foreground = _text;
        CardBody.Margin = widget.Kind == DesktopWidgetKind.Todo ? new Thickness(8, 4, 8, 8) : new Thickness(8);
        CardBody.Children.Clear();
        switch (widget.Kind)
        {
            case DesktopWidgetKind.Clock: Clock(widget); break;
            case DesktopWidgetKind.Calendar: Calendar(); break;
            case DesktopWidgetKind.Note: TextCard(widget.Text, "写下第一条记录", "便签"); break;
            case DesktopWidgetKind.Todo: Todo(widget); break;
            case DesktopWidgetKind.Translation: TranslationPreview(widget); break;
            case DesktopWidgetKind.Weather: Weather(widget); break;
        }
    }

    private TextBlock Text(string value, double size = 12, bool secondary = false, bool bold = false) => new()
    {
        Text = value, FontSize = size, FontFamily = new FontFamily(BoxAppearance.DefaultFontFamily),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = secondary ? _secondary : _text,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private void Clock(DesktopWidget widget)
    {
        var panel = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var format = widget.Use24HourClock ? "HH:mm" : "hh:mm";
        var time = Text(DateTime.Now.ToString(format + (widget.ShowSeconds ? ":ss" : "")), widget.ShowSeconds ? 26 : 32, bold: true);
        time.TextAlignment = TextAlignment.Center;
        var date = Text(DateTime.Today.ToString("M 月 d 日 · dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN")), 11.333, true);
        date.TextAlignment = TextAlignment.Center;
        panel.Children.Add(time); panel.Children.Add(date);
        CardBody.Children.Add(panel);
    }

    private void Calendar()
    {
        _calendarHeader.Children.Clear();
        var month = Text(DateTime.Today.ToString("yyyy.MM"), 12.5, bold: true);
        month.VerticalAlignment = VerticalAlignment.Center;
        _calendarHeader.Children.Add(month);
        var previous = new LucideIcon { Icon = LucideIconName.ChevronLeft, FontSize = 12, Foreground = _text };
        var next = new LucideIcon { Icon = LucideIconName.ChevronRight, FontSize = 12, Foreground = _text };
        var todayLabel = Text("今", 11.5); todayLabel.TextAlignment = TextAlignment.Center; todayLabel.VerticalAlignment = VerticalAlignment.Center;
        var todayFill = _text.Color; todayFill.A = 14;
        var todayButton = new Border { Child = todayLabel, Height = 24, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(todayFill), CornerRadius = new CornerRadius(Math.Min(6, PreviewAppearance?.Settings.CornerRadius ?? 6)) };
        _calendarHeader.Children.Add(previous); Grid.SetColumn(previous, 1);
        _calendarHeader.Children.Add(todayButton); Grid.SetColumn(todayButton, 2);
        _calendarHeader.Children.Add(next); Grid.SetColumn(next, 3);
        var grid = new Grid();
        for (var i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        for (var i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition());
        var week = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var i = 0; i < week.Length; i++)
        {
            var label = Text(week[i], 11, true); label.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(label); Grid.SetColumn(label, i);
        }
        var today = DateOnly.FromDateTime(DateTime.Today);
        var days = DesktopWidgets.CalendarDays(today);
        for (var i = 0; i < days.Count; i++)
        {
            var date = days[i];
            var label = Text(date.Day.ToString(), 11.5, date.Month != today.Month, date == today);
            label.HorizontalAlignment = HorizontalAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
            var cell = new Border { Child = label, CornerRadius = new CornerRadius(10), Width = 20, Height = 20 };
            if (date == today) { cell.Background = _accent; label.Foreground = new SolidColorBrush(Colors.White); }
            if (date.Month != today.Month) label.Opacity = .55;
            grid.Children.Add(cell); Grid.SetColumn(cell, i % 7); Grid.SetRow(cell, i / 7 + 1);
        }
        CardBody.Children.Add(grid);
    }

    private void TextCard(string value, string empty, string footer)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        value = DesktopWidgets.NormalizeText(value);
        var content = Text(string.IsNullOrWhiteSpace(value) ? empty : value[..Math.Min(value.Length, 400)], 13);
        content.TextWrapping = TextWrapping.Wrap; content.MaxLines = 6;
        grid.Children.Add(content);
        var caption = Text(footer, 11, true); grid.Children.Add(caption); Grid.SetRow(caption, 1);
        CardBody.Children.Add(grid);
    }

    private void Todo(DesktopWidget widget)
    {
        var panel = new Canvas();
        var width = DesktopWidgets.Dimensions(widget.Size).Width - 16;
        void Place(FrameworkElement element, double x, double y, double elementWidth, double height)
        {
            element.Width = elementWidth; element.Height = height;
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); panel.Children.Add(element);
        }
        var rowIndex = 0;
        var rows = Math.Max(1, (int)((DesktopWidgets.Dimensions(widget.Size).Height - 40) / 28));
        foreach (var task in widget.Tasks.Take(rows))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new());
            var check = new Border { Width = 14, Height = 14, Margin = new Thickness(4, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(3), BorderBrush = _secondary, BorderThickness = new Thickness(1) };
            if (task.IsCompleted)
            {
                check.Background = _accent; check.BorderBrush = _accent;
                check.Child = new LucideIcon { Icon = LucideIconName.Check, FontSize = 10, Foreground = new SolidColorBrush(Colors.White) };
            }
            var title = Text(task.Text, 12); title.VerticalAlignment = VerticalAlignment.Center; title.Margin = new Thickness(0, 0, 32, 0);
            if (task.IsCompleted) { title.Opacity = .65; title.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough; }
            row.Children.Add(check); row.Children.Add(title); Grid.SetColumn(title, 1);
            var rowColor = _text.Color; rowColor.A = (byte)(task.IsCompleted ? 5 : 10);
            var surface = new Border { Child = row, CornerRadius = new CornerRadius(Math.Min(6, PreviewAppearance?.Settings.CornerRadius ?? 6)), Background = new SolidColorBrush(rowColor) };
            Place(surface, 0, 2 + rowIndex++ * 28, width, 24);
        }
        if (widget.Tasks.Count == 0)
        {
            var hint = Text("点击顶栏 + 添加待办", 11.5, true); hint.TextWrapping = TextWrapping.Wrap;
            Place(hint, 0, 6, width, 40);
        }
        CardBody.Children.Add(panel);
    }

    private void TranslationPreview(DesktopWidget widget)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 5 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.Children.Add(Text("原文", 11, true));
        var language = Text(widget.TargetLanguage + " ▾", 11); grid.Children.Add(language); Grid.SetColumn(language, 1);
        foreach (var column in new[] { 0, 1 })
        {
            var value = column == 0 ? widget.Text : widget.Translation;
            var empty = string.IsNullOrWhiteSpace(value);
            var content = Text(empty ? column == 0 ? "输入要翻译的文字…" : "译文将在这里显示" : value[..Math.Min(value.Length, 250)], 13);
            content.Opacity = empty ? .6 : 1;
            content.TextWrapping = TextWrapping.Wrap; content.MaxLines = 4;
            var stroke = _text.Color; stroke.A = 28;
            var fill = _text.Color; fill.A = (byte)(column == 0 ? 10 : 6);
            var field = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(Math.Min(8, PreviewAppearance?.Settings.CornerRadius ?? 8)),
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(stroke), Child = content, Background = new SolidColorBrush(fill) };
            grid.Children.Add(field); Grid.SetRow(field, 1); Grid.SetColumn(field, column);
        }
        var hint = Text("Ctrl+Enter 翻译", 10, true); grid.Children.Add(hint); Grid.SetRow(hint, 2);
        var action = Text(widget.IsLoading ? "取消       复制" : "翻译       复制", 12); action.Foreground = _accent;
        grid.Children.Add(action); Grid.SetRow(action, 2); Grid.SetColumn(action, 1);
        CardBody.Children.Add(grid);
    }

    private void Weather(DesktopWidget widget)
    {
        var panel = new Canvas();
        var width = DesktopWidgets.Dimensions(widget.Size).Width - 16;
        void Place(FrameworkElement element, double x, double y, double elementWidth, double height)
        {
            element.Width = elementWidth; element.Height = height;
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); panel.Children.Add(element);
        }
        void Metric(LucideIconName icon, string value, double x, double y, double metricWidth)
        {
            Place(new LucideIcon { Icon = icon, FontSize = 12, Foreground = _secondary }, x, y, 14, 18);
            Place(Text(value, 10.5, true), x + 19, y, metricWidth - 19, 18);
        }
        var cityWidth = width - (widget.Weather?.FeelsLike.HasValue == true ? 60 : 0);
        Place(Text(widget.Location?.Name ?? "选择城市", 12), 0, 0, cityWidth - 14, 18);
        Place(new LucideIcon { Icon = LucideIconName.ChevronsUpDown, FontSize = 10, Foreground = _secondary }, cityWidth - 12, 0, 12, 18);
        Place(Text(widget.Weather is { } weather ? $"{weather.Temperature:0.#}°" : "—°", 30, bold: true), 0, 18, width - 52, 38);
        if (widget.Weather is { } forecast)
        {
            var name = DesktopWidgets.WeatherIconName(forecast);
            var dark = _text.Color.R > 150;
            var color = name is "Sun" or "CloudSun" or "CloudLightning"
                ? dark ? Color.FromArgb(255, 242, 184, 75) : Color.FromArgb(255, 156, 97, 0)
                : name is "CloudRain" or "CloudSnow" or "CloudHail"
                    ? dark ? Color.FromArgb(255, 104, 193, 226) : Color.FromArgb(255, 29, 106, 150) : _secondary.Color;
            Place(new LucideIcon { Icon = Enum.Parse<LucideIconName>(name), FontSize = 36, Foreground = new SolidColorBrush(color) }, width - 46, 18, 44, 38);
            if (forecast.FeelsLike is { } feelsLike) Metric(LucideIconName.Thermometer, $"{feelsLike:0}°", width - 56, 0, 56);
            Place(Text($"{forecast.Description} · {forecast.Minimum:0}° / {forecast.Maximum:0}°", 11, true), 0, 56, width, 18);
            var metricWidth = (width - 8) / 2;
            if (forecast.Humidity is { } humidity) Metric(LucideIconName.Droplets, $"{humidity:0}%", 0, 76, metricWidth);
            if (forecast.WindSpeed is { } wind) Metric(LucideIconName.Wind, $"{wind:0} km/h", metricWidth + 8, 76, metricWidth);
            Metric(forecast.AirQuality is not null ? LucideIconName.Gauge : LucideIconName.ChevronRight,
                forecast.AirQuality is { } air ? $"AQI {air:0} · 查看详情" : "查看天气详情", 0, 100, width);
        }
        else Place(Text("搜索所在城市查看天气", 11, true), 0, 56, width, 18);
        var cityIndex = widget.Cities.FindIndex(city => city.Id == widget.ActiveCityId) + 1;
        var provider = widget.Weather?.Provider ?? WidgetWeatherService.ProviderName;
        Place(Text(widget.IsLoading ? "正在更新…" : !string.IsNullOrEmpty(widget.Error) ? "更新失败 · 保留上次数据" :
            widget.Cities.Count > 1 ? $"‹  {provider} · {cityIndex}/{widget.Cities.Count}  ›" : provider, 10, true), 0, 120, width, 16);
        CardBody.Children.Add(panel);
    }

    private static Color Parse(string value, Color fallback) => HexColor.TryParse(value, out var color) ? color : fallback;
}
