using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace CrabDesk.Native;

/// <summary>
/// The target side of the shell's optimized-move handshake.
/// </summary>
/// <remarks>
/// When a drop target returns <c>DROPEFFECT_MOVE</c> from <c>IDropTarget::Drop</c>
/// and never calls <c>IDataObject::SetData(CFSTR_PERFORMEDDROPEFFECT)</c>, Explorer
/// assumes an unoptimized move: the target copied, so the source must delete the
/// originals. CrabDesk moves (or recycles) dropped files itself, so that deletion
/// targets files that are already gone and Explorer's file operation stalls in its
/// retry path, freezing both processes for seconds. The handshake tells the source
/// that the target already did the whole move (<c>Performed DropEffect = NONE</c>)
/// while <c>Logical Performed DropEffect = MOVE</c> keeps its bookkeeping honest.
/// Call it before <c>Drop</c> returns, then return a non-MOVE effect as well.
/// </remarks>
public static class ShellDropEffectProtocol
{
    public const string PerformedDropEffectFormat = "Performed DropEffect";
    public const string LogicalPerformedDropEffectFormat = "Logical Performed DropEffect";
    public const int DropEffectNone = 0;
    public const int DropEffectCopy = 1;
    public const int DropEffectMove = 2;

    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;

    /// <summary>
    /// Reports that this target performed the whole move itself. Returns false when
    /// the source's data object refuses <c>SetData</c> (non-shell sources often do);
    /// the caller should still return a non-MOVE effect so no source deletes anything.
    /// </summary>
    public static bool TryReportOptimizedMove(ComDataObject? dataObject) =>
        TrySetDropEffect(dataObject, PerformedDropEffectFormat, DropEffectNone) &
        TrySetDropEffect(dataObject, LogicalPerformedDropEffectFormat, DropEffectMove);

    public static bool TrySetDropEffect(ComDataObject? dataObject, string formatName, int dropEffect)
    {
        if (dataObject is null)
        {
            return false;
        }

        var handle = IntPtr.Zero;
        try
        {
            handle = GlobalAlloc(GmemMoveable | GmemZeroInit, (UIntPtr)sizeof(int));
            if (handle == IntPtr.Zero)
            {
                return false;
            }
            var memory = GlobalLock(handle);
            if (memory == IntPtr.Zero)
            {
                return false;
            }
            Marshal.WriteInt32(memory, dropEffect);
            GlobalUnlock(handle);

            var format = new FORMATETC
            {
                cfFormat = unchecked((short)System.Windows.Forms.DataFormats.GetFormat(formatName).Id),
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                ptd = IntPtr.Zero,
                tymed = TYMED.TYMED_HGLOBAL
            };
            var medium = new STGMEDIUM
            {
                tymed = TYMED.TYMED_HGLOBAL,
                unionmember = handle,
                pUnkForRelease = null
            };
            // fRelease = true: the data object owns the HGLOBAL from here on.
            dataObject.SetData(ref format, ref medium, true);
            handle = IntPtr.Zero;
            return true;
        }
        catch (Exception exception) when (exception is COMException or NotImplementedException or InvalidCastException)
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                GlobalFree(handle);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
