using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

/// <summary>
/// Maps the file-operation verbs of the native shell context menu onto the
/// commands the replacement desktop performs itself. Letting Explorer run
/// copy, cut, paste, or delete hands the work to every installed shell hook,
/// which costs seconds on a machine with security software attached and raises
/// Explorer's own collision and confirmation prompts. The managed commands are
/// immediate, silent, and resolve a name collision with the "_2" suffix.
/// </summary>
internal static class DesktopShellMenuPolicy
{
    internal static bool TryMapFileOperation(
        ShellContextMenuCommand command,
        out DesktopKeyboardCommand desktopCommand)
    {
        switch (command)
        {
            case ShellContextMenuCommand.Copy:
                desktopCommand = DesktopKeyboardCommand.Copy;
                return true;
            case ShellContextMenuCommand.Cut:
                desktopCommand = DesktopKeyboardCommand.Cut;
                return true;
            case ShellContextMenuCommand.Paste:
                desktopCommand = DesktopKeyboardCommand.Paste;
                return true;
            case ShellContextMenuCommand.Delete:
                desktopCommand = DesktopKeyboardCommand.Delete;
                return true;
            default:
                desktopCommand = default;
                return false;
        }
    }
}
