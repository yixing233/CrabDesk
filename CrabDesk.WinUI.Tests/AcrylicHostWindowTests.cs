using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CrabDesk.Native;
using CrabDesk.Runtime;
using CrabDesk.Core;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

[Collection("desktop-probe")]
public sealed class AcrylicHostWindowTests
{
    [Fact]
    public void ZeroSizeVisibleUtilityDoesNotObstructDesktopPlacement()
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            using var ordinary = TestWindow();
            using var utility = new ZeroSizeUtilityWindow();
            using var host = TestWindow();
            desktop.Show(); ordinary.Show(); host.Show();
            // Explorer and the external utility do not share WinForms' hidden
            // owner. Match those independent native window groups here.
            foreach (var window in new[] { desktop, ordinary, host }) SetWindowLongPtr(window.Handle, -8, IntPtr.Zero);
            Assert.True(SetWindowPos(utility.Handle, desktop.Handle, 0, 0, 0, 0, SwpNoActivate | SwpNoMove));
            Assert.True(IsWindowVisible(utility.Handle));
            Assert.Equal(Size.Empty, GetBounds(utility.Handle).Size);
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            // Put the empty helper immediately between the host and desktop.
            Assert.True(SetWindowPos(utility.Handle, host.Handle, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize));
            Assert.True(IsAbove(host.Handle, utility.Handle));
            Assert.True(IsAbove(utility.Handle, desktop.Handle));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.True(IsAbove(ordinary.Handle, host.Handle));
            Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
        });
    }

    // WinForms clamps even a borderless Form to at least 2 × 2; use a native
    // popup to reproduce the system utility's actual zero-size window.
    private sealed class ZeroSizeUtilityWindow : NativeWindow, IDisposable
    {
        public ZeroSizeUtilityWindow() => CreateHandle(new CreateParams
        {
            Caption = "CrabDesk zero-size utility test", X = -12000, Y = -12000,
            Width = 0, Height = 0, Style = unchecked((int)0x90000000)
        });
        public void Dispose() => DestroyHandle();
    }

    [Fact]
    public void ExplicitShowDesktopGroupsTheHostForTheTransitionAndRestoresItsIndependentOwner()
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            using var ordinary = TestWindow();
            desktop.Show(); ordinary.Show();
            using var host = new DesktopAcrylicHost(desktop.Handle, [TestMonitor()]);
            DesktopAcrylicWindowTools.Initialize(host.Handle, desktop.Handle);
            host.ShowAtDesktop(); Application.DoEvents();
            var previousOwner = GetWindow(host.Handle, 4);
            DesktopAcrylicWindowTools.PrepareForShowDesktop();
            Assert.Equal(desktop.Handle, GetWindow(host.Handle, 4));
            // No message pumping or WinEvent callback between Explorer's move
            // and this check: both HWNDs must have moved in the same operation.
            Assert.True(SetWindowPos(desktop.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            DesktopAcrylicWindowTools.FinishShowDesktopTransition(host.Handle, force: true);
            Assert.Equal(previousOwner, GetWindow(host.Handle, 4));
            Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
            // A disabled/consumed shell command must also release the group.
            DesktopAcrylicWindowTools.PrepareForShowDesktop();
            Thread.Sleep(1600);
            host.EnsureDesktopPlacement();
            Assert.Equal(previousOwner, GetWindow(host.Handle, 4));
            // Once the explicit switch is over, external overlay/menu raises
            // must not drag Explorer over an ordinary application again.
            Assert.True(SetWindowPos(ordinary.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize));
            host.EnsureDesktopPlacement();
            foreach (var extraFlags in new uint[] { 0, 0x0400 })
            {
                Assert.True(SetWindowPos(host.Handle, new IntPtr(-1), 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | extraFlags));
                Assert.True(IsAbove(ordinary.Handle, host.Handle));
                Assert.True(IsAbove(ordinary.Handle, desktop.Handle));
            }
            DesktopAcrylicWindowTools.PrepareForShowDesktop();
            host.HideFromDesktop();
            Assert.Equal(previousOwner, GetWindow(host.Handle, 4));
            Assert.False(IsWindowVisible(host.Handle));
        });
    }

    [Fact]
    public void DesktopOrderChangesRestoreTheHostWithoutWaitingForTheFallbackTimer()
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            using var taskbar = TestWindow();
            taskbar.TopMost = true;
            desktop.Show(); taskbar.Show();
            using var host = new DesktopAcrylicHost(desktop.Handle, [TestMonitor()]);
            DesktopAcrylicWindowTools.Initialize(host.Handle, desktop.Handle);
            host.ShowAtDesktop();
            Application.DoEvents(); // Drain initial-show recovery before testing desktop notifications.
            var timer = (System.Windows.Forms.Timer)typeof(DesktopAcrylicHost)
                .GetField("_orderTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(host)!;
            timer.Stop();
            RaiseDesktopBelowTopmostBoundary(desktop.Handle, taskbar.Handle);
            Assert.False(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));

            NotifyWinEvent(0x8004, desktop.Handle, 0, 0); // EVENT_OBJECT_REORDER / OBJID_WINDOW
            PumpMessagesUntil(() => DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle),
                "Desktop reordering must be handled by its notification, without the 500 ms fallback timer.");

            host.HideFromDesktop();
            NotifyWinEvent(0x8004, desktop.Handle, 0, 0);
            Application.DoEvents();
            Assert.False(IsWindowVisible(host.Handle), "Desktop notifications must respect application-requested hiding.");
        });
    }

    [Fact]
    public void HiddenTopmostWindowsRemainAboveTheHostDuringDesktopPlacement()
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            using var taskbar = TestWindow();
            using var host = TestWindow();
            taskbar.TopMost = true;
            desktop.Show(); taskbar.Show(); host.Show();
            RaiseDesktopBelowTopmostBoundary(desktop.Handle, taskbar.Handle);
            ShowWindow(taskbar.Handle, SwHide);
            using var observed = new OrderObserver(host.Handle, IntPtr.Zero);
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.Equal(0, observed.TopmostFrames);
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
        });
    }

    [Fact]
    public void DesktopRaisedBelowTaskbarUsesTheTopmostBoundaryWithoutBecomingTopmost()
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            using var ordinary = TestWindow();
            using var taskbar = TestWindow();
            taskbar.TopMost = true;
            desktop.Show(); ordinary.Show(); taskbar.Show();
            using var host = new DesktopAcrylicHost(desktop.Handle, [TestMonitor()]);
            DesktopAcrylicWindowTools.Initialize(host.Handle, desktop.Handle);
            host.ShowAtDesktop();

            // Show Desktop raises Explorer immediately below the topmost band.
            RaiseDesktopBelowTopmostBoundary(desktop.Handle, taskbar.Handle);
            Assert.False(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            var active = GetActiveWindow();
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowPosition>());
            try
            {
                Marshal.StructureToPtr(new WindowPosition { Window = host.Handle, InsertAfter = new IntPtr(-1),
                    Flags = SwpNoActivate | SwpNoMove | SwpNoSize }, pointer, false);
                DesktopAcrylicWindowTools.ConstrainPositionChange(host.Handle, desktop.Handle, pointer);
                Assert.Equal(taskbar.Handle, Marshal.PtrToStructure<WindowPosition>(pointer).InsertAfter);
            }
            finally { Marshal.FreeHGlobal(pointer); }

            host.EnsureDesktopPlacement();
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            Assert.True(IsAbove(taskbar.Handle, host.Handle));
            Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
            Assert.Equal(active, GetActiveWindow());
        });
    }

    [Fact]
    public void TopmostHostIsDemotedBeforePlacementBelowATopmostDesktopBoundary()
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            using var taskbar = TestWindow();
            using var host = TestWindow();
            taskbar.TopMost = true;
            host.TopMost = true;
            desktop.Show(); taskbar.Show(); host.Show();
            RaiseDesktopBelowTopmostBoundary(desktop.Handle, taskbar.Handle);
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
        });
    }

    [Fact]
    public void DesktopHostNeverRisesAboveAnOrdinaryWindowWhenAnotherWindowRequestsARaise()
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            using var ordinary = TestWindow();
            desktop.Show(); ordinary.Show();
            using var host = new DesktopAcrylicHost(desktop.Handle, [TestMonitor()]);
            DesktopAcrylicWindowTools.Initialize(host.Handle, desktop.Handle);
            Assert.NotEqual(desktop.Handle, GetWindow(host.Handle, 4));
            host.ShowAtDesktop();
            Assert.True(IsAbove(ordinary.Handle, host.Handle));
            var active = GetActiveWindow();
            using var observed = new OrderObserver(host.Handle, ordinary.Handle);
            foreach (var extraFlags in new uint[] { 0, 0x0400 }) // Include SWP_NOSENDCHANGING.
            foreach (var after in new[] { IntPtr.Zero, new IntPtr(-1), ordinary.Handle })
            {
                Assert.True(SetWindowPos(host.Handle, after, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | extraFlags));
                Assert.True(IsAbove(ordinary.Handle, host.Handle), $"after=0x{after.ToInt64():X} extraFlags=0x{extraFlags:X} ordinaryAboveDesktop={IsAbove(ordinary.Handle, desktop.Handle)} " +
                    DesktopAcrylicWindowTools.GetPlacementDiagnostics(host.Handle, desktop.Handle) + " events=" + string.Join(" | ", observed.Events));
                Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
                Assert.Equal(active, GetActiveWindow());
            }
            Assert.Equal(0, observed.RaisedFrames);
        });
    }

    [Fact]
    public void OpeningNestedWidgetMenuDoesNotRaiseItsDesktopOwner()
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            using var ordinary = TestWindow();
            desktop.Show(); ordinary.Show();
            using var host = new DesktopAcrylicHost(desktop.Handle, [TestMonitor()]);
            DesktopAcrylicWindowTools.Initialize(host.Handle, desktop.Handle);
            using var source = new Panel { Bounds = new Rectangle(0, 0, 100, 100) };
            host.Controls.Add(source);
            host.ShowAtDesktop();
            using var menu = new FluentContextMenuStrip { AnimationsEnabled = false };
            using var sizes = new FluentToolStripMenuItem("尺寸");
            sizes.DropDownItems.Add(new FluentToolStripMenuItem("紧凑"));
            menu.Items.Add(sizes);
            using var observed = new OrderObserver(host.Handle, ordinary.Handle);
            menu.Show(source, new Point(10, 10));
            sizes.ShowDropDown();
            Application.DoEvents();
            Assert.True(sizes.DropDown.Visible);
            Assert.True(IsAbove(ordinary.Handle, host.Handle));
            Assert.Equal(0, observed.RaisedFrames);
            Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
            sizes.HideDropDown(); menu.Close();
        });
    }

    [Fact]
    public void ClosingCaptureStyleOverlayDoesNotExposeDesktopAboveTheForegroundApp()
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            using var ordinary = TestWindow();
            desktop.Show(); ordinary.Show();
            using var host = new DesktopAcrylicHost(desktop.Handle, [TestMonitor()]);
            DesktopAcrylicWindowTools.Initialize(host.Handle, desktop.Handle);
            host.ShowAtDesktop();
            using var observed = new OrderObserver(host.Handle, ordinary.Handle);
            using var capture = TestWindow(); capture.TopMost = true;
            capture.Show(ordinary); Application.DoEvents();
            capture.Close(); Application.DoEvents();
            Assert.True(IsAbove(ordinary.Handle, host.Handle));
            Assert.Equal(0, observed.RaisedFrames);
            Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
        });
    }

    private static Form TestWindow() => new() { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
        Bounds = new Rectangle(-12000, -12000, 200, 120) };

    private static void RaiseDesktopBelowTopmostBoundary(IntPtr desktop, IntPtr boundary)
    {
        // Explorer and the taskbar do not share WinForms' hidden owner.
        SetWindowLongPtr(desktop, -8, IntPtr.Zero);
        SetWindowLongPtr(boundary, -8, IntPtr.Zero);
        // Keep the fake desktop non-topmost. Inserting it immediately after a
        // topmost form while more topmost windows follow would promote it.
        Assert.True(SetWindowPos(desktop, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoActivate | SwpNoMove | SwpNoSize));
        var previous = GetWindow(desktop, GwHwndPrev);
        while (previous != IntPtr.Zero && (GetWindowLongPtr(previous, GwlExStyle) & WsExTopmost) == IntPtr.Zero)
            previous = GetWindow(previous, GwHwndPrev);
        Assert.NotEqual(IntPtr.Zero, previous);
        if (previous != boundary)
            Assert.True(SetWindowPos(boundary, previous, 0, 0, 0, 0,
                SwpNoActivate | SwpNoMove | SwpNoSize));
        Assert.True(SetWindowPos(desktop, boundary, 0, 0, 0, 0,
            SwpNoActivate | SwpNoMove | SwpNoSize));
        Assert.Equal(boundary, GetWindow(desktop, GwHwndPrev));
        Assert.Equal(IntPtr.Zero, GetWindowLongPtr(desktop, GwlExStyle) & WsExTopmost);
        Assert.Equal(WsExTopmost, GetWindowLongPtr(boundary, GwlExStyle) & WsExTopmost);
    }

    private static MonitorLayout TestMonitor() => new() { Id = "ordering", DeviceName = "ordering", DpiScale = 1,
        Bounds = new(0, 0, 800, 600), WorkArea = new(0, 0, 800, 600), PixelBounds = new(-12000, -12000, 800, 600), PixelWorkArea = new(-12000, -12000, 800, 600) };

    private sealed class OrderObserver : NativeWindow, IDisposable
    {
        private readonly IntPtr _ordinary;
        internal int RaisedFrames { get; private set; }
        internal int TopmostFrames { get; private set; }
        internal List<string> Events { get; } = [];
        internal OrderObserver(IntPtr host, IntPtr ordinary) { _ordinary = ordinary; AssignHandle(host); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0046 || message.Msg == 0x0047)
            {
                var position = Marshal.PtrToStructure<WindowPosition>(message.LParam);
                Events.Add($"before msg=0x{message.Msg:X} after=0x{position.InsertAfter.ToInt64():X} flags=0x{position.Flags:X} above={IsAbove(_ordinary, Handle)}");
            }
            base.WndProc(ref message);
            if (message.Msg == 0x0047)
            {
                if ((GetWindowLongPtr(Handle, GwlExStyle) & WsExTopmost) != IntPtr.Zero) TopmostFrames++;
                Events.Add($"after changed above={IsAbove(_ordinary, Handle)} topmost={(GetWindowLongPtr(Handle, GwlExStyle) & WsExTopmost) != IntPtr.Zero}");
                if (IsWindowVisible(Handle) && !IsAbove(_ordinary, Handle)) RaisedFrames++;
            }
        }
        public void Dispose() => ReleaseHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }

    [Fact]
    public void HiddenHostIsRestoredAtDesktop()
    {
        RunInSta(() =>
        {
            using var desktop = new Form();
            using var host = new Form();
            using var child = new Panel { Dock = DockStyle.Fill };
            host.Controls.Add(child);
            desktop.Show();
            host.Show();
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));

            ShowWindow(host.Handle, SwHide);
            Assert.False(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));

            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.True(IsWindowVisible(host.Handle));
            Assert.True(IsWindowVisible(child.Handle));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
        });
    }

    [Fact]
    public void MinimizedHostRestoresBoundsAndChildrenWithoutActivationOrRaising()
    {
        RunInSta(() =>
        {
            using var desktop = new Form();
            using var host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(120, 140, 360, 240)
            };
            using var child = new Panel { Dock = DockStyle.Fill };
            using var spacer = new Form();
            using var ordinary = new Form();
            host.Controls.Add(child);
            desktop.Show();
            host.Show();
            spacer.Show();
            ordinary.Show();
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(spacer.Handle, desktop.Handle));
            Assert.False(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            var bounds = GetBounds(host.Handle);

            ShowWindow(host.Handle, SwMinimize);
            Assert.True(IsIconic(host.Handle));
            var active = GetActiveWindow();
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));

            Assert.False(IsIconic(host.Handle));
            Assert.Equal(bounds, GetBounds(host.Handle));
            Assert.True(IsWindowVisible(child.Handle));
            Assert.Equal(active, GetActiveWindow());
            Assert.True(IsAbove(ordinary.Handle, host.Handle));
            Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
        });
    }

    [Fact]
    public void AlreadyOrderedMinimizedHostIsRestored()
    {
        RunInSta(() =>
        {
            using var desktop = new Form();
            using var host = new Form();
            desktop.Show();
            host.Show();
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.Equal(host.Handle, GetWindow(desktop.Handle, GwHwndPrev));

            ShowWindow(host.Handle, SwMinimize);
            Assert.True(IsIconic(host.Handle));
            var previous = GetWindow(desktop.Handle, GwHwndPrev);
            Assert.True(SetWindowPos(host.Handle, previous, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize));
            Assert.Equal(host.Handle, GetWindow(desktop.Handle, GwHwndPrev));

            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.False(IsIconic(host.Handle));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
        });
    }

    [Fact]
    public void PlacementIsIdempotentAndInvalidHandlesAreRejected()
    {
        RunInSta(() =>
        {
            using var desktop = new Form();
            using var host = new Form();
            using var child = new Panel();
            host.Controls.Add(child);
            desktop.Show();
            host.Show();
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            Assert.False(DesktopAcrylicWindowTools.PlaceAtDesktop(IntPtr.Zero, desktop.Handle));
            Assert.False(DesktopAcrylicWindowTools.PlaceAtDesktop(new IntPtr(-1), desktop.Handle));
            Assert.False(DesktopAcrylicWindowTools.PlaceAtDesktop(child.Handle, desktop.Handle));
            Assert.False(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, IntPtr.Zero));
            Assert.False(DesktopAcrylicWindowTools.IsReadyAtDesktop(IntPtr.Zero, desktop.Handle));
        });
    }

    private static Rectangle GetBounds(IntPtr hwnd)
    {
        Assert.True(GetWindowRect(hwnd, out var rect));
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (var current = upper; current != IntPtr.Zero; current = GetWindow(current, GwHwndNext))
            if (current == lower) return true;
        return false;
    }

    private static void PumpMessagesUntil(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 200)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    [Theory]
    [InlineData("便签", false)]
    [InlineData("翻译", false)]
    [InlineData("待办", true)]
    public void DismissingWidgetInputDoesNotRaiseTheDesktopOverItsHost(string name, bool singleLine)
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            desktop.Show();
            using var host = new DesktopAcrylicHost(desktop.Handle, [TestMonitor()]);
            DesktopAcrylicWindowTools.Initialize(host.Handle, desktop.Handle);
            host.ShowAtDesktop();
            using var input = new DesktopWidgetTextInput(name);
            for (var cycle = 0; cycle < 3; cycle++)
            {
                var dismissals = 0;
                input.Present(new(-12000, -12000, 220, 100), "测试内容", Color.Black, Color.White, Color.Blue,
                    _ => { }, dismissed: _ => dismissals++, cornerRadius: 8, singleLine: singleLine);
                Assert.True(input.Visible);
                Assert.False(DesktopInputProxy.IsHiddenEditor(input.Handle));
                // Off-screen STA test windows do not always obtain global
                // foreground rights. Exercise activation when this field owns it.
                if (GetForegroundWindow() == input.Handle)
                    Assert.True(DesktopWindowTools.TryActivateDesktopInput(desktop.Handle));
                Assert.True(input.Visible);
                Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
                input.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
                Assert.False(input.Visible);
                Assert.False(IsWindowVisible(input.Handle));
                Assert.False(input.InputBox.IsKeyboardFocusWithin);
                Assert.Equal(1, dismissals);
                // The mouse-down handlers request desktop keyboard input after
                // dismissing the field. This second step caused the residual flash.
                Assert.True(DesktopInputProxy.IsHiddenEditor(input.Handle));
                if (GetForegroundWindow() == input.Handle)
                    Assert.True(DesktopWindowTools.TryActivateDesktopInput(desktop.Handle));
                Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
                Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
                input.Dismiss();
                Assert.Equal(1, dismissals);
            }
            using var monitor = new DesktopInputMonitor();
            Assert.True(monitor.IsDesktopForegroundWindow(input.Handle));
            using var unrelated = TestWindow();
            _ = unrelated.Handle;
            Assert.False(DesktopInputProxy.IsHiddenEditor(unrelated.Handle));
            Assert.False(monitor.IsDesktopForegroundWindow(unrelated.Handle));
            var editorHandle = input.Handle;
            input.Dispose();
            Assert.False(DesktopInputProxy.IsHiddenEditor(editorHandle));
        });
    }

    [Fact]
    public void DismissingWidgetInputPreservesTheWindowTheUserActivated()
    {
        RunInSta(() =>
        {
            using var ordinary = TestWindow();
            using var input = new DesktopWidgetTextInput("退出检测");
            ordinary.Show();
            input.Present(new(-12000, -12000, 220, 100), "测试内容", Color.Black, Color.White, Color.Blue, _ => { }, cornerRadius: 8);
            ordinary.Activate();
            input.Dismiss();
            Assert.Equal(ordinary.Handle, GetActiveWindow());
            Assert.False(input.Visible);
        });
    }

    [Theory]
    [InlineData("wifi", false)]
    [InlineData("wifi", true)]
    [InlineData("bluetooth", false)]
    [InlineData("bluetooth", true)]
    [InlineData("audio", false)]
    [InlineData("audio", true)]
    [InlineData("shortcuts", false)]
    [InlineData("shortcuts", true)]
    public void ClosingWidgetPopupKeepsTheHostAboveTheDesktopDuringFocusReturn(string kind, bool directClose)
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            desktop.Show();
            SetWindowLongPtr(desktop.Handle, -8, IntPtr.Zero);
            using var host = new DesktopAcrylicHost(desktop.Handle, [TestMonitor()]);
            DesktopAcrylicWindowTools.Initialize(host.Handle, desktop.Handle);
            host.ShowAtDesktop();
            var previousOwner = GetWindow(host.Handle, 4);
            using var popup = CreateClosingPopup(kind);
            popup.Location = new Point(-12000, -12000);
            popup.Show();
            Application.DoEvents();
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            if (directClose) popup.Close();
            else if (popup is ControlCenterPopup controls) controls.Dismiss();
            else ((AudioMixerPopup)popup).Dismiss();
            Assert.True(popup.IsDisposed);
            // Return focus/reorder as Explorer does after a popup is destroyed.
            // Check before any queued WinEvent repair can cover the missing frame.
            Assert.True(SetWindowPos(desktop.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize));
            Assert.True(DesktopAcrylicWindowTools.IsReadyAtDesktop(host.Handle, desktop.Handle));
            DesktopAcrylicWindowTools.FinishShowDesktopTransition(host.Handle, force: true);
            Assert.Equal(previousOwner, GetWindow(host.Handle, 4));
            Assert.Equal(IntPtr.Zero, GetWindowLongPtr(host.Handle, GwlExStyle) & WsExTopmost);
        });
    }

    [Theory]
    [InlineData("wifi")]
    [InlineData("bluetooth")]
    [InlineData("audio")]
    [InlineData("shortcuts")]
    public void PopupDeactivationLeavesTheNewApplicationAndDesktopOwnershipAlone(string kind)
    {
        RunInSta(() =>
        {
            using var desktop = TestWindow();
            using var ordinary = TestWindow();
            desktop.Show(); ordinary.Show();
            using var host = new DesktopAcrylicHost(desktop.Handle, [TestMonitor()]);
            DesktopAcrylicWindowTools.Initialize(host.Handle, desktop.Handle);
            host.ShowAtDesktop();
            var previousOwner = GetWindow(host.Handle, 4);
            using var popup = CreateClosingPopup(kind);
            popup.Location = new Point(-12000, -12000);
            popup.Show(); Application.DoEvents();
            ordinary.Activate(); Application.DoEvents();
            Assert.True(popup.IsDisposed);
            // These off-screen STA windows may lose foreground rights while
            // destroying the active HWND. The desktop host must never take them.
            Assert.NotEqual(host.Handle, GetActiveWindow());
            Assert.NotEqual(host.Handle, GetForegroundWindow());
            Assert.Equal(previousOwner, GetWindow(host.Handle, 4));
            Assert.True(IsAbove(ordinary.Handle, host.Handle));
        });
    }

    private static Form CreateClosingPopup(string kind)
    {
        if (kind == "shortcuts")
            return new QuickActionsEditorPopup([ControlCenterQuickAction.WiFi], _ => { }, false, Color.RoyalBlue, 1);
        if (kind == "audio")
        {
            var audio = new Mock<IAudioMixer>();
            audio.Setup(source => source.Read()).Returns(AudioMixerSnapshot.Empty);
            return new AudioMixerPopup(new AudioMixerController(audio.Object), false, Color.RoyalBlue, 1);
        }
        var connection = new Mock<IControlConnectionService>();
        connection.SetupGet(source => source.Kind).Returns(kind == "wifi" ? ControlConnectionKind.WiFi : ControlConnectionKind.Bluetooth);
        connection.Setup(source => source.ReadAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ControlConnectionSnapshot(true, []));
        return new ConnectionDetailsPopup(new ConnectionDetailsController(connection.Object), false, Color.RoyalBlue, 1, _ => { }, false);
    }

    private static void RunInSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA test thread timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private const int SwHide = 0, SwMinimize = 6, GwlExStyle = -20;
    private const uint GwHwndNext = 2, GwHwndPrev = 3;
    private const uint SwpNoSize = 1, SwpNoMove = 2, SwpNoActivate = 0x10;
    private static readonly IntPtr WsExTopmost = new(8);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern void NotifyWinEvent(uint eventType, IntPtr hwnd, int objectId, int childId);
}
