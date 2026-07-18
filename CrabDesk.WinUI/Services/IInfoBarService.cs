using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Services;

public sealed record InfoBarNotification(
    string Message,
    InfoBarSeverity Severity = InfoBarSeverity.Informational,
    TimeSpan? Duration = null);

public interface IInfoBarService
{
    event EventHandler<InfoBarNotification>? Requested;
    void Show(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, TimeSpan? duration = null);
}

public sealed class InfoBarService : IInfoBarService
{
    public event EventHandler<InfoBarNotification>? Requested;

    public void Show(
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational,
        TimeSpan? duration = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Requested?.Invoke(this, new InfoBarNotification(message, severity, duration));
        }
    }
}
