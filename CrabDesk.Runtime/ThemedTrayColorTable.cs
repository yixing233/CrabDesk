namespace CrabDesk.Runtime;

internal sealed class ThemedTrayColorTable : System.Windows.Forms.ProfessionalColorTable
{
    private readonly bool _isDark;

    internal ThemedTrayColorTable(bool isDark)
    {
        _isDark = isDark;
        UseSystemColors = false;
    }

    private System.Drawing.Color Background => _isDark
        ? System.Drawing.Color.FromArgb(37, 40, 45)
        : System.Drawing.Color.FromArgb(252, 252, 252);

    private System.Drawing.Color Border => _isDark
        ? System.Drawing.Color.FromArgb(74, 80, 89)
        : System.Drawing.Color.FromArgb(205, 212, 219);

    private System.Drawing.Color Selected => _isDark
        ? System.Drawing.Color.FromArgb(51, 57, 65)
        : System.Drawing.Color.FromArgb(229, 234, 238);

    public override System.Drawing.Color ToolStripDropDownBackground => Background;
    public override System.Drawing.Color ImageMarginGradientBegin => Background;
    public override System.Drawing.Color ImageMarginGradientMiddle => Background;
    public override System.Drawing.Color ImageMarginGradientEnd => Background;
    public override System.Drawing.Color MenuBorder => Border;
    public override System.Drawing.Color MenuItemBorder => Border;
    public override System.Drawing.Color MenuItemSelected => Selected;
    public override System.Drawing.Color MenuItemSelectedGradientBegin => Selected;
    public override System.Drawing.Color MenuItemSelectedGradientEnd => Selected;
    public override System.Drawing.Color MenuItemPressedGradientBegin => Selected;
    public override System.Drawing.Color MenuItemPressedGradientMiddle => Selected;
    public override System.Drawing.Color MenuItemPressedGradientEnd => Selected;
    public override System.Drawing.Color SeparatorDark => Border;
    public override System.Drawing.Color SeparatorLight => Background;
    public override System.Drawing.Color CheckBackground => Selected;
    public override System.Drawing.Color CheckPressedBackground => Selected;
    public override System.Drawing.Color CheckSelectedBackground => Selected;
}
