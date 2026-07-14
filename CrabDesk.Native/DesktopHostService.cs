using System.Text;
using CrabDesk.Core;

namespace CrabDesk.Native;

public sealed class DesktopHostService : IDesktopHost
{
    public IntPtr DesktopParent { get; private set; }
    public IntPtr DesktopListView { get; private set; }
    public IntPtr DesktopView { get; private set; }
    public bool IsAvailable => DesktopParent != IntPtr.Zero && NativeMethods.IsWindow(DesktopParent);

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
        return changed;
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
