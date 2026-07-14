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
        key.SetValue(null, "CrabDesk", RegistryValueKind.String);
        key.SetValue("MUIVerb", "CrabDesk", RegistryValueKind.String);
        key.SetValue("Icon", $"\"{normalizedExecutable}\",0", RegistryValueKind.String);
        key.SetValue("Position", "Bottom", RegistryValueKind.String);
        key.SetValue("SubCommands", string.Empty, RegistryValueKind.String);

        using var organize = key.CreateSubKey(@"shell\Organize", true)
            ?? throw new InvalidOperationException("无法创建智能整理菜单命令。");
        organize.SetValue(null, "智能整理", RegistryValueKind.String);
        organize.SetValue("Icon", $"\"{normalizedExecutable}\",0", RegistryValueKind.String);
        using var organizeCommand = organize.CreateSubKey("command", true)
            ?? throw new InvalidOperationException("无法创建智能整理命令行。");
        organizeCommand.SetValue(null, $"\"{normalizedExecutable}\" --organize", RegistryValueKind.String);

        using var open = key.CreateSubKey(@"shell\Open", true)
            ?? throw new InvalidOperationException("无法创建设置菜单命令。");
        open.SetValue(null, "打开设置", RegistryValueKind.String);
        using var openCommand = open.CreateSubKey("command", true)
            ?? throw new InvalidOperationException("无法创建设置命令行。");
        openCommand.SetValue(null, $"\"{normalizedExecutable}\"", RegistryValueKind.String);
    }
}
