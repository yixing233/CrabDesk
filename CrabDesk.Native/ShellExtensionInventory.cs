using System.Diagnostics;
using CrabDesk.Core;
using Microsoft.Win32;

namespace CrabDesk.Native;

/// <summary>
/// Lists the shell extensions that can slow Explorer's desktop thread down, by
/// reading the icon-overlay registrations and the non-Microsoft modules loaded
/// inside the desktop's own explorer.exe.
/// </summary>
/// <remarks>
/// Read-only on purpose. Disabling an overlay handler means renaming a value
/// under HKLM, which needs elevation and changes another product's
/// registration, so the report names the culprit and leaves the decision (and
/// the change) to the user.
/// </remarks>
public static class ShellExtensionInventory
{
    private const string DefaultOverlayKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";

    private const string ClassesClsidPath = @"SOFTWARE\Classes\CLSID";

    /// <summary>
    /// The overlay handlers registered for the desktop namespace. These run in
    /// Explorer's process for every item it paints, which is why a new desktop
    /// file can cost over a second when one of them has to activate a service.
    /// The hive and key path are injectable so tests can use a throwaway
    /// subtree instead of the real machine registration.
    /// </summary>
    public static IReadOnlyList<ShellExtensionEntry> ReadIconOverlayHandlers(
        RegistryKey? root = null,
        string? keyPath = null,
        string? clsidRoot = null)
    {
        var hive = root ?? Registry.LocalMachine;
        var path = keyPath ?? DefaultOverlayKeyPath;
        var clsidPath = clsidRoot ?? ClassesClsidPath;
        var entries = new List<ShellExtensionEntry>();
        try
        {
            using var overlay = hive.OpenSubKey(path, false);
            if (overlay is null)
            {
                return entries;
            }

            foreach (var name in overlay.GetSubKeyNames())
            {
                string? clsid;
                try
                {
                    using var handler = overlay.OpenSubKey(name, false);
                    clsid = handler?.GetValue(null) as string;
                }
                catch (Exception exception) when (exception is System.Security.SecurityException
                                                       or UnauthorizedAccessException
                                                       or IOException)
                {
                    continue;
                }

                var serverPath = clsid is null
                    ? string.Empty
                    : ResolveClsidServer(hive, clsid, clsidPath);
                entries.Add(new ShellExtensionEntry(
                    name.Trim(),
                    ShellExtensionKind.IconOverlay,
                    DesktopStallAnalysis.ClassifyProduct(serverPath),
                    serverPath));
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
                                               or UnauthorizedAccessException
                                               or IOException)
        {
            // A locked-down machine may deny even this read; the module list
            // below still produces evidence, so report what we have.
        }

        return entries;
    }

    /// <summary>
    /// Resolves a CLSID to its in-process server DLL, checking the 32-bit view
    /// as well because shell extensions are registered per bitness.
    /// </summary>
    public static string ResolveClsidServer(RegistryKey hive, string clsid, string? clsidRoot = null)
    {
        if (string.IsNullOrWhiteSpace(clsid))
        {
            return string.Empty;
        }

        var normalized = clsid.Trim();
        if (!normalized.StartsWith('{'))
        {
            normalized = "{" + normalized + "}";
        }

        var root = clsidRoot ?? ClassesClsidPath;
        foreach (var prefix in new[] { root, $@"{root}\WOW6432Node" })
        {
            var path = $@"{prefix}\{normalized}";
            try
            {
                using var server = hive.OpenSubKey($@"{path}\InprocServer32", false);
                if (server?.GetValue(null) is string dll && !string.IsNullOrWhiteSpace(dll))
                {
                    return dll.Trim('"');
                }

                using var key = hive.OpenSubKey(path, false);
                if (key?.GetValue(null) is string localServer && !string.IsNullOrWhiteSpace(localServer))
                {
                    return localServer.Trim('"');
                }
            }
            catch (Exception exception) when (exception is System.Security.SecurityException
                                                   or UnauthorizedAccessException
                                                   or IOException)
            {
                // Try the next view.
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Non-Microsoft DLLs loaded in the explorer.exe that owns the desktop
    /// thread. Enumerating another process's modules needs the same privilege
    /// as reading it, so a protected or elevated Explorer simply yields
    /// nothing and the overlay list carries the report.
    /// </summary>
    public static IReadOnlyList<ShellExtensionEntry> ReadDesktopExplorerModules(IntPtr desktopView)
    {
        if (desktopView == IntPtr.Zero || !NativeMethods.IsWindow(desktopView))
        {
            return [];
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(desktopView, out var processId);
        if (processId == 0 || threadId == 0)
        {
            return [];
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.Modules
                .OfType<ProcessModule>()
                .Select(module => module.FileName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(path => !DesktopStallAnalysis.IsMicrosoftOwnedPath(path))
                .Select(path => new ShellExtensionEntry(
                    Path.GetFileName(path),
                    ShellExtensionKind.LoadedModule,
                    DesktopStallAnalysis.ClassifyProduct(path),
                    path))
                .ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                               or System.ComponentModel.Win32Exception
                                               or NotSupportedException
                                               or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Both sources, deduplicated and ordered for display.</summary>
    public static IReadOnlyList<ShellExtensionEntry> Collect(
        IntPtr desktopView,
        RegistryKey? registryRoot = null,
        string? overlayKeyPath = null,
        string? clsidRoot = null) =>
        DesktopStallAnalysis.FilterRelevantExtensions(
            ReadIconOverlayHandlers(registryRoot, overlayKeyPath, clsidRoot)
                .Concat(ReadDesktopExplorerModules(desktopView)));
}
