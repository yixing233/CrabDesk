using CrabDesk.Core;
using Microsoft.Win32;

namespace CrabDesk.Native;

public sealed class ExplorerIconVisibility : IExplorerIconVisibility
{
    private const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public bool GetIconsHidden()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvancedKey);
        return Convert.ToInt32(key?.GetValue("HideIcons", 0) ?? 0) != 0;
    }

    public void SetIconsHidden(bool hidden)
    {
        if (GetIconsHidden() == hidden)
        {
            return;
        }

        var view = DesktopHostService.FindDesktopView();
        if (view != IntPtr.Zero)
        {
            NativeMethods.SendMessageTimeout(
                view,
                NativeMethods.WmCommand,
                new IntPtr(NativeMethods.DesktopToggleIconsCommand),
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                1500,
                out _);
        }

        if (GetIconsHidden() != hidden)
        {
            using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedKey);
            key.SetValue("HideIcons", hidden ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
