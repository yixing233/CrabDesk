using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.ViewModels;

public partial class AiClassificationViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IInfoBarService _notifications;
    private readonly IDialogService _dialogs;
    private readonly DispatcherQueue? _dispatcherQueue;
    private string _baseUrl;
    private string _apiKey;
    private string _model;
    private string _categoryLabels;
    private string _customPrompt;
    private bool _reassignExistingItems;
    private CancellationTokenSource? _classificationCancellation;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;
    private bool _isClassificationInProgress;
    private int _progressCompleted;
    private int _progressTotal;
    private bool _isProgressIndeterminate;
    private string _progressText = string.Empty;

    public AiClassificationViewModel(
        ICrabDeskService service,
        IInfoBarService notifications,
        IDialogService dialogs)
    {
        _service = service;
        _notifications = notifications;
        _dialogs = dialogs;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _service.Changed += OnServiceChanged;
        var settings = service.State.Settings.AiClassification;
        _baseUrl = settings.BaseUrl;
        _apiKey = settings.ApiKey;
        _model = settings.Model;
        _categoryLabels = settings.CategoryLabels;
        _customPrompt = settings.CustomPrompt;
        _reassignExistingItems = settings.ReassignExistingItems;
        if (!string.IsNullOrWhiteSpace(_model))
        {
            Models.Add(_model);
        }
    }

    public ObservableCollection<string> Models { get; } = [];

    public bool IsClassificationInProgress
    {
        get => _isClassificationInProgress;
        private set => SetProperty(ref _isClassificationInProgress, value);
    }

    public int ProgressCompleted
    {
        get => _progressCompleted;
        private set => SetProperty(ref _progressCompleted, value);
    }

    public int ProgressTotal
    {
        get => _progressTotal;
        private set => SetProperty(ref _progressTotal, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set { if (SetProperty(ref _baseUrl, value)) SaveSettings(); }
    }

    public string ApiKey
    {
        get => _apiKey;
        set { if (SetProperty(ref _apiKey, value)) SaveSettings(); }
    }

    public string Model
    {
        get => _model;
        set { if (SetProperty(ref _model, value)) SaveSettings(); }
    }

    public string CategoryLabels
    {
        get => _categoryLabels;
        set { if (SetProperty(ref _categoryLabels, value)) SaveSettings(); }
    }

    public string CustomPrompt
    {
        get => _customPrompt;
        set { if (SetProperty(ref _customPrompt, value)) SaveSettings(); }
    }

    public bool ReassignExistingItems
    {
        get => _reassignExistingItems;
        set { if (SetProperty(ref _reassignExistingItems, value)) SaveSettings(); }
    }

    [RelayCommand(CanExecute = nameof(CanRunAiOperation))]
    private async Task LoadModelsAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            SaveSettings();
            var models = await _service.GetAiModelsAsync();
            Models.Clear();
            foreach (var model in models)
            {
                Models.Add(model);
            }
            if (string.IsNullOrWhiteSpace(Model) || !Models.Contains(Model, StringComparer.OrdinalIgnoreCase))
            {
                Model = Models.FirstOrDefault() ?? string.Empty;
            }
            Status = $"已获取 {Models.Count} 个模型";
            _notifications.Show(Status, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Status = AiOperationMessages.ToUserMessage(exception);
            _notifications.Show(Status, InfoBarSeverity.Error, TimeSpan.FromSeconds(6));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunAiOperation))]
    private async Task ClassifyAsync()
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource();
        _classificationCancellation = cancellation;
        try
        {
            IsBusy = true;
            IsClassificationInProgress = true;
            ResetClassificationProgress();
            SaveSettings();
            var progress = new Progress<AiClassificationProgress>(UpdateClassificationProgress);
            var preview = await _service.PreviewAiClassificationAsync(
                progress,
                cancellationToken: cancellation.Token);
            ProgressCompleted = preview.Requested;
            ProgressTotal = preview.Requested;
            IsProgressIndeterminate = false;
            if (preview.Requested == 0)
            {
                Status = "没有需要分类的桌面图标";
                ProgressText = Status;
                _notifications.Show(Status, InfoBarSeverity.Success, TimeSpan.FromSeconds(6));
                return;
            }
            if (preview.Assignments.Count == 0)
            {
                Status = "AI 未返回可应用的分类结果";
                ProgressText = Status;
                _notifications.Show(Status, InfoBarSeverity.Warning, TimeSpan.FromSeconds(6));
                return;
            }

            ProgressText = $"预览已生成：{preview.Assignments.Count}/{preview.Requested} 项可应用，等待确认";
            var confirmed = await _dialogs.ConfirmAsync(
                "确认 AI 整理",
                $"将整理 {preview.Assignments.Count}/{preview.Requested} 个图标，" +
                (preview.NewBoxLabels.Count > 0
                    ? $"并新建 {preview.NewBoxLabels.Count} 个盒子。"
                    : "不会新建盒子。") +
                "文件不会移动或删除。",
                "应用整理");
            if (!confirmed)
            {
                Status = "已取消应用 AI 整理结果";
                ProgressText = Status;
                return;
            }

            IsProgressIndeterminate = true;
            ProgressText = "正在应用已确认的整理结果";
            var result = await _service.ApplyAiClassificationPreviewAsync(preview, cancellation.Token);
            Status = result.Requested == 0
                ? "没有需要分类的桌面图标"
                : $"已分类 {result.Applied}/{result.Requested} 项" +
                  (result.CreatedBoxes > 0 ? $"，新建 {result.CreatedBoxes} 个盒子" : string.Empty) +
                  (result.Unmatched > 0 ? $"，{result.Unmatched} 项未识别" : string.Empty);
            ProgressCompleted = result.Requested;
            ProgressTotal = result.Requested;
            IsProgressIndeterminate = false;
            ProgressText = Status;
            _notifications.Show(
                Status,
                result.Applied > 0 || result.Requested == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                TimeSpan.FromSeconds(6));
        }
        catch (OperationCanceledException)
        {
            Status = AiOperationMessages.ToUserMessage(new OperationCanceledException());
            IsProgressIndeterminate = false;
            ProgressText = Status;
            _notifications.Show(Status, InfoBarSeverity.Informational, TimeSpan.FromSeconds(6));
        }
        catch (Exception exception)
        {
            Status = AiOperationMessages.ToUserMessage(exception);
            IsProgressIndeterminate = false;
            ProgressText = Status;
            _notifications.Show(Status, InfoBarSeverity.Error, TimeSpan.FromSeconds(8));
        }
        finally
        {
            if (ReferenceEquals(_classificationCancellation, cancellation))
            {
                _classificationCancellation = null;
            }
            IsClassificationInProgress = false;
            IsProgressIndeterminate = false;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunAiOperation))]
    private async Task TestConnectivityAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            SaveSettings();
            await _service.TestAiModelConnectivityAsync();
            Status = "模型连通性测试成功。";
            _notifications.Show(Status, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Status = AiOperationMessages.ToUserMessage(exception);
            _notifications.Show(Status, InfoBarSeverity.Error, TimeSpan.FromSeconds(8));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelClassification))]
    private void CancelClassification()
    {
        _classificationCancellation?.Cancel();
        _service.CancelAiOrganization();
    }

    private bool CanRunAiOperation() => !IsBusy && !_service.IsAiOrganizationRunning;

    private bool CanCancelClassification() => _classificationCancellation is not null;

    private void OnServiceChanged(object? sender, EventArgs eventArgs)
    {
        if (_dispatcherQueue is { HasThreadAccess: false })
        {
            _dispatcherQueue.TryEnqueue(RefreshAiCommandState);
            return;
        }
        RefreshAiCommandState();
    }

    private void RefreshAiCommandState()
    {
        LoadModelsCommand.NotifyCanExecuteChanged();
        ClassifyCommand.NotifyCanExecuteChanged();
        TestConnectivityCommand.NotifyCanExecuteChanged();
        CancelClassificationCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshAiCommandState();
    }

    private void ResetClassificationProgress()
    {
        ProgressCompleted = 0;
        ProgressTotal = 0;
        IsProgressIndeterminate = true;
        ProgressText = "正在准备 AI 分类";
    }

    private void UpdateClassificationProgress(AiClassificationProgress progress)
    {
        ProgressCompleted = progress.CompletedItems;
        ProgressTotal = progress.TotalItems;
        IsProgressIndeterminate = progress.IsIndeterminate;
        ProgressText = progress.TotalItems == 0
            ? progress.Message
            : $"{progress.Message}（已处理 {progress.CompletedItems}/{progress.TotalItems}，" +
              $"第 {progress.CompletedBatches}/{progress.TotalBatches} 批）";
    }

    private void SaveSettings() => _service.ConfigureAiClassification(
        BaseUrl,
        ApiKey,
        Model,
        CategoryLabels,
        CustomPrompt,
        ReassignExistingItems);
}
