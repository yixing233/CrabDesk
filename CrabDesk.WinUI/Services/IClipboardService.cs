using Windows.ApplicationModel.DataTransfer;

namespace CrabDesk.WinUI.Services;

public interface IClipboardService
{
    Task SetTextAsync(string value);
}

public sealed class ClipboardService : IClipboardService
{
    private const int ClipboardBusyHResult = unchecked((int)0x800401D0);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200)
    ];

    public async Task SetTextAsync(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(value);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                return;
            }
            catch (System.Runtime.InteropServices.COMException exception)
                when (exception.HResult == ClipboardBusyHResult && attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt]);
            }
        }
    }
}
