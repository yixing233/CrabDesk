using System.Drawing;
using System.Reflection;
using Forms = System.Windows.Forms;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetCenterReorderTests
{
    [Fact]
    public async Task RuntimeReorderPersistsAndKeepsDesktopGeometry()
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var runtime = fixture.Runtime;
        runtime.SetWidgetCenterEnabled(true);
        var first = runtime.AddWidgetToCenter(DesktopWidgetKind.Note);
        var second = runtime.AddWidgetToCenter(DesktopWidgetKind.Calendar);
        var third = runtime.AddWidgetToCenter(DesktopWidgetKind.Clock);
        var desktop = runtime.AddWidget(DesktopWidgetKind.Todo);
        var originals = runtime.State.Widgets.ToDictionary(widget => widget.Id, widget => (widget.Bounds, widget.MonitorId));
        var changes = 0;
        runtime.Changed += (_, _) => changes++;
        runtime.ReorderWidgetCenter([third.Id, first.Id, second.Id]);
        Assert.Equal(1, changes);
        Assert.Equal(new[] { third.Id, first.Id, second.Id }, runtime.State.Widgets.Where(w => w.Placement == WidgetPlacement.Center).OrderBy(w => w.CenterOrder).Select(w => w.Id));
        runtime.ReorderWidgetCenter([third.Id, first.Id, second.Id]);
        runtime.ReorderWidgetCenter([third.Id, first.Id, first.Id]);
        runtime.ReorderWidgetCenter([desktop.Id, first.Id, second.Id]);
        Assert.Equal(1, changes);
        foreach (var widget in runtime.State.Widgets)
        {
            Assert.Equal(originals[widget.Id].Bounds, widget.Bounds);
            Assert.Equal(originals[widget.Id].MonitorId, widget.MonitorId);
        }
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.CenterReorder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonLayoutStore(root);
            await store.SaveAsync(runtime.State);
            var restored = await store.LoadAsync();
            Assert.Equal(new[] { third.Id, first.Id, second.Id }, restored.Widgets.Where(w => w.Placement == WidgetPlacement.Center).OrderBy(w => w.CenterOrder).Select(w => w.Id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(DesktopWidgetKind.Calendar)]
    [InlineData(DesktopWidgetKind.Clock)]
    [InlineData(DesktopWidgetKind.Note)]
    [InlineData(DesktopWidgetKind.FileShelf)]
    [InlineData(DesktopWidgetKind.ControlCenter)]
    [InlineData(DesktopWidgetKind.Lyrics)]
    [InlineData(DesktopWidgetKind.Todo)]
    [InlineData(DesktopWidgetKind.Translation)]
    [InlineData(DesktopWidgetKind.Weather)]
    [InlineData(DesktopWidgetKind.Performance)]
    [InlineData(DesktopWidgetKind.Network)]
    public void DragStartsOnTheCardBodyAndSkipsTheGap(DesktopWidgetKind kind) => Sta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var runtime = fixture.Runtime;
        runtime.SetWidgetCenterEnabled(true);
        runtime.AddWidgetToCenter(kind);
        using var center = CreateActiveCenter(runtime);
        var view = center.WidgetViewBounds.Values.Single();
        var x = (int)(view.X + view.Width / 2);
        var gap = (int)view.Y - 6;
        Call(center, "OnMouseDown", Mouse(x, gap));
        Call(center, "OnMouseMove", Mouse(x + 15, gap));
        Assert.False(center.Capture);

        var body = (int)(view.Y + view.Height - 14);
        Call(center, "OnMouseDown", Mouse(x, body));
        Call(center, "OnMouseMove", Mouse(x + 2, body));
        Assert.Null(ReorderingWidget(center));
        Call(center, "OnMouseMove", Mouse(x + 15, body));
        Assert.NotNull(ReorderingWidget(center));
        Call(center, "OnMouseUp", Mouse(-20, body));
        Assert.False(center.Capture);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DragPreviewOnlyCommitsOnSuccessfulRelease(bool commit) => Sta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var runtime = fixture.Runtime;
        runtime.SetWidgetCenterEnabled(true);
        var first = runtime.AddWidgetToCenter(DesktopWidgetKind.Note);
        var second = runtime.AddWidgetToCenter(DesktopWidgetKind.Calendar);
        var third = runtime.AddWidgetToCenter(DesktopWidgetKind.Clock);
        var original = first.Bounds;
        using var center = CreateActiveCenter(runtime);
        var firstView = center.WidgetViewBounds[first.Id];
        var thirdView = center.WidgetViewBounds[third.Id];
        var press = Mouse((int)(firstView.X + firstView.Width / 2), (int)(firstView.Y + firstView.Height / 2));
        var target = Mouse((int)(thirdView.X + thirdView.Width / 2), (int)(thirdView.Y + thirdView.Height / 2));
        Call(center, "OnMouseDown", press);
        Call(center, "OnMouseMove", target);
        Assert.Same(first, ReorderingWidget(center));
        Assert.Equal(0, first.CenterOrder);
        Assert.Equal(original, first.Bounds);
        Call(center, "OnMouseUp", commit ? target : Mouse(-20, target.Y));
        var expected = commit ? new[] { second.Id, third.Id, first.Id } : new[] { first.Id, second.Id, third.Id };
        Assert.Equal(expected, runtime.State.Widgets.OrderBy(widget => widget.CenterOrder).Select(widget => widget.Id));
        Assert.Equal(original, first.Bounds);
        Assert.False(center.Capture);
    });

    private static WidgetCenterForm CreateActiveCenter(CrabDeskRuntime runtime)
    {
        typeof(CrabDeskRuntime).GetField("_widgetCenterVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, true);
        return new WidgetCenterForm(runtime, runtime.Monitors[0]) { ClientSize = new(420, 700) };
    }

    private static DesktopWidget? ReorderingWidget(WidgetCenterForm center) =>
        (DesktopWidget?)typeof(WidgetCenterForm).GetField("_reorderWidget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(center);

    private static Forms.MouseEventArgs Mouse(int x, int y) => new(Forms.MouseButtons.Left, 1, x, y, 0);

    private static object? Call(object instance, string name, params object[] arguments) =>
        typeof(WidgetCenterForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
