using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using CrabDesk.Core;

namespace CrabDesk.Native;

public sealed class DesktopHostService : IDesktopHost
{
    private static readonly TimeSpan ExplorerExitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExplorerStartTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ExplorerPollInterval = TimeSpan.FromMilliseconds(150);

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
        return changed;
    }

    /// <summary>
    /// Updates only the affected desktop rows after a real file-attribute
    /// change. SHCNE_ATTRIBUTES makes the desktop ListView drop every row's
    /// image index on some Windows builds; SHCNE_UPDATEITEM preserves them.
    /// </summary>
    public int NotifyItemAttributesChanged(IEnumerable<string> paths)
    {
        var notified = 0;
        foreach (var itemPath in paths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Marshal.StringToCoTaskMemUni(itemPath);
            try
            {
                NativeMethods.SHChangeNotify(
                    NativeMethods.ShcneUpdateItem,
                    NativeMethods.ShcnfPathW,
                    path,
                    IntPtr.Zero);
                notified++;
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
        return notified;
    }

    /// <summary>
    /// Refreshes every existing Explorer desktop row and repaints the desktop
    /// view without broadcasting a Shell-wide association or directory
    /// change. Those notifications are for real Shell state changes and must
    /// not be used as a redraw command.
    /// </summary>
    public Task<bool> RepairIconImageListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = GetIconImageListState();
        if (!before.IsDesktopListViewAvailable)
        {
            return Task.FromResult(false);
        }

        var rowsUpdated = UpdateDesktopListViewRows(cancellationToken);
        RedrawDesktopListView();
        RedrawDesktopView();
        var after = GetIconImageListState();
        if (after.HasImageList)
        {
            return Task.FromResult(rowsUpdated);
        }

        // The image list handle exists but contains no images: the Shell
        // icon cache is damaged. Ask the Shell to rebuild its icon storage,
        // then retry the row refresh once Explorer has repopulated it.
        NativeMethods.SHChangeNotify(
            NativeMethods.ShcneAssocChanged,
            NativeMethods.ShcnfIdList,
            IntPtr.Zero,
            IntPtr.Zero);
        return Task.FromResult(false);
    }

    /// <summary>
    /// Restarts the Explorer process that owns the current desktop view.
    /// Windows usually relaunches that shell itself, but not always; if no
    /// explorer process appears shortly after the kill, one is started
    /// explicitly. With no shell running it becomes the new shell instead
    /// of opening an extra File Explorer window.
    /// </summary>
    public async Task<bool> RestartExplorerShellAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var desktopView = DesktopView != IntPtr.Zero ? DesktopView : FindDesktopView();
        if (desktopView == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(desktopView, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var explorer = Process.GetProcessById((int)processId);
            if (!string.Equals(explorer.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            explorer.Kill();
            if (!await WaitForExplorerExitAsync(explorer, cancellationToken))
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }

        if (!await WaitForExplorerProcessAsync(cancellationToken))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        DesktopParent = IntPtr.Zero;
        DesktopView = IntPtr.Zero;
        DesktopListView = IntPtr.Zero;
        return await WaitForDesktopShellAsync(cancellationToken);
    }

    private static async Task<bool> WaitForExplorerProcessAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Process.GetProcessesByName("explorer").Length > 0)
                {
                    return true;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            await Task.Delay(ExplorerPollInterval, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    public DesktopIconImageListState GetIconImageListState()
    {
        var listView = DesktopListView;
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView))
        {
            return new DesktopIconImageListState(false, IntPtr.Zero, IntPtr.Zero, 0, 0);
        }

        NativeMethods.SendMessageTimeout(
            listView,
            NativeMethods.LvmGetImageList,
            new IntPtr(NativeMethods.LvsilNormal),
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            500,
            out var normal);
        NativeMethods.SendMessageTimeout(
            listView,
            NativeMethods.LvmGetImageList,
            new IntPtr(NativeMethods.LvsilSmall),
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            500,
            out var small);
        // The image list handle can be non-null while the list itself is
        // empty or was cleared by a damaged icon cache. Items then render as
        // text-only labels. Check the actual image counts so an empty list
        // is treated as lost and repaired.
        var normalCount = ImageList_GetImageCount(normal);
        var smallCount = ImageList_GetImageCount(small);
        return new DesktopIconImageListState(true, normal, small, normalCount, smallCount);
    }

    [DllImport("comctl32.dll")]
    private static extern int ImageList_GetImageCount(IntPtr himl);

    private void RedrawDesktopListView()
    {
        var listView = DesktopListView;
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView))
        {
            return;
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
    }

    private async Task<bool> WaitForExplorerExitAsync(Process explorer, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ExplorerExitTimeout;
        while (!explorer.HasExited && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(ExplorerPollInterval, cancellationToken).ConfigureAwait(false);
        }
        return explorer.HasExited;
    }

    private async Task<bool> WaitForDesktopShellAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ExplorerStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Refresh();
            if (IsAvailable && DesktopView != IntPtr.Zero && DesktopListView != IntPtr.Zero)
            {
                return true;
            }
            await Task.Delay(ExplorerPollInterval, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private bool UpdateDesktopListViewRows(CancellationToken cancellationToken)
    {
        var listView = DesktopListView;
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView) ||
            NativeMethods.SendMessageTimeout(
                listView,
                NativeMethods.LvmGetItemCount,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                500,
                out var itemCountResult) == IntPtr.Zero)
        {
            return false;
        }

        var itemCount = itemCountResult.ToInt32();
        if (itemCount < 0)
        {
            return false;
        }

        for (var index = 0; index < itemCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.SendMessageTimeout(
                    listView,
                    NativeMethods.LvmUpdate,
                    new IntPtr(index),
                    IntPtr.Zero,
                    NativeMethods.SmtoAbortIfHung,
                    500,
                    out var updated) == IntPtr.Zero ||
                updated == IntPtr.Zero)
            {
                return false;
            }
        }

        return true;
    }

    private void RedrawDesktopView()
    {
        var view = DesktopView;
        if (view == IntPtr.Zero || !NativeMethods.IsWindow(view))
        {
            return;
        }

        NativeMethods.RedrawWindow(
            view,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.RdwInvalidate |
            NativeMethods.RdwErase |
            NativeMethods.RdwAllChildren |
            NativeMethods.RdwUpdateNow);
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

public readonly record struct DesktopIconImageListState(
    bool IsDesktopListViewAvailable,
    IntPtr Normal,
    IntPtr Small,
    int NormalCount = 0,
    int SmallCount = 0)
{
    public bool HasImageList =>
        (Normal != IntPtr.Zero && NormalCount > 0) ||
        (Small != IntPtr.Zero && SmallCount > 0);
}
