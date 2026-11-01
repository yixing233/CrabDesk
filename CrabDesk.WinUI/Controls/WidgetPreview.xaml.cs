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
    private readonly Grid _calendarHeader = new() { Height = 28, Width = 136, HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(8, 0, 0, 0), ColumnSpacing = 2, Visibility = Visibility.Collapsed };
    private readonly TextBlock _calendarUpcomingTitle = new() { Text = "近期节日 · 节气", Margin = new Thickness(192, 0, 36, 0),
        FontSize = 11.5, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily(BoxAppearance.DefaultFontFamily),
        VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
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
        CardLayout.Children.Add(_calendarUpcomingTitle);
        CardLayout.Children.Add(_todoAdd);
        _todoHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
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
        _calendarUpcomingTitle.Visibility = widget.Kind == DesktopWidgetKind.Calendar && width >= 300 ? Visibility.Visible : Visibility.Collapsed;
        _calendarUpcomingTitle.Foreground = _secondary;
        _todoAdd.Visibility = widget.Kind == DesktopWidgetKind.Todo ? Visibility.Visible : Visibility.Collapsed;
        _todoAdd.Foreground = _accent;
        _todoHeader.Visibility = widget.Kind == DesktopWidgetKind.Todo ? Visibility.Visible : Visibility.Collapsed;
        _todoTitle.Text = widget.DisplayTitle; _todoTitle.Foreground = _text;
        _todoTitle.MaxWidth = Math.Max(0, width - 128);
        _todoSummary.Text = $"· {widget.Tasks.Count(task => !task.IsCompleted)}";
        _todoSummary.Foreground = _secondary;
        _weatherLocations.Visibility = widget.Kind == DesktopWidgetKind.Weather ? Visibility.Visible : Visibility.Collapsed;
        _weatherLocations.Foreground = _accent;
        CardTitle.Margin = widget.Kind is DesktopWidgetKind.Todo or DesktopWidgetKind.Weather ? new Thickness(60, 0, 60, 0) : new Thickness(28, 0, 28, 0);
        CardTitle.Text = widget.Kind == DesktopWidgetKind.Calendar ? DateTime.Today.ToString("yyyy.MM") : widget.DisplayTitle;
        CardTitle.TextAlignment = widget.Kind == DesktopWidgetKind.Weather ? TextAlignment.Left : TextAlignment.Center;
        CardTitle.FontSize = widget.Kind == DesktopWidgetKind.Weather ? 12 : 13.333;
        if (widget.Kind == DesktopWidgetKind.Weather)
        {
            CardTitle.Margin = new Thickness(34, 0, 64, 0);
            CardTitle.Text = widget.Location is { } city ? $"{city.Name}  ⌄" : widget.DisplayTitle;
        }
        CardTitle.Foreground = _text;
        CardBody.Margin = widget.Kind == DesktopWidgetKind.Todo ? new Thickness(8, 4, 8, 8) : new Thickness(8);
        CardBody.Children.Clear();
        switch (widget.Kind)
        {
            case DesktopWidgetKind.Clock: Clock(widget); break;
            case DesktopWidgetKind.Calendar: Calendar(widget); break;
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
        var panel = new Canvas();
        var (cardWidth, cardHeight) = DesktopWidgets.Dimensions(widget.Size);
        var width = cardWidth - 16; var height = cardHeight - 16;
        var face = WidgetClockPresentation.Face(width, height);
        if (face.Width <= 0) return;
        var now = DateTime.Now;
        SolidColorBrush Ink(byte alpha) { var color = _text.Color; color.A = alpha; return new(color); }
        void Place(FrameworkElement element, double x, double y, double w, double h)
        { element.Width = w; element.Height = h; Canvas.SetLeft(element, x); Canvas.SetTop(element, y); panel.Children.Add(element); }
        void Stroke(double angle, double from, double to, SolidColorBrush color, double thickness)
        {
            var start = WidgetClockPresentation.PointOnFace(face, angle, from);
            var end = WidgetClockPresentation.PointOnFace(face, angle, to);
            panel.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
                Stroke = color, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        var faceFill = _text.Color.R > 128 ? Color.FromArgb(255, 26, 26, 26) : Color.FromArgb(255, 250, 250, 250);
        Place(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = new SolidColorBrush(faceFill), Stroke = Ink(35), StrokeThickness = 1 }, face.X, face.Y, face.Width, face.Height);
        for (var tick = 0; tick < 60; tick++) Stroke(tick * 6, tick % 5 == 0 ? .79 : .85, .9, Ink((byte)(tick % 5 == 0 ? 150 : 65)), tick % 5 == 0 ? 1.5 : .8);
        var boxSize = face.Width * .2;
        foreach (var hour in new[] { 12, 3, 6, 9 })
        {
            var point = WidgetClockPresentation.PointOnFace(face, hour * 30, .62);
            var label = Text(hour.ToString(), Math.Clamp(face.Width * .085, 8, 12));
            label.Foreground = Ink(210); label.TextAlignment = TextAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
            // A centered grid matches the painted dial's centered text box.
            var cell = new Grid(); cell.Children.Add(label);
            Place(cell, point.X - boxSize / 2, point.Y - boxSize / 2, boxSize, boxSize);
        }
        var hands = WidgetClockPresentation.Hands(now);
        Stroke(hands.Hour, 0, .46, _text, Math.Clamp(face.Width * .027, 2.2, 4));
        Stroke(hands.Minute, 0, .72, Ink(230), Math.Clamp(face.Width * .018, 1.6, 2.8));
        if (widget.ShowSeconds) Stroke(hands.Second, -.17, .82, Ink(160), 1.1);
        var center = WidgetClockPresentation.PointOnFace(face, 0, 0);
        var pivot = Math.Clamp(face.Width * .024, 2.2, 3.5);
        Place(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = _text }, center.X - pivot, center.Y - pivot, pivot * 2, pivot * 2);
        var pivotColor = _text.Color.R > 128 ? Color.FromArgb(255, 31, 35, 41) : Colors.White;
        Place(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = new SolidColorBrush(pivotColor) }, center.X - 1, center.Y - 1, 2, 2);
        var culture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
        if (WidgetClockPresentation.IsLandscape(width, height))
        {
            var left = face.X + face.Width + 18; var available = width - left;
            var time = Text(now.ToString(widget.ShowSeconds ? "HH:mm:ss" : "HH:mm", culture), 26, bold: true); time.VerticalAlignment = VerticalAlignment.Center;
            var date = Text(now.ToString("M月d日 · ddd", culture), 10.5, true); date.VerticalAlignment = VerticalAlignment.Center;
            Place(time, left, height / 2 - 29, available, 34);
            Place(date, left, height / 2 + 12, available, 20);
        }
        else
        {
            var date = Text(now.ToString("M月d日 · ddd", culture), 10.5, true); date.TextAlignment = TextAlignment.Center; date.VerticalAlignment = VerticalAlignment.Center;
            Place(date, 0, face.Y + face.Height + 4, width, 18);
        }
        CardBody.Children.Add(panel);
    }

    private void Calendar(DesktopWidget widget)
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
        var grid = new Grid { Width = 164, HorizontalAlignment = HorizontalAlignment.Left };
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
        if (DesktopWidgets.Dimensions(widget.Size).Width < 300) { CardBody.Children.Add(grid); return; }
        var layout = new Grid { ColumnSpacing = 20 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(164) });
        layout.ColumnDefinitions.Add(new());
        layout.Children.Add(grid);
        var line = _text.Color; line.A = 24;
        var divider = new Border { Background = new SolidColorBrush(line), Width = 1, Margin = new Thickness(-8, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
        layout.Children.Add(divider); Grid.SetColumn(divider, 1);
        var upcoming = new StackPanel { Spacing = 2 };
        foreach (var item in CalendarDateDetails.GetUpcomingEvents(today))
        {
            var row = new Grid { Height = 32, ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(40) });
            var labels = new StackPanel();
            labels.Children.Add(Text(item.Name, 12));
            labels.Children.Add(Text(item.DateLabel + " · " + item.KindLabel, 9.5, true));
            row.Children.Add(labels);
            if (item.DaysRemaining == 0)
            {
                var current = Text("今天", 12, bold: true); current.Foreground = _accent; current.TextAlignment = TextAlignment.Center;
                row.Children.Add(current); Grid.SetColumn(current, 1);
            }
            else
            {
                var count = new StackPanel();
                var number = Text(item.DaysRemaining.ToString(), 18, bold: true); number.Foreground = _accent; number.TextAlignment = TextAlignment.Center; number.Height = 20;
                var unit = Text("天后", 9, true); unit.TextAlignment = TextAlignment.Center; unit.Height = 12;
                count.Children.Add(number); count.Children.Add(unit);
                row.Children.Add(count); Grid.SetColumn(count, 1);
            }
            upcoming.Children.Add(row);
        }
        layout.Children.Add(upcoming); Grid.SetColumn(upcoming, 1);
        CardBody.Children.Add(layout);
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
        var rowTop = 0d;
        foreach (var item in WidgetTodoPresentation.Rows(widget))
        {
            var rowHeight = item.Height;
            if (rowTop + rowHeight > DesktopWidgets.Dimensions(widget.Size).Height - 40) break;
            if (item.IsCompletedHeader)
            {
                if (rowTop > 0)
                {
                    var line = _text.Color; line.A = 20;
                    Place(new Border { Background = new SolidColorBrush(line) }, 4, rowTop, width - 10, 1);
                }
                Place(new LucideIcon { Icon = LucideIconName.ChevronRight, FontSize = 12, Foreground = _secondary }, 4, rowTop + 6, 16, 16);
                var caption = Text($"已完成 · {widget.Tasks.Count(task => task.IsCompleted)}", 11, true); caption.VerticalAlignment = VerticalAlignment.Center;
                Place(caption, 28, rowTop + 2, width - 36, 24);
            }
            else if (item.Task is { } task)
            {
                var check = new Border { CornerRadius = new CornerRadius(Math.Min(5, PreviewAppearance?.Settings.CornerRadius ?? 5)),
                    BorderBrush = _secondary, BorderThickness = new Thickness(1.1), Opacity = .65 };
                Place(check, 5, rowTop + 7.5, 15, 15);
                var title = Text(task.Text, 12.5); title.VerticalAlignment = VerticalAlignment.Center;
                Place(title, 28, rowTop + 2, width - 36, 26);
                if (task.HasDateRange)
                {
                    var dates = Text(WidgetTodoPresentation.DateLabel(task, DateTime.Today.Year), 10.5, true);
                    dates.Opacity = .7; dates.VerticalAlignment = VerticalAlignment.Center;
                    Place(dates, 28, rowTop + 28, width - 36, 14);
                }
            }
            rowTop += rowHeight;
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
        var panel = new Canvas();
        var (cardWidth, cardHeight) = DesktopWidgets.Dimensions(widget.Size);
        var width = cardWidth - 16; var height = cardHeight - 44;
        var fieldWidth = (width - 10) / 2;
        var dark = _text.Color.R > 128;
        var ink = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 31, 31, 31));
        var muted = new SolidColorBrush(dark ? Color.FromArgb(255, 205, 205, 205) : Color.FromArgb(255, 90, 90, 90));
        var disabled = new SolidColorBrush(dark ? Color.FromArgb(255, 155, 155, 155) : Color.FromArgb(255, 115, 115, 115));
        void Place(FrameworkElement element, double x, double y, double elementWidth, double elementHeight)
        { element.Width = elementWidth; element.Height = elementHeight; Canvas.SetLeft(element, x); Canvas.SetTop(element, y); panel.Children.Add(element); }
        foreach (var column in new[] { 0, 1 })
        {
            var left = column * (fieldWidth + 10);
            var header = Text(column == 0 ? "原文" : widget.TargetLanguage + " ▾", 11);
            header.Foreground = ink; header.VerticalAlignment = VerticalAlignment.Center;
            Place(header, left, -4, fieldWidth - 36, 28);
            var enabled = column == 0 ? !string.IsNullOrEmpty(widget.Text) : !string.IsNullOrWhiteSpace(widget.Translation);
            Place(new LucideIcon { Icon = column == 0 ? LucideIconName.Eraser : LucideIconName.Copy, FontSize = 16,
                Foreground = enabled ? ink : disabled }, left + fieldWidth - 28, -4, 28, 28);
            var value = column == 0 ? widget.Text : widget.Translation;
            var empty = string.IsNullOrWhiteSpace(value);
            var content = Text(empty ? column == 0 ? "输入要翻译的文字…" : widget.IsLoading ? "正在翻译…" : "译文将在这里显示" : value[..Math.Min(value.Length, 250)], 13);
            content.Foreground = empty ? muted : ink;
            content.TextWrapping = TextWrapping.Wrap; content.MaxLines = 4;
            var stroke = ink.Color; stroke.A = 50;
            var field = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(Math.Min(8, PreviewAppearance?.Settings.CornerRadius ?? 8)),
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(stroke), Child = content,
                Background = new SolidColorBrush(dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 250, 250, 250)) };
            Place(field, left, 26, fieldWidth, height - 58);
        }
        var hint = Text(widget.IsLoading ? "正在翻译…" : !string.IsNullOrEmpty(widget.Error) ? widget.Error : "Ctrl+Enter 翻译", 10.5);
        hint.Foreground = muted; hint.VerticalAlignment = VerticalAlignment.Center;
        Place(hint, 0, height - 28, width - 84, 28);
        var canTranslate = widget.IsLoading || !string.IsNullOrWhiteSpace(widget.Text);
        var translate = Text(widget.IsLoading ? "取消" : "翻译", 12);
        translate.Foreground = canTranslate ? ink : new SolidColorBrush(dark ? Color.FromArgb(255, 170, 170, 170) : Color.FromArgb(255, 110, 110, 110));
        translate.TextAlignment = TextAlignment.Center; translate.VerticalAlignment = VerticalAlignment.Center;
        var buttonFill = ink.Color; buttonFill.A = (byte)(canTranslate ? 24 : 10);
        var buttonStroke = ink.Color; buttonStroke.A = (byte)(canTranslate ? 32 : 14);
        Place(new Border { Child = translate, CornerRadius = new CornerRadius(Math.Min(6, PreviewAppearance?.Settings.CornerRadius ?? 6)),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(buttonStroke),
            Background = new SolidColorBrush(buttonFill) }, width - 72, height - 28, 72, 28);
        CardBody.Children.Add(panel);
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
        void Metric(LucideIconName icon, string value, double x, double y, double metricWidth, double height = 18)
        {
            Place(new LucideIcon { Icon = icon, FontSize = 12, Foreground = _secondary }, x, y, 14, height);
            Place(Text(value, 10.5, true), x + 19, y + (height - 18) / 2, metricWidth - 19, 18);
        }
        var overview = Math.Min(width, 164);
        Place(Text(widget.Weather is { } weather ? $"{weather.Temperature:0.#}°" : "—°", 36, bold: true), 4, -2, overview - 56, 48);
        if (widget.Weather is { } forecast)
        {
            var name = DesktopWidgets.WeatherIconName(forecast);
            var dark = _text.Color.R > 150;
            var color = name is "Sun" or "CloudSun" or "CloudLightning"
                ? dark ? Color.FromArgb(255, 242, 184, 75) : Color.FromArgb(255, 156, 97, 0)
                : name is "CloudRain" or "CloudSnow" or "CloudHail"
                    ? dark ? Color.FromArgb(255, 104, 193, 226) : Color.FromArgb(255, 29, 106, 150) : _secondary.Color;
            Place(new LucideIcon { Icon = Enum.Parse<LucideIconName>(name), FontSize = 40, Foreground = new SolidColorBrush(color) }, overview - 48, 2, 44, 44);
            if (forecast.FeelsLike is { } feelsLike) Metric(LucideIconName.Thermometer, $"{feelsLike:0}°", overview - 56, 48, 52);
            Place(Text($"{forecast.Description} · {forecast.Minimum:0}° / {forecast.Maximum:0}°", 10.5, true), 4, 48, overview - 64, 18);
            var metricWidth = (width - 20) / 2;
            if (forecast.Humidity is { } humidity)
            {
                if (width >= 300) Metric(LucideIconName.Droplets, $"湿度 {humidity:0}%", width - 132, 2, 128, 24);
                else Metric(LucideIconName.Droplets, $"{humidity:0}%", 4, 68, metricWidth);
            }
            if (forecast.WindSpeed is { } wind)
            {
                if (width >= 300) Metric(LucideIconName.Wind, $"风速 {wind:0} km/h", width - 132, 34, 128, 24);
                else Metric(LucideIconName.Wind, $"{wind:0} km/h", metricWidth + 16, 68, metricWidth);
            }
            var statusTop = widget.Cities.Count > 1 ? 88 : 104;
            var statusHeight = widget.Cities.Count > 1 ? 24 : 28;
            var statusFill = _text.Color; statusFill.A = 14;
            Place(new Border { Background = new SolidColorBrush(statusFill), CornerRadius = new CornerRadius(8) }, 4, statusTop, width - 8, statusHeight);
            Metric(forecast.AirQuality is not null ? LucideIconName.Gauge : LucideIconName.ChevronRight,
                widget.IsLoading ? "正在更新…" : !string.IsNullOrEmpty(widget.Error) ? widget.Error :
                forecast.AirQuality is { } air ? $"AQI {air:0}" : "查看天气详情", 12, statusTop + (statusHeight - 18) / 2, width - 40);
            Place(new LucideIcon { Icon = LucideIconName.ChevronRight, FontSize = 12, Foreground = _secondary }, width - 28, statusTop, 12, statusHeight);
        }
        else Place(Text("搜索所在城市查看天气", 11, true), 0, 56, width, 18);
        var cityIndex = widget.Cities.FindIndex(city => city.Id == widget.ActiveCityId) + 1;
        if (widget.Cities.Count > 1)
        {
            var counter = Text($"‹     {cityIndex}/{widget.Cities.Count}     ›", 10, true); counter.TextAlignment = TextAlignment.Center;
            Place(counter, 0, 120, width, 16);
        }
        CardBody.Children.Add(panel);
    }

    private static Color Parse(string value, Color fallback) => HexColor.TryParse(value, out var color) ? color : fallback;
}
