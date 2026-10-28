using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;

namespace CrabDesk.Runtime;

/// <summary>Activatable native text input positioned inside a desktop widget.</summary>
internal sealed class DesktopWidgetTextInput : Forms.Form
{
    private readonly System.Windows.Forms.Integration.ElementHost _host;
    internal WpfControls.TextBox InputBox { get; }
    private Action<string>? _changed;
    private Action? _submit;
    private Action<double>? _dismissed;
    private WpfControls.Border? _frame;
    private int _presentationRevision;
    private bool _singleLine;
    private Action? _cancel;

    internal DesktopWidgetTextInput(string name, int maximumLength = 0)
    {
        Text = "CrabDesk " + name;
        AccessibleName = Text;
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        InputBox = new WpfControls.TextBox
        {
            AcceptsReturn = true, TextWrapping = Wpf.TextWrapping.Wrap,
            VerticalScrollBarVisibility = WpfControls.ScrollBarVisibility.Auto,
            FontFamily = new Media.FontFamily(BoxAppearance.DefaultFontFamily), FontSize = 13,
            MaxLength = maximumLength,
            Padding = new Wpf.Thickness(6, 4, 6, 4), BorderThickness = new Wpf.Thickness(1)
        };
        System.Windows.Automation.AutomationProperties.SetName(InputBox, name);
        Media.TextOptions.SetTextFormattingMode(InputBox, Media.TextFormattingMode.Display);
        InputBox.TextChanged += (_, _) =>
        {
            if (_changed is null) return;
            _presentationRevision++;
            _changed(DesktopWidgets.NormalizeText(InputBox.Text));
        };
        InputBox.PreviewKeyDown += (_, args) =>
        {
            _presentationRevision++;
            if (HandleKey(args.Key, Input.Keyboard.Modifiers)) args.Handled = true;
        };
        InputBox.PreviewMouseDown += (_, _) => _presentationRevision++;
        InputBox.PreviewMouseWheel += (_, _) => _presentationRevision++;
        _host = new System.Windows.Forms.Integration.ElementHost { Dock = Forms.DockStyle.Fill, Child = InputBox };
        Controls.Add(_host);
        Deactivate += (_, _) => Dismiss();
    }

    protected override Forms.CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80; return parameters; }
    }

    internal void Present(Rectangle bounds, string text, Color background, Color foreground, Color accent,
        Action<string> changed, Action? submit = null, Action<double>? dismissed = null, double? verticalOffset = null,
        double? cornerRadius = null, bool readOnly = false, PointF? caretPoint = null, bool singleLine = false, Action? cancelled = null)
    {
        _changed = null;
        var sameText = DesktopWidgets.NormalizeText(InputBox.Text) == DesktopWidgets.NormalizeText(text);
        var selectionStart = InputBox.SelectionStart;
        var selectionLength = sameText ? InputBox.SelectionLength : 0;
        if (!sameText) { InputBox.Text = text; selectionStart = InputBox.Text.Length; }
        InputBox.IsReadOnly = readOnly;
        _singleLine = singleLine;
        _cancel = cancelled;
        InputBox.AcceptsReturn = !singleLine;
        InputBox.TextWrapping = singleLine ? Wpf.TextWrapping.NoWrap : Wpf.TextWrapping.Wrap;
        BackColor = background;
        _host.BackColor = background;
        InputBox.Background = Brush(background);
        InputBox.Foreground = Brush(foreground);
        InputBox.CaretBrush = Brush(foreground);
        InputBox.BorderBrush = Brush(accent);
        InputBox.SelectionBrush = Brush(accent);
        InputBox.SelectionOpacity = .4;
        if (cornerRadius is { } radius) ConfigureRoundedField(radius, background, foreground, accent);
        if (singleLine)
        {
            // Match the painted row's text origin; the frame already supplies
            // its focus stroke, so a second text-field inset would move the text.
            InputBox.Padding = new Wpf.Thickness(0);
            InputBox.VerticalContentAlignment = Wpf.VerticalAlignment.Center;
            InputBox.HorizontalScrollBarVisibility = WpfControls.ScrollBarVisibility.Hidden;
            InputBox.VerticalScrollBarVisibility = WpfControls.ScrollBarVisibility.Disabled;
            InputBox.ApplyTemplate();
            if (InputBox.Template.FindName("PART_ContentHost", InputBox) is WpfControls.ScrollViewer contentHost)
                contentHost.VerticalAlignment = Wpf.VerticalAlignment.Center;
        }
        _changed = changed;
        _submit = submit;
        _dismissed = dismissed;
        // Negotiate the HWND's monitor DPI before applying monitor-pixel bounds.
        CreateControl();
        Bounds = bounds;
        Show();
        Bounds = bounds;
        if (cornerRadius is { } nativeRadius) UpdateRoundedRegion(nativeRadius);
        Activate();
        _host.Focus();
        InputBox.Focus();
        InputBox.Select(Math.Min(selectionStart, InputBox.Text.Length), Math.Min(selectionLength, Math.Max(0, InputBox.Text.Length - selectionStart)));
        InputBox.UpdateLayout();
        if (verticalOffset is { } offset) InputBox.ScrollToVerticalOffset(offset);
        if (caretPoint is { } point)
        {
            var index = InputBox.GetCharacterIndexFromPoint(new Wpf.Point(point.X, point.Y), true);
            InputBox.Select(Math.Max(0, index), 0);
        }
        var revision = ++_presentationRevision;
        // Keyboard focus and selection queue a later BringIntoView. Restore
        // the viewport after that work, unless the user has already interacted.
        if (verticalOffset is { } restoredOffset)
            InputBox.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (IsDisposed || !Visible || revision != _presentationRevision) return;
                InputBox.ScrollToVerticalOffset(restoredOffset);
                InputBox.UpdateLayout();
                if (caretPoint is { } restoredPoint)
                    InputBox.Select(Math.Max(0, InputBox.GetCharacterIndexFromPoint(new Wpf.Point(restoredPoint.X, restoredPoint.Y), true)), 0);
            }));
    }

    internal bool HandleKey(Input.Key key, Input.ModifierKeys modifiers)
    {
        if (key == Input.Key.Escape) { _cancel?.Invoke(); Dismiss(); return true; }
        // IME-confirmation keys arrive as ImeProcessed and must not submit.
        if (key != Input.Key.Enter || _submit is null || !(modifiers == Input.ModifierKeys.Control || _singleLine && modifiers == Input.ModifierKeys.None)) return false;
        if (string.IsNullOrWhiteSpace(InputBox.Text)) { if (_singleLine) Dismiss(); return true; }
        var submit = _submit;
        Dismiss();
        submit();
        return true;
    }

    internal void Dismiss()
    {
        _presentationRevision++;
        _changed = null;
        _submit = null;
        _cancel = null;
        var dismissed = _dismissed;
        _dismissed = null;
        var offset = InputBox.VerticalOffset;
        Hide();
        dismissed?.Invoke(offset);
    }

    private static Media.SolidColorBrush Brush(Color color) => new(Media.Color.FromRgb(color.R, color.G, color.B));

    private void ConfigureRoundedField(double radius, Color background, Color foreground, Color accent)
    {
        if (_frame is null)
        {
            // Keep WPF's native editing/IME behavior, with a compact field chrome
            // and an arrowless scrollbar matching the painted desktop surface.
            InputBox.Template = (WpfControls.ControlTemplate)Wpf.Markup.XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type TextBox}">
                  <Border Background="{TemplateBinding Background}">
                    <ScrollViewer x:Name="PART_ContentHost" Focusable="False">
                      <ScrollViewer.Template><ControlTemplate TargetType="{x:Type ScrollViewer}"><Grid>
                        <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" CanContentScroll="{TemplateBinding CanContentScroll}" Margin="{TemplateBinding Padding}" />
                        <ScrollBar x:Name="PART_VerticalScrollBar" Width="6" MinWidth="0" ClipToBounds="True" HorizontalAlignment="Right" Margin="0,6,1,6" Orientation="Vertical" Minimum="0" Maximum="{TemplateBinding ScrollableHeight}" ViewportSize="{TemplateBinding ViewportHeight}" Value="{Binding VerticalOffset, RelativeSource={RelativeSource TemplatedParent}, Mode=OneWay}" Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}" Style="{DynamicResource {x:Type ScrollBar}}" />
                      </Grid></ControlTemplate></ScrollViewer.Template>
                    </ScrollViewer>
                  </Border>
                </ControlTemplate>
                """);
            InputBox.Resources.Add(typeof(WpfControls.Primitives.ScrollBar), Wpf.Markup.XamlReader.Parse("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type ScrollBar}">
                  <Setter Property="Width" Value="6" /><Setter Property="MinWidth" Value="0" /><Setter Property="Background" Value="Transparent" />
                  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="{x:Type ScrollBar}">
                    <Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True">
                      <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageUpCommand}" Opacity="0" Focusable="False" /></Track.DecreaseRepeatButton>
                      <Track.Thumb><Thumb MinHeight="16" MinWidth="0"><Thumb.Template><ControlTemplate TargetType="{x:Type Thumb}"><Border Background="{DynamicResource ScrollThumbBrush}" CornerRadius="2" Margin="1,0" /></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                      <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageDownCommand}" Opacity="0" Focusable="False" /></Track.IncreaseRepeatButton>
                    </Track>
                  </ControlTemplate></Setter.Value></Setter>
                </Style>
                """));
            _host.Child = null;
            _frame = new WpfControls.Border { Child = InputBox };
            _host.Child = _frame;
            InputBox.FocusVisualStyle = null;
            InputBox.Padding = new Wpf.Thickness(8, 6, 8, 6);
            InputBox.BorderThickness = new Wpf.Thickness(0);
            InputBox.HorizontalScrollBarVisibility = WpfControls.ScrollBarVisibility.Disabled;
        }
        InputBox.Background = Media.Brushes.Transparent;
        InputBox.Resources["ScrollThumbBrush"] = new Media.SolidColorBrush(Media.Color.FromArgb(110, foreground.R, foreground.G, foreground.B));
        _frame.Background = Brush(background);
        _frame.BorderBrush = InputBox.IsReadOnly
            ? new Media.SolidColorBrush(Media.Color.FromArgb(70, foreground.R, foreground.G, foreground.B)) : Brush(accent);
        _frame.BorderThickness = new Wpf.Thickness(InputBox.IsReadOnly ? 1 : 1.5);
        _frame.CornerRadius = new Wpf.CornerRadius(radius);
    }

    private void UpdateRoundedRegion(double radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = (float)Math.Min(Math.Min(Width, Height), Math.Max(0, radius * DeviceDpi / 96.0 * 2));
        if (diameter < 1) path.AddRectangle(new Rectangle(0, 0, Width, Height));
        else
        {
            path.AddArc(0, 0, diameter, diameter, 180, 90);
            path.AddArc(Width - diameter, 0, diameter, diameter, 270, 90);
            path.AddArc(Width - diameter, Height - diameter, diameter, diameter, 0, 90);
            path.AddArc(0, Height - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
        }
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }
}
