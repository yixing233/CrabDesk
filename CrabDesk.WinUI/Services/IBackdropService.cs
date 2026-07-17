using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;

namespace CrabDesk.WinUI.Services;

public enum BackdropKind
{
    Mica,
    MicaAlt,
    Acrylic
}

public interface IBackdropService
{
    BackdropKind Current { get; }
    void Apply(Window window, BackdropKind kind);
}

public sealed class BackdropService : IBackdropService
{
    public BackdropKind Current { get; private set; }

    public void Apply(Window window, BackdropKind kind)
    {
        Current = kind;
        window.SystemBackdrop = kind switch
        {
            BackdropKind.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            BackdropKind.Acrylic => new DesktopAcrylicBackdrop(),
            _ => new MicaBackdrop { Kind = MicaKind.Base }
        };
    }
}
