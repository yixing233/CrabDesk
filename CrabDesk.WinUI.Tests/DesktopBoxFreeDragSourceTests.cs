using System.Reflection;
using CrabDesk.Core;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopBoxFreeDragSourceTests
{
    [Fact]
    public void LiveAndCommitMovementDoNotUseTheFourDipGrid()
    {
        var source = ReadSource();
        var update = Extract(source, "private void UpdateMovingBox(", "private void ClampBoxPositionForCommit(");
        var commit = Extract(source, "private void ClampBoxPositionForCommit(", "private static double SnapDipToMonitorPixel(");
        Assert.DoesNotContain("LayoutGrid.Snap", update, StringComparison.Ordinal);
        Assert.Contains("gridStep: 0", update, StringComparison.Ordinal);
        Assert.Contains("ApplyBoxDragPosition(box, nextBounds, _monitor)", update, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutGrid.Snap", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopBoxAlignmentEngine", commit, StringComparison.Ordinal);
    }

    [Fact]
    public void AlignmentGuidesAreClearedBeforeTheSettledFrameIsCommitted()
    {
        var source = ReadSource();
        var complete = Extract(source, "private void CompleteBoxTransform(", "private void UpdateMovingBox(");
        Assert.Contains("ClearBoxAlignmentGuides();", complete, StringComparison.Ordinal);
        Assert.True(complete.IndexOf("ClearBoxAlignmentGuides();", StringComparison.Ordinal) <
            complete.IndexOf("FlushTransformTrail();", StringComparison.Ordinal));
    }

    [Fact]
    public void CrossMonitorTransferAlsoDisablesGridSnapping()
    {
        var source = ReadSource();
        var complete = Extract(source, "private void CompleteBoxTransform(", "private void UpdateMovingBox(");
        Assert.Contains("grabOffsetX * _scale / targetScale", complete, StringComparison.Ordinal);
        Assert.Contains("grabOffsetY * _scale / targetScale", complete, StringComparison.Ordinal);
        Assert.Contains("gridStep: 0", complete, StringComparison.Ordinal);
    }

    [Fact]
    public void AlignmentGuideDirtyBoundsFollowTheDynamicTransformLayer()
    {
        var source = File.ReadAllText(Path.Combine(FindSolutionDirectory(), "CrabDesk.Runtime", "DesktopBoxForm.cs"));
        var dynamicBounds = Extract(source, "private RectangleF? GetDynamicVisualBounds(", "internal void SetIconLayerRenderRequest(");
        Assert.Contains("GetBoxAlignmentGuideBounds()", dynamicBounds, StringComparison.Ordinal);
        var dynamicRender = Extract(source, "internal void RenderDragOnIconLayerForMonitor(", "/// <summary>");
        Assert.Contains("DrawBoxAlignmentGuides(graphics, monitorId);", dynamicRender, StringComparison.Ordinal);
    }

    [Fact]
    public void RealBoxFormPreservesFreePositionUntilANearbyPeerCanSnap()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var runtime = (CrabDesk.Runtime.CrabDeskRuntime)System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(typeof(CrabDesk.Runtime.CrabDeskRuntime));
                var monitor = new MonitorLayout
                {
                    Id = "drag-test", DeviceName = "drag-test", DpiScale = 1.25,
                    Bounds = new(0, 0, 1200, 800), WorkArea = new(0, 0, 1200, 800),
                    PixelBounds = new(0, 0, 1500, 1000), PixelWorkArea = new(0, 0, 1500, 1000)
                };
                var state = new CrabDeskState();
                var moving = new DesktopBox { MonitorId = monitor.Id, Bounds = new(123.2, 100, 240, 180) };
                var remote = new DesktopBox { MonitorId = monitor.Id, Bounds = new(100, 500, 240, 180) };
                state.Boxes.AddRange([moving, remote]);
                Set(runtime, "<State>k__BackingField", state);
                Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
                Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
                using var form = new CrabDesk.Runtime.DesktopBoxForm(runtime, monitor);
                Invoke(form, "ApplyBoxDragPosition", moving, new LayoutRect(127.2, 103.2, 240, 180), monitor);
                Assert.Equal(new LayoutRect(127.2, 103.2, 240, 180), moving.Bounds);
                Assert.Empty(Guides(form));

                remote.Bounds = new LayoutRect(100, 331.2, 240, 180);
                Invoke(form, "ApplyBoxDragPosition", moving, new LayoutRect(104, 103.2, 240, 180), monitor);
                Assert.Equal(100, moving.Bounds.X);
                Assert.NotEmpty(Guides(form));

                Invoke(form, "ApplyBoxDragPosition", moving, new LayoutRect(108.1, 103.2, 240, 180), monitor);
                Assert.Equal(108.1, moving.Bounds.X);
                Assert.Empty(Guides(form));
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static IReadOnlyList<DesktopBoxAlignmentGuide> Guides(object form) =>
        (IReadOnlyList<DesktopBoxAlignmentGuide>)form.GetType()
            .GetField("_boxAlignmentGuides", Hidden)!.GetValue(form)!;

    private static void Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Hidden)!.Invoke(target, arguments);

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static string ReadSource() => File.ReadAllText(Path.Combine(
        FindSolutionDirectory(), "CrabDesk.Runtime", "DesktopBoxForm.Input.cs"));

    private static string Extract(string source, string startAnchor, string endAnchor)
    {
        var start = source.IndexOf(startAnchor, StringComparison.Ordinal);
        var end = source.IndexOf(endAnchor, Math.Max(0, start), StringComparison.Ordinal);
        Assert.True(start >= 0, $"{startAnchor} was not found.");
        Assert.True(end > start, $"{endAnchor} was not found.");
        return source[start..end];
    }

    private static string FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate CrabDesk.sln.");
    }
}
