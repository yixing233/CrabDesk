using System.Threading;
using CrabDesk.Core;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Services;
using CrabDesk.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CrabDesk.WinUI;

public partial class App : Application
{
    private readonly ServiceProvider _services;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _exitEvent;
    private EventWaitHandle? _organizeEvent;
    private EventWaitHandle? _createBoxEvent;
    private EventWaitHandle? _undoOrganizationEvent;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();
        AppDiagnostic.Info($"WinUI startup pid={Environment.ProcessId}");
        UnhandledException += (_, args) => AppDiagnostic.Error("WinUI UnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppDiagnostic.Error($"WinUI AppDomain exception terminating={args.IsTerminating}", exception);
            }
        };
    }

    public static App CurrentApp => (App)Current;

    public static T GetService<T>() where T : notnull =>
        CurrentApp._services.GetRequiredService<T>();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var exitExisting = commandLine.Any(argument => string.Equals(argument, "--exit-existing", StringComparison.OrdinalIgnoreCase));
        var organize = commandLine.Any(argument => string.Equals(argument, "--organize", StringComparison.OrdinalIgnoreCase));
        var createBox = commandLine.Any(argument => string.Equals(argument, "--create-box", StringComparison.OrdinalIgnoreCase));
        var undoOrganization = commandLine.Any(argument => string.Equals(argument, "--undo-organization", StringComparison.OrdinalIgnoreCase));
        var showSettings = commandLine.Any(argument => string.Equals(argument, "--show-settings", StringComparison.OrdinalIgnoreCase));
        var validationPage = GetArgumentValue(commandLine, "--validation-page");
        var validationTheme = GetArgumentValue(commandLine, "--validation-theme");
        var validationScale = GetArgumentValue(commandLine, "--validation-scale");
        var validationWidth = GetArgumentValue(commandLine, "--validation-width");
        var validationHeight = GetArgumentValue(commandLine, "--validation-height");
        var validationPane = GetArgumentValue(commandLine, "--validation-pane");
        _singleInstanceMutex = new Mutex(true, @"Local\CrabDesk.SingleInstance", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            SignalExistingInstance(exitExisting
                ? @"Local\CrabDesk.Exit"
                : organize
                    ? @"Local\CrabDesk.Organize"
                    : createBox
                        ? @"Local\CrabDesk.CreateBox"
                        : undoOrganization
                            ? @"Local\CrabDesk.UndoOrganization"
                            : @"Local\CrabDesk.Activate");
            Exit();
            return;
        }
        if (exitExisting)
        {
            DisposeInstanceResources();
            Exit();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrabDesk.Activate");
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrabDesk.Exit");
        _organizeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrabDesk.Organize");
        _createBoxEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrabDesk.CreateBox");
        _undoOrganizationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrabDesk.UndoOrganization");

        var runtime = GetService<CrabDeskRuntime>();
        await runtime.InitializeAsync();
        AppDiagnostic.Info("Runtime initialized; creating MainWindow");
        GetService<IThemeService>().Apply(runtime.State.Settings.ThemeMode);

        _window = GetService<MainWindow>();
        AppDiagnostic.Info("MainWindow created");
        if (Enum.TryParse<ApplicationThemeMode>(validationTheme, true, out var requestedTheme))
        {
            GetService<IThemeService>().Apply(requestedTheme);
        }
        if (!string.IsNullOrWhiteSpace(validationPage))
        {
            _window.NavigateTo(validationPage);
        }
        if (double.TryParse(validationScale, out var scale) &&
            double.TryParse(validationWidth, out var width) &&
            double.TryParse(validationHeight, out var height))
        {
            _window.ApplyValidationViewport(width, height, scale);
        }
        _window.ApplyValidationPane(validationPane);
        runtime.ShowSettingsRequested += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Page))
            {
                _window.NavigateTo(eventArgs.Page);
            }
            ActivateWindow();
        };
        runtime.ExitRequested += (_, _) => Shutdown();
        StartCommandListeners(runtime, _window.DispatcherQueue);

        if (organize) runtime.SmartOrganize();
        if (createBox) runtime.AddBox();
        if (undoOrganization) runtime.UndoLastOrganization();

        var background = commandLine.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        if (showSettings || (!background && !runtime.State.Settings.DesktopBehavior.LaunchToTray))
        {
            _window.Activate();
            AppDiagnostic.Info("MainWindow activated");
        }
    }

    internal void ActivateWindow()
    {
        _window?.AppWindow.Show();
        _window?.Activate();
    }

    internal void Shutdown()
    {
        GetService<CrabDeskRuntime>().Dispose();
        DisposeInstanceResources();
        Exit();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("WinUI dispatcher queue is unavailable.");
        services.AddSingleton(_ => new CrabDeskRuntime(action =>
            dispatcher.TryEnqueue(() => action())));
        services.AddSingleton<ICrabDeskService, CrabDeskService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IBackdropService, BackdropService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IFontCatalogService, SystemFontCatalogService>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<GeneralViewModel>();
        services.AddTransient<AboutViewModel>();
        services.AddTransient<HotkeysViewModel>();
        services.AddTransient<BackupViewModel>();
        services.AddTransient<OrganizationViewModel>();
        services.AddTransient<AppearanceViewModel>();
        services.AddTransient<BoxesViewModel>();
    }

    private void StartCommandListeners(CrabDeskRuntime runtime, DispatcherQueue dispatcher)
    {
        StartListener(_activateEvent!, dispatcher, ActivateWindow);
        StartListener(_exitEvent!, dispatcher, Shutdown);
        StartListener(_organizeEvent!, dispatcher, () => runtime.SmartOrganize());
        StartListener(_createBoxEvent!, dispatcher, () => runtime.AddBox());
        StartListener(_undoOrganizationEvent!, dispatcher, runtime.UndoLastOrganization);
    }

    private static void StartListener(EventWaitHandle handle, DispatcherQueue dispatcher, Action action)
    {
        _ = Task.Run(() =>
        {
            while (handle.WaitOne())
            {
                dispatcher.TryEnqueue(() => action());
            }
        });
    }

    private static void SignalExistingInstance(string eventName)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(eventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    private static string? GetArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }
        return null;
    }

    private void DisposeInstanceResources()
    {
        _activateEvent?.Dispose();
        _exitEvent?.Dispose();
        _organizeEvent?.Dispose();
        _createBoxEvent?.Dispose();
        _undoOrganizationEvent?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        _services.Dispose();
    }
}
