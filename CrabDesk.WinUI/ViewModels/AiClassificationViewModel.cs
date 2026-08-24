using System.Collections.ObjectModel;
using System.Diagnostics;
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

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;
    private bool _isConnectivityTestInProgress;
    private bool _hasConnectivityTestResult;
    private InfoBarSeverity _connectivityTestSeverity = InfoBarSeverity.Informational;
    private string _connectivityTestTitle = string.Empty;
    private string _connectivityTestMessage = string.Empty;

    public AiClassificationViewModel(
        ICrabDeskService service,
        IInfoBarService notifications,
        IDialogService dialogs)
    {
        _service = service;
        _notifications = notifications;
        _dialogs = dialogs;
        _dispatcherQueue = GetCurrentDispatcherQueueOrNull();
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
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            SaveSettings();
            var result = await _dialogs.RunAiOrganizationAsync(new AiOrganizationDialogRequest(
                (progress, modelOutput, token) => _service.PreviewAiClassificationAsync(progress, token, modelOutput),
                (preview, token) => _service.ApplyAiClassificationPreviewAsync(preview, token),
                _service.CancelAiOrganization));
            Status = result.Message;
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

    [RelayCommand(CanExecute = nameof(CanRunAiOperation))]
    private async Task TestConnectivityAsync()
    {
        if (IsBusy) return;
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
            SetConnectivityTestResult(
                InfoBarSeverity.Error,
                "模型连接失败",
                Status);
        }
        finally
        {
            IsConnectivityTestInProgress = false;
            IsBusy = false;
        }
    }

    private bool CanRunAiOperation() => !IsBusy && !_service.IsAiOrganizationRunning;

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
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshAiCommandState();
    }

    private void SetConnectivityTestResult(
        InfoBarSeverity severity,
        string title,
        string message)
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

    private static DispatcherQueue? GetCurrentDispatcherQueueOrNull()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 单元测试没有初始化 WinUI 运行时；此时命令状态直接在测试线程刷新即可。
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
}
