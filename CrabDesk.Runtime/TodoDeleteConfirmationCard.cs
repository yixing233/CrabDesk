using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

/// <summary>A compact confirmation hosted by the desktop's existing no-activate popup.</summary>
internal sealed class TodoDeleteConfirmationCard : Forms.Integration.ElementHost
{
    internal WpfControls.Button CancelButton { get; }
    internal WpfControls.Button DeleteButton { get; }
    internal WpfControls.TextBlock TaskText { get; }
    internal Wpf.FrameworkElement ContentView { get; }

    internal TodoDeleteConfirmationCard(string taskText, bool dark, double scale)
    {
        Size = new((int)Math.Round(272 * scale), (int)Math.Round(148 * scale));
        BackColor = dark ? Color.FromArgb(37, 40, 45) : Color.FromArgb(252, 252, 252);
        ForeColor = dark ? Color.FromArgb(244, 245, 247) : Color.FromArgb(32, 36, 42);
        var body = new WpfControls.Grid();
        body.RowDefinitions.Add(new() { Height = new Wpf.GridLength(24) });
        body.RowDefinitions.Add(new() { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = new Wpf.GridLength(30) });
        body.Children.Add(Label("删除这条待办？", 14, ForeColor, true));
        TaskText = Label(taskText, 12, dark ? Color.FromArgb(195, 199, 206) : Color.FromArgb(90, 95, 103));
        TaskText.TextWrapping = Wpf.TextWrapping.Wrap;
        TaskText.MaxHeight = 36;
        TaskText.Margin = new Wpf.Thickness(0, 6, 0, 8);
        body.Children.Add(TaskText); WpfControls.Grid.SetRow(TaskText, 1);
        var actions = new WpfControls.StackPanel { Orientation = WpfControls.Orientation.Horizontal, HorizontalAlignment = Wpf.HorizontalAlignment.Right };
        CancelButton = Button("取消", false);
        DeleteButton = Button("删除", true);
        DeleteButton.Margin = new Wpf.Thickness(8, 0, 0, 0);
        actions.Children.Add(CancelButton); actions.Children.Add(DeleteButton);
        body.Children.Add(actions); WpfControls.Grid.SetRow(actions, 2);
        ContentView = new WpfControls.Border { Padding = new Wpf.Thickness(16), Child = body };
        ContentView.Resources["MixerHover"] = Brush(dark ? Color.FromArgb(61, 67, 80) : Color.FromArgb(228, 233, 242));
        Child = ContentView;
        WpfControls.Button Button(string text, bool destructive)
        {
            var button = new WpfControls.Button { Content = Label(text, 12,
                destructive ? dark ? Color.FromArgb(255, 185, 180) : Color.FromArgb(165, 33, 29) : ForeColor),
                MinWidth = 68, Height = 30, Cursor = System.Windows.Input.Cursors.Hand,
                Style = (Wpf.Style)Wpf.Markup.XamlReader.Parse(AudioMixerPopup.ButtonStyle),
                Background = Brush(destructive ? dark ? Color.FromArgb(90, 46, 48) : Color.FromArgb(252, 233, 232)
                    : dark ? Color.FromArgb(55, 60, 68) : Color.FromArgb(237, 239, 242)) };
            Automation.SetName(button, text);
            return button;
        }
    }

    private static WpfControls.TextBlock Label(string text, double size, Color color, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = Brush(color),
        FontFamily = new Media.FontFamily(BoxAppearance.DefaultFontFamily),
        FontWeight = bold ? Wpf.FontWeights.SemiBold : Wpf.FontWeights.Normal,
        TextTrimming = Wpf.TextTrimming.CharacterEllipsis, VerticalAlignment = Wpf.VerticalAlignment.Center
    };

    private static Media.SolidColorBrush Brush(Color color) => new(Media.Color.FromArgb(color.A, color.R, color.G, color.B));
}
