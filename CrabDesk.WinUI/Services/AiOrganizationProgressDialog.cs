using System.Collections.ObjectModel;
using System.Text;
using CrabDesk.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Services;

internal sealed class AiOrganizationProgressDialog : ContentDialog
{
    private const int MaxLogEntries = 160;
    private const int MaxVisibleModelOutputLength = 128 * 1024;
    private const string TruncatedModelOutputMessage = "\r\n…模型输出过长，已停止继续显示。";

    private readonly AiOrganizationDialogRequest _request;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ObservableCollection<string> _logs = [];
    private readonly StringBuilder _modelOutput = new();
    private readonly ListView _logView;
    private readonly TextBox _modelOutputBox;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly InfoBar _outcomeInfo;
    private AiClassificationPreview? _preview;
    private AiOrganizationDialogResult? _result;
    private bool _isPreviewRunning;
    private bool _isApplying;
    private bool _cancellationRequested;
    private bool _isClosed;
    private bool _modelOutputRenderQueued;
    private bool _modelOutputTruncated;

    private AiOrganizationProgressDialog(AiOrganizationDialogRequest request)
    {
        _request = request;
        Title = "AI 整理";
        PrimaryButtonText = "确认应用";
        IsPrimaryButtonEnabled = false;
        CloseButtonText = "终止";
        DefaultButton = ContentDialogButton.Close;

        _statusText = new TextBlock
        {
            Text = "正在准备 AI 整理…",
            TextWrapping = TextWrapping.Wrap
        };
        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 1,
            Value = 0
        };
        _logView = new ListView
        {
            ItemsSource = _logs,
            MaxHeight = 140,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false
        };
        _modelOutputBox = new TextBox
        {
            Header = "模型输出（结构化 JSON）",
            Text = "正在等待模型返回…",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 140,
            MaxHeight = 220
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_modelOutputBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_modelOutputBox, ScrollBarVisibility.Disabled);
        _outcomeInfo = new InfoBar
        {
            IsOpen = false,
            IsClosable = false
        };

        var panel = new StackPanel { MinWidth = 540, Spacing = 12 };
        panel.Children.Add(_statusText);
        panel.Children.Add(_progressBar);
        panel.Children.Add(_modelOutputBox);
        panel.Children.Add(new ScrollViewer
        {
            Content = _logView,
            MaxHeight = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        panel.Children.Add(_outcomeInfo);
        Content = panel;

        Opened += OnOpened;
        PrimaryButtonClick += OnPrimaryButtonClick;
        CloseButtonClick += OnCloseButtonClick;
        Closed += OnClosed;
    }

    internal static async Task<AiOrganizationDialogResult> ShowAsync(
        XamlRoot xamlRoot,
        AiOrganizationDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(request);

        var dialog = new AiOrganizationProgressDialog(request)
        {
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
        return dialog._result ?? new AiOrganizationDialogResult(
            AiOrganizationDialogOutcome.Cancelled,
            "已取消应用 AI 整理结果。");
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        _ = RunPreviewAsync();

    private async Task RunPreviewAsync()
    {
        if (_isPreviewRunning || _isClosed)
        {
            return;
        }

        _isPreviewRunning = true;
        SetStatus("正在生成 AI 整理预览…", true);
        AppendLog("开始生成 AI 整理预览");
        var progress = new Progress<AiClassificationProgress>(UpdateProgress);
        var modelOutput = new Progress<string>(AppendModelOutput);
        try
        {
            var preview = await _request.PreviewAsync(progress, modelOutput, _cancellation.Token);
            if (_cancellationRequested)
            {
                CompleteTerminal(AiOrganizationDialogOutcome.Cancelled, "AI 整理已取消。", InfoBarSeverity.Informational);
                return;
            }

            _preview = preview;
            if (preview.Requested == 0)
            {
                CompleteTerminal(AiOrganizationDialogOutcome.NoWork, "没有需要 AI 整理的桌面图标。", InfoBarSeverity.Informational);
                return;
            }
            if (preview.Assignments.Count == 0)
            {
                CompleteTerminal(AiOrganizationDialogOutcome.NoAssignments, "AI 未返回可应用的分类结果。", InfoBarSeverity.Warning);
                return;
            }

            var summary = $"已生成预览：{preview.Assignments.Count}/{preview.Requested} 项可应用" +
                          (preview.NewBoxLabels.Count > 0 ? $"，将新建 {preview.NewBoxLabels.Count} 个盒子" : string.Empty) +
                          "。";
            AppendLog(summary + "等待确认应用。");
            SetStatus("AI 输出已完成，确认后才会改变桌面布局。", false);
            _outcomeInfo.Severity = InfoBarSeverity.Success;
            _outcomeInfo.Title = "预览已生成";
            _outcomeInfo.Message = summary;
            _outcomeInfo.IsOpen = true;
            CloseButtonText = "取消";
            DefaultButton = ContentDialogButton.Primary;
            IsPrimaryButtonEnabled = true;
        }
        catch (OperationCanceledException) when (_cancellationRequested || _cancellation.IsCancellationRequested)
        {
            CompleteTerminal(AiOrganizationDialogOutcome.Cancelled, "AI 整理已取消。", InfoBarSeverity.Informational);
        }
        catch (Exception) when (_cancellationRequested)
        {
            CompleteTerminal(AiOrganizationDialogOutcome.Cancelled, "AI 整理已取消。", InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            var message = AiOperationMessages.ToUserMessage(exception);
            CompleteTerminal(AiOrganizationDialogOutcome.Failed, message, InfoBarSeverity.Error);
        }
        finally
        {
            _isPreviewRunning = false;
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_preview is null || _isPreviewRunning || _isApplying || _cancellationRequested)
        {
            args.Cancel = true;
            return;
        }

        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            await ApplyPreviewAsync(_preview);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task ApplyPreviewAsync(AiClassificationPreview preview)
    {
        _isApplying = true;
        IsPrimaryButtonEnabled = false;
        CloseButtonText = "正在应用…";
        SetStatus("正在应用已确认的整理结果…", true);
        AppendLog("已确认应用，正在更新桌面布局");
        try
        {
            var result = await _request.ApplyAsync(preview, _cancellation.Token);
            if (_cancellationRequested)
            {
                CompleteTerminal(AiOrganizationDialogOutcome.Cancelled, "AI 整理已取消。", InfoBarSeverity.Informational);
                return;
            }

            var message = DescribeApplyResult(result);
            AppendLog(message);
            _result = new AiOrganizationDialogResult(AiOrganizationDialogOutcome.Applied, message, result);
            _outcomeInfo.Severity = InfoBarSeverity.Success;
            _outcomeInfo.Title = "AI 整理完成";
            _outcomeInfo.Message = message;
            _outcomeInfo.IsOpen = true;
            SetStatus("整理结果已应用。", false);
            Hide();
        }
        catch (OperationCanceledException) when (_cancellationRequested || _cancellation.IsCancellationRequested)
        {
            CompleteTerminal(AiOrganizationDialogOutcome.Cancelled, "AI 整理已取消。", InfoBarSeverity.Informational);
        }
        catch (Exception) when (_cancellationRequested)
        {
            CompleteTerminal(AiOrganizationDialogOutcome.Cancelled, "AI 整理已取消。", InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            var message = AiOperationMessages.ToUserMessage(exception);
            CompleteTerminal(AiOrganizationDialogOutcome.Failed, message, InfoBarSeverity.Error);
        }
        finally
        {
            _isApplying = false;
        }
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_isPreviewRunning || _isApplying)
        {
            args.Cancel = true;
            RequestCancellation();
            return;
        }

        _result ??= new AiOrganizationDialogResult(
            AiOrganizationDialogOutcome.Cancelled,
            "已取消应用 AI 整理结果。");
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _isClosed = true;
        if (_result is null)
        {
            RequestCancellation();
            _result = new AiOrganizationDialogResult(
                AiOrganizationDialogOutcome.Cancelled,
                "已取消应用 AI 整理结果。");
        }
        _cancellation.Dispose();
    }

    private void RequestCancellation()
    {
        if (_cancellationRequested)
        {
            return;
        }

        _cancellationRequested = true;
        IsPrimaryButtonEnabled = false;
        CloseButtonText = "正在终止…";
        SetStatus("正在终止 AI 整理…", true);
        AppendLog("已请求终止 AI 整理");
        _cancellation.Cancel();
        _request.Cancel();
    }

    private void UpdateProgress(AiClassificationProgress progress)
    {
        if (_isClosed || _cancellationRequested)
        {
            return;
        }

        AppendLog(progress.Message, progress.CompletedItems, progress.TotalItems);
        SetStatus(progress.Message, progress.IsIndeterminate);
        if (progress.TotalItems > 0)
        {
            _progressBar.Minimum = 0;
            _progressBar.Maximum = progress.TotalItems;
            _progressBar.Value = Math.Clamp(progress.CompletedItems, 0, progress.TotalItems);
            _progressBar.IsIndeterminate = progress.IsIndeterminate;
        }
    }

    private void AppendModelOutput(string content)
    {
        if (_isClosed || _cancellationRequested || string.IsNullOrEmpty(content) || _modelOutputTruncated)
        {
            return;
        }

        var availableLength = MaxVisibleModelOutputLength - _modelOutput.Length;
        if (availableLength <= 0)
        {
            _modelOutputTruncated = true;
            return;
        }

        if (content.Length <= availableLength)
        {
            _modelOutput.Append(content);
        }
        else
        {
            _modelOutput.Append(content, 0, availableLength);
            _modelOutput.Append(TruncatedModelOutputMessage);
            _modelOutputTruncated = true;
        }

        QueueModelOutputRender();
    }

    private void QueueModelOutputRender()
    {
        if (_modelOutputRenderQueued)
        {
            return;
        }

        _modelOutputRenderQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _modelOutputRenderQueued = false;
                if (_isClosed || _modelOutput.Length == 0)
                {
                    return;
                }

                _modelOutputBox.Text = _modelOutput.ToString();
                _modelOutputBox.Select(_modelOutputBox.Text.Length, 0);
            }))
        {
            _modelOutputRenderQueued = false;
        }
    }

    private void CompleteTerminal(
        AiOrganizationDialogOutcome outcome,
        string message,
        InfoBarSeverity severity)
    {
        _preview = null;
        _result = new AiOrganizationDialogResult(outcome, message);
        IsPrimaryButtonEnabled = false;
        CloseButtonText = "关闭";
        DefaultButton = ContentDialogButton.Close;
        SetStatus(message, false);
        AppendLog(message);
        _outcomeInfo.Severity = severity;
        _outcomeInfo.Title = outcome switch
        {
            AiOrganizationDialogOutcome.Cancelled => "已终止",
            AiOrganizationDialogOutcome.NoWork => "无需整理",
            AiOrganizationDialogOutcome.NoAssignments => "没有可应用结果",
            _ => "AI 整理失败"
        };
        _outcomeInfo.Message = message;
        _outcomeInfo.IsOpen = true;
    }

    private void SetStatus(string text, bool isIndeterminate)
    {
        _statusText.Text = text;
        _progressBar.IsIndeterminate = isIndeterminate;
    }

    private void AppendLog(string message, int completedItems = -1, int totalItems = 0)
    {
        var progress = totalItems > 0 && completedItems >= 0
            ? $"（{completedItems}/{totalItems}）"
            : string.Empty;
        var entry = $"{DateTime.Now:HH:mm:ss}  {message}{progress}";
        _logs.Add(entry);
        if (_logs.Count > MaxLogEntries)
        {
            _logs.RemoveAt(0);
        }
        _logView.ScrollIntoView(entry);
    }

    private static string DescribeApplyResult(AiClassificationApplyResult result) => result.Requested == 0
        ? "没有需要分类的桌面图标。"
        : $"已分类 {result.Applied}/{result.Requested} 项" +
          (result.CreatedBoxes > 0 ? $"，新建 {result.CreatedBoxes} 个盒子" : string.Empty) +
          (result.Unmatched > 0 ? $"，{result.Unmatched} 项未识别" : string.Empty) +
          "。";
}
