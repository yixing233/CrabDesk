using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;

namespace CrabDesk.WinUI.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IFilePickerService _pickers;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private LayoutBackupInfo? _selectedBackup;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public BackupViewModel(ICrabDeskService service, IFilePickerService pickers, IDialogService dialogs)
    {
        _service = service;
        _pickers = pickers;
        _dialogs = dialogs;
        _ = RefreshAsync();
    }

    public ObservableCollection<LayoutBackupInfo> Backups { get; } = [];
    public bool HasBackups => Backups.Count > 0;
    public bool HasNoBackups => !HasBackups;
    public string BackupDirectory => _service.BackupDirectory;
    public bool DailyBackup
    {
        get => _service.State.Settings.Backup.DailyBackup;
        set { if (value != DailyBackup) { _service.SetDailyBackup(value); OnPropertyChanged(); } }
    }
    public double RetentionDays
    {
        get => _service.State.Settings.Backup.RetentionDays;
        set
        {
            var days = (int)Math.Round(value);
            if (days == _service.State.Settings.Backup.RetentionDays) return;
            _ = _service.SetBackupRetentionDaysAsync(days);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private async Task CreateAsync() => await RunAsync(async () =>
    {
        var backup = await _service.CreateBackupAsync();
        await RefreshAsync(backup.Path);
        Status = "备份已创建";
    });

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = await _pickers.PickSaveFileAsync($"CrabDesk-layout-{DateTime.Now:yyyyMMdd-HHmm}", ".json");
        if (path is null) return;
        await RunAsync(async () =>
        {
            await _service.ExportBackupAsync(path);
            Status = "布局已导出";
        });
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = await _pickers.PickOpenFileAsync(".json");
        if (path is null) return;
        if (!await _dialogs.ConfirmAsync("导入布局", "当前布局会被导入文件替换。", "导入")) return;
        await RunAsync(async () =>
        {
            await _service.RestoreBackupAsync(path);
            Status = "布局已导入";
            await RefreshAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RestoreAsync()
    {
        if (SelectedBackup is null || !await _dialogs.ConfirmAsync("恢复布局", "当前布局会被所选备份替换。", "恢复")) return;
        await RunAsync(async () =>
        {
            await _service.RestoreBackupAsync(SelectedBackup.Path);
            Status = "布局已恢复";
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedBackup is null || !await _dialogs.ConfirmAsync("删除备份", "所选备份文件将被永久删除。", "删除")) return;
        var path = SelectedBackup.Path;
        await RunAsync(async () =>
        {
            await _service.DeleteBackupAsync(path);
            await RefreshAsync();
            Status = "备份已删除";
        });
    }

    [RelayCommand] private async Task RefreshAsync() => await RefreshAsync(null);

    partial void OnSelectedBackupChanged(LayoutBackupInfo? value)
    {
        RestoreCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection() => SelectedBackup is not null && !IsBusy;

    private async Task RefreshAsync(string? selectedPath)
    {
        var items = await _service.GetBackupsAsync();
        Backups.Clear();
        foreach (var item in items) Backups.Add(item);
        SelectedBackup = selectedPath is null ? Backups.FirstOrDefault() : Backups.FirstOrDefault(item => item.Path == selectedPath);
        OnPropertyChanged(nameof(BackupDirectory));
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(HasNoBackups));
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            await operation();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
