using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopBoxTransformCompletionTests
{
    [Fact]
    public void BoxSelectionCompletionRestoresHoverOwnedHeaderActions()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "DesktopBoxForm.Input.cs"));
        var methodStart = source.IndexOf(
            "private void FinishSelectionGesture()",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void OnMouseUp(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var settledFrameRequest = method.IndexOf("RequestVisualLayerRender();", StringComparison.Ordinal);
        var hoverReconcile = method.IndexOf("QueueHoverReconcile();", StringComparison.Ordinal);

        Assert.True(settledFrameRequest >= 0);
        Assert.True(hoverReconcile > settledFrameRequest);
    }

    [Fact]
    public void BoxTransformCompletionRestoresHoverOwnedHeaderActions()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "DesktopBoxForm.Input.cs"));
        var methodStart = source.IndexOf(
            "private void CompleteBoxTransform(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void UpdateMovingBox(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var settledFrameCommit = method.IndexOf("FlushTransformTrail();", StringComparison.Ordinal);
        var hoverReconcile = method.IndexOf("QueueHoverReconcile();", StringComparison.Ordinal);

        Assert.True(settledFrameCommit >= 0);
        Assert.True(hoverReconcile > settledFrameCommit);
    }

    [Fact]
    public void CaptureLossStillCommitsAMonitorTransfer()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "DesktopBoxForm.Input.cs"));
        var methodStart = source.IndexOf(
            "protected override void OnMouseCaptureChanged(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void CompleteBoxTransform(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains(
            "CompleteBoxTransform(_movingBox, _resizingBox, grabOffsetX, grabOffsetY, true);",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BoxTransformCompletionCommitsTheActualPreviousMonitor()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "DesktopBoxForm.Input.cs"));
        var methodStart = source.IndexOf(
            "private void CompleteBoxTransform(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void UpdateMovingBox(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains(
            "var previousMonitorId = _monitorTransferLastPreviousMonitorId;",
            method,
            StringComparison.Ordinal);
        Assert.Contains(
            "monitorChanged ? previousMonitorId : null",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "monitorChanged ? _monitor.Id : null",
            method,
            StringComparison.Ordinal);
    }

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
}
