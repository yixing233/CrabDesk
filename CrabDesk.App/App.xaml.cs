using System.Text.Json;
using System.Threading;
using System.Windows;

namespace CrabDesk.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _exitEvent;
    private EventWaitHandle? _organizeEvent;
    private EventWaitHandle? _undoOrganizationEvent;
    private CrabDeskRuntime? _runtime;
    private MainWindow? _settingsWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var backupVerificationResultPath = GetArgumentValue(e.Args, "--verify-backup-ui");
        var themeCaptureDirectory = GetArgumentValue(e.Args, "--capture-settings-themes");
        var exitExisting = e.Args.Any(argument =>
            string.Equals(argument, "--exit-existing", StringComparison.OrdinalIgnoreCase));
        var organizeExisting = e.Args.Any(argument =>
            string.Equals(argument, "--organize", StringComparison.OrdinalIgnoreCase));
        var undoOrganization = e.Args.Any(argument =>
            string.Equals(argument, "--undo-organization", StringComparison.OrdinalIgnoreCase));
        var commandOnly = exitExisting || organizeExisting || undoOrganization;
        _singleInstanceMutex = new Mutex(true, @"Local\CrabDesk.SingleInstance", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            if (backupVerificationResultPath is not null || themeCaptureDirectory is not null)
            {
                if (backupVerificationResultPath is not null)
                {
                    await WriteBackupVerificationResultAsync(
                        backupVerificationResultPath,
                        new BackupUiAutomationResult(false, false, false, false, false, false, "已有 CrabDesk 实例正在运行"));
                }
                if (themeCaptureDirectory is not null)
                {
                    await WriteThemeCaptureErrorAsync(themeCaptureDirectory, "已有 CrabDesk 实例正在运行");
                }
                Shutdown(2);
                return;
            }
            try
            {
                var eventName = exitExisting
                    ? @"Local\CrabDesk.Exit"
                    : organizeExisting
                        ? @"Local\CrabDesk.Organize"
                        : undoOrganization
                            ? @"Local\CrabDesk.UndoOrganization"
                            : @"Local\CrabDesk.Activate";
                using var signal = EventWaitHandle.OpenExisting(eventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            Shutdown();
            return;
        }
        if (commandOnly)
        {
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrabDesk.Activate");
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrabDesk.Exit");
        _organizeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrabDesk.Organize");
        _undoOrganizationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrabDesk.UndoOrganization");
        _ = Task.Run(() =>
        {
            while (_activateEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(ShowSettings);
            }
        });
        _ = Task.Run(() =>
        {
            while (_organizeEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(() => _runtime?.ApplyOrganizationRules());
            }
        });
        _ = Task.Run(() =>
        {
            while (_undoOrganizationEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(() => _runtime?.UndoLastOrganization());
            }
        });
        _ = Task.Run(() =>
        {
            while (_exitEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(ExitApplication);
            }
        });

        try
        {
            _runtime = new CrabDeskRuntime(Dispatcher);
            await _runtime.InitializeAsync();
            _runtime.ShowSettingsRequested += (_, _) => ShowSettings();
            _runtime.ExitRequested += (_, _) => ExitApplication();
            if (backupVerificationResultPath is not null)
            {
                ShowSettings();
                var result = await _settingsWindow!.RunBackupUiAutomationAsync();
                await WriteBackupVerificationResultAsync(backupVerificationResultPath, result);
                Shutdown(result.BackupCreated &&
                    result.FailureWasReported &&
                    result.FailedRestorePreservedCurrentState &&
                    result.SuccessfulRestoreRecoveredBackup &&
                    result.ResetCreatedBackup &&
                    result.ResetBackupRestoredLayout
                        ? 0
                        : 1);
                return;
            }
            if (themeCaptureDirectory is not null)
            {
                ShowSettings();
                var report = await _settingsWindow!.CaptureThemeScreenshotsAsync(themeCaptureDirectory);
                await File.WriteAllTextAsync(
                    Path.Combine(Path.GetFullPath(themeCaptureDirectory), "manifest.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
                return;
            }
            if (_runtime.State.Settings.DesktopBehavior.LaunchToTray)
            {
                _runtime.NotifyMinimizedToTray();
            }
            else
            {
                ShowSettings();
            }
        }
        catch (Exception exception)
        {
            if (backupVerificationResultPath is not null)
            {
                try
                {
                    await WriteBackupVerificationResultAsync(
                        backupVerificationResultPath,
                        new BackupUiAutomationResult(false, false, false, false, false, false, exception.Message));
                }
                catch
                {
                }
                Shutdown(1);
                return;
            }
            if (themeCaptureDirectory is not null)
            {
                try
                {
                    await WriteThemeCaptureErrorAsync(themeCaptureDirectory, exception.Message);
                }
                catch
                {
                }
                Shutdown(1);
                return;
            }
            MessageBox.Show(
                $"CrabDesk 启动失败。\n\n{exception.Message}",
                "CrabDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _runtime?.Dispose();
        _activateEvent?.Dispose();
        _exitEvent?.Dispose();
        _organizeEvent?.Dispose();
        _undoOrganizationEvent?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ShowSettings()
    {
        if (_runtime is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new MainWindow(_runtime);
            MainWindow = _settingsWindow;
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                if (!Dispatcher.HasShutdownStarted)
                {
                    Shutdown();
                }
            };
        }

        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }
        _settingsWindow.Activate();
    }

    private void ExitApplication()
    {
        _runtime?.Dispose();
        _runtime = null;
        Shutdown(0);
    }

    private static string? GetArgumentValue(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return index + 1 < arguments.Count && !string.IsNullOrWhiteSpace(arguments[index + 1])
                ? arguments[index + 1]
                : null;
        }
        return null;
    }

    private static async Task WriteBackupVerificationResultAsync(
        string path,
        BackupUiAutomationResult result)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("验证结果路径无效。"));
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task WriteThemeCaptureErrorAsync(string directory, string message)
    {
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        await File.WriteAllTextAsync(Path.Combine(fullDirectory, "error.txt"), message);
    }
}
