using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;
using Forms = System.Windows.Forms;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopDropForwardingTests
{
    [Fact]
    public void DragOverGoesToTheSurfaceUnderThePointerAndLeavesThePreviousOne()
    {
        var left = new FakeTarget(new LayoutRect(0, 0, 1920, 1080));
        var right = new FakeTarget(new LayoutRect(1920, 0, 1920, 1080));
        var forwarder = new DesktopDropForwarder(point =>
            new[] { left, right }.FirstOrDefault(target => target.ContainsScreenPixel(point)));

        forwarder.DragOver(Args(100, 100));
        Assert.Same(left, forwarder.Current);
        Assert.Equal(["over(100,100)"], left.Log);
        Assert.Empty(right.Log);

        forwarder.DragOver(Args(2000, 100));
        Assert.Same(right, forwarder.Current);
        Assert.Equal(["over(100,100)", "leave"], left.Log);
        Assert.Equal(["over(2000,100)"], right.Log);
    }

    [Fact]
    public void NoSurfaceUnderThePointerRefusesTheDropAndLeavesTheLastSurface()
    {
        var only = new FakeTarget(new LayoutRect(0, 0, 100, 100));
        var forwarder = new DesktopDropForwarder(point => only.ContainsScreenPixel(point) ? only : null);
        forwarder.DragOver(Args(10, 10));

        var outside = Args(500, 500, Forms.DragDropEffects.Copy);
        forwarder.DragOver(outside);

        Assert.Equal(Forms.DragDropEffects.None, outside.Effect);
        Assert.Null(forwarder.Current);
        Assert.Equal(["over(10,10)", "leave"], only.Log);
    }

    [Fact]
    public void DragLeaveAndDropClearTheCurrentSurface()
    {
        var only = new FakeTarget(new LayoutRect(0, 0, 100, 100));
        var forwarder = new DesktopDropForwarder(_ => only);

        forwarder.DragOver(Args(10, 10));
        forwarder.DragLeave();
        Assert.Null(forwarder.Current);
        Assert.Equal(["over(10,10)", "leave"], only.Log);

        forwarder.DragOver(Args(20, 20));
        forwarder.DragDrop(Args(20, 20));
        Assert.Null(forwarder.Current);
        Assert.Equal(["over(10,10)", "leave", "over(20,20)", "drop(20,20)"], only.Log);
    }

    [Fact]
    public void DropOnAnotherMonitorThanTheLastDragOverStillLeavesThePreviousSurface()
    {
        var left = new FakeTarget(new LayoutRect(0, 0, 100, 100));
        var right = new FakeTarget(new LayoutRect(100, 0, 100, 100));
        var forwarder = new DesktopDropForwarder(point =>
            new[] { left, right }.FirstOrDefault(target => target.ContainsScreenPixel(point)));

        forwarder.DragOver(Args(10, 10));
        forwarder.DragDrop(Args(150, 10));

        Assert.Equal(["over(10,10)", "leave"], left.Log);
        Assert.Equal(["drop(150,10)"], right.Log);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1919, 1079, true)]
    [InlineData(1920, 0, false)]
    [InlineData(-1, 5, false)]
    public void PixelContainmentUsesHalfOpenMonitorBounds(int x, int y, bool expected)
    {
        Assert.Equal(expected, DesktopDropForwarder.ContainsPixel(new LayoutRect(0, 0, 1920, 1080), new Point(x, y)));
    }

    [Fact]
    public void EveryExternalMoveDropCompletesTheShellOptimizedMoveHandshake()
    {
        // Returning MOVE from Drop without Performed DropEffect = NONE tells Explorer
        // to delete the originals that CrabDesk has already moved; its file operation
        // then stalls in the retry path and both processes freeze for seconds.
        var surface = ReadSource("CrabDesk.Runtime", "DesktopIconSurface.cs");
        var drop = surface[surface.IndexOf("private async void OnDragDrop(", StringComparison.Ordinal)..];
        drop = drop[..drop.IndexOf("private async Task ImportExternalDropToDesktopAsync(", StringComparison.Ordinal)];
        Assert.Contains("ShellDropEffectProtocol.TryReportOptimizedMove(", drop, StringComparison.Ordinal);
        Assert.Contains("eventArgs.Effect = move ? Forms.DragDropEffects.Copy : effect;", drop, StringComparison.Ordinal);
        Assert.Contains("QueueExternalDropCompletion(", drop, StringComparison.Ordinal);
        Assert.DoesNotContain("await ImportExternalDropToDesktopAsync(externalPaths, move, dropPoint);", drop, StringComparison.Ordinal);

        var box = ReadSource("CrabDesk.Runtime", "DesktopBoxForm.DragDrop.cs");
        Assert.Contains("ShellDropEffectProtocol.TryReportOptimizedMove(", box, StringComparison.Ordinal);
        // Every branch that imports an external payload with move semantics reports first.
        Assert.Equal(3, CountOccurrences(box, "ReportOptimizedMoveForExternalPayload(eventArgs, "));
        // CrabDesk's own drags reconcile through the runtime and must not be re-reported.
        Assert.Contains("eventArgs.Data.GetDataPresent(DesktopIconSurface.DesktopIconDragSessionFormat))", box, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    [Fact]
    public void TheIconSurfaceIsAForwardTargetAndTheAcrylicHostRegistersForwarding()
    {
        // The acrylic host is a top-level window above Explorer's desktop whose
        // HTTRANSPARENT answer is invisible to another process's OLE drag, so it
        // must accept drops itself and hand them to the icon surface.
        Assert.Contains(typeof(IDesktopDropForwardTarget), typeof(DesktopIconSurface).GetInterfaces());

        var host = ReadSource("CrabDesk.Runtime", "DesktopAcrylicHost.cs");
        Assert.Contains("AllowDrop = true;", host, StringComparison.Ordinal);
        Assert.Contains("protected override void OnDragEnter(", host, StringComparison.Ordinal);
        Assert.Contains("protected override void OnDragDrop(", host, StringComparison.Ordinal);

        var manager = ReadSource("CrabDesk.Runtime", "DesktopSurfaceManager.cs");
        Assert.Contains("SetDropForwarding(", manager, StringComparison.Ordinal);
    }

    private static Forms.DragEventArgs Args(int x, int y, Forms.DragDropEffects allowed = Forms.DragDropEffects.Copy | Forms.DragDropEffects.Move) =>
        new(null, 0, x, y, allowed, allowed);

    private static string ReadSource(params string[] pathParts) =>
        File.ReadAllText(Path.Combine([FindSolutionDirectory(), .. pathParts]));

    private static string FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the CrabDesk solution directory.");
    }

    private sealed class FakeTarget(LayoutRect bounds) : IDesktopDropForwardTarget
    {
        public List<string> Log { get; } = [];

        public bool ContainsScreenPixel(Point screenPixel) => DesktopDropForwarder.ContainsPixel(bounds, screenPixel);

        public void ForwardDragOver(Forms.DragEventArgs eventArgs) => Log.Add($"over({eventArgs.X},{eventArgs.Y})");

        public void ForwardDragLeave() => Log.Add("leave");

        public void ForwardDragDrop(Forms.DragEventArgs eventArgs) => Log.Add($"drop({eventArgs.X},{eventArgs.Y})");
    }
}
