using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

[Collection("desktop-probe")]
public sealed class ControlCenterDeviceMenuTests
{
    [Theory]
    [InlineData(false, 1d)]
    [InlineData(true, 1d)]
    [InlineData(false, 1.5d)]
    [InlineData(true, 1.5d)]
    public void SameRowTogglesItsMenuAndUsesScreenPixelBounds(bool display, double scale) => OnSta(() =>
    {
        var (runtime, widget) = Fixture(scale);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        form.Location = new(80, 60);
        var row = Row(widget, display);
        Click(form, widget, row);
        var first = Assert.IsType<FluentContextMenuStrip>(CurrentMenu(form));
        Assert.True(first.Visible);
        Assert.Equal(new Rectangle(form.PointToScreen(new Point(Pixel(row.X, scale), Pixel(row.Y, scale))),
            new Size(Pixel(row.Width, scale), Pixel(row.Height, scale))), first.ToggleSourceBounds);

        Click(form, widget, row);
        Assert.Null(CurrentMenu(form));
        Assert.False(first.Visible);
        Application.DoEvents();
        Assert.True(first.IsDisposed);

        Click(form, widget, row);
        var reopened = Assert.IsType<FluentContextMenuStrip>(CurrentMenu(form));
        Assert.True(reopened.Visible);
        Assert.NotSame(first, reopened);
    });

    [Fact]
    public void SwitchingRowsKeepsTheNewMenuWhenTheOldCloseAnimationFinishes() => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        Click(form, widget, Row(widget, true));
        var displays = CurrentMenu(form)!;
        displays.AnimationsEnabled = true;

        Click(form, widget, Row(widget, false));
        var outputs = Assert.IsType<FluentContextMenuStrip>(CurrentMenu(form));
        Assert.NotSame(displays, outputs);
        PumpUntil(() => displays.IsDisposed);
        Assert.Same(outputs, CurrentMenu(form));
        Assert.True(outputs.Visible);
        Assert.Equal(new[] { "Headphones", "Speakers" }, outputs.Items.Cast<ToolStripItem>().Select(item => item.Text));

        Click(form, widget, Row(widget, true));
        var reopened = Assert.IsType<FluentContextMenuStrip>(CurrentMenu(form));
        Assert.NotSame(outputs, reopened);
        Assert.True(reopened.Visible);
        Assert.False(outputs.Visible);
    });

    [Theory]
    [InlineData(ToolStripDropDownCloseReason.AppClicked)]
    [InlineData(ToolStripDropDownCloseReason.Keyboard)]
    [InlineData(ToolStripDropDownCloseReason.ItemClicked)]
    public void ClosingTheMenuClearsStateAndAllowsReopening(ToolStripDropDownCloseReason reason) => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        Click(form, widget, Row(widget, false));
        var menu = CurrentMenu(form)!;
        menu.Close(reason);
        Assert.Null(CurrentMenu(form));
        Application.DoEvents();
        Assert.True(menu.IsDisposed);
        Click(form, widget, Row(widget, false));
        Assert.True(CurrentMenu(form)!.Visible);
    });

    [Theory]
    [InlineData("dismiss")]
    [InlineData("move")]
    [InlineData("remove")]
    [InlineData("dispose")]
    public void MenuClosesWithItsWidget(string action) => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        Click(form, widget, Row(widget, false));
        var menu = CurrentMenu(form)!;
        if (action == "dismiss") form.DismissWidgetInputs();
        else if (action == "dispose") form.Dispose();
        else
        {
            if (action == "move") widget.Bounds = widget.Bounds with { X = widget.Bounds.X + 20 };
            else runtime.State.Widgets.Remove(widget);
            typeof(DesktopBoxForm).GetMethod("RefreshControlCenterDeviceMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        }
        Assert.Null(CurrentMenu(form));
        Assert.False(menu.Visible);
    });

    [Theory]
    [InlineData(false, "anchor", true)]
    [InlineData(true, "anchor", true)]
    [InlineData(false, "child", true)]
    [InlineData(false, "outside", false)]
    [InlineData(false, "right", false)]
    [InlineData(false, "covered", false)]
    [InlineData(false, "item", false)]
    public void OutsideClickConsumesOnlyTheLeftPressOnTheVisibleSourceRow(bool animate, string click, bool consumed) => OnSta(() =>
    {
        using var owner = new Form
        {
            StartPosition = FormStartPosition.Manual, Location = new(80, 60), ClientSize = new(320, 200),
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true
        };
        using var child = new Button { Bounds = new Rectangle(10, 10, 200, 30) };
        if (click == "child") owner.Controls.Add(child);
        owner.Show();
        var anchor = owner.RectangleToScreen(new Rectangle(10, 10, 200, 30));
        using var cover = new Form
        {
            StartPosition = FormStartPosition.Manual, Bounds = anchor, FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false, TopMost = true
        };
        if (click == "covered") cover.Show();
        using var menu = new FluentContextMenuStrip { AnimationsEnabled = animate, ToggleSourceBounds = anchor };
        menu.Items.Add("Device");
        menu.Show(owner, new Point(10, 40));
        // Attaching the popup to its native owner can change the owner's Z-order.
        // Keep the source row exposed for the hit-test probe.
        owner.TopMost = false;
        owner.TopMost = true;
        if (click == "covered")
        {
            cover.TopMost = false;
            cover.TopMost = true;
        }
        var point = click switch
        {
            "outside" => owner.PointToScreen(new Point(270, 150)),
            "item" => menu.PointToScreen(new Point(menu.Items[0].Bounds.X + 5, menu.Items[0].Bounds.Y + 5)),
            _ => new Point(anchor.Left + 5, anchor.Top + 5)
        };
        if (consumed)
        {
            Assert.Same(owner, menu.SourceControl);
            var hit = (IntPtr)typeof(FluentContextMenuStrip).GetMethod("WindowFromPoint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [point])!;
            Assert.Equal(click == "child" ? child.Handle : owner.Handle, hit);
        }
        Assert.True(menu.Visible);
        Assert.Equal(consumed, menu.HandleGlobalMouseDown(click == "right" ? 0x0204 : 0x0201, point));
        if (click == "item") Assert.True(menu.Visible);
        else PumpUntil(() => !menu.Visible);
    });

    private static (CrabDeskRuntime Runtime, DesktopWidget Widget) Fixture(double scale = 1)
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        Set(runtime, "<State>k__BackingField", new CrabDeskState());
        runtime.State.Settings.Appearance.AnimationEnabled = false;
        runtime.State.Settings.Appearance.CornerRadius = 8;
        Set(runtime, "_configuredSubmenus", new ConditionalWeakTable<ToolStripDropDown, object>());
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { new MonitorLayout
        {
            Id = "test", DeviceName = "Test display", IsPrimary = true, DpiScale = scale,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700),
            PixelBounds = new(0, 0, 1000 * scale, 700 * scale), PixelWorkArea = new(0, 0, 1000 * scale, 700 * scale)
        } });
        Set(runtime, "_beginInvoke", (Action<Action>)(action => action()));
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        Set(runtime, "_controlMonitor", new Mock<IWidgetControlCenterMonitor>().Object);
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(100, 100, 372, 292), Controls = new()
            {
                OutputDeviceId = "headphones",
                OutputDevices = [new("headphones", "Headphones", true), new("speakers", "Speakers", false)]
            }
        };
        runtime.State.Widgets.Add(widget);
        return (runtime, widget);
    }

    private static RectangleF Row(DesktopWidget widget, bool display)
    {
        var body = ControlCenterPresentation.Body(widget.Bounds.Width, widget.Bounds.Height, 8);
        var row = display ? ControlCenterPresentation.BrightnessDisplayRow(body) :
            ControlCenterPresentation.OutputRow(ControlCenterPresentation.MediaArea(body));
        return new((float)(widget.Bounds.X + row.X), (float)(widget.Bounds.Y + row.Y), (float)row.Width, (float)row.Height);
    }

    private static void Click(DesktopBoxForm form, DesktopWidget widget, RectangleF row) =>
        Assert.True((bool)typeof(DesktopBoxForm).GetMethod("HandleControlCenterClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, [widget, new PointF(row.X + row.Width / 2, row.Y + row.Height / 2)])!);

    private static FluentContextMenuStrip? CurrentMenu(DesktopBoxForm form) =>
        (FluentContextMenuStrip?)typeof(DesktopBoxForm).GetField("_controlDeviceMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);

    private static int Pixel(float value, double scale) => (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(2)) { Application.DoEvents(); Thread.Sleep(10); }
        Assert.True(done());
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // Match the app manifest so native hit testing and WinForms screen
            // coordinates use the same pixels on scaled monitors.
            var previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (previousDpiContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpiContext);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
