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
/// A desktop-owned confirmation shell whose content is rendered with WPF.
/// Keeping the small WinForms owner preserves correct modality for an
/// Explorer child surface, while ElementHost gives the dialog the same text,
/// focus and button rendering stack used by the WPF portions of the runtime.
/// </summary>
internal sealed class DesktopConfirmationDialog : Forms.Form
{
    private const int DialogWidth = 404;
    private const int DialogHeight = 156;
    private const int DialogCornerRadius = 10;
    private const int CsDropShadow = 0x00020000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    private readonly Color _borderColor;
    private readonly WpfControls.Button _cancelButton;

    private DesktopConfirmationDialog(
        bool isDarkTheme,
        string title,
        string message,
        string primaryText)
    {
        var background = isDarkTheme
            ? Color.FromArgb(32, 32, 32)
            : Color.FromArgb(250, 250, 250);
        _borderColor = isDarkTheme
            ? Color.FromArgb(70, 70, 70)
            : Color.FromArgb(226, 226, 226);
        var titleColor = isDarkTheme
            ? Color.FromArgb(245, 245, 245)
            : Color.FromArgb(31, 31, 31);
        var bodyColor = isDarkTheme
            ? Color.FromArgb(202, 202, 202)
            : Color.FromArgb(97, 97, 97);
        var dividerColor = isDarkTheme
            ? Color.FromArgb(67, 67, 67)
            : Color.FromArgb(228, 231, 235);
        var secondarySurface = isDarkTheme
            ? Color.FromArgb(44, 44, 44)
            : Color.FromArgb(250, 250, 250);
        var secondaryHover = isDarkTheme
            ? Color.FromArgb(60, 60, 60)
            : Color.FromArgb(243, 243, 243);
        var secondaryPressed = isDarkTheme
            ? Color.FromArgb(68, 68, 68)
            : Color.FromArgb(235, 235, 235);
        var secondaryText = isDarkTheme
            ? Color.FromArgb(245, 245, 245)
            : Color.FromArgb(31, 31, 31);
        var secondaryBorder = isDarkTheme
            ? Color.FromArgb(96, 96, 96)
            : Color.FromArgb(209, 209, 209);
        var danger = isDarkTheme
            ? Color.FromArgb(220, 74, 81)
            : Color.FromArgb(196, 43, 28);
        var dangerHover = isDarkTheme
            ? Color.FromArgb(232, 89, 96)
            : Color.FromArgb(179, 37, 24);
        var dangerPressed = isDarkTheme
            ? Color.FromArgb(194, 57, 64)
            : Color.FromArgb(151, 31, 21);
        var focusColor = isDarkTheme
            ? Color.FromArgb(142, 190, 255)
            : Color.FromArgb(0, 103, 192);

        Text = title;
        AccessibleName = "CrabDesk confirmation dialog";
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

        var content = BuildContent(
            title,
            message,
            primaryText,
            background,
            titleColor,
            bodyColor,
            dividerColor,
            secondarySurface,
            secondaryHover,
            secondaryPressed,
            secondaryText,
            secondaryBorder,
            danger,
            dangerHover,
            dangerPressed,
            focusColor,
            out _cancelButton);
        var host = new FormsIntegration.ElementHost
        {
            Dock = Forms.DockStyle.Fill,
            Margin = Forms.Padding.Empty,
            Child = content
        };
        Controls.Add(host);

        Shown += (_, _) => _cancelButton.Dispatcher.BeginInvoke(
            (Action)(() => _cancelButton.Focus()));
        KeyDown += OnDialogKeyDown;
    }

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= CsDropShadow;
            return parameters;
        }
    }

    internal static bool Show(
        Forms.IWin32Window owner,
        bool isDarkTheme,
        string title,
        string message,
        string primaryText)
    {
        using var dialog = new DesktopConfirmationDialog(isDarkTheme, title, message, primaryText);
        return dialog.ShowDialog(owner) == Forms.DialogResult.OK;
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        var preference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(
            Handle,
            DwmwaWindowCornerPreference,
            ref preference,
            sizeof(int));
    }

    protected override void OnPaint(Forms.PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRectangle(ClientRectangle, DialogCornerRadius);
        using var pen = new Pen(_borderColor);
        eventArgs.Graphics.DrawPath(pen, path);
    }

    protected override void OnSizeChanged(EventArgs eventArgs)
    {
        base.OnSizeChanged(eventArgs);
        UpdateRoundedRegion();
    }

    private WpfControls.Grid BuildContent(
        string title,
        string message,
        string primaryText,
        Color background,
        Color titleColor,
        Color bodyColor,
        Color dividerColor,
        Color secondarySurface,
        Color secondaryHover,
        Color secondaryPressed,
        Color secondaryText,
        Color secondaryBorder,
        Color danger,
        Color dangerHover,
        Color dangerPressed,
        Color focusColor,
        out WpfControls.Button cancelButton)
    {
        var root = new WpfControls.Grid
        {
            Margin = new Wpf.Thickness(20, 18, 20, 18),
            Background = ToBrush(background),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        root.RowDefinitions.Add(new WpfControls.RowDefinition { Height = new Wpf.GridLength(28) });
        root.RowDefinitions.Add(new WpfControls.RowDefinition { Height = new Wpf.GridLength(32) });
        root.RowDefinitions.Add(new WpfControls.RowDefinition { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        root.RowDefinitions.Add(new WpfControls.RowDefinition { Height = new Wpf.GridLength(1) });
        root.RowDefinitions.Add(new WpfControls.RowDefinition { Height = new Wpf.GridLength(32) });

        var heading = new WpfControls.TextBlock
        {
            Text = title,
            FontFamily = CreateFontFamily("Segoe UI Variable Text"),
            FontSize = 18,
            FontWeight = Wpf.FontWeights.SemiBold,
            Foreground = ToBrush(titleColor),
            TextTrimming = Wpf.TextTrimming.CharacterEllipsis,
            TextWrapping = Wpf.TextWrapping.NoWrap,
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        WpfAutomation.SetName(heading, title);
        root.Children.Add(heading);
        WpfControls.Grid.SetRow(heading, 0);

        var body = new WpfControls.TextBlock
        {
            Text = message,
            FontFamily = CreateFontFamily("Segoe UI Variable Text"),
            FontSize = 12.5,
            Foreground = ToBrush(bodyColor),
            TextWrapping = Wpf.TextWrapping.Wrap,
            VerticalAlignment = Wpf.VerticalAlignment.Top,
            Margin = new Wpf.Thickness(0, 3, 0, 0)
        };
        WpfAutomation.SetName(body, message);
        root.Children.Add(body);
        WpfControls.Grid.SetRow(body, 1);

        var divider = new WpfControls.Border
        {
            Height = 1,
            Background = ToBrush(dividerColor),
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        root.Children.Add(divider);
        WpfControls.Grid.SetRow(divider, 3);

        var actions = new WpfControls.Grid
        {
            HorizontalAlignment = Wpf.HorizontalAlignment.Right,
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        actions.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new Wpf.GridLength(88) });
        actions.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new Wpf.GridLength(8) });
        actions.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new Wpf.GridLength(94) });
        root.Children.Add(actions);
        WpfControls.Grid.SetRow(actions, 4);

        cancelButton = CreateButton(
            "取消",
            ToBrush(secondarySurface),
            ToBrush(secondaryHover),
            ToBrush(secondaryPressed),
            ToBrush(secondaryText),
            ToBrush(secondaryBorder),
            ToBrush(focusColor));
        WpfAutomation.SetName(cancelButton, "取消");
        cancelButton.Click += (_, _) => Complete(false);
        actions.Children.Add(cancelButton);
        WpfControls.Grid.SetColumn(cancelButton, 0);

        var primaryButton = CreateButton(
            primaryText,
            ToBrush(danger),
            ToBrush(dangerHover),
            ToBrush(dangerPressed),
            WpfMedia.Brushes.White,
            ToBrush(danger),
            ToBrush(focusColor));
        WpfAutomation.SetName(primaryButton, primaryText);
        primaryButton.Click += (_, _) => Complete(true);
        actions.Children.Add(primaryButton);
        WpfControls.Grid.SetColumn(primaryButton, 2);

        root.PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key is WpfInput.Key.Escape or WpfInput.Key.Enter)
            {
                Complete(false);
                eventArgs.Handled = true;
            }
        };
        return root;
    }

    private static WpfControls.Button CreateButton(
        string text,
        WpfMedia.Brush background,
        WpfMedia.Brush hoverBackground,
        WpfMedia.Brush pressedBackground,
        WpfMedia.Brush foreground,
        WpfMedia.Brush border,
        WpfMedia.Brush focus)
    {
        return new WpfControls.Button
        {
            Content = text,
            Style = CreateButtonStyle(background, hoverBackground, pressedBackground, foreground, border, focus),
            FontFamily = CreateFontFamily("Segoe UI Variable Text"),
            FontSize = 12.5,
            FontWeight = Wpf.FontWeights.SemiBold,
            Cursor = WpfInput.Cursors.Hand,
            Height = 32,
            Padding = new Wpf.Thickness(10, 0, 10, 0),
            IsDefault = false
        };
    }

    private static Wpf.Style CreateButtonStyle(
        WpfMedia.Brush background,
        WpfMedia.Brush hoverBackground,
        WpfMedia.Brush pressedBackground,
        WpfMedia.Brush foreground,
        WpfMedia.Brush border,
        WpfMedia.Brush focus)
    {
        var style = new Wpf.Style(typeof(WpfControls.Button));
        style.Setters.Add(new Wpf.Setter(WpfControls.Control.BackgroundProperty, background));
        style.Setters.Add(new Wpf.Setter(WpfControls.Control.ForegroundProperty, foreground));
        style.Setters.Add(new Wpf.Setter(WpfControls.Control.BorderBrushProperty, border));
        style.Setters.Add(new Wpf.Setter(WpfControls.Control.BorderThicknessProperty, new Wpf.Thickness(1)));
        style.Setters.Add(new Wpf.Setter(WpfControls.Control.TemplateProperty, CreateButtonTemplate()));

        var hover = new Wpf.Trigger { Property = WpfControls.Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Wpf.Setter(WpfControls.Control.BackgroundProperty, hoverBackground));
        style.Triggers.Add(hover);

        var pressed = new Wpf.Trigger { Property = WpfControls.Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Wpf.Setter(WpfControls.Control.BackgroundProperty, pressedBackground));
        style.Triggers.Add(pressed);

        var focused = new Wpf.Trigger { Property = WpfControls.Button.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Wpf.Setter(WpfControls.Control.BorderBrushProperty, focus));
        focused.Setters.Add(new Wpf.Setter(WpfControls.Control.BorderThicknessProperty, new Wpf.Thickness(2)));
        style.Triggers.Add(focused);
        return style;
    }

    private static WpfControls.ControlTemplate CreateButtonTemplate()
    {
        var border = new Wpf.FrameworkElementFactory(typeof(WpfControls.Border));
        border.SetValue(WpfControls.Border.CornerRadiusProperty, new Wpf.CornerRadius(4));
        border.SetBinding(WpfControls.Border.BackgroundProperty, TemplateBinding(WpfControls.Control.BackgroundProperty));
        border.SetBinding(WpfControls.Border.BorderBrushProperty, TemplateBinding(WpfControls.Control.BorderBrushProperty));
        border.SetBinding(WpfControls.Border.BorderThicknessProperty, TemplateBinding(WpfControls.Control.BorderThicknessProperty));
        border.SetBinding(WpfControls.Border.PaddingProperty, TemplateBinding(WpfControls.Control.PaddingProperty));

        var presenter = new Wpf.FrameworkElementFactory(typeof(WpfControls.ContentPresenter));
        presenter.SetValue(WpfControls.ContentPresenter.ContentSourceProperty, "Content");
        presenter.SetValue(WpfControls.ContentPresenter.HorizontalAlignmentProperty, Wpf.HorizontalAlignment.Center);
        presenter.SetValue(WpfControls.ContentPresenter.VerticalAlignmentProperty, Wpf.VerticalAlignment.Center);
        presenter.SetBinding(WpfControls.ContentPresenter.ContentTemplateProperty, TemplateBinding(WpfControls.ContentControl.ContentTemplateProperty));
        presenter.SetBinding(WpfControls.ContentPresenter.ContentTemplateSelectorProperty, TemplateBinding(WpfControls.ContentControl.ContentTemplateSelectorProperty));
        border.AppendChild(presenter);

        return new WpfControls.ControlTemplate(typeof(WpfControls.Button))
        {
            VisualTree = border
        };
    }

    private static WpfData.Binding TemplateBinding(Wpf.DependencyProperty property) =>
        new(property.Name)
        {
            RelativeSource = new WpfData.RelativeSource(WpfData.RelativeSourceMode.TemplatedParent)
        };

    private static WpfMedia.FontFamily CreateFontFamily(string name)
    {
        try
        {
            return new WpfMedia.FontFamily(name);
        }
        catch
        {
            return new WpfMedia.FontFamily("Segoe UI");
        }
    }

    private static WpfMedia.SolidColorBrush ToBrush(Color color)
    {
        var brush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private void Complete(bool accepted) =>
        DialogResult = accepted ? Forms.DialogResult.OK : Forms.DialogResult.Cancel;

    private void OnDialogKeyDown(object? sender, Forms.KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Forms.Keys.Escape || eventArgs.KeyCode == Forms.Keys.Enter)
        {
            Complete(false);
            eventArgs.Handled = true;
        }
    }

    private void UpdateRoundedRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        using var path = CreateRoundedRectangle(ClientRectangle, DialogCornerRadius);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter - 1, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter - 1, rectangle.Bottom - diameter - 1, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter - 1, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);
}
