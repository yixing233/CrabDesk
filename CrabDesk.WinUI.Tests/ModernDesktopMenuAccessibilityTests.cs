using System.Drawing;
using System.Runtime.ExceptionServices;
using CrabDesk.Native;
using Xunit;
using Forms = System.Windows.Forms;

namespace CrabDesk.WinUI.Tests;

public sealed class ModernDesktopMenuAccessibilityTests
{
    [Fact]
    public void AccessibilityReaderCachesTheEnabledRefreshMenuItemAndItsScreenBounds()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MenuHost
                {
                    ShowInTaskbar = false, StartPosition = Forms.FormStartPosition.Manual,
                    Location = new Point(20, 20), ClientSize = new Size(320, 80)
                };
                using var menu = new Forms.MenuStrip();
                menu.Items.Add(new Forms.ToolStripMenuItem("刷新(&R)"));
                menu.Items.Add(new Forms.ToolStripMenuItem("Refresh folder"));
                menu.Items.Add(new Forms.ToolStripMenuItem("Refresh") { Enabled = false });
                form.Controls.Add(menu);
                form.Show();
                var window = form.Handle;
                var read = Task.Run(() => ModernDesktopMenuTracker.ReadRefreshItems([window]));
                var deadline = Environment.TickCount64 + 5000;
                while (!read.IsCompleted && Environment.TickCount64 < deadline)
                {
                    Forms.Application.DoEvents();
                    Thread.Sleep(1);
                }
                Assert.True(read.IsCompleted, "Accessibility reads must complete on the background apartment.");
                var item = Assert.Single(read.GetAwaiter().GetResult());
                Assert.Equal(window, item.Window);
                Assert.True(item.Bounds.Width > 0 && item.Bounds.Height > 0);
                var expected = menu.RectangleToScreen(menu.Items[0].Bounds);
                Assert.True(item.Bounds.IntersectsWith(expected));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class MenuHost : Forms.Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
