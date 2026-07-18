using System.Text;
using System.Runtime.InteropServices;
using CrabDesk.Core;

namespace CrabDesk.Native;

public sealed class DesktopHostService : IDesktopHost
{
    public IntPtr DesktopParent { get; private set; }
    public IntPtr DesktopListView { get; private set; }
    public IntPtr DesktopView { get; private set; }
    public bool IsAvailable => DesktopParent != IntPtr.Zero && NativeMethods.IsWindow(DesktopParent);
    public bool IsDesktopInputEnabled => IsAvailable && NativeMethods.IsWindowEnabled(DesktopParent);

    public bool Refresh()
    {
        var view = FindDesktopView();
        var parent = view == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetParent(view);
        var listView = view == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.FindWindowEx(view, IntPtr.Zero, "SysListView32", "FolderView");

        var changed = parent != DesktopParent || listView != DesktopListView || view != DesktopView;
        DesktopParent = parent;
        DesktopListView = listView;
        DesktopView = view;
        if (changed && listView != IntPtr.Zero)
        {
            RefreshIconImageList();
        }
        return changed;
    }

    /// <summary>
    /// Repairs the Explorer desktop ListView when its rows remain but its
    /// image list has been dropped. This is the characteristic state where
    /// desktop names remain visible while every icon is blank.
    /// </summary>
    public bool RefreshIconImageList()
    {
        var listView = DesktopListView;
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView))
        {
            return false;
        }

        NativeMethods.SendMessageTimeout(
            listView,
            NativeMethods.LvmGetImageList,
            new IntPtr(NativeMethods.LvsilNormal),
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            500,
            out var before);
        NativeMethods.SendMessageTimeout(
            listView,
            NativeMethods.LvmGetImageList,
            new IntPtr(NativeMethods.LvsilSmall),
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            500,
            out var smallBefore);
        // File attribute changes only require a desktop-directory refresh.
        // Flushing the global association cache while Explorer still has a
        // healthy image list can detach that list and leave labels visible
        // with every icon blank. Reserve the heavier refresh for recovery.
        if (before == IntPtr.Zero && smallBefore == IntPtr.Zero)
        {
            NativeMethods.SHChangeNotify(
                NativeMethods.ShcneAssocChanged,
                NativeMethods.ShcnfIdList,
                IntPtr.Zero,
                IntPtr.Zero);
        }
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath))
        {
            var path = Marshal.StringToCoTaskMemUni(desktopPath);
            try
            {
                NativeMethods.SHChangeNotify(
                    NativeMethods.ShcneUpdatedir,
                    NativeMethods.ShcnfPathW,
                    path,
                    IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }

        NativeMethods.SendMessageTimeout(
            listView,
            NativeMethods.WmSetRedraw,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            500,
            out _);
        NativeMethods.SendMessageTimeout(
            listView,
            NativeMethods.WmSetRedraw,
            new IntPtr(1),
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            500,
            out _);
        NativeMethods.RedrawWindow(
            listView,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.RdwInvalidate |
            NativeMethods.RdwErase |
            NativeMethods.RdwAllChildren |
            NativeMethods.RdwUpdateNow);

        NativeMethods.SendMessageTimeout(
            listView,
            NativeMethods.LvmGetImageList,
            new IntPtr(NativeMethods.LvsilNormal),
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            500,
            out var after);
        NativeMethods.SendMessageTimeout(
            listView,
            NativeMethods.LvmGetImageList,
            new IntPtr(NativeMethods.LvsilSmall),
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            500,
            out var smallAfter);
        return before != IntPtr.Zero || smallBefore != IntPtr.Zero ||
            after != IntPtr.Zero || smallAfter != IntPtr.Zero;
    }

    /// <summary>
    /// Performs the inexpensive health check used by the host timer. Explorer
    /// can lose its image list without changing any of the desktop HWNDs.
    /// </summary>
    public bool EnsureIconImageList()
    {
        var listView = DesktopListView;
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView))
        {
            return false;
        }

        NativeMethods.SendMessageTimeout(
            listView,
            NativeMethods.LvmGetImageList,
            new IntPtr(NativeMethods.LvsilNormal),
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            250,
            out var normal);
        NativeMethods.SendMessageTimeout(
            listView,
            NativeMethods.LvmGetImageList,
            new IntPtr(NativeMethods.LvsilSmall),
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            250,
            out var small);
        return normal != IntPtr.Zero || small != IntPtr.Zero || RefreshIconImageList();
    }

    public bool EnsureDesktopInputEnabled()
    {
        if (!IsAvailable)
        {
            return false;
        }
        if (NativeMethods.IsWindowEnabled(DesktopParent))
        {
            return true;
        }

        NativeMethods.EnableWindow(DesktopParent, true);
        return NativeMethods.IsWindowEnabled(DesktopParent);
    }

    public static IntPtr FindDesktopView()
    {
        var progman = NativeMethods.FindWindow("Progman", "Program Manager");
        var view = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero)
        {
            return view;
        }

        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            var child = NativeMethods.FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child == IntPtr.Zero)
            {
                return true;
            }

            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static string GetWindowClass(IntPtr hwnd)
    {
        var builder = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }
}
