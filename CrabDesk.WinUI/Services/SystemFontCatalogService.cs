using System.Drawing.Text;

namespace CrabDesk.WinUI.Services;

public sealed class SystemFontCatalogService : IFontCatalogService
{
    public SystemFontCatalogService()
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            FontFamilies = fonts.Families
                .Select(family => family.Name)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch
        {
            FontFamilies = ["Segoe UI", "Microsoft YaHei UI", "Arial", "Consolas"];
        }
    }

    public IReadOnlyList<string> FontFamilies { get; }
}
