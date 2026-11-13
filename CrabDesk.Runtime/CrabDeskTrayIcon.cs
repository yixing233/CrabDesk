using System.Drawing;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed class CrabDeskTrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _image;
    internal FluentContextMenuStrip Menu { get; } = new();
    internal bool Visible => _icon.Visible;

    internal CrabDeskTrayIcon(Action settings, Action widgets, Action togglePause, Action exit,
        Func<bool> isPaused, Action<Forms.ContextMenuStrip> applyTheme, Action? leftClick = null,
        Action? center = null, Func<bool>? centerEnabled = null)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CrabDesk.ico");
        _image = File.Exists(iconPath) ? new Icon(iconPath, Forms.SystemInformation.SmallIconSize) : (Icon)SystemIcons.Application.Clone();
        Menu.Items.Add("打开设置", null, (_, _) => settings());
        Menu.Items.Add("小组件", null, (_, _) => widgets());
        var centerItem = center is null ? null : Menu.Items.Add("组件中心", null, (_, _) => center());
        var pause = Menu.Items.Add("暂停接管", null, (_, _) => togglePause());
        Menu.Items.Add(new Forms.ToolStripSeparator());
        Menu.Items.Add("退出 CrabDesk", null, (_, _) => exit());
        Menu.Opening += (_, _) =>
        {
            pause.Text = isPaused() ? "恢复接管" : "暂停接管";
            if (centerItem is not null) centerItem.Visible = centerEnabled?.Invoke() == true;
            applyTheme(Menu);
        };
        applyTheme(Menu);
        _icon = new Forms.NotifyIcon { Text = "CrabDesk", Icon = _image, ContextMenuStrip = Menu, Visible = true };
        _icon.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) (leftClick ?? settings)(); };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        Menu.Dispose();
        _image.Dispose();
    }
}
