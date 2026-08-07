using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed class DesktopConfirmationDialog : Forms.Form
{
    private const int DialogWidth = 440;
    private const int DialogHeight = 220;
    private const int DialogCornerRadius = 12;
    private const int CsDropShadow = 0x00020000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    private readonly Color _background;
    private readonly Color _borderColor;
    private readonly FluentDialogButton _cancelButton;
    private readonly FluentDialogButton _primaryButton;

    private DesktopConfirmationDialog(
        bool isDarkTheme,
        string title,
        string message,
        string primaryText)
    {
        _background = isDarkTheme
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
        BackColor = _background;
        ClientSize = new Size(DialogWidth, DialogHeight);
        FormBorderStyle = Forms.FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.CenterParent;
        KeyPreview = true;
        Font = CreateFont("Segoe UI Variable Text", 9F, FontStyle.Regular);
        Padding = new Forms.Padding(24);
        UpdateRoundedRegion();

        var heading = new Forms.Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 16F, FontStyle.Bold),
            ForeColor = titleColor,
            Location = new Point(Padding.Left, 24),
            Size = new Size(ClientSize.Width - Padding.Horizontal, 32),
            Text = title,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        Controls.Add(heading);

        var messageLabel = new Forms.Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 9.5F, FontStyle.Regular),
            ForeColor = bodyColor,
            Location = new Point(Padding.Left, 72),
            Size = new Size(ClientSize.Width - Padding.Horizontal, 44),
            Text = message,
            TextAlign = ContentAlignment.TopLeft,
            UseMnemonic = false
        };
        Controls.Add(messageLabel);

        _cancelButton = new FluentDialogButton(
            "取消",
            secondarySurface,
            secondaryHover,
            secondaryPressed,
            secondaryText,
            secondaryBorder,
            focusColor)
        {
            AccessibleName = "取消",
            Location = new Point(220, 162),
            Size = new Size(92, 34),
            TabIndex = 0
        };
        _cancelButton.Click += (_, _) => DialogResult = Forms.DialogResult.Cancel;

        _primaryButton = new FluentDialogButton(
            primaryText,
            danger,
            dangerHover,
            dangerPressed,
            Color.White,
            danger,
            focusColor)
        {
            AccessibleName = primaryText,
            Location = new Point(320, 162),
            Size = new Size(96, 34),
            TabIndex = 1
        };
        _primaryButton.Click += (_, _) => DialogResult = Forms.DialogResult.OK;
        Controls.Add(_cancelButton);
        Controls.Add(_primaryButton);

        Shown += (_, _) => _cancelButton.Focus();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Font.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnDialogKeyDown(object? sender, Forms.KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Forms.Keys.Escape || eventArgs.KeyCode == Forms.Keys.Enter)
        {
            DialogResult = Forms.DialogResult.Cancel;
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

    private static Font CreateFont(string familyName, float size, FontStyle style)
    {
        try
        {
            return new Font(familyName, size, style);
        }
        catch (ArgumentException)
        {
            return new Font("Segoe UI", size, style);
        }
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

    private sealed class FluentDialogButton : Forms.Control
    {
        private const int CornerRadius = 4;
        private readonly Color _normalBackground;
        private readonly Color _hoverBackground;
        private readonly Color _pressedBackground;
        private readonly Color _borderColor;
        private readonly Color _focusColor;
        private bool _hovered;
        private bool _pressed;

        internal FluentDialogButton(
            string text,
            Color normalBackground,
            Color hoverBackground,
            Color pressedBackground,
            Color foreground,
            Color borderColor,
            Color focusColor)
        {
            Text = text;
            BackColor = normalBackground;
            ForeColor = foreground;
            _normalBackground = normalBackground;
            _hoverBackground = hoverBackground;
            _pressedBackground = pressedBackground;
            _borderColor = borderColor;
            _focusColor = focusColor;
            Cursor = Forms.Cursors.Hand;
            Font = CreateFont("Segoe UI Variable Text", 9F, FontStyle.Bold);
            SetStyle(
                Forms.ControlStyles.AllPaintingInWmPaint |
                Forms.ControlStyles.OptimizedDoubleBuffer |
                Forms.ControlStyles.Selectable |
                Forms.ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaint(Forms.PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var background = _pressed
                ? _pressedBackground
                : _hovered
                    ? _hoverBackground
                    : _normalBackground;
            var bounds = ClientRectangle;
            bounds.Width--;
            bounds.Height--;
            using var path = CreateRoundedRectangle(bounds, CornerRadius);
            using var fill = new SolidBrush(background);
            using var border = new Pen(Focused ? _focusColor : _borderColor, Focused ? 2F : 1F);
            eventArgs.Graphics.FillPath(fill, path);
            eventArgs.Graphics.DrawPath(border, path);
            Forms.TextRenderer.DrawText(
                eventArgs.Graphics,
                Text,
                Font,
                ClientRectangle,
                ForeColor,
                Forms.TextFormatFlags.HorizontalCenter |
                Forms.TextFormatFlags.VerticalCenter |
                Forms.TextFormatFlags.NoPadding |
                Forms.TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseEnter(EventArgs eventArgs)
        {
            base.OnMouseEnter(eventArgs);
            _hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            base.OnMouseLeave(eventArgs);
            _hovered = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(Forms.MouseEventArgs eventArgs)
        {
            base.OnMouseDown(eventArgs);
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                Focus();
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(Forms.MouseEventArgs eventArgs)
        {
            var shouldClick = _pressed && ClientRectangle.Contains(eventArgs.Location);
            _pressed = false;
            Invalidate();
            base.OnMouseUp(eventArgs);
            if (shouldClick)
            {
                OnClick(EventArgs.Empty);
            }
        }

        protected override void OnGotFocus(EventArgs eventArgs)
        {
            base.OnGotFocus(eventArgs);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs eventArgs)
        {
            base.OnLostFocus(eventArgs);
            Invalidate();
        }

        protected override void OnKeyDown(Forms.KeyEventArgs eventArgs)
        {
            base.OnKeyDown(eventArgs);
            if (eventArgs.KeyCode is Forms.Keys.Space or Forms.Keys.Enter)
            {
                _pressed = true;
                Invalidate();
                eventArgs.Handled = true;
            }
        }

        protected override void OnKeyUp(Forms.KeyEventArgs eventArgs)
        {
            var shouldClick = _pressed && eventArgs.KeyCode is Forms.Keys.Space or Forms.Keys.Enter;
            _pressed = false;
            Invalidate();
            base.OnKeyUp(eventArgs);
            if (shouldClick)
            {
                OnClick(EventArgs.Empty);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Font.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
