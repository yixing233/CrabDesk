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
    public void GripDoesNotOverlapCardContent(DesktopWidgetKind kind) => Sta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        fixture.Runtime.State.Widgets.Add(new() { Kind = kind, Placement = WidgetPlacement.Center, MonitorId = fixture.Runtime.Monitors[0].Id });
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        var view = center.WidgetViewBounds.Values.Single();
        var grip = WidgetCenterForm.ReorderGripBounds(view);
        Assert.Equal(view.Y, grip.Bottom);
        Assert.True(grip.Left >= view.X);
        Assert.True(grip.Right <= view.X + view.Width);
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
        using var center = new WidgetCenterForm(runtime, runtime.Monitors[0]);
        var firstView = center.WidgetViewBounds[first.Id];
        var thirdView = center.WidgetViewBounds[third.Id];
        var grip = WidgetCenterForm.ReorderGripBounds(firstView);
        var press = new PointF(grip.X + grip.Width / 2, grip.Y + grip.Height / 2);
        Assert.True((bool)Call(center, "BeginWidgetReorder", new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, (int)press.X, (int)press.Y, 0), press)!);
        var target = new PointF((float)(thirdView.X + thirdView.Width / 2), (float)(thirdView.Y + thirdView.Height / 2));
        Call(center, "MoveWidgetReorder", new Forms.MouseEventArgs(Forms.MouseButtons.Left, 0, (int)target.X, (int)target.Y, 0), target);
        Assert.Equal(0, first.CenterOrder);
        Assert.Equal(original, first.Bounds);
        Call(center, "FinishWidgetReorder", commit);
        var expected = commit ? new[] { second.Id, third.Id, first.Id } : new[] { first.Id, second.Id, third.Id };
        Assert.Equal(expected, runtime.State.Widgets.OrderBy(widget => widget.CenterOrder).Select(widget => widget.Id));
        Assert.Equal(original, first.Bounds);
        Assert.False(center.Capture);
    });

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
