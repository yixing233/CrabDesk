using Microsoft.Win32;

namespace CrabDesk.Runtime;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CrabDesk";

    internal static bool IsEnabled(RegistryKey? registryRoot = null, string runKey = RunKey)
    {
        using var key = (registryRoot ?? Registry.CurrentUser).OpenSubKey(runKey);
        var executable = GetExecutable(key?.GetValue(ValueName) as string);
        return executable is not null && File.Exists(executable);
    }

    internal static void SetEnabled(bool enabled, RegistryKey? registryRoot = null, string runKey = RunKey, string? executablePath = null)
    {
        var root = registryRoot ?? Registry.CurrentUser;
        using var key = enabled ? root.CreateSubKey(runKey) : root.OpenSubKey(runKey, writable: true);
        if (key is null) return;
        if (enabled)
        {
            // The launch-to-tray preference decides whether to show settings.
            // Forcing --background here overrides that preference at logon.
            var executable = executablePath ?? Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "CrabDesk.WinUI.exe");
            key.SetValue(ValueName, $"\"{executable}\"");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    internal static void NormalizeLaunchCommand(RegistryKey? registryRoot = null, string runKey = RunKey)
    {
        using var key = (registryRoot ?? Registry.CurrentUser).OpenSubKey(runKey, writable: true);
        if (key?.GetValue(ValueName) is not string command) return;
        var executable = GetExecutable(command);
        if (executable is not null && File.Exists(executable) &&
            string.Equals(command.Trim(), $"\"{executable}\" --background", StringComparison.OrdinalIgnoreCase))
            key.SetValue(ValueName, $"\"{executable}\"");
    }

    private static string? GetExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var separator = command.IndexOf(' ');
        return separator < 0 ? command : command[..separator];
    }
}
