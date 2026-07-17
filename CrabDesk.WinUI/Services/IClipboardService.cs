using Windows.ApplicationModel.DataTransfer;

namespace CrabDesk.WinUI.Services;

public interface IClipboardService
{
    void SetText(string value);
}

public sealed class ClipboardService : IClipboardService
{
    public void SetText(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
