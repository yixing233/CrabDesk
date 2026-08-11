namespace CrabDesk.Native;

/// <summary>
/// Reads the Windows desktop icon-size preference and forwards an explicit
/// Ctrl+wheel gesture. It never enumerates, reads from, or writes to
/// Explorer's desktop ListView memory.
/// </summary>
public static class DesktopIconPositionService
{
    private const string DesktopBagPath = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";
    private const uint LvmFirst = 0x1000;
    private const uint LvmGetItemSpacing = LvmFirst + 51;
    private const uint WmMouseWheel = 0x020A;
    private const uint MkControl = 0x0008;
    private const uint MessageTimeoutMilliseconds = 500;

    public static int? GetDesktopIconSize()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DesktopBagPath);
            return key?.GetValue("IconSize") is int value ? Math.Clamp(value, 16, 256) : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool ForwardControlMouseWheel(IntPtr listView, int screenX, int screenY, int delta)
    {
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView) || delta == 0)
        {
            return false;
        }

        var wheel = unchecked((uint)(ushort)(short)delta);
        var keysAndDelta = new IntPtr(unchecked((int)((wheel << 16) | MkControl)));
        var coordinates = unchecked((uint)(ushort)(short)screenX) |
            (unchecked((uint)(ushort)(short)screenY) << 16);
        return NativeMethods.PostMessage(
            listView,
            WmMouseWheel,
            keysAndDelta,
            new IntPtr(unchecked((int)coordinates)));
    }

    public static System.Drawing.Size GetItemSpacing(IntPtr listView)
    {
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView) ||
            NativeMethods.SendMessageTimeout(
                listView,
                LvmGetItemSpacing,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                MessageTimeoutMilliseconds,
                out var result) == IntPtr.Zero)
        {
            return new System.Drawing.Size(88, 96);
        }

        var packed = result.ToInt64();
        var horizontal = unchecked((ushort)(packed & 0xffff));
        var vertical = unchecked((ushort)((packed >> 16) & 0xffff));
        return new System.Drawing.Size(
            horizontal > 0 ? horizontal : 88,
            vertical > 0 ? vertical : 96);
    }
}
