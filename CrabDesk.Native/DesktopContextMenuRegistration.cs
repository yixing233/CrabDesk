using CrabDesk.Core;
using Microsoft.Win32;
using System.IO;

namespace CrabDesk.Native;

public sealed class DesktopContextMenuRegistration : IDesktopContextMenuRegistration
{
    private const string DefaultKeyPath = @"Software\Classes\DesktopBackground\Shell\CrabDesk";
    private const string DefaultSubmenuClassName = "CrabDesk.DesktopContextMenu.Commands";
    private const string DefaultSubmenuKeyPath = @"Software\Classes\CrabDesk.DesktopContextMenu.Commands";
    private const string DefaultLegacyOrganizeKeyPath =
        @"Software\Classes\DesktopBackground\Shell\CrabDesk.Organize";
    private readonly RegistryKey _root;
    private readonly string _keyPath;
    private readonly string _submenuClassName;
    private readonly string _submenuKeyPath;
    private readonly string _legacyOrganizeKeyPath;

    public DesktopContextMenuRegistration(
        RegistryKey? root = null,
        string? keyPath = null,
        string? submenuClassName = null,
        string? submenuKeyPath = null,
        string? legacyOrganizeKeyPath = null)
    {
        _root = root ?? Registry.CurrentUser;
        _keyPath = keyPath ?? DefaultKeyPath;
        _submenuClassName = submenuClassName ?? DefaultSubmenuClassName;
        _submenuKeyPath = submenuKeyPath ?? DefaultSubmenuKeyPath;
        _legacyOrganizeKeyPath = legacyOrganizeKeyPath ?? DefaultLegacyOrganizeKeyPath;
    }

    public bool IsEnabled
    {
        get
        {
            using var key = _root.OpenSubKey(_keyPath, false);
            using var submenu = _root.OpenSubKey(_submenuKeyPath, false);
            return key is not null && submenu is not null;
        }
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        if (!enabled)
        {
            DeleteOwnedKeys();
            return;
        }

        var normalizedExecutable = Path.GetFullPath(executablePath);
        DeleteOwnedKeys();

        using var key = _root.CreateSubKey(_keyPath, true)
            ?? throw new InvalidOperationException("Unable to create the CrabDesk desktop context-menu entry.");
        key.SetValue(null, "CrabDesk", RegistryValueKind.String);
        key.SetValue("MUIVerb", "CrabDesk", RegistryValueKind.String);
        key.SetValue("Icon", $"\"{normalizedExecutable}\",0", RegistryValueKind.String);
        key.SetValue("Position", "Bottom", RegistryValueKind.String);
        key.SetValue("ExtendedSubCommandsKey", _submenuClassName, RegistryValueKind.String);

        using var submenu = _root.CreateSubKey(_submenuKeyPath, true)
            ?? throw new InvalidOperationException("Unable to create the CrabDesk submenu command store.");
        WriteCommand(submenu, "01Open", "\u6253\u5F00\u8BBE\u7F6E", normalizedExecutable, null);
        WriteCommand(submenu, "02CreateBox", "\u521B\u5EFA\u76D2\u5B50", normalizedExecutable, "--create-box");
        WriteCommand(submenu, "03Organize", "\u667A\u80FD\u6574\u7406", normalizedExecutable, "--organize");
    }

    private static void WriteCommand(
        RegistryKey submenu,
        string keyName,
        string title,
        string executablePath,
        string? argument)
    {
        using var verb = submenu.CreateSubKey($@"shell\{keyName}", true)
            ?? throw new InvalidOperationException($"Unable to create the CrabDesk {keyName} submenu entry.");
        verb.SetValue(null, title, RegistryValueKind.String);
        verb.SetValue("MUIVerb", title, RegistryValueKind.String);
        verb.SetValue("Icon", $"\"{executablePath}\",0", RegistryValueKind.String);
        using var command = verb.CreateSubKey("command", true)
            ?? throw new InvalidOperationException($"Unable to create the CrabDesk {keyName} command.");
        var commandLine = argument is null
            ? $"\"{executablePath}\""
            : $"\"{executablePath}\" {argument}";
        command.SetValue(null, commandLine, RegistryValueKind.String);
    }

    private void DeleteOwnedKeys()
    {
        _root.DeleteSubKeyTree(_keyPath, false);
        _root.DeleteSubKeyTree(_submenuKeyPath, false);
        _root.DeleteSubKeyTree(_legacyOrganizeKeyPath, false);
    }
}
