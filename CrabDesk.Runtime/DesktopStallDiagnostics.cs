using System.Diagnostics;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

/// <summary>
/// Attributes the "Explorer freezes for seconds after a file lands on the
/// desktop" freeze, by measuring the desktop thread's responsiveness around a
/// probe file and comparing it against a control folder and against CrabDesk's
/// own surface.
/// </summary>
/// <remarks>
/// Read-only with respect to the system: it creates one temporary file in the
/// desktop folder (and one in Documents as the control), deletes both, and
/// reads the registry. It never disables anything. See
/// docs/superpowers/plans/2026-09-18-desktop-stall-diagnostics.md for the
/// measurements this reproduces.
/// </remarks>
internal sealed class DesktopStallDiagnostics
{
    private const int BaselineSamples = 5;
    private const int ProbeSamples = 30;
    private const int SampleSpacingMs = 150;
    private const int BaselineSpacingMs = 120;

    private readonly IntPtr _desktopView;
    private readonly IntPtr _crabDeskSurface;

    /// <summary>
    /// The window handles are resolved by the caller on the UI thread before
    /// this runs: CrabDesk's surface list is mutated there, and reading it from
    /// the measurement thread would race with a surface rebuild. A handle that
    /// dies mid-measurement simply reads as "not measured", which the verdict
    /// reports as inconclusive rather than guessing.
    /// </summary>
    internal DesktopStallDiagnostics(IntPtr desktopView, IntPtr crabDeskSurface)
    {
        _desktopView = desktopView;
        _crabDeskSurface = crabDeskSurface;
    }

    internal Task<DesktopStallReport> RunAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(cancellationToken), cancellationToken);

    internal DesktopStallReport Run(CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var desktopView = _desktopView;
        if (desktopView == IntPtr.Zero)
        {
            notes.Add("未找到桌面窗口，无法测量。请确认资源管理器正在运行。");
            return new DesktopStallReport(
                DateTimeOffset.Now,
                DesktopFound: false,
                DesktopStallAnalysis.NotMeasured,
                DesktopStallAnalysis.NotMeasured,
                DesktopStallAnalysis.NotMeasured,
                DesktopStallAnalysis.NotMeasured,
                DesktopStallVerdict.Inconclusive,
                [],
                notes);
        }

        var extensions = ShellExtensionInventory.Collect(desktopView);
        notes.Add($"桌面线程窗口：{DesktopHostService.GetWindowClass(desktopView)}");

        var idle = DesktopStallProbe.MeasureWorstResponse(
            desktopView,
            BaselineSamples,
            BaselineSpacingMs);
        notes.Add(idle.HasValue
            ? $"空闲基线：{idle.WorstMs} ms（{idle.MeasuredCount} 次采样）"
            : "空闲基线：测量失败");

        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var controlDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        cancellationToken.ThrowIfCancellationRequested();

        // The control run first: it must not be influenced by a desktop probe
        // file that is still settling in Explorer's view.
        var control = MeasureWithProbeFile(
            desktopView,
            controlDirectory,
            "control",
            cancellationToken,
            out var controlNote);
        notes.Add(controlNote);

        cancellationToken.ThrowIfCancellationRequested();

        // Interleaved sampling: Explorer and CrabDesk are measured alternately
        // in the same round, so a "CrabDesk kept answering" claim is about the
        // same instant as the stall it is compared against.
        var crabDeskSurface = _crabDeskSurface;
        var desktopFolderMs = DesktopStallAnalysis.NotMeasured;
        var crabDeskMs = DesktopStallAnalysis.NotMeasured;
        var probeNote = string.Empty;
        var desktopFile = CreateProbeFile(desktopDirectory);
        try
        {
            if (desktopFile is null)
            {
                probeNote = "桌面目录写入：探针文件创建失败，未测量";
            }
            else
            {
                var paired = DesktopStallProbe.MeasurePairedWorstResponse(
                    desktopView,
                    crabDeskSurface,
                    ProbeSamples,
                    SampleSpacingMs);
                desktopFolderMs = paired.First.WorstMs;
                crabDeskMs = paired.Second.WorstMs;
                probeNote = paired.Second.HasValue
                    ? $"桌面目录写入：{Describe(desktopFolderMs)}；同时刻 CrabDesk 桌面层：{Describe(crabDeskMs)}"
                    : $"桌面目录写入：{Describe(desktopFolderMs)}；CrabDesk 桌面层不可测（无表面窗口）";
            }
        }
        finally
        {
            DeleteProbeFile(desktopFile);
        }

        notes.Add(probeNote);

        var verdict = DesktopStallAnalysis.ClassifyVerdict(
            desktopFound: true,
            idle.WorstMs,
            control.WorstMs,
            desktopFolderMs,
            crabDeskMs);
        notes.AddRange(DescribeVerdict(verdict, desktopFolderMs, control.WorstMs, crabDeskMs));
        AddExtensionNotes(notes, extensions);

        return new DesktopStallReport(
            DateTimeOffset.Now,
            DesktopFound: true,
            idle.WorstMs,
            control.WorstMs,
            desktopFolderMs,
            crabDeskMs,
            verdict,
            extensions,
            notes);
    }

    private static StallSample MeasureWithProbeFile(
        IntPtr desktopView,
        string directory,
        string label,
        CancellationToken cancellationToken,
        out string note)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            note = $"{label}：目录不可用（{directory}），未测量";
            return new StallSample(DesktopStallAnalysis.NotMeasured, 0, 0);
        }

        var probeFile = CreateProbeFile(directory);
        if (probeFile is null)
        {
            note = $"{label}：探针文件创建失败，未测量";
            return new StallSample(DesktopStallAnalysis.NotMeasured, 0, 0);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = DesktopStallProbe.MeasureWorstResponse(
                desktopView,
                ProbeSamples,
                SampleSpacingMs);
            note = $"{label}（{directory}）：{Describe(sample.WorstMs)}";
            return sample;
        }
        finally
        {
            DeleteProbeFile(probeFile);
        }
    }

    /// <summary>
    /// Creates the probe file Explorer has to react to, and returns its path
    /// for the caller to delete. The caller deletes it in a finally block.
    /// </summary>
    /// <remarks>
    /// The file is deliberately a normal visible file: a hidden one is filtered
    /// out of Explorer's view, so its overlay handlers would never run and the
    /// measurement would not reproduce the stall. That means the probe file is
    /// briefly visible on the desktop, which the diagnostics page warns about.
    /// </remarks>
    private static string? CreateProbeFile(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"CrabDesk-stall-probe-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 1,
                       FileOptions.None))
            {
                stream.WriteByte(0);
            }

            return path;
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException
                                               or ArgumentException)
        {
            return null;
        }
    }

    private static void DeleteProbeFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException)
        {
            DiagnosticLog.Info($"Stall probe file could not be removed path={path}");
        }
    }

    private static string Describe(int milliseconds) => milliseconds == DesktopStallAnalysis.NotMeasured
        ? "未测量"
        : $"{milliseconds} ms";

    private static IEnumerable<string> DescribeVerdict(
        DesktopStallVerdict verdict,
        int desktopFolderMs,
        int controlMs,
        int crabDeskMs)
    {
        yield return verdict switch
        {
            DesktopStallVerdict.ThirdPartyShellExtensions =>
                $"结论：桌面目录（{desktopFolderMs} ms）明显慢于普通目录（{controlMs} ms），" +
                $"而 CrabDesk 自身在同一时刻仍能响应（{crabDeskMs} ms）。" +
                "卡顿来自桌面命名空间注册的第三方 Shell 扩展，CrabDesk 未受影响。",
            DesktopStallVerdict.CrabDeskBlocked =>
                $"结论：CrabDesk 与资源管理器同时卡住（桌面 {desktopFolderMs} ms，CrabDesk {crabDeskMs} ms）。" +
                "这说明 CrabDesk 的输入队列又被附着到了资源管理器线程上，属于本程序的问题，请反馈。",
            DesktopStallVerdict.NoStall =>
                $"结论：桌面线程响应正常（{desktopFolderMs} ms），当前没有可复现的卡顿。",
            _ =>
                "结论：测量结果不足以判定归属。可能是桌面目录与普通目录耗时接近，" +
                "或某项测量未能完成。"
        };

        if (verdict == DesktopStallVerdict.ThirdPartyShellExtensions)
        {
            yield return "建议：禁用上表中可疑的图标覆盖处理程序（如用 ShellExView），" +
                         "或退出/卸载对应软件后重新诊断。CrabDesk 不会代你修改注册表。";
        }
    }

    private static void AddExtensionNotes(List<string> notes, IReadOnlyList<ShellExtensionEntry> extensions)
    {
        if (extensions.Count == 0)
        {
            notes.Add("未读取到第三方 Shell 扩展（可能权限不足，或系统上确实没有）。");
            return;
        }

        var byProduct = DesktopStallAnalysis.CountByProduct(extensions);
        notes.Add("可疑的第三方 Shell 扩展：" +
                  string.Join("、", byProduct.Select(item => $"{item.Product}（{item.Count} 项）")));
    }
}
