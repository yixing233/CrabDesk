using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;

namespace CrabDesk.WinUI.ViewModels;

public partial class BoxesViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _pickers;

    [ObservableProperty] private DesktopBox? _selectedBox;
    [ObservableProperty] private string _status = string.Empty;

    public BoxesViewModel(ICrabDeskService service, IDialogService dialogs, IFilePickerService pickers)
    {
        _service = service;
        _dialogs = dialogs;
        _pickers = pickers;
        _service.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<DesktopBox> Boxes { get; } = [];
    public bool HasSelection => SelectedBox is not null;
    public bool IsMappedFolder => SelectedBox?.IsMappedFolder == true;
    public string BoxTypeText => IsMappedFolder ? "映射文件夹" : "普通盒子";
    public string MonitorId => SelectedBox?.MonitorId ?? string.Empty;
    public string Title
    {
        get => SelectedBox?.Title ?? string.Empty;
        set
        {
            if (SelectedBox is null || string.IsNullOrWhiteSpace(value) || value == SelectedBox.Title) return;
            SelectedBox.Title = value.Trim();
            _service.BoxChanged(SelectedBox, true);
            OnPropertyChanged();
        }
    }
    public string MappedFolderPath => SelectedBox?.MappedFolder?.Path ?? string.Empty;
    public bool MappedFolderReadOnly
    {
        get => SelectedBox?.MappedFolder?.IsReadOnly == true;
        set { if (SelectedBox?.IsMappedFolder == true && value != MappedFolderReadOnly) _service.SetMappedFolderReadOnly(SelectedBox, value); }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var title = await _dialogs.PromptAsync("创建盒子", "盒子名称", "新盒子");
        if (title is null) return;
        SelectedBox = _service.AddBox(title);
    }

    [RelayCommand]
    private async Task AddMappedAsync()
    {
        var path = await _pickers.PickFolderAsync();
        if (path is null) return;
        SelectedBox = await _service.AddMappedFolderBoxAsync(path);
    }

    [RelayCommand]
    private async Task ChangeMappedAsync()
    {
        if (SelectedBox?.IsMappedFolder != true) return;
        var path = await _pickers.PickFolderAsync();
        if (path is null) return;
        await _service.UpdateMappedFolderAsync(SelectedBox, path);
        Status = "映射文件夹已更新";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedBox))]
    private async Task DeleteAsync()
    {
        if (SelectedBox is null || !await _dialogs.ConfirmAsync("删除盒子", $"删除“{SelectedBox.Title}”？盒子内文件不会被删除。", "删除")) return;
        _service.DeleteBox(SelectedBox);
    }

    partial void OnSelectedBoxChanged(DesktopBox? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsMappedFolder));
        OnPropertyChanged(nameof(BoxTypeText));
        OnPropertyChanged(nameof(MonitorId));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(MappedFolderPath));
        OnPropertyChanged(nameof(MappedFolderReadOnly));
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelectedBox() => SelectedBox is not null;

    private void Refresh()
    {
        var selectedId = SelectedBox?.Id;
        Boxes.Clear();
        foreach (var box in _service.Boxes) Boxes.Add(box);
        SelectedBox = Boxes.FirstOrDefault(box => box.Id == selectedId) ?? Boxes.FirstOrDefault();
        OnPropertyChanged(string.Empty);
    }
}
