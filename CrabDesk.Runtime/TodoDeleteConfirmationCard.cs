using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>A compact confirmation hosted by the desktop's existing no-activate popup.</summary>
internal sealed class TodoDeleteConfirmationCard : Forms.UserControl
{
    internal Forms.Button CancelButton { get; }
    internal Forms.Button DeleteButton { get; }
    private readonly Font _bodyFont;
    private readonly Font _headingFont;

    internal TodoDeleteConfirmationCard(string taskText, bool dark, double scale)
    {
        AutoScaleMode = Forms.AutoScaleMode.None;
        int Px(double value) => (int)Math.Round(value * scale);
        Size = new(Px(272), Px(140));
        BackColor = dark ? Color.FromArgb(37, 40, 45) : Color.FromArgb(252, 252, 252);
        ForeColor = dark ? Color.FromArgb(244, 245, 247) : Color.FromArgb(32, 36, 42);
        Font = _bodyFont = new(BoxAppearance.DefaultFontFamily, (float)(12 * scale), GraphicsUnit.Pixel);
        _headingFont = new(BoxAppearance.DefaultFontFamily, (float)(14 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        Controls.Add(new Forms.Label { Text = "删除这条待办？", Font = _headingFont, AutoSize = false,
            Bounds = new(Px(12), Px(8), Px(248), Px(24)), UseMnemonic = false });
        Controls.Add(new Forms.Label { Text = taskText, AccessibleName = taskText, AutoEllipsis = true, UseMnemonic = false,
            ForeColor = dark ? Color.FromArgb(195, 199, 206) : Color.FromArgb(90, 95, 103),
            Bounds = new(Px(12), Px(38), Px(248), Px(40)) });
        CancelButton = Button("取消", new(Px(112), Px(96), Px(68), Px(30)), false);
        DeleteButton = Button("删除", new(Px(188), Px(96), Px(72), Px(30)), true);
        Controls.Add(CancelButton); Controls.Add(DeleteButton);
        TabStop = false;
        Forms.Button Button(string text, Rectangle bounds, bool destructive)
        {
            var button = new Forms.Button { Text = text, AccessibleName = text, Bounds = bounds,
                FlatStyle = Forms.FlatStyle.Flat, Cursor = Forms.Cursors.Hand, UseVisualStyleBackColor = false,
                BackColor = destructive ? dark ? Color.FromArgb(90, 46, 48) : Color.FromArgb(252, 233, 232)
                    : dark ? Color.FromArgb(55, 60, 68) : Color.FromArgb(237, 239, 242),
                ForeColor = destructive ? dark ? Color.FromArgb(255, 185, 180) : Color.FromArgb(165, 33, 29) : ForeColor };
            button.FlatAppearance.BorderSize = 0;
            using var path = new GraphicsPath();
            var diameter = Px(10);
            path.AddArc(0, 0, diameter, diameter, 180, 90);
            path.AddArc(bounds.Width - diameter, 0, diameter, diameter, 270, 90);
            path.AddArc(bounds.Width - diameter, bounds.Height - diameter, diameter, diameter, 0, 90);
            path.AddArc(0, bounds.Height - diameter, diameter, diameter, 90, 90); path.CloseFigure();
            button.Region = new Region(path);
            return button;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _bodyFont.Dispose(); _headingFont.Dispose(); }
    }
}
