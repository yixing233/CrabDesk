namespace CrabDesk.WinUI.Services;

public interface IFontCatalogService
{
    IReadOnlyList<string> FontFamilies { get; }
}
