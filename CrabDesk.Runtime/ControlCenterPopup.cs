using System.Drawing;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;
using Input = System.Windows.Input;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

internal abstract class ControlCenterPopup : DesktopWidgetInputForm
{
    private readonly WpfControls.Border _content;
    private readonly Wpf.Style _buttonStyle;
    protected readonly WpfControls.Grid Body = new();
    protected readonly WpfControls.StackPanel Footer = new();
    protected readonly Media.Brush ForegroundInk, SecondaryInk, AccentInk, HoverInk, TrackInk;
    private readonly WpfControls.TextBlock _status;
    protected bool IsClosing { get; private set; }
    internal Wpf.FrameworkElement ContentView => _content;

    protected ControlCenterPopup(string title, bool dark, Color accent, double scale, int width, int height,
        LucideRuntimeIcon? titleIcon = null)
    {
        Text = AccessibleName = title;
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        ClientSize = new((int)Math.Round(width * scale), (int)Math.Round(height * scale));
        BackColor = dark ? Color.FromArgb(36, 39, 45) : Color.FromArgb(248, 249, 251);
        ForegroundInk = Brush(dark ? Color.FromArgb(242, 244, 248) : Color.FromArgb(31, 35, 41));
        SecondaryInk = Brush(dark ? Color.FromArgb(176, 183, 195) : Color.FromArgb(102, 110, 124));
        AccentInk = Brush(accent);
        HoverInk = Brush(dark ? Color.FromArgb(61, 67, 80) : Color.FromArgb(228, 233, 242));
        TrackInk = Brush(dark ? Color.FromArgb(88, 95, 109) : Color.FromArgb(191, 198, 211));
        _buttonStyle = (Wpf.Style)Wpf.Markup.XamlReader.Parse(AudioMixerPopup.ButtonStyle);
        var grid = new WpfControls.Grid();
        grid.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        var header = new WpfControls.Grid { Height = 26, Margin = new Wpf.Thickness(0, 0, 0, 8) };
        var titleLabel = TextLabel(title, 14, true);
        if (titleIcon is { } icon)
        {
            var heading = new WpfControls.StackPanel { Orientation = WpfControls.Orientation.Horizontal };
            var glyph = Glyph(icon, 18);
            glyph.Foreground = SecondaryInk;
            glyph.Margin = new Wpf.Thickness(0, 0, 8, 0);
            heading.Children.Add(glyph);
            heading.Children.Add(titleLabel);
            header.Children.Add(heading);
        }
        else header.Children.Add(titleLabel);
        var close = IconButton(LucideRuntimeIcon.X, "关闭" + title, 24);
        close.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        close.Click += (_, _) => Dismiss();
        header.Children.Add(close);
        grid.Children.Add(header);
        WpfControls.Grid.SetRow(Body, 1); grid.Children.Add(Body);
        Footer.Margin = new Wpf.Thickness(0, 8, 0, 0);
        WpfControls.Grid.SetRow(Footer, 2); grid.Children.Add(Footer);
        _status = TextLabel("", 11); _status.Foreground = SecondaryInk;
        _status.TextWrapping = Wpf.TextWrapping.Wrap;
        _status.Visibility = Wpf.Visibility.Collapsed;
        _status.Margin = new Wpf.Thickness(0, 6, 0, 0);
        WpfControls.Grid.SetRow(_status, 3); grid.Children.Add(_status);
        _content = new WpfControls.Border { CornerRadius = new Wpf.CornerRadius(12), Padding = new Wpf.Thickness(12, 10, 12, 10),
            Background = Brush(BackColor), BorderThickness = new Wpf.Thickness(1),
            BorderBrush = Brush(dark ? Color.FromArgb(64, 69, 79) : Color.FromArgb(216, 222, 230)), Child = grid };
        _content.Resources["MixerAccent"] = AccentInk;
        _content.Resources["MixerTrack"] = TrackInk;
        _content.Resources["MixerHover"] = HoverInk;
        _content.Resources["MixerScrollThumb"] = Brush(dark ? Color.FromArgb(105, 116, 135) : Color.FromArgb(169, 179, 196));
        _content.Resources[typeof(WpfControls.Primitives.ScrollBar)] = Wpf.Markup.XamlReader.Parse(AudioMixerPopup.ScrollBarStyle);
        _content.PreviewKeyDown += (_, args) => { if (args.Key == Input.Key.Escape) { args.Handled = true; Dismiss(); } };
        Controls.Add(new System.Windows.Forms.Integration.ElementHost { Dock = Forms.DockStyle.Fill, Child = _content });
        Deactivate += (_, _) => Dismiss();
    }

    protected WpfControls.TextBlock TextLabel(string text, double size = 12, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = ForegroundInk, FontFamily = new Media.FontFamily(BoxAppearance.DefaultFontFamily),
        FontWeight = bold ? Wpf.FontWeights.SemiBold : Wpf.FontWeights.Normal,
        VerticalAlignment = Wpf.VerticalAlignment.Center, TextTrimming = Wpf.TextTrimming.CharacterEllipsis
    };

    protected WpfControls.TextBlock Glyph(LucideRuntimeIcon icon, double size = 16) => new()
    {
        Text = LucideRuntimeIcons.GetGlyph(icon), FontFamily = LucideRuntimeIcons.CreateWpfFontFamily(), FontSize = size,
        Foreground = ForegroundInk, VerticalAlignment = Wpf.VerticalAlignment.Center, HorizontalAlignment = Wpf.HorizontalAlignment.Center
    };

    protected Wpf.FrameworkElement ButtonContent(string text, LucideRuntimeIcon? icon = null)
    {
        var label = TextLabel(text, 11);
        if (icon is not { } value)
        {
            label.Margin = new Wpf.Thickness(8, 0, 8, 0);
            return label;
        }
        var content = new WpfControls.StackPanel { Orientation = WpfControls.Orientation.Horizontal,
            Margin = new Wpf.Thickness(8, 0, 8, 0), VerticalAlignment = Wpf.VerticalAlignment.Center };
        var glyph = Glyph(value, 14);
        glyph.Margin = new Wpf.Thickness(0, 0, 5, 0);
        content.Children.Add(glyph);
        content.Children.Add(label);
        return content;
    }

    protected WpfControls.Button TextButton(string text, string? name = null, LucideRuntimeIcon? icon = null)
    {
        var button = new WpfControls.Button { Content = ButtonContent(text, icon), Height = 28, Style = _buttonStyle, Cursor = Input.Cursors.Hand };
        Automation.SetName(button, name ?? text);
        return button;
    }

    protected WpfControls.Button IconButton(LucideRuntimeIcon icon, string name, double size = 26)
    {
        var button = new WpfControls.Button { Content = Glyph(icon), Width = size, Height = size,
            Style = _buttonStyle, ToolTip = name, Cursor = Input.Cursors.Hand };
        Automation.SetName(button, name);
        return button;
    }

    protected WpfControls.ScrollViewer ScrollContent(Wpf.UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = WpfControls.ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = WpfControls.ScrollBarVisibility.Disabled,
        Padding = new Wpf.Thickness(0, 0, 4, 0), Focusable = false
    };

    protected void SetStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = string.IsNullOrWhiteSpace(text) ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
    }

    protected void Post(Action action)
    {
        if (IsClosing || IsDisposed) return;
        if (_content.Dispatcher.CheckAccess()) action();
        else _content.Dispatcher.BeginInvoke((Action)(() => { if (!IsClosing && !IsDisposed) action(); }));
    }

    internal void ShowAt(Forms.IWin32Window owner, Rectangle anchor)
    {
        var area = Forms.Screen.FromRectangle(anchor).WorkingArea;
        Size = new(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = AudioMixerPopup.PopupLocation(anchor, Size, area);
        Show(owner);
        Activate();
    }

    internal void Dismiss()
    {
        if (IsClosing || IsDisposed) return;
        IsClosing = true;
        HideWithoutActivation();
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var corners = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) IsClosing = true;
        base.Dispose(disposing);
    }

    private static Media.SolidColorBrush Brush(Color color)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze(); return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
