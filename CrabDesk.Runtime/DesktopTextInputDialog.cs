using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using FormsIntegration = System.Windows.Forms.Integration;
using Wpf = System.Windows;
using WpfAutomation = System.Windows.Automation.AutomationProperties;
using WpfControls = System.Windows.Controls;
using WpfData = System.Windows.Data;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;

namespace CrabDesk.Runtime;

/// <summary>
/// Desktop-owned text input dialog using the same WPF surface as confirmations.
/// </summary>
internal sealed class DesktopTextInputDialog : Forms.Form
{
    private const int DialogWidth = 420;
    private const int DialogHeight = 240;
    private const int CornerRadius = 12;
    private const int CsDropShadow = 0x00020000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    private readonly Color _borderColor;
    private readonly WpfControls.TextBox _input;
    private readonly WpfControls.Button _confirm;

    private DesktopTextInputDialog(bool isDarkTheme, string title, string label, string initialValue)
    {
        var background = isDarkTheme ? Color.FromArgb(37, 40, 45) : Color.White;
        _borderColor = isDarkTheme ? Color.FromArgb(62, 66, 73) : Color.FromArgb(226, 229, 234);
        var titleColor = isDarkTheme ? Color.FromArgb(242, 244, 247) : Color.FromArgb(28, 32, 38);
        var fieldBackground = isDarkTheme ? Color.FromArgb(29, 32, 36) : Color.FromArgb(249, 250, 251);
        var fieldBorder = isDarkTheme ? Color.FromArgb(87, 94, 103) : Color.FromArgb(197, 203, 211);
        var accent = isDarkTheme ? Color.FromArgb(138, 180, 248) : Color.FromArgb(0, 103, 192);

        Text = title;
        AccessibleName = title;
        AutoScaleMode = Forms.AutoScaleMode.Dpi;
        BackColor = background;
        ClientSize = new Size(DialogWidth, DialogHeight);
        FormBorderStyle = Forms.FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.CenterParent;
        KeyPreview = true;
        UpdateRoundedRegion();

        var root = new WpfControls.Grid
        {
            Background = ToBrush(background),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        var content = new WpfControls.Grid { Margin = new Wpf.Thickness(20, 16, 20, 16) };
        content.RowDefinitions.Add(new WpfControls.RowDefinition { Height = Wpf.GridLength.Auto });
        content.RowDefinitions.Add(new WpfControls.RowDefinition { Height = Wpf.GridLength.Auto });
        content.RowDefinitions.Add(new WpfControls.RowDefinition { Height = Wpf.GridLength.Auto });
        content.RowDefinitions.Add(new WpfControls.RowDefinition { Height = Wpf.GridLength.Auto });
        root.Children.Add(content);

        var heading = new WpfControls.Grid { Height = 34 };
        heading.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = Wpf.GridLength.Auto });
        heading.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = Wpf.GridLength.Auto });
        var glyph = new WpfControls.TextBlock
        {
            Text = LucideRuntimeIcons.GetGlyph(LucideRuntimeIcon.Tags),
            FontFamily = LucideRuntimeIcons.CreateWpfFontFamily(),
            FontSize = 19,
            Foreground = ToBrush(accent),
            VerticalAlignment = Wpf.VerticalAlignment.Center,
            Margin = new Wpf.Thickness(0, 0, 10, 0)
        };
        heading.Children.Add(glyph);
        var titleText = new WpfControls.TextBlock
        {
            Text = title,
            FontFamily = CreateFontFamily("Microsoft YaHei UI"),
            FontSize = 19,
            FontWeight = Wpf.FontWeights.SemiBold,
            Foreground = ToBrush(titleColor),
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        WpfAutomation.SetName(titleText, title);
        heading.Children.Add(titleText);
        WpfControls.Grid.SetColumn(titleText, 1);
        var close = CreateIconButton(LucideRuntimeIcon.X, titleColor);
        close.ToolTip = "关闭";
        close.Click += (_, _) => Complete(false);
        heading.Children.Add(close);
        WpfControls.Grid.SetColumn(close, 2);
        heading.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == WpfInput.MouseButton.Left && e.OriginalSource is not WpfControls.Button)
            {
                ReleaseCapture();
                _ = SendMessage(Handle, 0xA1, 0x2, 0);
                e.Handled = true;
            }
        };
        content.Children.Add(heading);

        var separator = new WpfControls.Border
        {
            Height = 1,
            Background = ToBrush(isDarkTheme ? Color.FromArgb(55, 59, 66) : Color.FromArgb(238, 240, 243)),
            Margin = new Wpf.Thickness(0, 8, 0, 14)
        };
        content.Children.Add(separator);
        WpfControls.Grid.SetRow(separator, 1);

        _input = new WpfControls.TextBox
        {
            Text = initialValue,
            FontFamily = CreateFontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            Foreground = ToBrush(titleColor),
            Background = WpfMedia.Brushes.Transparent,
            BorderThickness = new Wpf.Thickness(0),
            Padding = new Wpf.Thickness(11, 7, 11, 7),
            Height = 36,
            VerticalContentAlignment = Wpf.VerticalAlignment.Center,
            HorizontalContentAlignment = Wpf.HorizontalAlignment.Left,
            TextWrapping = Wpf.TextWrapping.NoWrap,
            SelectionBrush = ToBrush(accent),
            SelectionTextBrush = WpfMedia.Brushes.White,
            CaretBrush = ToBrush(accent),
            ToolTip = label
        };
        WpfAutomation.SetName(_input, label);
        var field = new WpfControls.Border
        {
            Height = 40,
            Background = ToBrush(fieldBackground),
            BorderBrush = ToBrush(fieldBorder),
            BorderThickness = new Wpf.Thickness(1),
            CornerRadius = new Wpf.CornerRadius(6),
            Padding = new Wpf.Thickness(1)
        };
        field.Child = _input;
        content.Children.Add(field);
        WpfControls.Grid.SetRow(field, 2);

        var actions = new WpfControls.StackPanel
        {
            Orientation = WpfControls.Orientation.Horizontal,
            HorizontalAlignment = Wpf.HorizontalAlignment.Right,
            Margin = new Wpf.Thickness(0, 16, 0, 0)
        };
        var cancel = CreateButton("取消", isDarkTheme ? Color.FromArgb(47, 51, 57) : Color.White,
            isDarkTheme ? Color.FromArgb(56, 60, 67) : Color.FromArgb(244, 246, 248),
            isDarkTheme ? Color.FromArgb(64, 69, 76) : Color.FromArgb(235, 238, 241),
            isDarkTheme ? Color.FromArgb(232, 235, 240) : Color.FromArgb(36, 41, 47),
            isDarkTheme ? Color.FromArgb(72, 77, 85) : Color.FromArgb(217, 221, 226), accent);
        cancel.Click += (_, _) => Complete(false);
        actions.Children.Add(cancel);
        _confirm = CreateButton("确定", accent, isDarkTheme ? Color.FromArgb(154, 193, 255) : Color.FromArgb(0, 87, 166),
            isDarkTheme ? Color.FromArgb(116, 157, 220) : Color.FromArgb(0, 72, 140), Color.White, accent, accent);
        _confirm.Click += (_, _) => Complete(!string.IsNullOrWhiteSpace(_input.Text));
        actions.Children.Add(_confirm);
        content.Children.Add(actions);
        WpfControls.Grid.SetRow(actions, 3);

        root.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WpfInput.Key.Enter)
            {
                Complete(!string.IsNullOrWhiteSpace(_input.Text));
                e.Handled = true;
            }
            else if (e.Key == WpfInput.Key.Escape)
            {
                Complete(false);
                e.Handled = true;
            }
        };

        var host = new FormsIntegration.ElementHost { Dock = Forms.DockStyle.Fill, Child = root };
        Controls.Add(host);
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WpfInput.Key.Enter)
            {
                Complete(!string.IsNullOrWhiteSpace(_input.Text));
                e.Handled = true;
            }
            else if (e.Key == WpfInput.Key.Escape)
            {
                Complete(false);
                e.Handled = true;
            }
        };
        Shown += (_, _) =>
        {
            _input.SelectAll();
            _input.Focus();
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Forms.Keys.Escape) { Complete(false); e.Handled = true; }
            else if (e.KeyCode == Forms.Keys.Enter) { Complete(!string.IsNullOrWhiteSpace(_input.Text)); e.Handled = true; }
        };
    }

    internal static string? Show(Forms.IWin32Window owner, bool isDarkTheme, string title, string label, string initialValue)
    {
        using var dialog = new DesktopTextInputDialog(isDarkTheme, title, label, initialValue);
        return dialog.ShowDialog(owner) == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog._input.Text)
            ? dialog._input.Text.Trim()
            : null;
    }

    protected override Forms.CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.ClassStyle |= CsDropShadow; return parameters; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var preference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRectangle(ClientRectangle, CornerRadius);
        using var pen = new Pen(_borderColor);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateRoundedRegion(); }

    private static WpfControls.Button CreateIconButton(LucideRuntimeIcon icon, Color foreground)
    {
        var button = new WpfControls.Button
        {
            Content = LucideRuntimeIcons.GetGlyph(icon),
            FontFamily = LucideRuntimeIcons.CreateWpfFontFamily(),
            FontSize = 17,
            Foreground = ToBrush(foreground),
            Background = WpfMedia.Brushes.Transparent,
            BorderThickness = new Wpf.Thickness(0),
            Width = 30,
            Height = 30,
            Padding = new Wpf.Thickness(0),
            Cursor = WpfInput.Cursors.Hand
        };
        var template = new WpfControls.ControlTemplate(typeof(WpfControls.Button));
        var border = new Wpf.FrameworkElementFactory(typeof(WpfControls.Border));
        border.SetValue(WpfControls.Border.CornerRadiusProperty, new Wpf.CornerRadius(6));
        border.SetBinding(WpfControls.Border.BackgroundProperty, new WpfData.Binding("Background") { RelativeSource = new WpfData.RelativeSource(WpfData.RelativeSourceMode.TemplatedParent) });
        var presenter = new Wpf.FrameworkElementFactory(typeof(WpfControls.ContentPresenter));
        presenter.SetValue(WpfControls.ContentPresenter.HorizontalAlignmentProperty, Wpf.HorizontalAlignment.Center);
        presenter.SetValue(WpfControls.ContentPresenter.VerticalAlignmentProperty, Wpf.VerticalAlignment.Center);
        border.AppendChild(presenter); template.VisualTree = border;
        var hover = new Wpf.Trigger { Property = WpfControls.Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Wpf.Setter(WpfControls.Control.BackgroundProperty, ToBrush(Color.FromArgb(28, foreground.R, foreground.G, foreground.B))));
        button.Style = new Wpf.Style(typeof(WpfControls.Button)) { Setters = { new Wpf.Setter(WpfControls.Control.TemplateProperty, template), new Wpf.Setter(WpfControls.Control.BackgroundProperty, WpfMedia.Brushes.Transparent) }, Triggers = { hover } };
        return button;
    }

    private static WpfControls.Button CreateButton(string text, Color background, Color hover, Color pressed, Color foreground, Color border, Color focus)
    {
        var style = new Wpf.Style(typeof(WpfControls.Button));
        style.Setters.Add(new Wpf.Setter(WpfControls.Control.BackgroundProperty, ToBrush(background)));
        style.Setters.Add(new Wpf.Setter(WpfControls.Control.ForegroundProperty, ToBrush(foreground)));
        style.Setters.Add(new Wpf.Setter(WpfControls.Control.BorderBrushProperty, ToBrush(border)));
        style.Setters.Add(new Wpf.Setter(WpfControls.Control.BorderThicknessProperty, new Wpf.Thickness(1)));
        var over = new Wpf.Trigger { Property = WpfControls.Button.IsMouseOverProperty, Value = true }; over.Setters.Add(new Wpf.Setter(WpfControls.Control.BackgroundProperty, ToBrush(hover))); style.Triggers.Add(over);
        var down = new Wpf.Trigger { Property = WpfControls.Button.IsPressedProperty, Value = true }; down.Setters.Add(new Wpf.Setter(WpfControls.Control.BackgroundProperty, ToBrush(pressed))); style.Triggers.Add(down);
        var focused = new Wpf.Trigger { Property = WpfControls.Button.IsKeyboardFocusedProperty, Value = true }; focused.Setters.Add(new Wpf.Setter(WpfControls.Control.BorderBrushProperty, ToBrush(focus))); style.Triggers.Add(focused);
        var button = new WpfControls.Button { Content = text, Style = style, FontFamily = CreateFontFamily("Microsoft YaHei UI"), FontSize = 13, FontWeight = Wpf.FontWeights.SemiBold, Cursor = WpfInput.Cursors.Hand, Height = 34, MinWidth = 88, Margin = new Wpf.Thickness(0, 0, 10, 0), Padding = new Wpf.Thickness(17, 0, 17, 0) };
        var template = new WpfControls.ControlTemplate(typeof(WpfControls.Button));
        var borderElement = new Wpf.FrameworkElementFactory(typeof(WpfControls.Border));
        borderElement.SetValue(WpfControls.Border.CornerRadiusProperty, new Wpf.CornerRadius(6));
        borderElement.SetBinding(WpfControls.Border.BackgroundProperty, new WpfData.Binding("Background") { RelativeSource = new WpfData.RelativeSource(WpfData.RelativeSourceMode.TemplatedParent) });
        borderElement.SetBinding(WpfControls.Border.BorderBrushProperty, new WpfData.Binding("BorderBrush") { RelativeSource = new WpfData.RelativeSource(WpfData.RelativeSourceMode.TemplatedParent) });
        borderElement.SetBinding(WpfControls.Border.BorderThicknessProperty, new WpfData.Binding("BorderThickness") { RelativeSource = new WpfData.RelativeSource(WpfData.RelativeSourceMode.TemplatedParent) });
        var presenter = new Wpf.FrameworkElementFactory(typeof(WpfControls.ContentPresenter)); presenter.SetValue(WpfControls.ContentPresenter.HorizontalAlignmentProperty, Wpf.HorizontalAlignment.Center); presenter.SetValue(WpfControls.ContentPresenter.VerticalAlignmentProperty, Wpf.VerticalAlignment.Center); borderElement.AppendChild(presenter); template.VisualTree = borderElement; button.Template = template;
        return button;
    }

    private static WpfControls.ControlTemplate CreateTextBoxTemplate()
    {
        var border = new Wpf.FrameworkElementFactory(typeof(WpfControls.Border));
        border.SetValue(WpfControls.Border.CornerRadiusProperty, new Wpf.CornerRadius(6));
        border.SetBinding(WpfControls.Border.BackgroundProperty, new WpfData.Binding("Background") { RelativeSource = new WpfData.RelativeSource(WpfData.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(WpfControls.Border.BorderBrushProperty, new WpfData.Binding("BorderBrush") { RelativeSource = new WpfData.RelativeSource(WpfData.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(WpfControls.Border.BorderThicknessProperty, new WpfData.Binding("BorderThickness") { RelativeSource = new WpfData.RelativeSource(WpfData.RelativeSourceMode.TemplatedParent) });
        var content = new Wpf.FrameworkElementFactory(typeof(WpfControls.ScrollViewer));
        content.Name = "PART_ContentHost";
        content.SetValue(WpfControls.ScrollViewer.HorizontalScrollBarVisibilityProperty, WpfControls.ScrollBarVisibility.Hidden);
        content.SetValue(WpfControls.ScrollViewer.VerticalScrollBarVisibilityProperty, WpfControls.ScrollBarVisibility.Hidden);
        content.SetBinding(WpfControls.ScrollViewer.PaddingProperty, new WpfData.Binding("Padding") { RelativeSource = new WpfData.RelativeSource(WpfData.RelativeSourceMode.TemplatedParent) });
        border.AppendChild(content);
        return new WpfControls.ControlTemplate(typeof(WpfControls.TextBox)) { VisualTree = border };
    }

    private void Complete(bool accepted) => DialogResult = accepted ? Forms.DialogResult.OK : Forms.DialogResult.Cancel;
    private void UpdateRoundedRegion() { if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return; using var path = CreateRoundedRectangle(ClientRectangle, CornerRadius); var previous = Region; Region = new Region(path); previous?.Dispose(); }
    private static GraphicsPath CreateRoundedRectangle(Rectangle rectangle, int radius) { var diameter = radius * 2; var path = new GraphicsPath(); path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90); path.AddArc(rectangle.Right - diameter - 1, rectangle.Top, diameter, diameter, 270, 90); path.AddArc(rectangle.Right - diameter - 1, rectangle.Bottom - diameter - 1, diameter, diameter, 0, 90); path.AddArc(rectangle.Left, rectangle.Bottom - diameter - 1, diameter, diameter, 90, 90); path.CloseFigure(); return path; }
    private static WpfMedia.FontFamily CreateFontFamily(string name) { try { return new WpfMedia.FontFamily(name); } catch { return new WpfMedia.FontFamily("Segoe UI"); } }
    private static WpfMedia.SolidColorBrush ToBrush(Color color) { var brush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(color.A, color.R, color.G, color.B)); brush.Freeze(); return brush; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, int wParam, int lParam);
}
