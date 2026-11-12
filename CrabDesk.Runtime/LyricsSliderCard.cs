using System.Drawing;
using CrabDesk.Core;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

internal sealed class LyricsSliderCard : System.Windows.Forms.Integration.ElementHost
{
    internal const int CardWidth = 240;
    internal const int CardHeight = 96;
    internal WpfControls.Slider Slider { get; }
    internal WpfControls.Button DecreaseButton { get; }
    internal WpfControls.Button IncreaseButton { get; }
    internal WpfControls.Button ResetButton { get; }
    internal WpfControls.TextBlock ValueText { get; }
    internal Wpf.FrameworkElement ContentView { get; }
    internal event Action<double>? ValueChanged;
    private readonly Func<double, string> _format;
    private bool _snapping;
    private bool _dragging;
    private double? _pendingValue;
    private readonly System.Windows.Threading.DispatcherTimer _valueTimer;

    internal LyricsSliderCard(string title, string resetText, double minimum, double maximum,
        double step, double largeChange, double value, double defaultValue,
        string minimumLabel, string maximumLabel, Func<double, string> format,
        bool dark, Color accent, double scale)
    {
        _format = format;
        _valueTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(80) };
        _valueTimer.Tick += (_, _) => FlushPendingValue();
        Size = new((int)Math.Round(CardWidth * scale), (int)Math.Round(CardHeight * scale));
        BackColor = dark ? Color.FromArgb(37, 40, 45) : Color.FromArgb(252, 252, 252);
        AccessibleName = title;
        var ink = Brush(dark ? Color.FromArgb(242, 244, 248) : Color.FromArgb(31, 35, 41));
        var secondary = Brush(dark ? Color.FromArgb(176, 183, 195) : Color.FromArgb(102, 110, 124));
        var grid = new WpfControls.Grid();
        foreach (var height in new[] { 24d, 4, 26, 18 }) grid.RowDefinitions.Add(new() { Height = new Wpf.GridLength(height) });
        var header = new WpfControls.Grid();
        header.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = Wpf.GridLength.Auto });
        ValueText = Label(format(value), 16, Brush(accent), true);
        ValueText.FontFamily = new Media.FontFamily("Segoe UI");
        header.Children.Add(ValueText);
        grid.Children.Add(header);

        var buttonStyle = new Wpf.Style(typeof(WpfControls.Button), (Wpf.Style)Wpf.Markup.XamlReader.Parse(AudioMixerPopup.ButtonStyle));
        buttonStyle.Setters.Add(new Wpf.Setter(WpfControls.Control.BackgroundProperty,
            Brush(dark ? Color.FromArgb(51, 57, 67) : Color.FromArgb(237, 240, 245))));
        Slider = new WpfControls.Slider
        {
            Minimum = minimum, Maximum = maximum, Value = value, SmallChange = step, LargeChange = largeChange,
            TickFrequency = step, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true,
            FocusVisualStyle = null, Cursor = System.Windows.Input.Cursors.Hand,
            Template = (WpfControls.ControlTemplate)Wpf.Markup.XamlReader.Parse(AudioMixerPopup.SliderTemplate),
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        Automation.SetName(Slider, title);
        Automation.SetHelpText(Slider, minimumLabel + "；" + maximumLabel + "。方向键微调。");
        var adjustment = new WpfControls.Grid();
        foreach (var width in new[] { 24d, 8, double.NaN, 8, 24 })
            adjustment.ColumnDefinitions.Add(new() { Width = double.IsNaN(width)
                ? new Wpf.GridLength(1, Wpf.GridUnitType.Star) : new Wpf.GridLength(width) });
        DecreaseButton = StepButton("−", "减少");
        IncreaseButton = StepButton("+", "增加");
        DecreaseButton.Click += (_, _) => Slider.Value = Math.Max(Slider.Minimum, Slider.Value - Slider.SmallChange);
        IncreaseButton.Click += (_, _) => Slider.Value = Math.Min(Slider.Maximum, Slider.Value + Slider.SmallChange);
        adjustment.Children.Add(DecreaseButton);
        WpfControls.Grid.SetColumn(Slider, 2);
        adjustment.Children.Add(Slider);
        WpfControls.Grid.SetColumn(IncreaseButton, 4);
        adjustment.Children.Add(IncreaseButton);
        WpfControls.Grid.SetRow(adjustment, 2);
        grid.Children.Add(adjustment);
        var limits = new WpfControls.Grid();
        limits.Children.Add(Label(minimumLabel, 10.5, secondary));
        var maximumText = Label(maximumLabel, 10.5, secondary);
        maximumText.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        limits.Children.Add(maximumText);
        WpfControls.Grid.SetRow(limits, 3);
        grid.Children.Add(limits);

        ResetButton = new WpfControls.Button
        {
            Content = Label(resetText, 11, ink), Width = 64, Height = 24,
            HorizontalAlignment = Wpf.HorizontalAlignment.Right, Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null, Style = buttonStyle
        };
        Automation.SetName(ResetButton, resetText);
        ResetButton.Click += (_, _) => Slider.Value = defaultValue;
        WpfControls.Grid.SetColumn(ResetButton, 1);
        header.Children.Add(ResetButton);

        var content = new WpfControls.Border { Padding = new Wpf.Thickness(12), Background = Brush(BackColor), Child = grid };
        content.Resources["MixerAccent"] = Brush(accent);
        content.Resources["MixerTrack"] = Brush(dark ? Color.FromArgb(88, 95, 109) : Color.FromArgb(207, 214, 225));
        content.Resources["MixerHover"] = Brush(dark ? Color.FromArgb(61, 67, 80) : Color.FromArgb(228, 233, 242));
        ContentView = content;
        Child = content;
        Slider.ValueChanged += OnValueChanged;
        Slider.AddHandler(WpfControls.Primitives.Thumb.DragStartedEvent,
            new WpfControls.Primitives.DragStartedEventHandler((_, _) => _dragging = true));
        Slider.AddHandler(WpfControls.Primitives.Thumb.DragCompletedEvent,
            new WpfControls.Primitives.DragCompletedEventHandler((_, _) => { _dragging = false; FlushPendingValue(); }));
        UpdateStepButtons();

        WpfControls.Button StepButton(string symbol, string action)
        {
            var button = new WpfControls.Button
            {
                Content = Label(symbol, 17, ink), Width = 24, Height = 24,
                VerticalAlignment = Wpf.VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null, Style = buttonStyle,
                ToolTip = $"{action} {format(step).TrimStart('+')}"
            };
            Automation.SetName(button, title + action);
            Automation.SetHelpText(button, (string)button.ToolTip);
            return button;
        }
    }

    private void OnValueChanged(object sender, Wpf.RoutedPropertyChangedEventArgs<double> args)
    {
        if (_snapping) return;
        var value = Math.Clamp(Math.Round(args.NewValue / Slider.TickFrequency, MidpointRounding.AwayFromZero)
            * Slider.TickFrequency, Slider.Minimum, Slider.Maximum);
        _snapping = true;
        try { Slider.Value = value; }
        finally { _snapping = false; }
        ValueText.Text = _format(value);
        UpdateStepButtons();
        if (_dragging)
        {
            _pendingValue = value;
            if (!_valueTimer.IsEnabled) _valueTimer.Start();
        }
        else
        {
            _pendingValue = null;
            _valueTimer.Stop();
            ValueChanged?.Invoke(value);
        }
    }

    internal void FlushPendingValue()
    {
        _valueTimer.Stop();
        if (_pendingValue is not { } value) return;
        _pendingValue = null;
        ValueChanged?.Invoke(value);
    }

    internal void FinishAdjustment()
    {
        _dragging = false;
        FlushPendingValue();
    }

    internal void ApplyPopupMaterial(System.Windows.Forms.ToolStripDropDown menu, BoxAppearance appearance,
        GlobalAppearanceSettings settings, bool? acrylic = null)
    {
        if (ContentView is WpfControls.Border content) content.Background = Media.Brushes.Transparent;
        _ = new PopupCardMaterial(menu, this, appearance, settings, settings.AnimationEnabled, acrylic);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) FinishAdjustment();
        base.Dispose(disposing);
    }

    private void UpdateStepButtons()
    {
        DecreaseButton.IsEnabled = Slider.Value > Slider.Minimum;
        IncreaseButton.IsEnabled = Slider.Value < Slider.Maximum;
    }

    private static WpfControls.TextBlock Label(string text, double size, Media.Brush ink, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = ink, FontFamily = new Media.FontFamily(BoxAppearance.DefaultFontFamily),
        FontWeight = bold ? Wpf.FontWeights.SemiBold : Wpf.FontWeights.Normal,
        VerticalAlignment = Wpf.VerticalAlignment.Center, TextTrimming = Wpf.TextTrimming.CharacterEllipsis
    };

    private static Media.SolidColorBrush Brush(Color color) => new(Media.Color.FromArgb(color.A, color.R, color.G, color.B));
}
