using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed class FluentToolStripMenuItem : ToolStripMenuItem
{
    internal FluentToolStripMenuItem(string text)
        : base(text)
    {
    }

    internal FluentToolStripMenuItem(string text, Image? image, EventHandler? onClick)
        : base(text, image, onClick)
    {
    }

    protected override ToolStripDropDown CreateDefaultDropDown() =>
        new FluentToolStripDropDownMenu();
}

internal sealed class FluentToolStripDropDownMenu : ToolStripDropDownMenu
{
    private const int GwlpHwndParent = -8;

    internal int ExtendedWindowStyleForTesting => CreateParams.ExStyle;

    protected override Padding DefaultPadding =>
        FluentContextMenuStrip.CalculateOuterPadding(DeviceDpi);

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle = FluentContextMenuStrip.NormalizeExtendedWindowStyle(parameters.ExStyle);
            return parameters;
        }
    }

    protected override void OnOpening(CancelEventArgs eventArgs)
    {
        base.OnOpening(eventArgs);
        AttachToParentMenu();
    }

    protected override void OnOpened(EventArgs eventArgs)
    {
        base.OnOpened(eventArgs);
        AttachToParentMenu();
    }

    private void AttachToParentMenu()
    {
        if (!IsHandleCreated || OwnerItem?.Owner is not { IsHandleCreated: true } parentMenu)
        {
            return;
        }

        var parentHandle = parentMenu.Handle;
        if (parentHandle != IntPtr.Zero && parentHandle != Handle)
        {
            SetWindowLongPtr(Handle, GwlpHwndParent, parentHandle);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}

internal sealed class FluentContextMenuStrip : ContextMenuStrip
{
    private const int GwlpHwndParent = -8;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExNoActivate = 0x08000000;
    private const uint GaRoot = 2;
    private const int WhMouseLowLevel = 14;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private bool _stretchingItems;
    private IntPtr _mouseHook;
    private MouseHookProcedure? _mouseHookProcedure;
    private readonly System.Windows.Forms.Timer _opacityTimer;
    private long _animationStarted;
    private double _animationFrom;
    private double _animationTo;
    private int _animationDuration;
    private bool _closingAnimation;
    private bool _allowImmediateClose;
    private ToolStripDropDownCloseReason _pendingCloseReason;

    internal int MinimumMenuWidth { get; init; } = 112;
    internal bool AnimationsEnabled { get; set; } = true;

    protected override Padding DefaultPadding => CalculateOuterPadding(DeviceDpi);

    internal FluentContextMenuStrip()
    {
        _opacityTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _opacityTimer.Tick += OnOpacityTimerTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle = NormalizeExtendedWindowStyle(parameters.ExStyle);
            return parameters;
        }
    }

    internal static int NormalizeExtendedWindowStyle(int extendedStyle) =>
        (extendedStyle | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow;

    internal static Padding CalculateOuterPadding(int deviceDpi)
    {
        var dpiScale = Math.Max(0.75f, deviceDpi / 96f);
        var horizontal = (int)Math.Round(4 * dpiScale);
        var vertical = (int)Math.Round(6 * dpiScale);
        return new Padding(horizontal, vertical, horizontal, vertical);
    }

    protected override void OnOpening(CancelEventArgs eventArgs)
    {
        StopOpacityAnimation();
        Opacity = AnimationsEnabled ? 0 : 1;
        base.OnOpening(eventArgs);
        AttachToTopLevelOwner();
        if (eventArgs.Cancel)
        {
            Opacity = 1;
        }
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        StretchItemsToClientWidth();
    }

    protected override void OnOpened(EventArgs eventArgs)
    {
        base.OnOpened(eventArgs);
        AttachToTopLevelOwner();
        StretchItemsToClientWidth();
        StartOutsideClickMonitor();
        Invalidate(true);
        if (AnimationsEnabled)
        {
            StartOpacityAnimation(1, 90, false);
        }
        else
        {
            Opacity = 1;
        }
    }

    protected override void OnClosing(ToolStripDropDownClosingEventArgs eventArgs)
    {
        if (!_allowImmediateClose && AnimationsEnabled && Visible && !IsDisposed)
        {
            eventArgs.Cancel = true;
            _pendingCloseReason = eventArgs.CloseReason;
            if (!_closingAnimation)
            {
                StartOpacityAnimation(0, 70, true);
            }
            return;
        }
        base.OnClosing(eventArgs);
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs eventArgs)
    {
        StopOpacityAnimation();
        _closingAnimation = false;
        Opacity = 1;
        StopOutsideClickMonitor();
        base.OnClosed(eventArgs);
    }

    protected override void Dispose(bool disposing)
    {
        StopOpacityAnimation();
        StopOutsideClickMonitor();
        if (disposing)
        {
            AnimationsEnabled = false;
            _allowImmediateClose = true;
            _opacityTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void StartOpacityAnimation(double target, int duration, bool closing)
    {
        _opacityTimer.Stop();
        _animationFrom = Opacity;
        _animationTo = target;
        _animationDuration = Math.Max(1, duration);
        _animationStarted = Stopwatch.GetTimestamp();
        _closingAnimation = closing;
        _opacityTimer.Start();
    }

    private void OnOpacityTimerTick(object? sender, EventArgs eventArgs)
    {
        var elapsed = Stopwatch.GetElapsedTime(_animationStarted).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / _animationDuration, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 2);
        Opacity = _animationFrom + (_animationTo - _animationFrom) * eased;
        if (progress < 1)
        {
            return;
        }

        _opacityTimer.Stop();
        Opacity = _animationTo;
        if (!_closingAnimation)
        {
            return;
        }
        _closingAnimation = false;
        _allowImmediateClose = true;
        try
        {
            Close(_pendingCloseReason);
        }
        finally
        {
            _allowImmediateClose = false;
        }
    }

    private void StopOpacityAnimation()
    {
        _opacityTimer.Stop();
        _closingAnimation = false;
    }

    private void AttachToTopLevelOwner()
    {
        if (SourceControl is not { IsHandleCreated: true } sourceControl ||
            !IsHandleCreated)
        {
            return;
        }

        // DesktopBoxForm is an Explorer child window and cannot directly own
        // a top-level popup. Use its root desktop host as the native owner so
        // the menu remains grouped with the desktop instead of the taskbar.
        var topLevelOwner = GetAncestor(sourceControl.Handle, GaRoot);
        if (topLevelOwner != IntPtr.Zero && topLevelOwner != Handle)
        {
            SetWindowLongPtr(Handle, GwlpHwndParent, topLevelOwner);
        }
    }

    private void StretchItemsToClientWidth()
    {
        if (_stretchingItems || ClientSize.Width <= 0)
        {
            return;
        }
        _stretchingItems = true;
        try
        {
            foreach (ToolStripItem item in Items)
            {
                var width = Math.Max(
                    1,
                    ClientSize.Width - Padding.Right - item.Bounds.Left - item.Margin.Right);
                item.AutoSize = false;
                item.Width = width;
            }
        }
        finally
        {
            _stretchingItems = false;
        }
    }

    private void StartOutsideClickMonitor()
    {
        StopOutsideClickMonitor();
        _mouseHookProcedure = OnGlobalMouseMessage;
        _mouseHook = SetWindowsHookEx(
            WhMouseLowLevel,
            _mouseHookProcedure,
            GetModuleHandle(null),
            0);
    }

    private void StopOutsideClickMonitor()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _mouseHookProcedure = null;
    }

    private IntPtr OnGlobalMouseMessage(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && IsMouseButtonDown(message.ToInt32()) && Visible)
        {
            var mouse = Marshal.PtrToStructure<LowLevelMouseInput>(data);
            if (!ContainsScreenPoint(this, mouse.Point))
            {
                Close(ToolStripDropDownCloseReason.AppClicked);
            }
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private static bool ContainsScreenPoint(ToolStripDropDown menu, Point point)
    {
        var bounds = new Rectangle(menu.PointToScreen(Point.Empty), menu.Size);
        if (menu.Visible && bounds.Contains(point))
        {
            return true;
        }
        foreach (ToolStripMenuItem item in menu.Items.OfType<ToolStripMenuItem>())
        {
            if (item.DropDown.Visible && ContainsScreenPoint(item.DropDown, point))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsMouseButtonDown(int message) =>
        message is WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown or WmXButtonDown;

    private delegate IntPtr MouseHookProcedure(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        internal readonly int X;
        internal readonly int Y;

        public static implicit operator Point(NativePoint point) => new(point.X, point.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelMouseInput
    {
        internal readonly NativePoint Point;
        internal readonly uint MouseData;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        MouseHookProcedure procedure,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
