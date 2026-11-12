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
    private double WidgetCornerRadius => PreviewAppearance?.Settings.ResolveWidgetCornerRadius() ?? 8;
    public static readonly DependencyProperty WidgetProperty = DependencyProperty.Register(nameof(Widget), typeof(DesktopWidget),
        typeof(WidgetPreview), new PropertyMetadata(null, OnPreviewChanged));
    public static readonly DependencyProperty PreviewAppearanceProperty = DependencyProperty.Register(nameof(PreviewAppearance), typeof(WidgetPreviewAppearance),
        typeof(WidgetPreview), new PropertyMetadata(null, OnPreviewChanged));
    public static readonly DependencyProperty PreviewRevisionProperty = DependencyProperty.Register(nameof(PreviewRevision), typeof(int),
        typeof(WidgetPreview), new PropertyMetadata(0, OnPreviewChanged));
    public static readonly DependencyProperty IsCatalogPreviewProperty = DependencyProperty.Register(nameof(IsCatalogPreview), typeof(bool),
        typeof(WidgetPreview), new PropertyMetadata(false, OnPreviewChanged));
    private bool _previewRefreshQueued;
    private SolidColorBrush _text = new(Colors.White);
    private SolidColorBrush _secondary = new(Colors.White);
    private SolidColorBrush _accent = new(Colors.CornflowerBlue);
    private Color _background = Color.FromArgb(255, 42, 45, 50);
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
    private readonly TextBlock _todoSummary = new() { FontSize = 13.333,
        FontFamily = new FontFamily(BoxAppearance.DefaultFontFamily), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Canvas _weatherHeader = new() { Height = 36, Visibility = Visibility.Collapsed };
    private readonly LucideIcon _fileShelfAdd = new() { Icon = LucideIconName.Plus, FontSize = 14, Width = 24, Height = 24,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _fileShelfAddButton = new() { Width = 24, Height = 24,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };

    public DesktopWidget? Widget { get => (DesktopWidget?)GetValue(WidgetProperty); set => SetValue(WidgetProperty, value); }
    public WidgetPreviewAppearance? PreviewAppearance { get => (WidgetPreviewAppearance?)GetValue(PreviewAppearanceProperty); set => SetValue(PreviewAppearanceProperty, value); }
    public int PreviewRevision { get => (int)GetValue(PreviewRevisionProperty); set => SetValue(PreviewRevisionProperty, value); }
    public bool IsCatalogPreview { get => (bool)GetValue(IsCatalogPreviewProperty); set => SetValue(IsCatalogPreviewProperty, value); }

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
        CardLayout.Children.Add(_weatherHeader);
        _fileShelfAddButton.Child = _fileShelfAdd;
        CardLayout.Children.Add(_fileShelfAddButton);
        Loaded += (_, _) => QueueRefresh();
        Unloaded += (_, _) => ResetFileShelfIcons();
    }

    private static void OnPreviewChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((WidgetPreview)sender).QueueRefresh();

    private void QueueRefresh()
    {
        // Bindings arrive separately. Generate the preview once after all of
        // them settle, and only while the virtualized card is loaded.
        if (!IsLoaded || _previewRefreshQueued) return;
        _previewRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _previewRefreshQueued = false;
            if (IsLoaded) Refresh();
        })) _previewRefreshQueued = false;
    }

    public void Refresh()
    {
        if (CardSurface is null || Widget is not { } widget) return;
        BeginFileShelfIconRender(widget);
        CardTitle.FontWeight = FontWeights.SemiBold;
        _calendarUpcomingTitle.FontSize = 11.5;
        _calendarUpcomingTitle.FontWeight = FontWeights.SemiBold;
        _todoTitle.FontSize = 13.333;
        _todoTitle.FontWeight = FontWeights.SemiBold;
        _todoSummary.FontSize = 13.333;
        _todoSummary.FontWeight = FontWeights.Normal;
        var appearance = PreviewAppearance ?? new WidgetPreviewAppearance(new(), new());
        var surface = WidgetAppearance.Resolve(widget, appearance.Surface,
            WidgetAppearance.UsesAcrylic(widget, appearance.Settings.UseAcrylicBoxes, appearance.Settings.WidgetAcrylic),
            widgetAcrylic: appearance.Settings.WidgetAcrylic);
        var background = Parse(surface.Background, Color.FromArgb(255, 42, 45, 50));
        _background = background;
        var dark = background.R * .299 + background.G * .587 + background.B * .114 < 150;
        _text = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 31, 35, 41));
        _secondary = new SolidColorBrush(dark ? Color.FromArgb(255, 237, 240, 245) : Color.FromArgb(255, 52, 59, 69));
        var solid = widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid;
        if (solid)
            _secondary = new SolidColorBrush(dark ? Color.FromArgb(255, 176, 183, 195) : Color.FromArgb(255, 102, 113, 128));
        _accent = new SolidColorBrush(Parse(surface.Accent, Color.FromArgb(255, 78, 161, 211)));
        var alpha = surface.Opacity;
        var hasBackground = DesktopWidgets.HasBackground(widget);
        background.A = hasBackground ? (byte)Math.Round(alpha * 255) : (byte)0;
        // Signature cards carry a diagonal wash; a flat brush would
        // preview a different card from the one painted on the desktop.
        if (hasBackground && surface.GradientEnd is { } gradientEnd)
        {
            var end = Parse(gradientEnd, background);
            end.A = background.A;
            CardSurface.Background = new LinearGradientBrush
            {
                StartPoint = new global::Windows.Foundation.Point(0, 0),
                EndPoint = new global::Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = background, Offset = 0 },
                    new GradientStop { Color = end, Offset = 1 }
                }
            };
        }
        else CardSurface.Background = new SolidColorBrush(background);
        CardSurface.CornerRadius = new CornerRadius(WidgetCornerRadius);
        var stroke = BoxBorderStyle.ApplyTint(BoxBorderStyle.ResolveStroke(System.Drawing.Color.FromArgb(background.R, background.G, background.B),
            dark, appearance.Settings.BorderColor, appearance.Settings.BorderOpacity), alpha);
        CardSurface.BorderBrush = new SolidColorBrush(Color.FromArgb(stroke.A, stroke.R, stroke.G, stroke.B));
        CardSurface.BorderThickness = new Thickness(hasBackground && appearance.Settings.ShowBorder ? Math.Clamp(appearance.Settings.BorderWidth, 1, 6) : 0);
        var (width, height) = DesktopWidgets.Dimensions(widget);
        CardSurface.Width = width;
        CardSurface.Height = height;
        // Both of these render without a card title, so the body takes the title row.
        var networkWithoutTitle = widget.Kind == DesktopWidgetKind.Network &&
            widget.NetworkDisplayMode is NetworkWidgetDisplayMode.Speeds or NetworkWidgetDisplayMode.Curve;
        var fullCardBody = networkWithoutTitle || widget.Kind == DesktopWidgetKind.ControlCenter ||
            widget.Kind == DesktopWidgetKind.Weather && width >= 300;
        CardTitle.Visibility = fullCardBody || widget.Kind is DesktopWidgetKind.Clock or DesktopWidgetKind.Calendar or DesktopWidgetKind.Todo or DesktopWidgetKind.Weather ? Visibility.Collapsed : Visibility.Visible;
        _calendarHeader.Visibility = widget.Kind == DesktopWidgetKind.Calendar ? Visibility.Visible : Visibility.Collapsed;
        _calendarUpcomingTitle.Visibility = widget.Kind == DesktopWidgetKind.Calendar && width >= 300 ? Visibility.Visible : Visibility.Collapsed;
        _calendarUpcomingTitle.Foreground = _secondary;
        _todoAdd.Visibility = widget.Kind == DesktopWidgetKind.Todo ? Visibility.Visible : Visibility.Collapsed;
        _todoAdd.Foreground = _accent;
        _todoHeader.Visibility = widget.Kind == DesktopWidgetKind.Todo ? Visibility.Visible : Visibility.Collapsed;
        _todoTitle.Text = widget.DisplayTitle; _todoTitle.Foreground = _text;
        _todoSummary.Text = $"· {widget.Tasks.Count(task => !task.IsCompleted)}";
        _todoSummary.Foreground = _secondary;
        _weatherHeader.Visibility = widget.Kind == DesktopWidgetKind.Weather ? Visibility.Visible : Visibility.Collapsed;
        _weatherHeader.Children.Clear();
        _fileShelfAddButton.Visibility = widget.Kind == DesktopWidgetKind.FileShelf ? Visibility.Visible : Visibility.Collapsed;
        _fileShelfAdd.Foreground = widget.FileShelfBusy ? _secondary : _accent;
        var fileShelfAdd = WidgetFileShelfPresentation.AddButton(WidgetCornerRadius);
        _fileShelfAddButton.Margin = new Thickness(fileShelfAdd.X, fileShelfAdd.Y, 0, 0);
        CardTitle.Margin = widget.Kind is DesktopWidgetKind.Todo or DesktopWidgetKind.Weather ? new Thickness(60, 0, 60, 0) : new Thickness(28, 0, 28, 0);
        if (widget.Kind == DesktopWidgetKind.FileShelf)
            CardTitle.Margin = new Thickness(fileShelfAdd.X + fileShelfAdd.Width + 8, 0, fileShelfAdd.X + fileShelfAdd.Width + 8, 0);
        CardTitle.Text = widget.Kind == DesktopWidgetKind.Calendar ? DateTime.Today.ToString("yyyy.MM") : widget.DisplayTitle;
        CardTitle.TextAlignment = widget.Kind == DesktopWidgetKind.Weather ? TextAlignment.Left : TextAlignment.Center;
        CardTitle.FontSize = widget.Kind == DesktopWidgetKind.Weather ? 12 : 13.333;
        if (widget.Kind == DesktopWidgetKind.Weather)
        {
            CardTitle.Margin = new Thickness(34, 0, 64, 0);
            CardTitle.Text = widget.Location is { } city ? $"{city.Name}  ⌄" : widget.DisplayTitle;
        }
        CardTitle.Foreground = widget.Kind == DesktopWidgetKind.Note ? ContentTone("note") : _text;
        if (solid && widget.Kind is DesktopWidgetKind.Performance or DesktopWidgetKind.Network)
        {
            CardTitle.Margin = new Thickness(12, 0, 32, 0);
            CardTitle.FontSize = 11.5;
            CardTitle.TextAlignment = TextAlignment.Left;
            CardTitle.Foreground = _secondary;
        }
        Grid.SetRow(CardBody, fullCardBody ? 0 : 1);
        Grid.SetRowSpan(CardBody, fullCardBody ? 2 : 1);
        CardBody.Margin = fullCardBody || widget.Kind == DesktopWidgetKind.Weather ? new Thickness(0) :
            widget.Kind == DesktopWidgetKind.Todo ? new Thickness(8, 4, 8, 8) : new Thickness(8);
        CardBody.Children.Clear();
        switch (widget.Kind)
        {
            case DesktopWidgetKind.Clock: Clock(widget); break;
            case DesktopWidgetKind.Calendar: Calendar(widget); break;
            case DesktopWidgetKind.Note: TextCard(widget.Text, "写下第一条记录", "便签"); break;
            case DesktopWidgetKind.Todo: Todo(widget); break;
            case DesktopWidgetKind.Translation: TranslationPreview(widget); break;
            case DesktopWidgetKind.Weather: Weather(widget); break;
            case DesktopWidgetKind.Performance: Performance(widget); break;
            case DesktopWidgetKind.ControlCenter: ControlCenter(widget); break;
            case DesktopWidgetKind.Lyrics: Lyrics(widget); break;
            case DesktopWidgetKind.Network: Network(widget); break;
            case DesktopWidgetKind.FileShelf: FileShelf(widget); break;
        }
        if (widget.Kind != DesktopWidgetKind.Lyrics) ApplyTypography(CardLayout, widget);
        if (widget.Kind == DesktopWidgetKind.Todo)
        {
            var available = new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
            _todoSummary.Measure(available);
            _todoTitle.MaxWidth = Math.Max(0, width - _todoHeader.Margin.Left - _todoHeader.Margin.Right
                - _todoHeader.ColumnSpacing - _todoSummary.DesiredSize.Width);
            _todoTitle.Measure(available);
            _todoSummary.RenderTransform = new TranslateTransform
            {
                Y = (_todoSummary.DesiredSize.Height - _todoTitle.DesiredSize.Height) / 2
                    + _todoTitle.BaselineOffset - _todoSummary.BaselineOffset
            };
        }
    }

    private static void ApplyTypography(FrameworkElement element, DesktopWidget widget)
    {
        switch (element)
        {
            case TextBlock text:
                text.FontFamily = new FontFamily(WidgetTypography.NormalizeFamily(widget.FontFamily));
                text.FontSize *= WidgetTypography.NormalizeScale(widget.FontScale);
                text.FontStyle = widget.FontItalic ? global::Windows.UI.Text.FontStyle.Italic : global::Windows.UI.Text.FontStyle.Normal;
                if (widget.FontWeight != 0) text.FontWeight = widget.FontWeight == 700 ? FontWeights.Bold : FontWeights.Normal;
                break;
            case Panel panel:
                foreach (var child in panel.Children.OfType<FrameworkElement>()) ApplyTypography(child, widget);
                break;
            case Border { Child: FrameworkElement child }:
                ApplyTypography(child, widget);
                break;
            case ContentControl { Content: FrameworkElement content }:
                ApplyTypography(content, widget);
                break;
        }
    }

    private TextBlock Text(string value, double size = 12, bool secondary = false, bool bold = false) => new()
    {
        Text = value, FontSize = size, FontFamily = new FontFamily(BoxAppearance.DefaultFontFamily),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = secondary ? _secondary : _text,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private void Lyrics(DesktopWidget widget)
    {
        var (width, height) = DesktopWidgets.Dimensions(widget);
        var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        CardBody.Children.Add(image);
        var ink = System.Drawing.Color.FromArgb(_text.Color.A, _text.Color.R, _text.Color.G, _text.Color.B);
        var accent = System.Drawing.Color.FromArgb(_accent.Color.A, _accent.Color.R, _accent.Color.G, _accent.Color.B);
        _ = LoadMediaArtworkAsync(image, DesktopLyricsPreview.Render(widget, ink, accent, width - 16, height - 44));
    }

    private void Clock(DesktopWidget widget)
    {
        var panel = new Canvas();
        var (cardWidth, cardHeight) = DesktopWidgets.Dimensions(widget);
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
        var solid = widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid;
        var faceFill = solid ? MixContent(_background, _text.Color, .06)
            : _text.Color.R > 128 ? Color.FromArgb(255, 26, 26, 26) : Color.FromArgb(255, 250, 250, 250);
        var rim = solid ? _accent.Color with { A = 65 } : _text.Color with { A = 35 };
        var majorTick = solid ? new SolidColorBrush(_accent.Color with { A = 220 }) : Ink(150);
        Place(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = new SolidColorBrush(faceFill), Stroke = new SolidColorBrush(rim), StrokeThickness = 1 }, face.X, face.Y, face.Width, face.Height);
        for (var tick = 0; tick < 60; tick++) Stroke(tick * 6, tick % 5 == 0 ? .79 : .85, .9, tick % 5 == 0 ? majorTick : Ink(65), tick % 5 == 0 ? 1.5 : .8);
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
        Stroke(hands.Minute, 0, .72, solid ? _accent : Ink(230), Math.Clamp(face.Width * .018, 1.6, 2.8));
        if (widget.ShowSeconds) Stroke(hands.Second, -.17, .82, solid ? ContentTone("content-muted") : Ink(160), 1.1);
        var center = WidgetClockPresentation.PointOnFace(face, 0, 0);
        var pivot = Math.Clamp(face.Width * .024, 2.2, 3.5);
        Place(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = solid ? _accent : _text }, center.X - pivot, center.Y - pivot, pivot * 2, pivot * 2);
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
            Background = new SolidColorBrush(todayFill), CornerRadius = new CornerRadius(Math.Min(6, WidgetCornerRadius)) };
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
            if (i >= 5) label.Foreground = ContentTone("content-muted");
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
            if (date == today) { cell.Background = _accent; label.Foreground = new SolidColorBrush(InkOnAccent()); }
            else if (i % 7 >= 5 && date.Month == today.Month) label.Foreground = ContentTone("content-muted");
            if (date.Month != today.Month) label.Opacity = .55;
            grid.Children.Add(cell); Grid.SetColumn(cell, i % 7); Grid.SetRow(cell, i / 7 + 1);
        }
        if (DesktopWidgets.Dimensions(widget).Width < 300) { CardBody.Children.Add(grid); return; }
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
        content.TextWrapping = TextWrapping.Wrap; content.MaxLines = Math.Max(1, (int)((CardSurface.Height - 76) / 18));
        grid.Children.Add(content);
        var captionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        captionRow.Children.Add(ContentIcon("content-source", 14));
        captionRow.Children.Add(Text("点击编辑 · 自动保存", 11, true));
        grid.Children.Add(captionRow); Grid.SetRow(captionRow, 1);
        CardBody.Children.Add(grid);
    }

    private void Todo(DesktopWidget widget)
    {
        var panel = new Canvas();
        var width = DesktopWidgets.Dimensions(widget).Width - 16;
        void Place(FrameworkElement element, double x, double y, double elementWidth, double height)
        {
            element.Width = elementWidth; element.Height = height;
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); panel.Children.Add(element);
        }
        var rowTop = 0d;
        foreach (var item in WidgetTodoPresentation.Rows(widget))
        {
            var rowHeight = item.Height;
            if (rowTop + rowHeight > DesktopWidgets.Dimensions(widget).Height - 40) break;
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
                var check = new LucideIcon { Icon = task.IsCompleted ? LucideIconName.SquareCheck : LucideIconName.Square,
                    FontSize = 18, Foreground = task.IsCompleted ? _accent : _secondary };
                Place(check, 3.5, rowTop + 6, 18, 18);
                var title = Text(task.Text, 12.5); title.VerticalAlignment = VerticalAlignment.Center;
                Place(title, 28, rowTop + 2, width - 36, 26);
                if (task.HasDateRange)
                {
                    var dates = Text(WidgetTodoPresentation.DateLabel(task, DateTime.Today.Year), 10.5, true);
                    dates.Foreground = ContentTone("content-check"); dates.VerticalAlignment = VerticalAlignment.Center;
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

    private void FileShelf(DesktopWidget widget)
    {
        var (cardWidth, cardHeight) = DesktopWidgets.Dimensions(widget);
        var width = cardWidth - 16; var height = cardHeight - 44;
        var panel = new Canvas();
        void Place(FrameworkElement element, double x, double y, double elementWidth, double elementHeight)
        {
            element.Width = elementWidth; element.Height = elementHeight;
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); panel.Children.Add(element);
        }
        var layout = WidgetFileShelfPresentation.Arrange(width, height, widget.FontScale);
        var scale = Math.Max(1, WidgetTypography.NormalizeScale(widget.FontScale));
        SolidColorBrush Tint(SolidColorBrush brush, byte alpha) => new(Color.FromArgb(alpha, brush.Color.R, brush.Color.G, brush.Color.B));
        Place(new Border { Background = Tint(_text, 10), CornerRadius = new CornerRadius(6) }, 0, 0, layout.Mode.Width, layout.Mode.Height);
        Place(Text(WidgetFileShelfPresentation.ModeLabel(widget, layout.Mode.Width), 10.5, true), 7, 2 * scale, layout.Mode.Width - 25, layout.Mode.Height - 2 * scale);
        Place(Text("⌄", 12, true), layout.Mode.Width - 16, 0, 12, layout.Mode.Height);
        var count = Text($"{widget.FileShelfItems.Count} 项", 10.5, true); count.TextAlignment = TextAlignment.Right;
        Place(count, layout.Count.X, 2 * scale, layout.Count.Width, layout.Count.Height - 2 * scale);
        var overflow = widget.FileShelfItems.Count > layout.Rows;
        var rowWidth = width - (overflow ? 7 : 0);
        var rowTop = layout.List.Y;
        foreach (var item in widget.FileShelfItems.Take(layout.Rows))
        {
            var missing = item.IsAvailable is false;
            Place(new Border { Background = Tint(_text, 7), CornerRadius = new CornerRadius(6) }, 0, rowTop + 1, rowWidth, layout.RowHeight - 2);
            Place(CreateFileShelfIcon(widget, item), 5, rowTop + (layout.RowHeight - 22) / 2, 22, 22);
            Place(Text(item.Name, 12, missing), 34, rowTop, rowWidth - 38, 18 * scale);
            var detail = Text(WidgetFileShelfPresentation.ItemDetail(item), 10, true);
            if (missing) detail.Foreground = ContentTone("content-muted");
            Place(detail, 34, rowTop + 18 * scale, rowWidth - 38, 14 * scale);
            rowTop += layout.RowHeight;
        }
        if (widget.FileShelfItems.Count == 0)
        {
            Place(new Border { Background = Tint(_accent, 8), BorderBrush = Tint(_accent, 70), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) },
                1, layout.List.Y + 2, width - 2, layout.List.Height - 4);
            var showIcon = layout.List.Height - 4 >= 82 * scale;
            var contentHeight = (showIcon ? 36 : 0) + 36 * scale;
            var y = layout.List.Y + (layout.List.Height - contentHeight) / 2;
            if (showIcon) { Place(ContentIcon("file-shelf", 30), (width - 30) / 2, y, 30, 30); y += 36; }
            var empty = Text("拖入文件，集中暂存", 12, true);
            empty.Foreground = _text; empty.TextAlignment = TextAlignment.Center;
            Place(empty, 5, y, width - 10, 20 * scale);
            var hint = Text("或点击此处添加", 10, true); hint.TextAlignment = TextAlignment.Center;
            Place(hint, 5, y + 20 * scale, width - 10, 16 * scale);
        }
        if (overflow && layout.Rows > 0)
        {
            var trackHeight = layout.Rows * layout.RowHeight - 4;
            Place(new Border { Background = Tint(_text, 12), CornerRadius = new CornerRadius(1.5) }, width - 3, layout.List.Y + 2, 3, trackHeight);
            Place(new Border { Background = Tint(_secondary, 90), CornerRadius = new CornerRadius(1.5) }, width - 3, layout.List.Y + 2, 3,
                Math.Max(12, trackHeight * layout.Rows / widget.FileShelfItems.Count));
        }
        var footerWidth = width;
        if (overflow && width >= 280)
        {
            var rangeWidth = 80 * scale;
            var range = Text($"1–{layout.Rows} / {widget.FileShelfItems.Count}", 10, true); range.TextAlignment = TextAlignment.Right;
            Place(range, width - rangeWidth, layout.Footer.Y, rangeWidth, layout.Footer.Height);
            footerWidth -= rangeWidth + 8;
        }
        Place(Text(WidgetFileShelfPresentation.Footer(widget), 10, true), 0, layout.Footer.Y, footerWidth, layout.Footer.Height);
        CardBody.Children.Add(panel);
    }

    private void TranslationPreview(DesktopWidget widget)
    {
        var panel = new Canvas();
        var (cardWidth, cardHeight) = DesktopWidgets.Dimensions(widget);
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
            var asset = column == 0 ? "content-source" : "content-translation";
            Place(ContentIcon(asset, 16), left, 2, 16, 16);
            header.Foreground = ink; header.VerticalAlignment = VerticalAlignment.Center;
            Place(header, left + 22, -4, Math.Max(0, fieldWidth - 58), 28);
            var enabled = column == 0 ? !string.IsNullOrEmpty(widget.Text) : !string.IsNullOrWhiteSpace(widget.Translation);
            Place(new LucideIcon { Icon = column == 0 ? LucideIconName.Eraser : LucideIconName.Copy, FontSize = 16,
                Foreground = enabled ? ink : disabled }, left + fieldWidth - 28, -4, 28, 28);
            var value = column == 0 ? widget.Text : widget.Translation;
            var empty = string.IsNullOrWhiteSpace(value);
            var content = Text(empty ? column == 0 ? "输入要翻译的文字…" : widget.IsLoading ? "正在翻译…" : "译文将在这里显示" : value[..Math.Min(value.Length, 250)], 13);
            content.Foreground = empty ? muted : ink;
            content.TextWrapping = TextWrapping.Wrap; content.MaxLines = 4;
            var stroke = ContentTone(asset).Color; stroke.A = 85;
            var field = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(Math.Min(8, WidgetCornerRadius)),
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(stroke), Child = content,
                Background = new SolidColorBrush(MixContent(dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 250, 250, 250), ContentTone(asset).Color, .07)) };
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
        Place(new Border { Child = translate, CornerRadius = new CornerRadius(Math.Min(6, WidgetCornerRadius)),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(buttonStroke),
            Background = new SolidColorBrush(buttonFill) }, width - 72, height - 28, 72, 28);
        CardBody.Children.Add(panel);
    }

    private void Weather(DesktopWidget widget)
    {
        var panel = new Canvas();
        var (width, height) = DesktopWidgets.Dimensions(widget);
        var layout = WidgetWeatherPresentation.Arrange(width, height, WidgetCornerRadius, widget.Cities.Count > 1);
        void Place(FrameworkElement element, LayoutRect bounds, bool header = false)
        {
            element.Width = bounds.Width; element.Height = bounds.Height;
            Canvas.SetLeft(element, bounds.X); Canvas.SetTop(element, bounds.Y - (header ? 0 : 36));
            (header ? _weatherHeader : panel).Children.Add(element);
        }
        void Label(string value, double fontSize, LayoutRect bounds, bool secondary = false, bool bold = false)
        {
            var text = Text(value, fontSize, secondary, bold); text.VerticalAlignment = VerticalAlignment.Center;
            Place(text, bounds);
        }
        void Metric(LucideIconName icon, string value, LayoutRect bounds)
        {
            var asset = icon switch { LucideIconName.Thermometer => "content-temperature", LucideIconName.Droplets => "content-humidity", LucideIconName.Wind => "content-wind", _ => null };
            if (asset is null) Place(new LucideIcon { Icon = icon, FontSize = 13, Foreground = _secondary }, bounds with { Width = 14 });
            else Place(ContentIcon(asset, 16), bounds with { Width = 16 });
            Label(value, 12.5, bounds with { X = bounds.X + 19, Width = bounds.Width - 19 }, true);
        }
        // Wide cards keep the header free of location controls; only the city
        // name stays so the summary column has room.
        if (layout.Location.Width > 0)
            Place(new LucideIcon { Icon = LucideIconName.Locate, FontSize = 16, Foreground = _text }, layout.Location, true);
        var cityText = Text(widget.Location is { } city ? $"{city.Name} ⌄" : widget.DisplayTitle, 13, bold: true);
        cityText.VerticalAlignment = VerticalAlignment.Center; Place(cityText, layout.City, true);
        if (layout.Locations.Width > 0)
            Place(new LucideIcon { Icon = LucideIconName.MapPin, FontSize = 15, Foreground = _accent }, layout.Locations, true);
        if (width >= 300)
        {
            WideWeather(widget, layout);
            return;
        }
        Label(widget.Weather is { } weather ? $"{weather.Temperature:0.#}°" : "—°", widget.Cities.Count > 1 && width < 300 ? 38 : 40, layout.Temperature, bold: true);
        Place(new WidgetColorIcon { AssetName = WidgetIconCatalog.GetWeatherName(widget.Weather, DateTimeOffset.Now) }, layout.Icon);
        if (widget.Weather is { } forecast)
        {
            if (forecast.FeelsLike is { } feelsLike) Metric(LucideIconName.Thermometer, $"{feelsLike:0}°", layout.FeelsLike);
            Label($"{forecast.Description} · {forecast.Minimum:0}° / {forecast.Maximum:0}°", 12, layout.Forecast, true);
            if (forecast.Humidity is { } humidity)
            {
                Metric(LucideIconName.Droplets, $"{(width >= 300 ? "湿度 " : "")}{humidity:0}%", layout.Humidity);
            }
            if (forecast.WindSpeed is { } wind)
            {
                Metric(LucideIconName.Wind, $"{(width >= 300 ? "风速 " : "")}{wind:0} km/h", layout.Wind);
            }
            var statusFill = _text.Color; statusFill.A = 14;
            Place(new Border { Background = new SolidColorBrush(statusFill), CornerRadius = new CornerRadius(8) }, layout.Status);
            Metric(forecast.AirQuality is not null ? LucideIconName.Gauge : LucideIconName.ChevronRight,
                widget.IsLoading ? "正在更新…" : !string.IsNullOrEmpty(widget.Error) ? widget.Error :
                forecast.AirQuality is { } air ? $"AQI {air:0}" : "查看天气详情", layout.Status with { X = layout.Status.X + 8, Width = layout.Status.Width - 32 });
            Place(new LucideIcon { Icon = LucideIconName.ChevronRight, FontSize = 12, Foreground = _secondary }, layout.Status with { X = layout.Status.X + layout.Status.Width - 20, Width = 12 });
        }
        else Label("搜索所在城市查看天气", 12, layout.Forecast, true);
        var cityIndex = widget.Cities.FindIndex(city => city.Id == widget.ActiveCityId) + 1;
        if (widget.Cities.Count > 1)
        {
            var counter = Text($"{cityIndex}/{widget.Cities.Count}", 11, true); counter.TextAlignment = TextAlignment.Center;
            counter.VerticalAlignment = VerticalAlignment.Center; Place(counter, layout.Counter);
            foreach (var (label, bounds) in new[] { ("‹", layout.Previous), ("›", layout.Next) })
            { var arrow = Text(label, 18); arrow.TextAlignment = TextAlignment.Center; arrow.VerticalAlignment = VerticalAlignment.Center; Place(arrow, bounds); }
        }
        CardBody.Children.Add(panel);
    }

    private void Performance(DesktopWidget widget)
    {
        var panel = new Canvas();
        var (width, height) = DesktopWidgets.Dimensions(widget);
        var radius = WidgetCornerRadius;
        var body = WidgetMonitorPresentation.Body(width, height, radius);
        var metrics = WidgetMonitorPresentation.PerformanceMetrics(widget.Performance);
        void Place(FrameworkElement element, LayoutRect bounds)
        {
            if (element is TextBlock label)
            {
                label.VerticalAlignment = VerticalAlignment.Center;
                var cell = new Grid(); cell.Children.Add(label); element = cell;
            }
            element.Width = bounds.Width; element.Height = bounds.Height;
            Canvas.SetLeft(element, bounds.X - 8); Canvas.SetTop(element, bounds.Y - 36); panel.Children.Add(element);
        }
        for (var index = 0; index < metrics.Length; index++)
        {
            var cell = WidgetMonitorPresentation.PerformanceCell(body, index); var metric = metrics[index];
            var wide = body.Width >= 300;
            var asset = WidgetContentVisuals.PerformanceAsset(index);
            var solid = widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid;
            var rowInk = solid ? _text.Color : ContentTone(asset).Color;
            rowInk.A = (byte)(solid ? 6 : 16);
            Place(new Border { Background = new SolidColorBrush(rowInk), CornerRadius = new CornerRadius(7) },
                cell with { X = cell.X - 4, Y = cell.Y - 2, Width = cell.Width + 8, Height = cell.Height - 2 });
            Place(ContentIcon(asset, 20), cell with { Width = 20, Height = 20 });
            Place(Text(metric.Label, 12, true), cell with { X = cell.X + 26, Width = cell.Width - 78, Height = 20 });
            var value = Text(metric.Value, 13.5, bold: true); value.TextAlignment = TextAlignment.Center;
            Place(value, cell with { X = cell.X + cell.Width - 52, Width = 52, Height = 20 });
            if (wide) Place(Text(metric.Detail, 10.5, true), cell with { Y = cell.Y + 24, Height = 20 });
            var track = cell with { Y = cell.Y + cell.Height - (wide ? 4 : 7), Height = 3 };
            var trackColor = ContentTone(asset).Color; trackColor.A = 36;
            Place(new Border { Background = new SolidColorBrush(trackColor), CornerRadius = new CornerRadius(Math.Min(1.5, radius)) }, track);
            if (metric.Percentage is { } percentage && double.IsFinite(percentage) && percentage > 0)
                Place(new Border { Background = ContentTone(asset), CornerRadius = new CornerRadius(Math.Min(1.5, radius)) },
                    track with { Width = track.Width * Math.Clamp(percentage, 0, 100) / 100 });
        }
        CardBody.Children.Add(panel);
    }

    /// <summary>
    /// Mirrors the desktop card in the settings preview: same tiles, sliders and
    /// media strip, so the thumbnail matches what will be placed.
    /// </summary>
    private void ControlCenter(DesktopWidget widget)
    {
        var panel = new Canvas();
        var (width, height) = DesktopWidgets.Dimensions(widget);
        var radius = WidgetCornerRadius;
        var body = ControlCenterPresentation.Body(width, height, radius);
        var snapshot = widget.Controls;
        // Same ink rule as the desktop card, so the thumbnail shows the colours
        // the user will actually get.
        var ink = widget.ControlCenterLightText is true ? Color.FromArgb(255, 255, 255, 255)
            : widget.ControlCenterLightText is false ? Color.FromArgb(255, 31, 35, 41) : _text.Color;
        var softInk = widget.ControlCenterLightText is true ? Color.FromArgb(255, 214, 220, 230)
            : widget.ControlCenterLightText is false ? Color.FromArgb(255, 70, 78, 90) : _secondary.Color;
        var baseColor = _background;
        var surfaceInk = _text.Color;
        var solid = widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid;
        var panelFill = MixContent(baseColor, surfaceInk, solid ? .04 : .10);
        TextBlock ControlText(string value, double size = 12, bool secondary = false)
        {
            var text = Text(value, size, secondary);
            text.Foreground = new SolidColorBrush(secondary ? softInk : ink);
            return text;
        }
        void Place(FrameworkElement element, LayoutRect bounds)
        {
            Canvas.SetLeft(element, bounds.X); Canvas.SetTop(element, bounds.Y);
            element.Width = bounds.Width; element.Height = bounds.Height;
            panel.Children.Add(element);
        }

        var quickActions = ControlQuickActions.For(widget);
        for (var index = 0; index < quickActions.Count; index++)
        {
            var action = quickActions[index];
            var toggle = ControlQuickActions.Toggle(action);
            var cell = ControlCenterPresentation.QuickAction(body, index, quickActions.Count);
            var available = toggle is null || ControlCenterPresentation.ToggleAvailable(toggle.Value, snapshot);
            var on = toggle switch
            {
                ControlCenterToggle.WiFi => snapshot?.WiFiEnabled is true,
                ControlCenterToggle.Bluetooth => snapshot?.BluetoothEnabled is true,
                ControlCenterToggle.NightLight => snapshot?.NightLightEnabled is true,
                _ => false
            };
            var tileInk = new SolidColorBrush(solid && on ? Colors.White : available ? ink : softInk);
            var asset = WidgetContentVisuals.ControlAsset(action, snapshot);
            var tone = Parse(WidgetContentVisuals.Tone(asset, surfaceInk.R > 128), _accent.Color);
            var tileFill = solid
                ? on ? MixContent(Parse(WidgetContentVisuals.Tone(asset, false), _accent.Color), Colors.Black, .12)
                    : MixContent(panelFill, surfaceInk, .03)
                : MixContent(panelFill, available ? tone : surfaceInk, on ? .30 : available ? .12 : .06);
            var tile = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(tileFill),
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new LucideIcon { Icon = action switch
                        {
                            ControlCenterQuickAction.WiFi => snapshot?.WiFiEnabled is false ? LucideIconName.WifiOff : LucideIconName.Wifi,
                            ControlCenterQuickAction.Bluetooth => LucideIconName.Bluetooth,
                            ControlCenterQuickAction.NightLight => snapshot?.NightLightEnabled is false ? LucideIconName.Sun : LucideIconName.Moon,
                            ControlCenterQuickAction.Display => LucideIconName.Monitor,
                            ControlCenterQuickAction.Cast => LucideIconName.Cast,
                            ControlCenterQuickAction.Microphone => LucideIconName.Mic,
                            ControlCenterQuickAction.ApplicationVolume => LucideIconName.SlidersHorizontal,
                            ControlCenterQuickAction.ScreenCapture => LucideIconName.ScanLine,
                            ControlCenterQuickAction.NetworkAdapters => LucideIconName.Network,
                            _ => LucideIconName.Cog
                        }, FontSize = 22, Width = 22, Height = 22, Foreground = new SolidColorBrush(solid && on ? Colors.White : available ? tone : softInk),
                            HorizontalAlignment = HorizontalAlignment.Center, Opacity = available ? 1 : .45 },
                        new TextBlock { Text = ControlQuickActions.Label(action, snapshot), FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Foreground = tileInk },
                        new TextBlock { Text = ControlQuickActions.Status(action, snapshot), FontSize = 9,
                            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(3, 2, 3, 0),
                            Foreground = tileInk }
                    }
                }
            };
            if (ControlQuickActions.HasDetails(action))
            {
                var content = tile.Child;
                tile.Child = null;
                var combined = new Grid(); combined.Children.Add(content);
                combined.Children.Add(new LucideIcon { Icon = LucideIconName.ChevronRight, Width = 10, Height = 12,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 8, 5, 0),
                    Foreground = tileInk });
                tile.Child = combined;
            }
            Place(tile, cell);
        }

        var sliders = ControlCenterPresentation.SliderArea(body);
        var half = (sliders.Width - 14) / 2;
        Slider("音量", ControlCenterPresentation.VolumeText(snapshot), snapshot?.VolumePercent,
            new LayoutRect(sliders.X, sliders.Y, half, sliders.Height), labelInset: 26, valueInset: 26);
        var muteButton = ControlCenterPresentation.VolumeMuteButton(body);
        Place(new LucideIcon { Icon = snapshot?.VolumeMuted is true ? LucideIconName.VolumeX : LucideIconName.Volume2,
            FontSize = 14, Foreground = new SolidColorBrush(ink), Opacity = snapshot?.VolumeMuted is not null ? 1 : .4 },
            new LayoutRect(muteButton.X + 4, muteButton.Y + 3, 14, 14));
        var mixerButton = ControlCenterPresentation.VolumeMixerButton(body);
        Place(new LucideIcon { Icon = LucideIconName.SlidersHorizontal, Foreground = new SolidColorBrush(ink) },
            new LayoutRect(mixerButton.X + 4, mixerButton.Y + 3, 14, 14));
        Slider("亮度", ControlCenterPresentation.BrightnessText(snapshot), snapshot?.BrightnessPercent,
            new LayoutRect(sliders.X + half + 14, sliders.Y, half, sliders.Height), labelInset: 20);
        Place(new LucideIcon { Icon = LucideIconName.Sun, FontSize = 16, Foreground = new SolidColorBrush(ink),
            Opacity = snapshot?.BrightnessPercent is null ? .45 : 1 },
            new(sliders.X + half + 14, sliders.Y, 16, 16));

        void Slider(string label, string value, double? percent, LayoutRect area, double labelInset = 0, double valueInset = 0)
        {
            var color = ContentTone(label == "音量" ? "content-volume" : "content-brightness");
            var header = ControlText(label, 11, true);
            Place(header, area with { X = area.X + labelInset, Width = area.Width / 2 - labelInset, Height = 16 });
            var amount = ControlText(value, 11, true); amount.TextAlignment = TextAlignment.Right;
            Place(amount, area with { Width = area.Width - valueInset, Height = 16 });
            var track = new Border
            {
                CornerRadius = new CornerRadius(2.5), Height = 5,
                Background = new SolidColorBrush(_text.Color with { A = 28 }),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Border
                {
                    CornerRadius = new CornerRadius(2.5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = percent is { } level ? area.Width * ControlCenterPresentation.ClampPercent(level) / 100 : 0,
                    Background = color
                }
            };
            Place(track, area with { Y = area.Y + 24, Height = 5 });
            if (percent is { } current && double.IsFinite(current) && current > 0)
            {
                var position = area.Width * ControlCenterPresentation.ClampPercent(current) / 100;
                Place(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = color }, new(area.X + position - 5, area.Y + 21.5, 10, 10));
            }
        }

        var displayLabel = ControlText("亮度 · " + (string.IsNullOrEmpty(widget.ControlBrightnessDisplayId) ? "组件所在显示器" : widget.ControlBrightnessDisplayId.Replace(@"\\.\DISPLAY", "显示器 ")), 10.5);
        displayLabel.VerticalAlignment = VerticalAlignment.Center;
        displayLabel.Margin = new Thickness(28, 0, 24, 0);
        var displayContent = new Grid(); displayContent.Children.Add(displayLabel);
        displayContent.Children.Add(new LucideIcon { Icon = LucideIconName.Monitor, FontSize = 14, Width = 14, Height = 14, Foreground = new SolidColorBrush(ink),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0, 0, 0) });
        displayContent.Children.Add(new LucideIcon { Icon = LucideIconName.ChevronsUpDown, Width = 13, Height = 13,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0), Foreground = new SolidColorBrush(softInk) });
        Place(new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(MixContent(panelFill, surfaceInk, solid ? .04 : .14)), Child = displayContent },
            ControlCenterPresentation.BrightnessDisplayRow(body));
        var media = ControlCenterPresentation.MediaArea(body);
        var outputRow = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(MixContent(panelFill, surfaceInk, solid ? .04 : .14)),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new LucideIcon { Icon = snapshot?.OutputDeviceName is { } output &&
                        (output.Contains("耳机") || output.Contains("headphone", StringComparison.OrdinalIgnoreCase) || output.Contains("headset", StringComparison.OrdinalIgnoreCase))
                        ? LucideIconName.Headphones : LucideIconName.Volume2, FontSize = 14, Width = 14, Height = 14, Foreground = new SolidColorBrush(ink) },
                    ControlText(snapshot?.OutputDeviceName is { Length: > 0 } outputName ? outputName : "输出设备不可用", 10.5, true)
                }
            }
        };
        Place(outputRow, ControlCenterPresentation.OutputRow(media));
        Place(new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(MixContent(panelFill, surfaceInk, solid ? .04 : .14)),
            Child = new LucideIcon { Icon = LucideIconName.Mic, FontSize = 14, Foreground = new SolidColorBrush(ink) } },
            ControlCenterPresentation.MicrophoneButton(body));

        var mediaPanel = ControlCenterPresentation.MediaPanel(media);
        var trackMedia = snapshot?.Media;
        Place(new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(panelFill) }, mediaPanel);
        var artwork = new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(MixContent(panelFill, surfaceInk, .08)) };
        if (trackMedia?.Artwork is { Length: > 0 } bytes)
        {
            var cover = new Image { Stretch = Stretch.UniformToFill };
            artwork.Child = cover;
            _ = LoadMediaArtworkAsync(cover, bytes);
        }
        else artwork.Child = new LucideIcon { Icon = LucideIconName.Music, FontSize = 24, Foreground = new SolidColorBrush(softInk) };
        Place(artwork, ControlCenterPresentation.MediaArtwork(mediaPanel));
        Place(ControlText(trackMedia is null ? "没有正在播放的媒体" : string.IsNullOrWhiteSpace(trackMedia.Title) ? "未知曲目" : trackMedia.Title, 11.5),
            ControlCenterPresentation.MediaTitle(mediaPanel));
        Place(ControlText(trackMedia is null ? "" : string.IsNullOrWhiteSpace(trackMedia.Artist) ? trackMedia.Album : trackMedia.Artist, 10, true),
            ControlCenterPresentation.MediaArtist(mediaPanel));
        var buttons = ControlCenterPresentation.MediaButtonRects(mediaPanel);
        var icons = new[] { LucideIconName.SkipBack, trackMedia?.IsPlaying is true ? LucideIconName.Pause : LucideIconName.Play, LucideIconName.SkipForward };
        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            Place(new LucideIcon { Icon = icons[index], Foreground = new SolidColorBrush(ink),
                Opacity = trackMedia is null ? .35 : 1 }, new(button.X + (button.Width - 15) / 2, button.Y + 3.5, 15, 15));
        }
        if (trackMedia is { Duration.TotalSeconds: > 0 })
        {
            var now = DateTimeOffset.Now;
            var trackBounds = ControlCenterPresentation.MediaProgress(mediaPanel);
            var ratio = trackMedia.PositionNow(now).TotalSeconds / trackMedia.Duration.TotalSeconds;
            Place(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(_text.Color with { A = 30 }),
                Child = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
                    Width = ratio * trackBounds.Width, Background = new SolidColorBrush(_accent.Color) } }, trackBounds);
            if (trackMedia.CanSeek) Place(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(_accent.Color) },
                new(trackBounds.X + ratio * trackBounds.Width - 3, trackBounds.Y - 1, 6, 6));
            var time = ControlText(ControlCenterPresentation.MediaTimeText(trackMedia, now), 9, true);
            time.TextAlignment = TextAlignment.Right;
            Place(time, ControlCenterPresentation.MediaTime(mediaPanel));
        }

        CardBody.Children.Add(panel);
    }

    private static async Task LoadMediaArtworkAsync(Image image, byte[] bytes)
    {
        try
        {
            using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using var writer = new global::Windows.Storage.Streams.DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            stream.Seek(0);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (Exception) { }
    }

    private void Network(DesktopWidget widget)
    {
        var panel = new Canvas();
        var (width, height) = DesktopWidgets.Dimensions(widget);
        var radius = WidgetCornerRadius;
        var fields = WidgetMonitorPresentation.NetworkFields(WidgetMonitorPresentation.Body(width, height, radius));
        var network = widget.Network;
        var speedsOnly = widget.NetworkDisplayMode == NetworkWidgetDisplayMode.Speeds;
        var uploadInk = ContentTone("content-upload");
        var downloadInk = ContentTone("content-download");
        void Place(FrameworkElement element, LayoutRect bounds)
        {
            if (element is TextBlock label)
            {
                label.VerticalAlignment = VerticalAlignment.Center;
                var cell = new Grid(); cell.Children.Add(label); element = cell;
            }
            element.Width = bounds.Width; element.Height = bounds.Height;
            Canvas.SetLeft(element, bounds.X - (speedsOnly ? 0 : 8));
            Canvas.SetTop(element, bounds.Y - (speedsOnly ? 0 : 36)); panel.Children.Add(element);
        }
        void Label(string value, double size, LayoutRect bounds, bool secondary = false, bool bold = false, bool rightAligned = false)
        {
            var label = Text(value, size, secondary, bold); label.VerticalAlignment = VerticalAlignment.Center;
            if (rightAligned) label.TextAlignment = TextAlignment.Right;
            Place(label, bounds);
        }
        void Speed(LucideIconName direction, string? caption, double? speed, LayoutRect bounds, SolidColorBrush? ink = null, bool compact = false)
        {
            var asset = direction == LucideIconName.ArrowUpToLine ? "content-upload" : "content-download";
            if (compact)
            {
                Place(new LucideIcon { Icon = direction, FontSize = 18, Foreground = ContentTone(asset) }, bounds with { Width = 18 });
                SpeedValue(speed, bounds with { X = bounds.X + 22, Width = bounds.Width - 22 }, 18, rightAligned: true);
                return;
            }
            Place(ContentWash(asset, widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid ? (byte)10 : (byte)18),
                bounds with { X = bounds.X - 3, Y = bounds.Y - 2, Width = bounds.Width + 6, Height = bounds.Height + 4 });
            if (caption is null)
            {
                Place(new LucideIcon { Icon = direction, FontSize = 16, Foreground = ContentTone(asset) },
                    bounds with { Y = bounds.Y + (bounds.Height - 16) / 2, Width = 16, Height = 16 });
                SpeedValue(speed, bounds with { X = bounds.X + 22, Width = bounds.Width - 22 }, 21);
                return;
            }
            Place(new LucideIcon { Icon = direction, FontSize = 16, Foreground = ContentTone(asset) }, bounds with { Width = 16, Height = 16 });
            Label(caption, 10.5, bounds with { X = bounds.X + 19, Width = bounds.Width - 19, Height = 14 }, true);
            SpeedValue(speed, bounds with { Y = bounds.Y + 14, Height = bounds.Height - 14 }, bounds.Height < 40 ? 20 : 21);
        }
        void SpeedValue(double? speed, LayoutRect bounds, double size, bool rightAligned = false)
        {
            var label = Text(WidgetMonitorMetrics.Speed(speed), size, false, true);
            label.TextAlignment = rightAligned ? TextAlignment.Right : TextAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            Place(label, bounds);
        }
        if (speedsOnly)
        {
            var speeds = WidgetNetworkGraph.SpeedLayout(width, height, radius);
            Speed(LucideIconName.ArrowDownToLine, "下载", network?.DownloadBytesPerSecond, speeds.Download, _accent, width < 300);
            Speed(LucideIconName.ArrowUpToLine, "上传", network?.UploadBytesPerSecond, speeds.Upload, uploadInk, width < 300);
            CardBody.Children.Add(panel);
            return;
        }
        if (widget.NetworkDisplayMode is NetworkWidgetDisplayMode.Curve or NetworkWidgetDisplayMode.LegacyChart)
        {
            // One curve style now: headline speeds above a plot that fills the
            // rest of the card.
            var layout = WidgetNetworkGraph.CurveLayout(width, height, radius);
            Speed(LucideIconName.ArrowDownToLine, "下载", network?.DownloadBytesPerSecond, layout.Download, _accent);
            Speed(LucideIconName.ArrowUpToLine, "上传", network?.UploadBytesPerSecond, layout.Upload, uploadInk);
            var graph = WidgetNetworkGraph.Project(widget.NetworkHistory, layout.Plot, DateTimeOffset.Now);
            var plot = new Canvas();
            var gridColor = _text.Color; gridColor.A = 24;
            for (var row = 0; row <= 2; row++)
            {
                var y = layout.Plot.Height * row / 2;
                plot.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = 0, X2 = layout.Plot.Width, Y1 = y, Y2 = y,
                    Stroke = new SolidColorBrush(gridColor), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            }
            void Series(IReadOnlyList<WidgetNetworkGraph.Point[]> segments, SolidColorBrush ink)
            {
                foreach (var segment in segments)
                {
                    if (segment.Length == 1)
                    {
                        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, Fill = ink };
                        Canvas.SetLeft(dot, segment[0].X - layout.Plot.X - 2); Canvas.SetTop(dot, segment[0].Y - layout.Plot.Y - 2);
                        plot.Children.Add(dot);
                    }
                    else if (segment.Length > 1)
                    {
                        var points = new PointCollection();
                        foreach (var point in segment) points.Add(new global::Windows.Foundation.Point(point.X - layout.Plot.X, point.Y - layout.Plot.Y));
                        plot.Children.Add(new Microsoft.UI.Xaml.Shapes.Polyline { Points = points, Stroke = ink, StrokeThickness = 2,
                            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                    }
                }
            }
            Series(graph.Download, downloadInk); Series(graph.Upload, uploadInk);
            Place(plot, layout.Plot);
            if (graph.Download.Count == 0 && graph.Upload.Count == 0)
            {
                var hint = Text(network is { IsConnected: false } ? "未连接网络" : "正在积累数据…", 11, true);
                hint.TextAlignment = TextAlignment.Center; Place(hint, layout.Plot);
            }
            Label("最近 60 秒", 10, layout.Footer with { Width = layout.Footer.Width / 2 }, true);
            var maximum = Text("上限 " + WidgetMonitorMetrics.Speed(graph.Maximum), 10, true);
            maximum.TextAlignment = TextAlignment.Right;
            Place(maximum, layout.Footer with { X = layout.Footer.X + layout.Footer.Width / 2, Width = layout.Footer.Width / 2 });
            CardBody.Children.Add(panel);
            return;
        }
        Speed(LucideIconName.ArrowDownToLine, null, network?.DownloadBytesPerSecond, fields.Download);
        Speed(LucideIconName.ArrowUpToLine, null, network?.UploadBytesPerSecond, fields.Upload);
        Place(new LucideIcon { Icon = LucideIconName.Network, FontSize = 16, Foreground = ContentTone("content-network"),
            Opacity = network?.IsConnected is false ? .45 : 1 }, fields.Connection with { Y = fields.Connection.Y + 1, Width = 16, Height = 16 });
        Label(WidgetMonitorPresentation.ConnectionText(network), 11.5, fields.Connection with { X = fields.Connection.X + 21, Width = Math.Max(0, fields.Connection.Width - 21) }, true);
        Place(new LucideIcon { Icon = LucideIconName.Clock, FontSize = 16, Foreground = ContentTone("content-latency") },
            fields.Latency with { Y = fields.Latency.Y + 1, Width = 16, Height = 16 });
        Label(network?.LatencyMilliseconds is { } latency ? "延迟 " + latency + " ms" : "延迟 " + (network?.LatencyStatus ?? "等待测量"), 11,
            fields.Latency with { X = fields.Latency.X + 21, Width = Math.Max(0, fields.Latency.Width - 21) }, true);
        Label("进程排行 · 全部网卡", 10.5, fields.RankingTitle, true);
        foreach (var upload in new[] { false, true })
        {
            var selected = widget.NetworkProcessSort == (upload ? NetworkProcessSort.Upload : NetworkProcessSort.Download);
            var button = upload ? fields.SortUpload : fields.SortDownload;
            if (selected) Place(ContentWash(upload ? "content-upload" : "content-download", 28), button);
            var label = Text(upload ? "上传" : "下载", 10.5, !selected, selected);
            label.TextAlignment = TextAlignment.Center;
            Place(label, button);
        }
        var processes = network?.Processes ?? WidgetNetworkProcesses.Waiting;
        var top = processes.Top(widget.NetworkProcessSort);
        if (processes.Status != NetworkProcessStatus.Ready || top.Count == 0)
        {
            Label(processes.Message(widget.NetworkProcessSort), 11, fields.Processes[0] with { Height = 24 }, true);
            if (processes.Status == NetworkProcessStatus.PermissionRequired)
                Label("请以管理员身份运行 CrabDesk", 10, fields.Processes[2], true);
        }
        else
            for (var index = 0; index < top.Count; index++)
            {
                var row = fields.Processes[index];
                Label((index + 1).ToString(), 10, row with { Width = 16 }, true);
                Label(top[index].Name, 11, row with { X = row.X + 19, Width = Math.Max(0, row.Width - 113) });
                Label(WidgetMonitorMetrics.Speed(top[index].Rate(widget.NetworkProcessSort)), 10.5,
                    row with { X = row.X + row.Width - 90, Width = 90 }, rightAligned: true);
            }
        CardBody.Children.Add(panel);
    }

    private Color InkOnAccent() =>
        _accent.Color.R * .299 + _accent.Color.G * .587 + _accent.Color.B * .114 < 150 ? Colors.White : Color.FromArgb(255, 31, 35, 41);

    private static Color Parse(string value, Color fallback) => HexColor.TryParse(value, out var color) ? color : fallback;
}
