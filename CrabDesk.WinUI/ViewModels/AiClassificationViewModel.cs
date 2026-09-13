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
    // Conversation flow cap: keeps the ListView bounded for long sessions.
    private const int MaxConversationMessages = 200;
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
    private bool _hasModelsNotice;
    private InfoBarSeverity _modelsNoticeSeverity = InfoBarSeverity.Informational;
    private string _modelsNoticeTitle = string.Empty;
    private string _modelsNoticeMessage = string.Empty;
    private string _connectivityTestTitle = string.Empty;
    private string _connectivityTestMessage = string.Empty;

    private bool _isBusy;
    private string _status = "准备就绪";
    private int _completedItems;
    private int _totalItems;
    private bool _isProgressIndeterminate;
    private string _structuredOutput = "尚未生成分类结果。";
    private bool _isJsonViewMode;
    private TimeSpan _totalDuration;
    private TimeSpan? _firstTokenLatency;
    private int? _inputTokens;
    private int? _outputTokens;
    private int? _totalTokens;
    private bool _hasPreview;
    private bool _disposed;
    private long _reasoningCharacters;
    private long _contentCharacters;
    private int _activeWebSearchIndex = -1;

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
    public ObservableCollection<AiConversationMessageViewModel> Conversation { get; } = [];

    private AiConversationMessageViewModel? _liveMessage;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanApplyPreview));
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

    public string StructuredOutput
    {
        get => _structuredOutput;
        private set => SetProperty(ref _structuredOutput, value);
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

    public bool HasModelsNotice
    {
        get => _hasModelsNotice;
        private set => SetProperty(ref _hasModelsNotice, value);
    }

    public InfoBarSeverity ModelsNoticeSeverity
    {
        get => _modelsNoticeSeverity;
        private set => SetProperty(ref _modelsNoticeSeverity, value);
    }

    public string ModelsNoticeTitle
    {
        get => _modelsNoticeTitle;
        private set => SetProperty(ref _modelsNoticeTitle, value);
    }

    public string ModelsNoticeMessage
    {
        get => _modelsNoticeMessage;
        private set => SetProperty(ref _modelsNoticeMessage, value);
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
    private async Task CopyMessageThinkingAsync(AiConversationMessageViewModel? message)
    {
        if (message is null || string.IsNullOrWhiteSpace(message.ThinkingText))
        {
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(message.ThinkingText);
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
    private async Task CopyMessageResultJsonAsync(AiConversationMessageViewModel? message)
    {
        if (message is null || string.IsNullOrWhiteSpace(message.ResultJsonText))
        {
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(message.ResultJsonText);
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
            SetModelsNotice(
                InfoBarSeverity.Success,
                "模型列表已更新",
                $"已获取 {Models.Count} 个模型，可在下拉框中选择。");
        }
        catch (Exception exception)
        {
            Status = AiOperationMessages.ToUserMessage(exception);
            SetModelsNotice(InfoBarSeverity.Error, "获取模型失败", Status);
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
            StartRunConversation(SelectedItemCount);
            _operationStopwatch.Restart();
            runCancellation = new CancellationTokenSource();
            _runCancellation = runCancellation;
            _streamFlushTimer?.Start();
            var progress = new Progress<AiClassificationProgress>(UpdateProgress);
            var modelStream = new DirectProgress<AiClassificationModelStreamUpdate>(AppendModelStream);
            var usage = new Progress<AiClassificationUsageProgress>(UpdateUsage);
            var transport = new Progress<AiClassificationTransportProgress>(AppendTransportProgress);
            var webSearch = new Progress<AiWebSearchProgress>(AppendWebSearchProgress);
            var preview = await _service.PreviewAiClassificationAsync(
                _workspaceRevision,
                selectedKeys,
                progress,
                runCancellation.Token,
                modelOutput: null,
                modelStream: modelStream,
                usageProgress: usage,
                transportProgress: transport,
                webSearchProgress: webSearch);
            FlushModelStream();
            if (runCancellation.IsCancellationRequested)
            {
                Status = "AI 整理已取消。";
                FinishLiveMessage(Status, isError: false);
                return;
            }

            _preview = preview;
            ApplyPreviewToWorkspace(preview);
            HasPreview = true;
            Status = preview.Assignments.Count == 0
                ? "AI 未能确定分类，请为待确认项目选择标签后应用。"
                : $"已生成预览：{preview.Assignments.Count}/{preview.Requested} 项获得 AI 分类。";
            FinishLiveMessage(Status, isError: false);
        }
        catch (OperationCanceledException) when (runCancellation?.IsCancellationRequested == true)
        {
            Status = "AI 整理已取消。";
            FinishLiveMessage(Status, isError: false);
        }
        catch (Exception exception)
        {
            Status = AiOperationMessages.ToUserMessage(exception);
            _notifications.Show(Status, InfoBarSeverity.Error, TimeSpan.FromSeconds(8));
            FinishLiveMessage(Status, isError: true);
        }
        finally
        {
            _streamFlushTimer?.Stop();
            UpdateTotalDuration();
            _operationStopwatch.Stop();
            FlushModelStream();
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
            AppendConversationMessage(AiConversationRole.Assistant, Status);
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
        AppendConversationMessage(AiConversationRole.System, "已请求终止 AI 整理");
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
            Status = WorkspaceItems.Count == 0
                ? "没有可整理的桌面图标。"
                : $"已载入 {WorkspaceItems.Count} 个桌面图标，默认全部选中。";
            AppendConversationMessage(AiConversationRole.System, Status);
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

    private void ResetRunState()
    {
        _streamAccumulator.Clear();
        StructuredOutput = "正在等待分类结果…";
        ResetUsageMetrics();
        _activeWebSearchIndex = -1;
        _reasoningCharacters = 0;
        _contentCharacters = 0;
        CompletedItems = 0;
        TotalItems = 0;
        IsProgressIndeterminate = true;
    }

    private void StartRunConversation(int itemCount)
    {
        AppendConversationMessage(AiConversationRole.User, $"开始 AI 分类（{itemCount} 项）");
        var live = new AiConversationMessageViewModel(AiConversationRole.Assistant, "正在准备 AI 分类…")
        {
            IsRunning = true,
            IsThinkingExpanded = true
        };
        Conversation.Add(live);
        _liveMessage = live;
        ResetRunState();
        Status = "正在准备 AI 分类…";
    }

    private void FinishLiveMessage(string text, bool isError)
    {
        if (_liveMessage is not { } message)
        {
            return;
        }

        message.Text = text;
        message.IsError = isError;
        message.IsRunning = false;
        message.IsWebSearchActive = false;
        _liveMessage = null;
    }

    private void AppendConversationMessage(AiConversationRole role, string text)
    {
        Conversation.Add(new AiConversationMessageViewModel(role, text));
        if (Conversation.Count > MaxConversationMessages)
        {
            Conversation.RemoveAt(0);
        }
    }

    private void UpdateProgress(AiClassificationProgress progress)
    {
        CompletedItems = progress.CompletedItems;
        TotalItems = progress.TotalItems;
        IsProgressIndeterminate = progress.IsIndeterminate;
        Status = progress.Message;
        if (_liveMessage is { } live)
        {
            live.Text = progress.Message;
        }
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressValue));
    }

    private void AppendModelStream(AiClassificationModelStreamUpdate update)
    {
        var characters = (long)update.Text.Length;
        if (update.Kind == AiClassificationModelStreamKind.Reasoning)
        {
            Interlocked.Add(ref _reasoningCharacters, characters);
        }
        else
        {
            Interlocked.Add(ref _contentCharacters, characters);
        }
        _streamAccumulator.Append(update);
        if (_streamFlushTimer is null)
        {
            FlushModelStream();
        }
    }

    private void AppendTransportProgress(AiClassificationTransportProgress progress)
    {
        var text = $"第 {progress.Attempt}/{progress.TotalAttempts} 次模型请求 · " +
                   (progress.IsStreaming ? "流式" : "非流式") +
                   (progress.IsCompatibilityFallback ? " · 兼容模式回退" : string.Empty);
        if (_liveMessage is { } live)
        {
            live.TransportText = text;
        }
    }

    private void AppendWebSearchProgress(AiWebSearchProgress progress)
    {
        if (_liveMessage is not { } live)
        {
            return;
        }
        switch (progress.Phase)
        {
            case AiWebSearchPhase.Started:
                _activeWebSearchIndex = live.ToolCalls.Count;
                live.ToolCalls.Add(new AiToolCallViewModel
                {
                    Icon = "Globe",
                    Title = $"联网检索 {progress.CandidateCount} 个待确认项目",
                    Detail = "Tavily 搜索中…",
                    TimeText = DateTime.Now.ToString("HH:mm:ss"),
                    IsRunning = true
                });
                live.IsWebSearchActive = true;
                break;
            case AiWebSearchPhase.Completed:
                live.IsWebSearchActive = false;
                CompleteToolCall(live, _activeWebSearchIndex,
                    progress.EvidenceCount > 0
                        ? $"返回 {progress.EvidenceCount} 条辅助证据，已用于二次分类" +
                          (string.IsNullOrWhiteSpace(progress.Message) ? string.Empty : $" · 耗时 {progress.Message}")
                        : "没有检索到辅助证据",
                    isError: false);
                break;
            case AiWebSearchPhase.Failed:
                live.IsWebSearchActive = false;
                CompleteToolCall(live, _activeWebSearchIndex,
                    "联网检索失败，已跳过辅助识别",
                    isError: true);
                break;
        }
    }

    private void CompleteToolCall(AiConversationMessageViewModel message, int index, string detail, bool isError)
    {
        if (index < 0 || index >= message.ToolCalls.Count)
        {
            return;
        }
        var entry = message.ToolCalls[index];
        entry.Detail = detail;
        entry.IsError = isError;
        entry.IsRunning = false;
    }

    private void FlushModelStream()
    {
        if (!_streamAccumulator.TryFlush(out var snapshot))
        {
            return;
        }

        var reasoning = snapshot.Reasoning ?? string.Empty;
        var structured = snapshot.StructuredOutput ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(structured))
        {
            StructuredOutput = structured;
        }

        if (_liveMessage is not { } live)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            live.ThinkingText = reasoning;
        }
        live.ThinkingStatsText = _reasoningCharacters > 0
            ? $"已思考 {Interlocked.Read(ref _reasoningCharacters):N0} 字" +
              (_contentCharacters > 0 ? $" · 输出 {Interlocked.Read(ref _contentCharacters):N0} 字" : string.Empty)
            : string.Empty;
        var receivedItems = CountOccurrences(structured, "\"id\"");
        live.ParsedText = receivedItems > 0 ? $"已识别 {receivedItems} 项" : string.Empty;
        live.ResultJsonText = structured;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private void UpdateUsage(AiClassificationUsageProgress usage)
    {
        _firstTokenLatency = usage.FirstTokenLatency;
        _inputTokens = usage.InputTokens;
        _outputTokens = usage.OutputTokens;
        _totalTokens = usage.TotalTokens;
        UpdateLiveMetrics();
    }

    private void ResetUsageMetrics()
    {
        _operationStopwatch.Reset();
        _totalDuration = TimeSpan.Zero;
        _firstTokenLatency = null;
        _inputTokens = null;
        _outputTokens = null;
        _totalTokens = null;
        UpdateLiveMetrics();
    }

    private void UpdateTotalDuration()
    {
        if (!_operationStopwatch.IsRunning)
        {
            return;
        }

        _totalDuration = _operationStopwatch.Elapsed;
        UpdateLiveMetrics();
    }

    private void UpdateLiveMetrics()
    {
        if (_liveMessage is not { } live)
        {
            return;
        }

        var metrics = $"总耗时 {FormatDuration(_totalDuration)} · 首字 {FormatDurationOrDash(_firstTokenLatency)}" +
                      $" · 输入 {FormatTokens(_inputTokens)} · 输出 {FormatTokens(_outputTokens)} · 总计 {FormatTokens(_totalTokens)}";
        live.MetricsText = metrics;
    }

    private static string FormatDurationOrDash(TimeSpan? value) => value is { } v ? FormatDuration(v) : "—";

    private static string FormatDuration(TimeSpan value) => value.TotalMinutes >= 1
        ? $"{(int)value.TotalMinutes} 分 {value.Seconds:D2} 秒"
        : $"{value.TotalSeconds:F1} 秒";

    private static string FormatTokens(int? value) => value?.ToString("N0") ?? "—";

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

    private void SetModelsNotice(InfoBarSeverity severity, string title, string message)
    {
        ModelsNoticeSeverity = severity;
        ModelsNoticeTitle = title;
        ModelsNoticeMessage = message;
        HasModelsNotice = true;
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
