using System.Runtime.InteropServices;
using System.Text;
using CrabDesk.Core;

namespace CrabDesk.Native;

public sealed class DesktopInputMonitor : IDesktopInputMonitor
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint MnGetHMenu = 0x01E1;
    private const uint MfByPosition = 0x0400;
    private const uint ExplorerRefreshCommandId = 0x7003;
    private const uint MenuCommandTimeoutMilliseconds = 50;
    private const long DesktopContextMenuTrackingWindowMilliseconds = 10_000;
    private const uint WmQuit = 0x0012;
    private const int HookInstallTimeoutMilliseconds = 5_000;
    private const int HookShutdownTimeoutMilliseconds = 2_000;
    private const int VkControl = 0x11;
    private const int VkReturn = 0x0D;
    private const int VkDelete = 0x2E;
    private const int VkA = 0x41;
    private const int VkC = 0x43;
    private const int VkV = 0x56;
    private const int VkX = 0x58;
    private const int VkF2 = 0x71;
    private const int VkF5 = 0x74;
    private readonly LowLevelHookProc _mouseCallback;
    private readonly LowLevelHookProc _keyboardCallback;
    private readonly Thread _hookThread;
    private readonly ManualResetEventSlim _hooksInstalled = new(false);
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private uint _hookThreadId;
    private long _desktopContextMenuExpiresAt;
    private readonly HashSet<uint> _interceptedKeyboardKeys = [];
    private volatile bool _enabled;
    private bool _disposed;

    /// <summary>
    /// Windows delivers a low-level hook callback on the thread that installed
    /// the hook and holds the input event back until that callback returns.
    /// Both hooks therefore run on a dedicated message-loop thread: a busy UI
    /// thread can no longer stall every keystroke and pointer move in the
    /// system while it renders, reloads icons, or waits on the shell.
    /// </summary>
    public DesktopInputMonitor()
    {
        _mouseCallback = MouseHook;
        _keyboardCallback = KeyboardHook;
        _hookThread = new Thread(RunHookMessageLoop)
        {
            IsBackground = true,
            Name = "CrabDesk desktop input"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        _hooksInstalled.Wait(HookInstallTimeoutMilliseconds);
        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("无法监听桌面双击操作。");
        }
    }

    public event EventHandler<DesktopIconZoomEventArgs>? IconZoomRequested;
    public event EventHandler<DesktopMouseWheelEventArgs>? BoxDragMouseWheelRequested;
    public event EventHandler? DesktopSurfaceClicked;
    public event EventHandler? DesktopContextMenuRequested;
    public event EventHandler? DesktopContextMenuCommandRequested;
    public event EventHandler? DesktopContextMenuRefreshRequested;
    public event EventHandler? DesktopDeleteRequested;
    public event EventHandler? DesktopRenameRequested;
    public event EventHandler<DesktopKeyboardCommandEventArgs>? DesktopKeyboardCommandRequested;

    public IntPtr DesktopListView { get; set; }
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
    public Func<int, int, bool>? IsPointerOverBox { get; set; }
    public Func<bool>? IsBoxItemDragActive { get; set; }
    public Func<bool>? IsDesktopIconDragActive { get; set; }
    public Func<bool>? CanDeleteDesktopItems { get; set; }
    public Func<bool>? CanRenameDesktopItems { get; set; }
    public Func<DesktopKeyboardCommand, bool>? CanHandleDesktopKeyboardCommand { get; set; }

    /// <summary>
    /// Arms command tracking when the replacement icon layer forwards a
    /// right-click to Explorer itself. In that case the initial mouse message
    /// targets CrabDesk rather than Explorer, so the low-level hook has no
    /// other opportunity to start its native-menu tracking window.
    /// </summary>
    public void TrackDesktopContextMenu()
    {
        Volatile.Write(
            ref _desktopContextMenuExpiresAt,
            Environment.TickCount64 + DesktopContextMenuTrackingWindowMilliseconds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Enabled = false;
        var hookThreadId = _hookThreadId;
        if (hookThreadId != 0)
        {
            PostThreadMessage(hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
        var joined = _hookThread == Thread.CurrentThread ||
            _hookThread.Join(HookShutdownTimeoutMilliseconds);
        // The hook thread unhooks itself when its loop ends. Repeating it here
        // covers a thread that never reached the loop and a shutdown that ran
        // out of time; the exchange makes either path unhook exactly once.
        ReleaseHooks();
        if (joined)
        {
            _hooksInstalled.Dispose();
        }
    }

    private void RunHookMessageLoop()
    {
        _hookThreadId = GetCurrentThreadId();
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseCallback, module, 0);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, module, 0);
        _hooksInstalled.Set();
        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero || _disposed)
        {
            ReleaseHooks();
            return;
        }

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        ReleaseHooks();
    }

    private void ReleaseHooks()
    {
        var mouseHook = Interlocked.Exchange(ref _mouseHook, IntPtr.Zero);
        if (mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(mouseHook);
        }
        var keyboardHook = Interlocked.Exchange(ref _keyboardHook, IntPtr.Zero);
        if (keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(keyboardHook);
        }
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        var hook = _mouseHook;
        var msg = message.ToInt32();
        // WH_MOUSE_LL also reports every pointer move. Only the three handled
        // messages need window hit testing, so a move costs this test alone.
        if (code >= 0 && Enabled && DesktopListView != IntPtr.Zero && IsHandledMouseMessage(msg))
        {
            try
            {
                if (HandleMouseMessage(msg, Marshal.PtrToStructure<LowLevelMouseHookStruct>(data)))
                {
                    return new IntPtr(1);
                }
            }
            catch (Exception)
            {
                // The callback runs on unmanaged input dispatch, where an
                // escaping exception ends the process. A message this hook
                // cannot classify is left to its normal owner instead.
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private static bool IsHandledMouseMessage(int message) =>
        message is WmLButtonDown or WmRButtonDown or WmMouseWheel;

    private bool HandleMouseMessage(int message, LowLevelMouseHookStruct mouse)
    {
        var isDesktopSurface = IsDesktopSurfacePoint(mouse.Point);
        var targetWindow = WindowFromPoint(mouse.Point);
        if ((message == WmLButtonDown || message == WmRButtonDown) &&
            isDesktopSurface &&
            !IsCurrentProcessWindow(targetWindow))
        {
            DesktopSurfaceClicked?.Invoke(this, EventArgs.Empty);
            if (message == WmRButtonDown)
            {
                TrackDesktopContextMenu();
                DesktopContextMenuRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (message == WmLButtonDown && IsDesktopContextMenuActive())
        {
            var isRefresh = IsNativeRefreshMenuItem(targetWindow, mouse.Point);
            if (isRefresh)
            {
                Volatile.Write(ref _desktopContextMenuExpiresAt, 0);
                DesktopContextMenuRefreshRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (IsNativeSortMenuItem(targetWindow, mouse.Point))
            {
                Volatile.Write(ref _desktopContextMenuExpiresAt, 0);
                DesktopContextMenuCommandRequested?.Invoke(this, EventArgs.Empty);
            }
            // "Sort by" itself is a submenu. Retain the tracking window
            // after that parent item is clicked so the following click on
            // Name, Size, Type, or Date modified can be recognized.
        }
        else if (message == WmMouseWheel)
        {
            var delta = unchecked((short)(mouse.MouseData >> 16));
            if (delta != 0)
            {
                var controlPressed = GetAsyncKeyState(VkControl) < 0;
                var dragActive = IsBoxItemDragActive?.Invoke() == true ||
                    IsDesktopIconDragActive?.Invoke() == true;
                if (!controlPressed && !dragActive)
                {
                    // Neither the box-drag route nor the zoom route can claim
                    // this wheel, so the pointer hit test is skipped: an
                    // ordinary scroll in any application costs nothing here.
                    return false;
                }

                var overBox = IsPointerOverBox?.Invoke(mouse.Point.X, mouse.Point.Y) == true;
                if (ShouldRouteBoxDragWheel(
                        controlPressed,
                        isDesktopSurface,
                        overBox,
                        dragActive,
                        delta))
                {
                    BoxDragMouseWheelRequested?.Invoke(
                        this,
                        new DesktopMouseWheelEventArgs(delta, mouse.Point.X, mouse.Point.Y));
                    // The hook is the single wheel owner during the OLE drag
                    // loop. Consuming this message avoids a duplicate WinForms
                    // MouseWheel if the current drop target happens to dispatch it.
                    return true;
                }

                if (controlPressed && isDesktopSurface)
                {
                    // Ctrl+wheel over a box zooms the icons of that box instead
                    // of Explorer unassigned-icon layer. Keep forwarding to the
                    // native ListView only while the pointer is on the desktop.
                    if (IsCurrentProcessWindow(targetWindow) && !overBox)
                    {
                        DesktopIconPositionService.ForwardControlMouseWheel(
                            DesktopListView,
                            mouse.Point.X,
                            mouse.Point.Y,
                            delta);
                    }
                    IconZoomRequested?.Invoke(
                        this,
                        new DesktopIconZoomEventArgs(delta, mouse.Point.X, mouse.Point.Y));
                }
            }
        }
        return false;
    }

    internal static bool ShouldRouteBoxDragWheel(
        bool controlPressed,
        bool isDesktopSurface,
        bool pointerOverBox,
        bool boxItemDragActive,
        int delta) =>
        !controlPressed &&
        pointerOverBox &&
        boxItemDragActive &&
        delta != 0;

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        var hook = _keyboardHook;
        if (code >= 0 && Enabled && DesktopListView != IntPtr.Zero)
        {
            try
            {
                if (HandleKeyboardMessage(
                        message.ToInt32(),
                        Marshal.PtrToStructure<LowLevelKeyboardHookStruct>(data)))
                {
                    return new IntPtr(1);
                }
            }
            catch (Exception)
            {
                // See MouseHook: an exception must not escape into the input
                // dispatch, and an unclassified key belongs to its own window.
            }
        }

        return CallNextHookEx(hook, code, message, data);
    }

    private bool HandleKeyboardMessage(int message, LowLevelKeyboardHookStruct keyboard)
    {
        if (message == WmKeyUp || message == WmSysKeyUp)
        {
            return _interceptedKeyboardKeys.Remove(keyboard.VirtualKeyCode);
        }

        if (message != WmKeyDown && message != WmSysKeyDown)
        {
            return false;
        }

        if (_interceptedKeyboardKeys.Contains(keyboard.VirtualKeyCode))
        {
            return true;
        }

        if (!TryGetDesktopKeyboardCommand(keyboard.VirtualKeyCode, out var command) ||
            !IsDesktopForeground() ||
            !CanHandleDesktopCommand(command))
        {
            return false;
        }

        _interceptedKeyboardKeys.Add(keyboard.VirtualKeyCode);
        DesktopKeyboardCommandRequested?.Invoke(
            this,
            new DesktopKeyboardCommandEventArgs(command));
        if (command == DesktopKeyboardCommand.Delete)
        {
            DesktopDeleteRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (command == DesktopKeyboardCommand.Rename)
        {
            DesktopRenameRequested?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    private bool TryGetDesktopKeyboardCommand(uint virtualKey, out DesktopKeyboardCommand command)
    {
        command = virtualKey switch
        {
            (uint)VkDelete => DesktopKeyboardCommand.Delete,
            (uint)VkF2 => DesktopKeyboardCommand.Rename,
            (uint)VkF5 => DesktopKeyboardCommand.Refresh,
            (uint)VkReturn => DesktopKeyboardCommand.Open,
            (uint)VkA when GetAsyncKeyState(VkControl) < 0 => DesktopKeyboardCommand.SelectAll,
            (uint)VkC when GetAsyncKeyState(VkControl) < 0 => DesktopKeyboardCommand.Copy,
            (uint)VkX when GetAsyncKeyState(VkControl) < 0 => DesktopKeyboardCommand.Cut,
            (uint)VkV when GetAsyncKeyState(VkControl) < 0 => DesktopKeyboardCommand.Paste,
            _ => default
        };
        return virtualKey == (uint)VkDelete ||
            virtualKey == (uint)VkF2 ||
            virtualKey == (uint)VkF5 ||
            virtualKey == (uint)VkReturn ||
            ((virtualKey == (uint)VkA ||
              virtualKey == (uint)VkC ||
              virtualKey == (uint)VkX ||
              virtualKey == (uint)VkV) && GetAsyncKeyState(VkControl) < 0);
    }

    private bool CanHandleDesktopCommand(DesktopKeyboardCommand command)
    {
        if (CanHandleDesktopKeyboardCommand is not null)
        {
            return CanHandleDesktopKeyboardCommand(command);
        }

        return command switch
        {
            DesktopKeyboardCommand.Delete => CanDeleteDesktopItems?.Invoke() == true,
            DesktopKeyboardCommand.Rename => CanRenameDesktopItems?.Invoke() == true,
            _ => false
        };
    }

    private bool IsDesktopContextMenuActive()
    {
        var expiresAt = Volatile.Read(ref _desktopContextMenuExpiresAt);
        if (expiresAt <= Environment.TickCount64)
        {
            Volatile.Write(ref _desktopContextMenuExpiresAt, 0);
            return false;
        }
        return true;
    }

    private static bool IsNativeRefreshMenuItem(IntPtr targetWindow, NativePoint point)
    {
        var menuWindow = GetAncestor(targetWindow, 2);
        if (menuWindow == IntPtr.Zero)
        {
            menuWindow = targetWindow;
        }
        if (!IsNativeMenuWindow(menuWindow) ||
            NativeMethods.SendMessageTimeout(
                menuWindow,
                MnGetHMenu,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                MenuCommandTimeoutMilliseconds,
                out var menu) == IntPtr.Zero ||
            menu == IntPtr.Zero)
        {
            return false;
        }

        var itemPosition = MenuItemFromPoint(menuWindow, menu, point);
        if (itemPosition < 0)
        {
            return false;
        }

        if (GetMenuItemID(menu, itemPosition) == ExplorerRefreshCommandId)
        {
            return true;
        }

        var text = new StringBuilder(256);
        return GetMenuString(menu, (uint)itemPosition, text, text.Capacity, MfByPosition) > 0 &&
            (text.ToString().Contains("Refresh", StringComparison.OrdinalIgnoreCase) ||
             text.ToString().Contains("\u5237\u65b0", StringComparison.Ordinal));
    }

    private static bool IsNativeSortMenuItem(IntPtr targetWindow, NativePoint point)
    {
        var menuWindow = GetAncestor(targetWindow, 2);
        if (menuWindow == IntPtr.Zero)
        {
            menuWindow = targetWindow;
        }
        if (!IsNativeMenuWindow(menuWindow) ||
            NativeMethods.SendMessageTimeout(
                menuWindow,
                MnGetHMenu,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                MenuCommandTimeoutMilliseconds,
                out var menu) == IntPtr.Zero ||
            menu == IntPtr.Zero)
        {
            return false;
        }
        var itemPosition = MenuItemFromPoint(menuWindow, menu, point);
        if (itemPosition < 0)
        {
            return false;
        }

        var text = new StringBuilder(256);
        if (GetMenuString(menu, (uint)itemPosition, text, text.Capacity, MfByPosition) <= 0)
        {
            return false;
        }

        var label = text.ToString();
        return label.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Size", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Type", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Modified", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("名称", StringComparison.Ordinal) ||
            label.Contains("大小", StringComparison.Ordinal) ||
            label.Contains("类型", StringComparison.Ordinal) ||
            label.Contains("日期", StringComparison.Ordinal) ||
            label.Contains("修改", StringComparison.Ordinal);
    }

    private static bool IsNativeMenuWindow(IntPtr window)
    {
        var className = new StringBuilder(16);
        GetClassName(window, className, className.Capacity);
        return string.Equals(className.ToString(), "#32768", StringComparison.Ordinal);
    }

    private bool IsDesktopSurfacePoint(NativePoint screenPoint)
    {
        return IsDesktopSurfaceWindow(WindowFromPoint(screenPoint));
    }

    private bool IsDesktopForeground()
    {
        var window = GetForegroundWindow();
        return window != IntPtr.Zero &&
            !IsCurrentProcessWindow(window) &&
            IsDesktopSurfaceWindow(window);
    }

    private bool IsDesktopSurfaceWindow(IntPtr window)
    {
        if (window == DesktopListView ||
            IsChild(DesktopListView, window) ||
            IsChild(window, DesktopListView) ||
            IsDesktopBackgroundWindow(window))
        {
            return true;
        }

        var desktopView = GetParent(DesktopListView);
        return desktopView != IntPtr.Zero &&
            (window == desktopView || IsChild(desktopView, window));
    }

    private static bool IsDesktopBackgroundWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        if (processId == Environment.ProcessId)
        {
            return false;
        }
        var root = GetAncestor(window, 2);
        var className = new StringBuilder(64);
        GetClassName(root == IntPtr.Zero ? window : root, className, className.Capacity);
        return className.ToString() is "WorkerW" or "Progman";
    }

    private static bool IsCurrentProcessWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return processId == Environment.ProcessId;
    }

    private delegate IntPtr LowLevelHookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseHookStruct
    {
        internal NativePoint Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardHookStruct
    {
        internal uint VirtualKeyCode;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadMessage
    {
        internal IntPtr Window;
        internal uint Message;
        internal IntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal NativePoint Point;
    }


    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out ThreadMessage message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref ThreadMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref ThreadMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    private static extern int MenuItemFromPoint(IntPtr window, IntPtr menu, NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(
        IntPtr menu,
        uint item,
        StringBuilder text,
        int maxCount,
        uint flags);

}
