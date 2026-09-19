using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.ViewModels;

/// <summary>
/// Drives the diagnostics page: runs the desktop stall attribution and shows
/// the result, including which third-party shell extensions are involved.
/// </summary>
public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IClipboardService _clipboard;
    private readonly IInfoBarService? _notifications;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _verdictText = string.Empty;
    [ObservableProperty] private string _verdictDetail = string.Empty;
    [ObservableProperty] private InfoBarSeverity _verdictSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _idleBaselineText = "—";
    [ObservableProperty] private string _ordinaryFolderText = "—";
    [ObservableProperty] private string _desktopFolderText = "—";
    [ObservableProperty] private string _crabDeskSurfaceText = "—";
    [ObservableProperty] private IReadOnlyList<ShellExtensionEntry> _extensions = [];
    [ObservableProperty] private string _extensionSummaryText = string.Empty;
    [ObservableProperty] private bool _hasExtensions;

    public DiagnosticsViewModel(
        ICrabDeskService service,
        IClipboardService clipboard,
        IInfoBarService? notifications = null)
    {
        _service = service;
        _clipboard = clipboard;
        _notifications = notifications;
    }

    private DesktopStallReport? LastReport { get; set; }

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        StatusText = "正在测量桌面线程响应…（会短暂创建一个临时文件用于触发 Explorer 刷新）";
        try
        {
            var report = await _service.RunDesktopStallDiagnosticsAsync().ConfigureAwait(true);
            LastReport = report;
            ApplyReport(report);
            StatusText = $"测量完成（{report.CapturedAt:HH:mm:ss}）";
        }
        catch (Exception exception)
        {
            global::CrabDesk.WinUI.AppDiagnostic.Error("Desktop stall diagnostics failed", exception);
            const string message = "诊断失败：测量过程中出现错误，请查看日志后重试";
            StatusText = message;
            _notifications?.Show(message, InfoBarSeverity.Error);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        if (LastReport is null)
        {
            _notifications?.Show("请先运行一次诊断", InfoBarSeverity.Informational, TimeSpan.FromSeconds(3));
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(_service.FormatDesktopStallReport(LastReport));
            _notifications?.Show("诊断报告已复制", InfoBarSeverity.Success, TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            const string message = "复制诊断报告失败：系统剪贴板暂时不可用，请稍后重试";
            global::CrabDesk.WinUI.AppDiagnostic.Error("Copy stall report failed", exception);
            _notifications?.Show(message, InfoBarSeverity.Error);
        }
    }

    private void ApplyReport(DesktopStallReport report)
    {
        HasReport = true;
        IdleBaselineText = FormatMs(report.IdleBaselineMs);
        OrdinaryFolderText = FormatMs(report.OrdinaryFolderMs);
        DesktopFolderText = FormatMs(report.DesktopFolderMs);
        CrabDeskSurfaceText = FormatMs(report.CrabDeskSurfaceMs);
        VerdictText = DescribeVerdict(report.Verdict);
        VerdictDetail = string.Join(Environment.NewLine, report.Notes);
        VerdictSeverity = ResolveSeverity(report.Verdict);
        Extensions = report.Extensions;
        HasExtensions = report.Extensions.Count > 0;
        ExtensionSummaryText = HasExtensions
            ? string.Join("、", DesktopStallAnalysis
                .CountByProduct(report.Extensions)
                .Select(item => $"{item.Product}（{item.Count} 项）"))
            : "未读取到第三方 Shell 扩展";
    }

    private static string FormatMs(int milliseconds) => milliseconds == DesktopStallAnalysis.NotMeasured
        ? "未测量"
        : $"{milliseconds} ms";

    private static string DescribeVerdict(DesktopStallVerdict verdict) => verdict switch
    {
        DesktopStallVerdict.ThirdPartyShellExtensions => "第三方 Shell 扩展导致资源管理器卡顿",
        DesktopStallVerdict.CrabDeskBlocked => "CrabDesk 与资源管理器同时卡住",
        DesktopStallVerdict.NoStall => "桌面线程响应正常",
        _ => "结果不足以判定"
    };

    private static InfoBarSeverity ResolveSeverity(DesktopStallVerdict verdict) => verdict switch
    {
        DesktopStallVerdict.ThirdPartyShellExtensions => InfoBarSeverity.Warning,
        DesktopStallVerdict.CrabDeskBlocked => InfoBarSeverity.Error,
        DesktopStallVerdict.NoStall => InfoBarSeverity.Success,
        _ => InfoBarSeverity.Informational
    };
}
