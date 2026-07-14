using CrabDesk.Core;
using Microsoft.Win32;
using System.IO;

namespace CrabDesk.Native;

public sealed class DesktopContextMenuRegistration : IDesktopContextMenuRegistration
{
    private const string DefaultKeyPath = @"Software\Classes\DesktopBackground\Shell\CrabDesk";
    private readonly RegistryKey _root;
    private readonly string _keyPath;

    public DesktopContextMenuRegistration(RegistryKey? root = null, string? keyPath = null)
    {
        _root = root ?? Registry.CurrentUser;
        _keyPath = keyPath ?? DefaultKeyPath;
    }

    public bool IsEnabled
    {
        get
        {
            using var key = _root.OpenSubKey(_keyPath, false);
            return key is not null;
        }
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        if (!enabled)
        {
            _root.DeleteSubKeyTree(_keyPath, false);
            return;
        }

        var normalizedExecutable = Path.GetFullPath(executablePath);
        using var key = _root.CreateSubKey(_keyPath, true)
            ?? throw new InvalidOperationException("无法创建桌面右键菜单注册项。");
        key.SetValue(null, "打开 CrabDesk", RegistryValueKind.String);
        key.SetValue("Icon", $"\"{normalizedExecutable}\",0", RegistryValueKind.String);
        key.SetValue("Position", "Bottom", RegistryValueKind.String);
        using var command = key.CreateSubKey("command", true)
            ?? throw new InvalidOperationException("无法创建桌面右键菜单命令。");
        command.SetValue(null, $"\"{normalizedExecutable}\"", RegistryValueKind.String);
    }
}
