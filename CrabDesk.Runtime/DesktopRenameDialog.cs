using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>
/// A modal editor for filesystem item names. It is owned by the replacement
/// surface because Explorer's native label editor is hidden during takeover.
/// </summary>
internal static class DesktopRenameDialog
{
    internal static string? Show(
        Forms.IWin32Window owner,
        bool isDarkTheme,
        DesktopItemRef item)
    {
        const string title = "重命名";
        var background = isDarkTheme ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250);
        var surface = isDarkTheme ? Color.FromArgb(48, 48, 48) : Color.White;
        var foreground = isDarkTheme ? Color.FromArgb(245, 245, 245) : Color.FromArgb(31, 31, 31);
        var secondary = isDarkTheme ? Color.FromArgb(66, 66, 66) : Color.FromArgb(238, 238, 238);
        var primary = isDarkTheme ? Color.FromArgb(72, 114, 202) : Color.FromArgb(0, 103, 192);

        using var dialog = new Forms.Form
        {
            Text = title,
            AccessibleName = title,
            AutoScaleMode = Forms.AutoScaleMode.Dpi,
            BackColor = background,
            ClientSize = new Size(390, 164),
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = Forms.FormStartPosition.CenterParent,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point)
        };

        var label = new Forms.Label
        {
            AutoSize = true,
            ForeColor = foreground,
            Location = new Point(20, 22),
            Text = "新名称"
        };
        var input = new Forms.TextBox
        {
            AccessibleName = "新名称",
            BackColor = surface,
            ForeColor = foreground,
            BorderStyle = Forms.BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            Location = new Point(20, 52),
            Size = new Size(350, 28),
            Text = item.DisplayName
        };
        var cancel = new Forms.Button
        {
            BackColor = secondary,
            DialogResult = Forms.DialogResult.Cancel,
            ForeColor = foreground,
            Location = new Point(202, 108),
            Size = new Size(78, 30),
            Text = "取消",
            UseVisualStyleBackColor = false
        };
        var confirm = new Forms.Button
        {
            BackColor = primary,
            DialogResult = Forms.DialogResult.OK,
            ForeColor = Color.White,
            Location = new Point(292, 108),
            Size = new Size(78, 30),
            Text = "重命名",
            UseVisualStyleBackColor = false
        };

        dialog.Controls.AddRange([label, input, cancel, confirm]);
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) =>
        {
            input.SelectAll();
            input.Focus();
        };

        if (dialog.ShowDialog(owner) != Forms.DialogResult.OK)
        {
            return null;
        }

        var newName = input.Text.Trim();
        return string.IsNullOrWhiteSpace(newName) ? null : newName;
    }
}
