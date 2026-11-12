using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>Receives CF_HDROP directly, including desktop OLE sources on the same UI thread.</summary>
public sealed class FileShelfNativeDropTarget : IDisposable
{
    private readonly List<IntPtr> _windows = [];
    private readonly DropTarget _target;
    private bool _disposed;

    public FileShelfNativeDropTarget(IntPtr window, Func<string[], string?, bool> accepts, Action<bool> hover, Action<string[]> drop)
    {
        _target = new DropTarget(accepts, hover, drop);
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        try
        {
            Register(window);
            EnumChildWindows(window, (child, _) =>
            {
                var name = new StringBuilder(128); GetClassName(child, name, name.Capacity);
                if (name.ToString() == "Microsoft.UI.Content.DesktopChildSiteBridge") Register(child);
                return true;
            }, IntPtr.Zero);
        }
        catch { Dispose(); throw; }
    }

    private void Register(IntPtr window)
    {
        var result = RegisterDragDrop(window, _target);
        if (result == unchecked((int)0x80040101)) { RevokeDragDrop(window); result = RegisterDragDrop(window, _target); }
        Marshal.ThrowExceptionForHR(result); _windows.Add(window);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _target.Active = false;
        foreach (var window in _windows) RevokeDragDrop(window);
        _windows.Clear(); OleUninitialize();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropTarget(Func<string[], string?, bool> accepts, Action<bool> hover, Action<string[]> drop) : IDropTarget
    {
        public bool Active = true;
        private string[] _paths = [];
        private string? _source;
        private bool Read(IDataObject data)
        {
            try
            {
                var files = new Forms.DataObject(data);
                _paths = files.GetData(Forms.DataFormats.FileDrop) as string[] ?? [];
                _source = files.GetDataPresent(DesktopBoxForm.FileShelfDragFormat) ? files.GetData(DesktopBoxForm.FileShelfDragFormat) as string : null;
                return _paths.Length > 0;
            }
            catch (Exception exception) { DiagnosticLog.Error("File transfer drag data failed", exception); _paths = []; return false; }
        }
        private bool Accept(uint effect) => Active && (effect & 1) != 0 && _paths.Length > 0 && accepts(_paths, _source);
        public int DragEnter(IDataObject data, uint keys, NativePoint point, ref uint effect)
        {
            var accepted = Read(data) && Accept(effect); effect = accepted ? 1u : 0;
            if (Active) hover(accepted); return 0;
        }
        public int DragOver(uint keys, NativePoint point, ref uint effect)
        {
            var accepted = Accept(effect); effect = accepted ? 1u : 0;
            if (Active) hover(accepted); return 0;
        }
        public int DragLeave() { _paths = []; if (Active) hover(false); return 0; }
        public int Drop(IDataObject data, uint keys, NativePoint point, ref uint effect)
        {
            var accepted = Read(data) && Accept(effect); effect = accepted ? 1u : 0;
            var paths = _paths; _paths = [];
            if (Active) { hover(false); if (accepted) drop(paths); }
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, NativePoint point, ref uint effect);
        [PreserveSig] int DragOver(uint keys, NativePoint point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, NativePoint point, ref uint effect);
    }
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr data);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumWindowProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
}
