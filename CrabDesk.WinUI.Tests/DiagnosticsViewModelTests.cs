using CrabDesk.Core;
using CrabDesk.WinUI.Services;
using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DiagnosticsViewModelTests
{
    private static DesktopStallReport CreateReport(
        DesktopStallVerdict verdict = DesktopStallVerdict.ThirdPartyShellExtensions,
        int idle = 8,
        int control = 2,
        int desktop = 1200,
        int crabDesk = 2,
        IReadOnlyList<ShellExtensionEntry>? extensions = null) =>
        new(
            DateTimeOffset.Parse("2026-09-18T10:00:00+08:00"),
            DesktopFound: true,
            idle,
            control,
            desktop,
            crabDesk,
            verdict,
            extensions ?? [],
            ["桌面线程窗口：SHELLDLL_DefView"]);

    [Fact]
    public async Task RunningDiagnosticsPublishesEveryMeasurement()
    {
        var service = new Mock<ICrabDeskService>();
        service.Setup(item => item.RunDesktopStallDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReport());
        var viewModel = new DiagnosticsViewModel(service.Object, Mock.Of<IClipboardService>());

        await viewModel.RunDiagnosticsCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasReport);
        Assert.Equal("8 ms", viewModel.IdleBaselineText);
        Assert.Equal("2 ms", viewModel.OrdinaryFolderText);
        Assert.Equal("1200 ms", viewModel.DesktopFolderText);
        Assert.Equal("2 ms", viewModel.CrabDeskSurfaceText);
        Assert.Equal("第三方 Shell 扩展导致资源管理器卡顿", viewModel.VerdictText);
        Assert.Equal(InfoBarSeverity.Warning, viewModel.VerdictSeverity);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task ABlockedCrabDeskIsSurfacedAsAnError()
    {
        var service = new Mock<ICrabDeskService>();
        service.Setup(item => item.RunDesktopStallDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReport(
                DesktopStallVerdict.CrabDeskBlocked,
                crabDesk: 1180));
        var viewModel = new DiagnosticsViewModel(service.Object, Mock.Of<IClipboardService>());

        await viewModel.RunDiagnosticsCommand.ExecuteAsync(null);

        Assert.Equal("CrabDesk 与资源管理器同时卡住", viewModel.VerdictText);
        Assert.Equal(InfoBarSeverity.Error, viewModel.VerdictSeverity);
    }

    [Fact]
    public async Task UnmeasuredValuesRenderAsPlaceholdersInsteadOfNegativeNumbers()
    {
        var service = new Mock<ICrabDeskService>();
        service.Setup(item => item.RunDesktopStallDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReport(
                DesktopStallVerdict.Inconclusive,
                control: DesktopStallAnalysis.NotMeasured,
                crabDesk: DesktopStallAnalysis.NotMeasured));
        var viewModel = new DiagnosticsViewModel(service.Object, Mock.Of<IClipboardService>());

        await viewModel.RunDiagnosticsCommand.ExecuteAsync(null);

        Assert.Equal("未测量", viewModel.OrdinaryFolderText);
        Assert.Equal("未测量", viewModel.CrabDeskSurfaceText);
        Assert.Equal("结果不足以判定", viewModel.VerdictText);
    }

    [Fact]
    public async Task ExtensionsAreSummarizedPerProduct()
    {
        var extensions = Enumerable.Range(0, 7)
            .Select(index => new ShellExtensionEntry(
                $".WorkspaceExt{index}",
                ShellExtensionKind.IconOverlay,
                "百度网盘",
                @"D:\BaiduNetdisk\YunShellExtV164.dll"))
            .Append(new ShellExtensionEntry(
                "bdzshl.x64.dll",
                ShellExtensionKind.LoadedModule,
                "Bandizip",
                @"D:\Program Files\Bandizip\bdzshl.x64.dll"))
            .ToArray();
        var service = new Mock<ICrabDeskService>();
        service.Setup(item => item.RunDesktopStallDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReport(extensions: extensions));
        var viewModel = new DiagnosticsViewModel(service.Object, Mock.Of<IClipboardService>());

        await viewModel.RunDiagnosticsCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasExtensions);
        Assert.Equal(8, viewModel.Extensions.Count);
        Assert.Equal("百度网盘（7 项）、Bandizip（1 项）", viewModel.ExtensionSummaryText);
    }

    [Fact]
    public async Task NoExtensionsReadIsStatedPlainly()
    {
        var service = new Mock<ICrabDeskService>();
        service.Setup(item => item.RunDesktopStallDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReport(extensions: []));
        var viewModel = new DiagnosticsViewModel(service.Object, Mock.Of<IClipboardService>());

        await viewModel.RunDiagnosticsCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasExtensions);
        Assert.Equal("未读取到第三方 Shell 扩展", viewModel.ExtensionSummaryText);
    }

    [Fact]
    public async Task CopyingBeforeRunningAsksForARunInsteadOfCopyingNothing()
    {
        var notifications = new Mock<IInfoBarService>();
        var clipboard = new Mock<IClipboardService>();
        var viewModel = new DiagnosticsViewModel(
            Mock.Of<ICrabDeskService>(),
            clipboard.Object,
            notifications.Object);

        await viewModel.CopyReportCommand.ExecuteAsync(null);

        clipboard.Verify(item => item.SetTextAsync(It.IsAny<string>()), Times.Never);
        notifications.Verify(
            item => item.Show("请先运行一次诊断", It.IsAny<InfoBarSeverity>(), It.IsAny<TimeSpan?>()),
            Times.Once);
    }

    [Fact]
    public async Task CopyingSendsTheFormattedReportToTheClipboard()
    {
        var service = new Mock<ICrabDeskService>();
        service.Setup(item => item.RunDesktopStallDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReport());
        service.Setup(item => item.FormatDesktopStallReport(It.IsAny<DesktopStallReport>()))
            .Returns("formatted report");
        var clipboard = new Mock<IClipboardService>();
        clipboard.Setup(item => item.SetTextAsync("formatted report")).Returns(Task.CompletedTask);
        var notifications = new Mock<IInfoBarService>();
        var viewModel = new DiagnosticsViewModel(service.Object, clipboard.Object, notifications.Object);
        await viewModel.RunDiagnosticsCommand.ExecuteAsync(null);

        await viewModel.CopyReportCommand.ExecuteAsync(null);

        clipboard.Verify(item => item.SetTextAsync("formatted report"), Times.Once);
        notifications.Verify(
            item => item.Show("诊断报告已复制", It.IsAny<InfoBarSeverity>(), It.IsAny<TimeSpan?>()),
            Times.Once);
    }

    [Fact]
    public async Task AFailedMeasurementIsReportedWithoutThrowing()
    {
        var service = new Mock<ICrabDeskService>();
        service.Setup(item => item.RunDesktopStallDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var notifications = new Mock<IInfoBarService>();
        var viewModel = new DiagnosticsViewModel(
            service.Object,
            Mock.Of<IClipboardService>(),
            notifications.Object);

        await viewModel.RunDiagnosticsCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasReport);
        Assert.False(viewModel.IsRunning);
        Assert.Contains("诊断失败", viewModel.StatusText, StringComparison.Ordinal);
        notifications.Verify(
            item => item.Show(It.IsAny<string>(), InfoBarSeverity.Error, It.IsAny<TimeSpan?>()),
            Times.Once);
    }

    [Fact]
    public async Task ASecondRunIsIgnoredWhileTheFirstIsStillMeasuring()
    {
        var gate = new TaskCompletionSource<DesktopStallReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<ICrabDeskService>();
        service.Setup(item => item.RunDesktopStallDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        var viewModel = new DiagnosticsViewModel(service.Object, Mock.Of<IClipboardService>());

        var first = viewModel.RunDiagnosticsCommand.ExecuteAsync(null);
        await viewModel.RunDiagnosticsCommand.ExecuteAsync(null);
        gate.SetResult(CreateReport());
        await first;

        service.Verify(
            item => item.RunDesktopStallDiagnosticsAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
