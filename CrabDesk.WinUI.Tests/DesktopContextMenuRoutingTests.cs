using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CrabDesk.Native;
using Forms = System.Windows.Forms;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopContextMenuRoutingTests
{
    [Theory]
    [InlineData(728, 270)]
    [InlineData(-1200, -300)]
    public void BlankAreaMenuTargetsTheViewBackgroundInsteadOfTheIconList(int x, int y)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // Other WinForms tests can initialize process DPI awareness while
            // this thread pumps messages. Keep both windows and the sender in
            // the same explicit context so WM_CONTEXTMENU is not rescaled.
            var previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                using var view = new MenuWindow(IntPtr.Zero);
                using var icons = new MenuWindow(view.Handle);
                Assert.True(IsWindowVisible(icons.Handle));

                Assert.True(DesktopWindowTools.ShowDesktopContextMenu(icons.Handle, x, y));
                Assert.False(IsWindowVisible(icons.Handle));
                Assert.True(IsWindowVisible(view.Handle));
                var deadline = Environment.TickCount64 + 1000;
                while (view.ContextMessages.Count == 0 && icons.ContextMessages.Count == 0 &&
                       Environment.TickCount64 < deadline)
                {
                    Forms.Application.DoEvents();
                    Thread.Sleep(1);
                }

                Assert.Empty(icons.ContextMessages);
                var message = Assert.Single(view.ContextMessages);
                Assert.Equal(view.Handle, message.WParam);
                Assert.Equal(x, unchecked((short)message.LParam.ToInt64()));
                Assert.Equal(y, unchecked((short)(message.LParam.ToInt64() >> 16)));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (previousDpiContext != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(previousDpiContext);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void MissingDesktopViewDoesNotFallBackToAnItemMenu()
    {
        Assert.False(DesktopWindowTools.ShowDesktopContextMenu(IntPtr.Zero, 100, 100));
    }

    private sealed class MenuWindow : Forms.NativeWindow, IDisposable
    {
        public List<Forms.Message> ContextMessages { get; } = [];

        public MenuWindow(IntPtr parent)
        {
            CreateHandle(new Forms.CreateParams
            {
                Parent = parent,
                X = -32000,
                Y = -32000,
                Width = 1,
                Height = 1,
                ExStyle = 0x08000080,
                Style = parent == IntPtr.Zero ? 0x10000000 : 0x50000000
            });
        }

        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == 0x007B)
            {
                ContextMessages.Add(message);
                return;
            }
            base.WndProc(ref message);
        }

        public void Dispose() => DestroyHandle();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
