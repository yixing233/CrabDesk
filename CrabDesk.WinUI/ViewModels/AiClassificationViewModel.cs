using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.WinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.ViewModels;

public partial class AiClassificationViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IInfoBarService _notifications;
    private string _baseUrl;
    private string _apiKey;
    private string _model;
    private string _categoryLabels;
    private string _customPrompt;
    private bool _reassignExistingItems;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    public AiClassificationViewModel(ICrabDeskService service, IInfoBarService notifications)
    {
        _service = service;
        _notifications = notifications;
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

    [RelayCommand]
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
            Status = exception.Message;
            _notifications.Show(Status, InfoBarSeverity.Error, TimeSpan.FromSeconds(6));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClassifyAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            SaveSettings();
            var result = await _service.ApplyAiClassificationAsync();
            Status = result.Requested == 0
                ? "没有需要分类的桌面图标"
                : $"已分类 {result.Applied}/{result.Requested} 项" +
                  (result.CreatedBoxes > 0 ? $"，新建 {result.CreatedBoxes} 个盒子" : string.Empty) +
                  (result.Unmatched > 0 ? $"，{result.Unmatched} 项未识别" : string.Empty);
            _notifications.Show(
                Status,
                result.Applied > 0 || result.Requested == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                TimeSpan.FromSeconds(6));
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            _notifications.Show(Status, InfoBarSeverity.Error, TimeSpan.FromSeconds(8));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
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
            Status = exception.Message;
            _notifications.Show(Status, InfoBarSeverity.Error, TimeSpan.FromSeconds(8));
        }
        finally
        {
            IsBusy = false;
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
