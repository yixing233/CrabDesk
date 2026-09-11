using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace CrabDesk.WinUI.Services;

public interface IFilePickerService
{
    void RegisterWindow(Window window);
    Task<string?> PickFolderAsync();
    Task<string?> PickOpenFileAsync(params string[] extensions);
    Task<string?> PickSaveFileAsync(string suggestedName, string extension);
}

public sealed class FilePickerService : IFilePickerService
{
    private IntPtr _windowHandle;

    public void RegisterWindow(Window window) =>
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add("*");
        Initialize(picker);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public async Task<string?> PickOpenFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        if (extensions is not null && extensions.Length > 0)
        {
            foreach (var ext in extensions)
            {
                picker.FileTypeFilter.Add(NormalizeExtension(ext));
            }
        }
        else
        {
            picker.FileTypeFilter.Add("*");
        }
        Initialize(picker);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string extension)
    {
        var normalized = NormalizeExtension(extension);
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add("CrabDesk JSON", [normalized]);
        Initialize(picker);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private void Initialize(object picker)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The picker window has not been registered.");
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
    }

    private static string NormalizeExtension(string value) =>
        value.StartsWith('.') ? value : "." + value;
}
