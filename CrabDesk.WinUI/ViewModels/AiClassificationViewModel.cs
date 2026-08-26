using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.ViewModels;

public partial class AiClassificationViewModel : ObservableObject, IDisposable
{
    private const int MaxLogEntries = 160;
    // The activity tab renders these strings in WinUI text controls. Keep the
    // visible transcript bounded and update it at a human-readable cadence so
    // a verbose model response cannot monopolize the UI thread.
    private const int ModelStreamRefreshIntervalMilliseconds = 250;
    private const int MaximumVisibleReasoningLength = 8 * 1024;
    private const int MaximumVisibleStructuredOutputLength = 24 * 1024;
    private readonly ICrabDeskService _service;
    private readonly IInfoBarService _notifications;
    private readonly IDialogService _dialogs;
    private readonly DesktopItemIconSourceFactory? _iconSourceFactory;
    private readonly IClipboardService _clipboard;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly AiClassificationStreamAccumulator _streamAccumulator = new(
        MaximumVisibleReasoningLength,
        MaximumVisibleStructuredOutputLength);
    private readonly Stopwatch _operationStopwatch = new();
    private readonly DispatcherQueueTimer? _streamFlushTimer;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _workspaceIconCancellation;
    private AiClassificationPreview? _preview;
    private long _workspaceRevision;
    private string _baseUrl;
    private string _apiKey;
    private string _model;
    private string _categoryLabels = string.Empty;
    private string _customPrompt;
    private bool _reassignExistingItems;
    private bool _webSearchEnabled;
    private string _webSearchApiKey;
    private bool _isConnectivityTestInProgress;
    private bool _hasConnectivityTestResult;
    private InfoBarSeverity _connectivityTestSeverity = InfoBarSeverity.Informational;
    private string _connectivityTestTitle = string.Empty;
    private string _connectivityTestMessage = string.Empty;

    private bool _isBusy;
    private string _status = "准备就绪";
    private int _completedItems;
    private int _totalItems;
    private bool _isProgressIndeterminate;
    private string _reasoningOutput = "尚未开始 AI 分类。";
    private string _structuredOutput = "尚未生成分类结果。";
    private bool _isThinkingExpanded;
    private bool _isJsonViewMode;
    private TimeSpan _totalDuration;
    private TimeSpan? _firstTokenLatency;
    private int? _inputTokens;
    private int? _outputTokens;
    private int? _totalTokens;
    private int _selectedInspectorTabIndex;
    private bool _hasPreview;
    private bool _disposed;

    public AiClassificationViewModel(
        ICrabDeskService service,
        IInfoBarService notifications,
        IDialogService dialogs,
        DesktopItemIconSourceFactory? iconSourceFactory = null,
        IClipboardService? clipboard = null)
    {
        _service = service;
        _notifications = notifications;
        _dialogs = dialogs;
        _iconSourceFactory = iconSourceFactory;
        _clipboard = clipboard ?? new ClipboardService();
        _dispatcherQueue = GetCurrentDispatcherQueueOrNull();
        if (_dispatcherQueue is not null)
        {
            _streamFlushTimer = _dispatcherQueue.CreateTimer();
            _streamFlushTimer.Interval = TimeSpan.FromMilliseconds(ModelStreamRefreshIntervalMilliseconds);
            _streamFlushTimer.Tick += (_, _) =>
            {
                UpdateTotalDuration();
                FlushModelStream();
            };
        }

        _service.Changed += OnServiceChanged;
        var settings = service.State.Settings.AiClassification;
        _baseUrl = settings.BaseUrl;
        _apiKey = settings.ApiKey;
        _model = settings.Model;
        SetCategoryLabels(settings.CategoryLabels, save: false);
        _customPrompt = settings.CustomPrompt;
        _reassignExistingItems = settings.ReassignExistingItems;
        _webSearchEnabled = settings.WebSearchEnabled;
        _webSearchApiKey = settings.WebSearchApiKey;
        if (!string.IsNullOrWhiteSpace(_model))
        {
            Models.Add(_model);
        }

        ReloadWorkspaceCore(clearPreview: true);
    }

    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<string> CategoryTags { get; } = [];
    public ObservableCollection<AiWorkbenchItemViewModel> WorkspaceItems { get; } = [];
    public ObservableCollection<AiClassificationGroupViewModel> ResultGroups { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanApplyPreview));
                OnPropertyChanged(nameof(ThinkingHeaderText));
                RefreshAiCommandState();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public int CompletedItems
    {
        get => _completedItems;
        private set => SetProperty(ref _completedItems, value);
    }

    public int TotalItems
    {
        get => _totalItems;
        private set => SetProperty(ref _totalItems, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public string ReasoningOutput
    {
        get => _reasoningOutput;
        private set => SetProperty(ref _reasoningOutput, value);
    }

    public string StructuredOutput
    {
        get => _structuredOutput;
        private set => SetProperty(ref _structuredOutput, value);
    }

    public bool IsThinkingExpanded
    {
        get => _isThinkingExpanded;
        set => SetProperty(ref _isThinkingExpanded, value);
    }

    public int SelectedInspectorTabIndex
    {
        get => _selectedInspectorTabIndex;
        set => SetProperty(ref _selectedInspectorTabIndex, value);
    }

    public bool HasPreview
    {
        get => _hasPreview;
        private set
        {
            if (SetProperty(ref _hasPreview, value))
            {
                OnPropertyChanged(nameof(CanApplyPreview));
                UpdateResultGroups();
                RefreshAiCommandState();
            }
        }
    }

    public int SelectedItemCount => WorkspaceItems.Count(item => item.IsSelected);
    public string SelectedItemSummary => $"已选择 {SelectedItemCount} 项";
    public int EffectiveAssignmentCount => WorkspaceItems.Count(item =>
        item.IsSelected && !string.IsNullOrWhiteSpace(item.EffectiveLabel));
    public bool IsWorkspaceEmpty => WorkspaceItems.Count == 0;
    public bool CanApplyPreview => !IsBusy && HasPreview && EffectiveAssignmentCount > 0;
    public bool HasProgress => TotalItems > 0;
    public double ProgressValue => TotalItems == 0 ? 0 : Math.Clamp((double)CompletedItems / TotalItems * 100, 0, 100);
    public string ThinkingHeaderText => IsBusy ? "正在思考…" : "思考过程";
    public string TotalDurationText => FormatDuration(_totalDuration);
    public string FirstTokenLatencyText => _firstTokenLatency is { } value ? FormatDuration(value) : "—";
    public string InputTokenText => FormatTokens(_inputTokens);
    public string OutputTokenText => FormatTokens(_outputTokens);
    public string TotalTokenText => FormatTokens(_totalTokens);
    public bool HasResultGroups => ResultGroups.Count > 0;
    public string ResultSummaryText => !HasPreview
        ? "尚未生成分类结果"
        : $"已识别 {EffectiveAssignmentCount}/{SelectedItemCount} 项" + (WorkspaceItems.Count(item => item.IsSelected && item.IsUncertain) is var uncertain && uncertain > 0 ? $"（{uncertain} 项待确认）" : string.Empty);

    public bool IsJsonViewMode
    {
        get => _isJsonViewMode;
        set => SetProperty(ref _isJsonViewMode, value);
    }

    public bool IsConnectivityTestInProgress
    {
        get => _isConnectivityTestInProgress;
        private set
        {
            if (SetProperty(ref _isConnectivityTestInProgress, value))
            {
                OnPropertyChanged(nameof(ConnectivityTestButtonText));
            }
        }
    }

    public bool HasConnectivityTestResult
    {
        get => _hasConnectivityTestResult;
        private set => SetProperty(ref _hasConnectivityTestResult, value);
    }

    public InfoBarSeverity ConnectivityTestSeverity
    {
        get => _connectivityTestSeverity;
        private set => SetProperty(ref _connectivityTestSeverity, value);
    }

    public string ConnectivityTestTitle
    {
        get => _connectivityTestTitle;
        private set => SetProperty(ref _connectivityTestTitle, value);
    }

    public string ConnectivityTestMessage
    {
        get => _connectivityTestMessage;
        private set => SetProperty(ref _connectivityTestMessage, value);
    }

    public string ConnectivityTestButtonText => IsConnectivityTestInProgress
        ? "正在测试…"
        : "测试模型连通性";

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetProperty(ref _baseUrl, value))
            {
                ClearConnectivityTestResult();
                SaveSettings();
            }
        }
    }

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                ClearConnectivityTestResult();
                SaveSettings();
            }
        }
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
            {
                ClearConnectivityTestResult();
                SaveSettings();
            }
        }
    }

    public string CategoryLabels
    {
        get => _categoryLabels;
        set => SetCategoryLabels(value, save: true);
    }

    public string CustomPrompt
    {
        get => _customPrompt;
        set
        {
            if (SetProperty(ref _customPrompt, value))
            {
                SaveSettings();
            }
        }
    }

    // Kept for backward-compatible settings migration. The workbench itself
    // always operates on desktop items that are not already in a box.
    public bool ReassignExistingItems
    {
        get => _reassignExistingItems;
        set
        {
            if (SetProperty(ref _reassignExistingItems, value))
            {
                SaveSettings();
            }
        }
    }

    public bool WebSearchEnabled
    {
        get => _webSearchEnabled;
        set
        {
            if (SetProperty(ref _webSearchEnabled, value))
            {
                SaveWebSearchSettings();
            }
        }
    }

    public string WebSearchApiKey
    {
        get => _webSearchApiKey;
        set
        {
            if (SetProperty(ref _webSearchApiKey, value))
            {
                SaveWebSearchSettings();
            }
        }
    }

    [RelayCommand]
    private void ShowCardView() => IsJsonViewMode = false;

    [RelayCommand]
    private void ShowJsonView() => IsJsonViewMode = true;

    [RelayCommand]
    private async Task CopyReasoningAsync()
    {
        if (string.IsNullOrWhiteSpace(ReasoningOutput))
        {
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(ReasoningOutput);
            _notifications.Show("思考过程已复制到剪贴板", InfoBarSeverity.Success, TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            AppDiagnostic.Error("Failed to copy reasoning", exception);
        }
    }

    [RelayCommand]
    private async Task CopyStructuredOutputAsync()
    {
        if (string.IsNullOrWhiteSpace(StructuredOutput))
        {
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(StructuredOutput);
            _notifications.Show("分类结果已复制到剪贴板", InfoBarSeverity.Success, TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            AppDiagnostic.Error("Failed to copy structured output", exception);
        }
    }

    [RelayCommand]
    private async Task AddCategoryTagsAsync()
    {
        var input = await _dialogs.PromptAsync("添加分类标签", "多个标签请用空格分隔");
        if (!string.IsNullOrWhiteSpace(input))
        {
            AddCategoryTags(input);
        }
    }

    [RelayCommand]
    private void RemoveCategoryTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var remaining = CategoryTags
            .Where(candidate => !string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase));
        SetCategoryLabels(string.Join("\n", remaining), save: true);
    }

    [RelayCommand(CanExecute = nameof(CanRunAiOperation))]
    private async Task LoadModelsAsync()
    {
        if (IsBusy)
        {
            return;
        }

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
        if (IsBusy || SelectedItemCount == 0)
        {
            return;
        }

        var selectedKeys = WorkspaceItems
            .Where(item => item.IsSelected)
            .Select(item => item.ItemKey)
            .ToArray();
        CancellationTokenSource? runCancellation = null;
        try
        {
            IsBusy = true;
            SaveSettings();
            ClearPreviewState();
            ResetActivity("正在准备 AI 分类…");
            _operationStopwatch.Restart();
            runCancellation = new CancellationTokenSource();
            _runCancellation = runCancellation;
            _streamFlushTimer?.Start();
            var progress = new Progress<AiClassificationProgress>(UpdateProgress);
            var modelStream = new DirectProgress<AiClassificationModelStreamUpdate>(AppendModelStream);
            var usage = new Progress<AiClassificationUsageProgress>(UpdateUsage);
            var preview = await _service.PreviewAiClassificationAsync(
                _workspaceRevision,
                selectedKeys,
                progress,
                runCancellation.Token,
                modelOutput: null,
                modelStream: modelStream,
                usageProgress: usage);
            FlushModelStream();
            if (runCancellation.IsCancellationRequested)
            {
                Status = "AI 整理已取消。";
                return;
            }

            _preview = preview;
            ApplyPreviewToWorkspace(preview);
            HasPreview = true;
            Status = preview.Assignments.Count == 0
                ? "AI 未能确定分类，请为待确认项目选择标签后应用。"
                : $"已生成预览：{preview.Assignments.Count}/{preview.Requested} 项获得 AI 分类。";
            AppendLog(Status);
        }
        catch (OperationCanceledException) when (runCancellation?.IsCancellationRequested == true)
        {
            Status = "AI 整理已取消。";
            AppendLog(Status);
        }
        catch (Exception exception)
        {
            Status = AiOperationMessages.ToUserMessage(exception);
            _notifications.Show(Status, InfoBarSeverity.Error, TimeSpan.FromSeconds(8));
            AppendLog(Status);
        }
        finally
        {
            _streamFlushTimer?.Stop();
            UpdateTotalDuration();
            _operationStopwatch.Stop();
            FlushModelStream();
            IsThinkingExpanded = false;
            if (runCancellation is not null && ReferenceEquals(_runCancellation, runCancellation))
            {
                _runCancellation = null;
            }
            runCancellation?.Dispose();
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyPreview))]
    private async Task ApplyAsync()
    {
        if (_preview is null || IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var preview = BuildPreviewWithManualAssignments(_preview);
            var result = await _service.ApplyAiClassificationPreviewAsync(preview);
            Status = DescribeApplyResult(result);
            _notifications.Show(Status, InfoBarSeverity.Success);
            ReloadWorkspaceCore(clearPreview: true);
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

    [RelayCommand(CanExecute = nameof(CanCancelAiOperation))]
    private void Cancel()
    {
        if (!IsBusy)
        {
            return;
        }

        Status = "正在终止 AI 整理…";
        AppendLog("已请求终止 AI 整理");
        _runCancellation?.Cancel();
        _service.CancelAiOrganization();
    }

    [RelayCommand(CanExecute = nameof(CanModifyWorkspace))]
    private void SelectAll()
    {
        foreach (var item in WorkspaceItems)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyWorkspace))]
    private void ClearSelection()
    {
        foreach (var item in WorkspaceItems)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyWorkspace))]
    private void ToggleWorkspaceItemSelection(AiWorkbenchItemViewModel? item)
    {
        if (item is null || !WorkspaceItems.Contains(item))
        {
            return;
        }

        item.IsSelected = !item.IsSelected;
    }

    [RelayCommand(CanExecute = nameof(CanModifyWorkspace))]
    private void RefreshWorkspace() => ReloadWorkspaceCore(clearPreview: true);

    [RelayCommand(CanExecute = nameof(CanRunAiOperation))]
    private async Task TestConnectivityAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            IsBusy = true;
            IsConnectivityTestInProgress = true;
            SetConnectivityTestResult(
                InfoBarSeverity.Informational,
                "正在测试模型连接",
                "正在发送最小测试请求，请稍候…");
            SaveSettings();
            await _service.TestAiModelConnectivityAsync();
            Status = "模型连通性测试成功。";
            SetConnectivityTestResult(
                InfoBarSeverity.Success,
                "模型连接正常",
                $"模型已成功响应（耗时 {stopwatch.Elapsed.TotalSeconds:0.0} 秒），可以开始 AI 分类。");
        }
        catch (Exception exception)
        {
            Status = AiOperationMessages.ToUserMessage(exception);
            SetConnectivityTestResult(InfoBarSeverity.Error, "模型连接失败", Status);
        }
        finally
        {
            IsConnectivityTestInProgress = false;
            IsBusy = false;
        }
    }

    private void ReloadWorkspaceCore(bool clearPreview)
    {
        if (IsBusy)
        {
            return;
        }

        var workspace = _service.GetAiClassificationWorkspace();
        if (workspace is null)
        {
            return;
        }

        _workspaceRevision = workspace.WorkspaceRevision;
        _workspaceIconCancellation?.Cancel();
        _workspaceIconCancellation?.Dispose();
        _workspaceIconCancellation = _iconSourceFactory is null ? null : new CancellationTokenSource();
        foreach (var item in WorkspaceItems)
        {
            item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
        }

        WorkspaceItems.Clear();
        var iconRequests = new List<(AiWorkbenchItemViewModel Item, string ParsingName)>();
        foreach (var source in workspace.Items)
        {
            var item = new AiWorkbenchItemViewModel
            {
                ItemKey = source.ItemKey,
                DisplayName = source.DisplayName,
                IsSelected = true
            };
            item.PropertyChanged += OnWorkspaceItemPropertyChanged;
            WorkspaceItems.Add(item);
            iconRequests.Add((item, source.ParsingName));
        }

        if (_workspaceIconCancellation is { } iconCancellation && iconRequests.Count > 0)
        {
            _ = LoadWorkspaceIconsAsync(iconRequests, iconCancellation.Token);
        }

        if (clearPreview)
        {
            ClearPreviewState();
            ResetActivity(
                WorkspaceItems.Count == 0
                    ? "没有可整理的桌面图标。"
                    : $"已载入 {WorkspaceItems.Count} 个桌面图标，默认全部选中。",
                activateActivityTab: false);
        }

        NotifyWorkbenchStateChanged();
    }

    private void ApplyPreviewToWorkspace(AiClassificationPreview preview)
    {
        HasPreview = true;
        var assignments = preview.Assignments
            .GroupBy(assignment => assignment.ItemKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var item in WorkspaceItems)
        {
            item.ResetClassification();
            if (!item.IsSelected)
            {
                continue;
            }

            item.HasPreview = true;
            if (assignments.TryGetValue(item.ItemKey, out var assignment))
            {
                item.AiLabel = assignment.Label;
            }
        }

        NotifyWorkbenchStateChanged();
    }

    private async Task LoadWorkspaceIconsAsync(
        IReadOnlyList<(AiWorkbenchItemViewModel Item, string ParsingName)> requests,
        CancellationToken cancellationToken)
    {
        if (_iconSourceFactory is null)
        {
            return;
        }

        using var gate = new SemaphoreSlim(4, 4);
        var tasks = requests.Select(async request =>
        {
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
                try
                {
                    var icon = await _iconSourceFactory.LoadAsync(request.ParsingName, cancellationToken)
                        .ConfigureAwait(false);
                    if (!cancellationToken.IsCancellationRequested && WorkspaceItems.Contains(request.Item))
                    {
                        await SetWorkspaceIconAsync(request.Item, icon, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Refreshing the workbench replaces this request set.
            }
            catch (Exception exception)
            {
                AppDiagnostic.Error("AI workbench icon load failed", exception);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private Task SetWorkspaceIconAsync(
        AiWorkbenchItemViewModel item,
        Microsoft.UI.Xaml.Media.ImageSource? icon,
        CancellationToken cancellationToken)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            if (!cancellationToken.IsCancellationRequested && WorkspaceItems.Contains(item))
            {
                item.Icon = icon;
            }

            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                if (!cancellationToken.IsCancellationRequested && WorkspaceItems.Contains(item))
                {
                    item.Icon = icon;
                }

                completion.TrySetResult();
            }))
        {
            completion.TrySetException(new InvalidOperationException("WinUI dispatcher queue is unavailable."));
        }

        return completion.Task;
    }

    private AiClassificationPreview BuildPreviewWithManualAssignments(AiClassificationPreview preview)
    {
        var manualLabels = WorkspaceItems
            .Where(item => item.IsSelected && item.IsUncertain && !string.IsNullOrWhiteSpace(item.ManualLabel))
            .ToDictionary(item => item.ItemKey, item => item.ManualLabel!, StringComparer.OrdinalIgnoreCase);
        var assignments = AiClassificationWorkbench.MergeManualAssignments(preview, manualLabels, CategoryTags);
        var names = WorkspaceItems.ToDictionary(item => item.ItemKey, item => item.DisplayName, StringComparer.OrdinalIgnoreCase);
        var namedAssignments = assignments
            .Select(assignment => assignment with
            {
                ItemName = names.GetValueOrDefault(assignment.ItemKey, assignment.ItemName)
            })
            .ToArray();
        var existingBoxes = _service.Boxes
            .Where(box => !box.IsMappedFolder)
            .Select(box => box.Title.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newBoxLabels = namedAssignments
            .Select(assignment => assignment.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(label => !existingBoxes.Contains(label))
            .ToArray();
        return preview with
        {
            Assignments = namedAssignments,
            NewBoxLabels = newBoxLabels
        };
    }

    private void ClearPreviewState()
    {
        _preview = null;
        HasPreview = false;
        foreach (var item in WorkspaceItems)
        {
            item.ResetClassification();
        }

        NotifyWorkbenchStateChanged();
    }

    private void ResetActivity(string initialStatus, bool activateActivityTab = true)
    {
        if (activateActivityTab)
        {
            SelectedInspectorTabIndex = 1;
        }
        _streamAccumulator.Clear();
        ReasoningOutput = "正在等待模型思考…";
        StructuredOutput = "正在等待分类结果…";
        IsThinkingExpanded = true;
        ResetUsageMetrics();
        ActivityLog.Clear();
        CompletedItems = 0;
        TotalItems = 0;
        IsProgressIndeterminate = true;
        Status = initialStatus;
        AppendLog(initialStatus);
    }

    private void UpdateProgress(AiClassificationProgress progress)
    {
        CompletedItems = progress.CompletedItems;
        TotalItems = progress.TotalItems;
        IsProgressIndeterminate = progress.IsIndeterminate;
        Status = progress.Message;
        AppendLog(progress.Message, progress.CompletedItems, progress.TotalItems);
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressValue));
    }

    private void AppendModelStream(AiClassificationModelStreamUpdate update)
    {
        _streamAccumulator.Append(update);
        if (_streamFlushTimer is null)
        {
            FlushModelStream();
        }
    }

    private void FlushModelStream()
    {
        if (!_streamAccumulator.TryFlush(out var snapshot))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Reasoning))
        {
            ReasoningOutput = snapshot.Reasoning;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.StructuredOutput))
        {
            StructuredOutput = snapshot.StructuredOutput;
        }
    }

    private void UpdateUsage(AiClassificationUsageProgress usage)
    {
        _firstTokenLatency = usage.FirstTokenLatency;
        _inputTokens = usage.InputTokens;
        _outputTokens = usage.OutputTokens;
        _totalTokens = usage.TotalTokens;
        NotifyUsageMetricsChanged();
    }

    private void ResetUsageMetrics()
    {
        _operationStopwatch.Reset();
        _totalDuration = TimeSpan.Zero;
        _firstTokenLatency = null;
        _inputTokens = null;
        _outputTokens = null;
        _totalTokens = null;
        NotifyUsageMetricsChanged();
    }

    private void UpdateTotalDuration()
    {
        if (!_operationStopwatch.IsRunning)
        {
            return;
        }

        _totalDuration = _operationStopwatch.Elapsed;
        OnPropertyChanged(nameof(TotalDurationText));
    }

    private void NotifyUsageMetricsChanged()
    {
        OnPropertyChanged(nameof(TotalDurationText));
        OnPropertyChanged(nameof(FirstTokenLatencyText));
        OnPropertyChanged(nameof(InputTokenText));
        OnPropertyChanged(nameof(OutputTokenText));
        OnPropertyChanged(nameof(TotalTokenText));
    }

    private static string FormatDuration(TimeSpan value) => value.TotalMinutes >= 1
        ? $"{(int)value.TotalMinutes} 分 {value.Seconds:D2} 秒"
        : $"{value.TotalSeconds:F1} 秒";

    private static string FormatTokens(int? value) => value?.ToString("N0") ?? "—";

    private void AppendLog(string message, int completedItems = -1, int totalItems = 0)
    {
        var progress = totalItems > 0 && completedItems >= 0
            ? $"（{completedItems}/{totalItems}）"
            : string.Empty;
        ActivityLog.Add($"{DateTime.Now:HH:mm:ss}  {message}{progress}");
        if (ActivityLog.Count > MaxLogEntries)
        {
            ActivityLog.RemoveAt(0);
        }
    }

    private bool CanRunAiOperation() => !IsBusy && !_service.IsAiOrganizationRunning;
    private bool CanModifyWorkspace() => !IsBusy && !_service.IsAiOrganizationRunning;
    private bool CanCancelAiOperation() => IsBusy;

    private void AddCategoryTags(string input)
    {
        var tags = CategoryTags
            .Concat(ParseCategoryTags(input))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        SetCategoryLabels(string.Join("\n", tags), save: true);
    }

    private void SetCategoryLabels(string? value, bool save)
    {
        var tags = ParseCategoryTags(value);
        var serializedTags = string.Join("\n", tags);
        var tagsChanged = !CategoryTags.SequenceEqual(tags, StringComparer.Ordinal);
        if (tagsChanged)
        {
            CategoryTags.Clear();
            foreach (var tag in tags)
            {
                CategoryTags.Add(tag);
            }
        }

        if (SetProperty(ref _categoryLabels, serializedTags) && save)
        {
            SaveSettings();
        }
    }

    private static IReadOnlyList<string> ParseCategoryTags(string? value) =>
        value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

    private void OnWorkspaceItemPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(AiWorkbenchItemViewModel.IsSelected) or
            nameof(AiWorkbenchItemViewModel.ManualLabel) or
            nameof(AiWorkbenchItemViewModel.AiLabel) or
            nameof(AiWorkbenchItemViewModel.HasPreview))
        {
            NotifyWorkbenchStateChanged();
        }
    }

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
        ApplyCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        ToggleWorkspaceItemSelectionCommand.NotifyCanExecuteChanged();
        RefreshWorkspaceCommand.NotifyCanExecuteChanged();
        TestConnectivityCommand.NotifyCanExecuteChanged();
    }

    private void NotifyWorkbenchStateChanged()
    {
        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(SelectedItemSummary));
        OnPropertyChanged(nameof(EffectiveAssignmentCount));
        OnPropertyChanged(nameof(IsWorkspaceEmpty));
        OnPropertyChanged(nameof(CanApplyPreview));
        UpdateResultGroups();
        RefreshAiCommandState();
    }

    private void UpdateResultGroups()
    {
        ResultGroups.Clear();
        if (!HasPreview)
        {
            OnPropertyChanged(nameof(HasResultGroups));
            OnPropertyChanged(nameof(ResultSummaryText));
            return;
        }

        var selectedItems = WorkspaceItems.Where(item => item.IsSelected).ToList();
        var classifiedGroups = selectedItems
            .Where(item => !string.IsNullOrWhiteSpace(item.EffectiveLabel))
            .GroupBy(item => item.EffectiveLabel!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in classifiedGroups)
        {
            var groupVm = new AiClassificationGroupViewModel
            {
                CategoryName = group.Key,
                IsUncertainGroup = false
            };
            foreach (var item in group)
            {
                groupVm.Items.Add(item);
            }
            ResultGroups.Add(groupVm);
        }

        var uncertainItems = selectedItems.Where(item => item.IsUncertain).ToList();
        if (uncertainItems.Count > 0)
        {
            var uncertainGroup = new AiClassificationGroupViewModel
            {
                CategoryName = "待确认项目",
                IsUncertainGroup = true
            };
            foreach (var item in uncertainItems)
            {
                uncertainGroup.Items.Add(item);
            }
            ResultGroups.Add(uncertainGroup);
        }

        OnPropertyChanged(nameof(HasResultGroups));
        OnPropertyChanged(nameof(ResultSummaryText));
    }

    private void SetConnectivityTestResult(InfoBarSeverity severity, string title, string message)
    {
        ConnectivityTestSeverity = severity;
        ConnectivityTestTitle = title;
        ConnectivityTestMessage = message;
        HasConnectivityTestResult = true;
    }

    private void ClearConnectivityTestResult()
    {
        HasConnectivityTestResult = false;
        ConnectivityTestTitle = string.Empty;
        ConnectivityTestMessage = string.Empty;
        ConnectivityTestSeverity = InfoBarSeverity.Informational;
    }

    private static string DescribeApplyResult(AiClassificationApplyResult result) => result.Requested == 0
        ? "没有需要分类的桌面图标。"
        : $"已分类 {result.Applied}/{result.Requested} 项" +
          (result.CreatedBoxes > 0 ? $"，新建 {result.CreatedBoxes} 个盒子" : string.Empty) +
          (result.Unmatched > 0 ? $"，{result.Unmatched} 项未识别" : string.Empty) +
          "。";

    private static DispatcherQueue? GetCurrentDispatcherQueueOrNull()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private void SaveSettings() => _service.ConfigureAiClassification(
        BaseUrl,
        ApiKey,
        Model,
        CategoryLabels,
        CustomPrompt,
        ReassignExistingItems);

    private void SaveWebSearchSettings() =>
        _service.ConfigureAiWebSearch(WebSearchEnabled, WebSearchApiKey);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.Changed -= OnServiceChanged;
        _streamFlushTimer?.Stop();
        if (_runCancellation is not null)
        {
            _runCancellation.Cancel();
            _service.CancelAiOrganization();
            _runCancellation.Dispose();
            _runCancellation = null;
        }
        _workspaceIconCancellation?.Cancel();
        _workspaceIconCancellation?.Dispose();
        _workspaceIconCancellation = null;
        foreach (var item in WorkspaceItems)
        {
            item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
        }

        GC.SuppressFinalize(this);
    }

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
