using CrabDesk.Core;
using Microsoft.Win32;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

public sealed class DesktopContextMenuRegistration : IDesktopContextMenuRegistration
{
    private const string DefaultKeyPath = @"Software\Classes\DesktopBackground\Shell\CrabDesk";
    private const int MenuIconSize = 32;
    private static readonly Color MenuIconColor = Color.FromArgb(50, 55, 65);
    private static readonly Color AccentBlue = Color.FromArgb(74, 91, 177);
    private static readonly Color AccentBlueLight = Color.FromArgb(120, 74, 91, 177);
    private const string DefaultSubmenuClassName = "CrabDesk.DesktopContextMenu.Commands";
    private const string DefaultSubmenuKeyPath = @"Software\Classes\CrabDesk.DesktopContextMenu.Commands";
    private const string DefaultLegacyOrganizeKeyPath =
        @"Software\Classes\DesktopBackground\Shell\CrabDesk.Organize";

    private readonly RegistryKey _root;
    private readonly string _keyPath;
    private readonly string _submenuClassName;
    private readonly string _submenuKeyPath;
    private readonly string _legacyOrganizeKeyPath;

    public DesktopContextMenuRegistration(
        RegistryKey? root = null,
        string? keyPath = null,
        string? submenuClassName = null,
        string? submenuKeyPath = null,
        string? legacyOrganizeKeyPath = null)
    {
        _root = root ?? Registry.CurrentUser;
        _keyPath = keyPath ?? DefaultKeyPath;
        _submenuClassName = submenuClassName ?? DefaultSubmenuClassName;
        _submenuKeyPath = submenuKeyPath ?? DefaultSubmenuKeyPath;
        _legacyOrganizeKeyPath = legacyOrganizeKeyPath ?? DefaultLegacyOrganizeKeyPath;
    }

    public bool IsEnabled
    {
        get
        {
            using var key = _root.OpenSubKey(_keyPath, false);
            using var submenu = _root.OpenSubKey(_submenuKeyPath, false);
            return key is not null && submenu is not null;
        }
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        if (!enabled)
        {
            DeleteOwnedKeys();
            return;
        }

        var normalizedExecutable = Path.GetFullPath(executablePath);
        DeleteOwnedKeys();

        using var key = _root.CreateSubKey(_keyPath, true)
            ?? throw new InvalidOperationException("Unable to create the CrabDesk desktop context-menu entry.");
        key.SetValue(null, "CrabDesk", RegistryValueKind.String);
        key.SetValue("MUIVerb", "CrabDesk", RegistryValueKind.String);
        key.SetValue("Icon", $"\"{normalizedExecutable}\",0", RegistryValueKind.String);
        key.SetValue("Position", "Top", RegistryValueKind.String);
        key.SetValue("ExtendedSubCommandsKey", _submenuClassName, RegistryValueKind.String);
        // Keep the root item executable as well as exposing the cascading
        // commands.  Explorer does not invoke a root that only contains
        // ExtendedSubCommandsKey on all desktop-shell builds; the direct
        // command makes a click on "CrabDesk" launch/activate the settings
        // window even when no instance is currently running (including when
        // the user's normal startup mode is tray-only).
        using (var rootCommand = key.CreateSubKey("command", true)
               ?? throw new InvalidOperationException("Unable to create the CrabDesk root command."))
        {
            rootCommand.SetValue(null, $"\"{normalizedExecutable}\" --show-settings", RegistryValueKind.String);
        }

        using var submenu = _root.CreateSubKey(_submenuKeyPath, true)
            ?? throw new InvalidOperationException("Unable to create the CrabDesk submenu command store.");
        var iconDirectory = EnsureMenuIconDirectory();
        WriteCommand(
            submenu,
            "01CreateBox",
            "\u521B\u5EFA\u76D2\u5B50",
            normalizedExecutable,
            "--create-box",
            GetMenuIconPath(iconDirectory, "create-box.ico", DrawCreateBoxIcon));
        WriteCommand(
            submenu,
            "02Settings",
            "\u8BBE\u7F6E\u4E2D\u5FC3",
            normalizedExecutable,
            "--show-settings",
            GetMenuIconPath(iconDirectory, "settings.ico", DrawSettingsIcon));
        WriteCommand(
            submenu,
            "03RuleOrganize",
            "\u89C4\u5219\u6574\u7406",
            normalizedExecutable,
            "--organize",
            GetMenuIconPath(iconDirectory, "organize.ico", DrawOrganizeIcon));
        WriteCommand(
            submenu,
            "04AiOrganize",
            "AI \u6574\u7406",
            normalizedExecutable,
            "--ai-organize",
            GetMenuIconPath(iconDirectory, "ai-organize.ico", DrawAiOrganizeIcon));
        WriteCommand(
            submenu,
            "05Reconnect",
            "\u91CD\u65B0\u8FDE\u63A5\u684C\u9762",
            normalizedExecutable,
            "--reconnect",
            GetMenuIconPath(iconDirectory, "reconnect.ico", DrawReconnectIcon));
        // Refresh re-places every icon in Explorer's active sort order. It is
        // registered here so the command is reachable as an ordinary command
        // line, which works on every Windows shell build rather than only where
        // the desktop popup exposes a classic #32768 menu for the input hook.
        WriteCommand(
            submenu,
            "06Refresh",
            "\u5237\u65B0\u56FE\u6807",
            normalizedExecutable,
            "--refresh-desktop",
            GetMenuIconPath(iconDirectory, "refresh.ico", DrawRefreshIcon));
        WriteCommand(
            submenu,
            "07Exit",
            "\u9000\u51FA CrabDesk",
            normalizedExecutable,
            "--exit",
            GetMenuIconPath(iconDirectory, "exit.ico", DrawExitIcon));

        try
        {
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
        }
    }

    private static void WriteCommand(
        RegistryKey submenu,
        string keyName,
        string title,
        string executablePath,
        string? argument,
        string iconPath)
    {
        using var verb = submenu.CreateSubKey($@"shell\{keyName}", true)
            ?? throw new InvalidOperationException($"Unable to create the CrabDesk {keyName} submenu entry.");
        verb.SetValue(null, title, RegistryValueKind.String);
        verb.SetValue("MUIVerb", title, RegistryValueKind.String);
        verb.SetValue("Icon", iconPath, RegistryValueKind.String);
        using var command = verb.CreateSubKey("command", true)
            ?? throw new InvalidOperationException($"Unable to create the CrabDesk {keyName} command.");
        var commandLine = argument is null
            ? $"\"{executablePath}\""
            : $"\"{executablePath}\" {argument}";
        command.SetValue(null, commandLine, RegistryValueKind.String);
    }

    private void DeleteOwnedKeys()
    {
        _root.DeleteSubKeyTree(_keyPath, false);
        _root.DeleteSubKeyTree(_submenuKeyPath, false);
        _root.DeleteSubKeyTree(_legacyOrganizeKeyPath, false);

        try
        {
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
        }
    }

    // ---- Dedicated menu icons ----------------------------------------
    //
    // Explorer renders the Icon value of each registered verb at 16 px in
    // the desktop context menu. Pointing every verb at the application
    // executable shows the same large scaled logo for each entry; these
    // small dedicated icons keep the menu readable on any DPI.

    private static string EnsureMenuIconDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrabDesk",
            "icons_v2");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetMenuIconPath(
        string directory,
        string fileName,
        Action<Graphics, int> draw)
    {
        var path = Path.Combine(directory, fileName);
        try
        {
            WriteMultiSizeIcon(path, draw);
        }
        catch
        {
            // Best-effort: the registration still works with the icon
            // path even if the file could not be written this time.
        }
        return path;
    }

    private static PrivateFontCollection? s_fontCollection;
    private static FontFamily? s_lucideFontFamily;
    private static readonly object s_fontLock = new();

    private static void WriteMultiSizeIcon(string path, Action<Graphics, int> draw)
    {
        var sizes = new[] { 16, 24, 32, 48 };
        var bitmaps = new Bitmap[sizes.Length];

        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bmp);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            draw(graphics, size);
            bitmaps[i] = bmp;
        }

        try
        {
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);

            // ICONDIR
            bw.Write((short)0); // Reserved
            bw.Write((short)1); // Type 1 = Icon
            bw.Write((short)sizes.Length); // Image count

            var offset = 6 + (16 * sizes.Length);
            var imageBuffers = new byte[sizes.Length][];

            for (var i = 0; i < sizes.Length; i++)
            {
                var size = sizes[i];
                var bmp = bitmaps[i];

                using var ms = new MemoryStream();
                using var imgBw = new BinaryWriter(ms);

                var maskRowWidth = ((size + 31) / 32) * 4;
                var maskSize = maskRowWidth * size;
                var imageSize = size * size * 4;

                // BITMAPINFOHEADER
                imgBw.Write((int)40); // biSize
                imgBw.Write((int)size); // biWidth
                imgBw.Write((int)(size * 2)); // biHeight (doubled for mask)
                imgBw.Write((short)1); // biPlanes
                imgBw.Write((short)32); // biBitCount
                imgBw.Write((int)0); // biCompression (BI_RGB)
                imgBw.Write((int)(imageSize + maskSize)); // biSizeImage
                imgBw.Write((int)0); // biXPelsPerMeter
                imgBw.Write((int)0); // biYPelsPerMeter
                imgBw.Write((int)0); // biClrUsed
                imgBw.Write((int)0); // biClrImportant

                // Bottom-up BGRA
                for (var y = size - 1; y >= 0; y--)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var pixel = bmp.GetPixel(x, y);
                        imgBw.Write((byte)pixel.B);
                        imgBw.Write((byte)pixel.G);
                        imgBw.Write((byte)pixel.R);
                        imgBw.Write((byte)pixel.A);
                    }
                }

                // AND mask
                imgBw.Write(new byte[maskSize]);
                imgBw.Flush();

                var buffer = ms.ToArray();
                imageBuffers[i] = buffer;

                // Directory entry
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)0); // Colors
                bw.Write((byte)0); // Reserved
                bw.Write((short)1); // Planes
                bw.Write((short)32); // BitCount
                bw.Write((int)buffer.Length); // Bytes in image
                bw.Write((int)offset); // Offset

                offset += buffer.Length;
            }

            for (var i = 0; i < sizes.Length; i++)
            {
                bw.Write(imageBuffers[i]);
            }

            bw.Flush();
        }
        finally
        {
            foreach (var bmp in bitmaps)
            {
                bmp.Dispose();
            }
        }
    }

    private static FontFamily? GetLucideFontFamily()
    {
        lock (s_fontLock)
        {
            if (s_lucideFontFamily != null)
            {
                return s_lucideFontFamily;
            }

            try
            {
                var assemblyDir = Path.GetDirectoryName(typeof(DesktopContextMenuRegistration).Assembly.Location) ?? string.Empty;
                var baseDir = AppContext.BaseDirectory;
                var candidatePaths = new[]
                {
                    Path.Combine(baseDir, "Assets", "lucide.ttf"),
                    Path.Combine(baseDir, "lucide.ttf"),
                    Path.Combine(assemblyDir, "Assets", "lucide.ttf"),
                    Path.Combine(assemblyDir, "lucide.ttf"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "lucide.ttf"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lucide.ttf"),
                    Path.Combine(baseDir, "..", "..", "..", "..", "CrabDesk.Runtime", "Assets", "lucide.ttf"),
                    Path.Combine(assemblyDir, "..", "..", "..", "..", "CrabDesk.Runtime", "Assets", "lucide.ttf")
                };

                foreach (var rawPath in candidatePaths)
                {
                    if (string.IsNullOrWhiteSpace(rawPath))
                    {
                        continue;
                    }
                    var fontPath = Path.GetFullPath(rawPath);
                    if (File.Exists(fontPath))
                    {
                        var collection = new PrivateFontCollection();
                        collection.AddFontFile(fontPath);
                        if (collection.Families.Length > 0)
                        {
                            s_fontCollection = collection;
                            s_lucideFontFamily = collection.Families[0];
                            return s_lucideFontFamily;
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }
    }

    private static void DrawLucideGlyph(
        Graphics graphics,
        int size,
        string glyph,
        Color color)
    {
        var family = GetLucideFontFamily();
        if (family is null)
        {
            return;
        }

        var fontSize = (float)Math.Round(size * 0.68f);
        using var font = new Font(family, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoClip
        };
        graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
    }

    private static void DrawCreateBoxIcon(Graphics graphics, int size)
    {
        var family = GetLucideFontFamily();
        if (family != null)
        {
            DrawLucideGlyph(graphics, size, "\uE173", MenuIconColor); // SquarePlus
        }
        else
        {
            DrawCreateBoxIconFallback(graphics, new RectangleF(0, 0, size, size));
        }
    }

    private static void DrawSettingsIcon(Graphics graphics, int size)
    {
        var family = GetLucideFontFamily();
        if (family != null)
        {
            DrawLucideGlyph(graphics, size, "\uE154", MenuIconColor); // Settings
        }
        else
        {
            DrawSettingsIconFallback(graphics, new RectangleF(0, 0, size, size));
        }
    }

    private static void DrawOrganizeIcon(Graphics graphics, int size)
    {
        var family = GetLucideFontFamily();
        if (family != null)
        {
            DrawLucideGlyph(graphics, size, "\uE460", MenuIconColor); // ListFilter
        }
        else
        {
            DrawOrganizeIconFallback(graphics, new RectangleF(0, 0, size, size));
        }
    }

    private static void DrawAiOrganizeIcon(Graphics graphics, int size)
    {
        var family = GetLucideFontFamily();
        if (family != null)
        {
            DrawLucideGlyph(graphics, size, "\uE412", MenuIconColor); // Sparkles
        }
        else
        {
            DrawAiOrganizeIconFallback(graphics, new RectangleF(0, 0, size, size));
        }
    }

    private static void DrawReconnectIcon(Graphics graphics, int size)
    {
        var family = GetLucideFontFamily();
        if (family != null)
        {
            DrawLucideGlyph(graphics, size, "\uE145", MenuIconColor); // RefreshCw
        }
        else
        {
            DrawReconnectIconFallback(graphics, new RectangleF(0, 0, size, size));
        }
    }

    private static void DrawRefreshIcon(Graphics graphics, int size)
    {
        var family = GetLucideFontFamily();
        if (family != null)
        {
            // RefreshCcw reads as "re-run" while the reconnect entry keeps the
            // clockwise glyph, so the two neighbouring commands stay distinct.
            DrawLucideGlyph(graphics, size, "\uE144", MenuIconColor); // RefreshCcw
        }
        else
        {
            DrawReconnectIconFallback(graphics, new RectangleF(0, 0, size, size));
        }
    }

    private static void DrawExitIcon(Graphics graphics, int size)
    {
        var family = GetLucideFontFamily();
        if (family != null)
        {
            DrawLucideGlyph(graphics, size, "\uE10E", MenuIconColor); // LogOut
        }
        else
        {
            DrawExitIconFallback(graphics, new RectangleF(0, 0, size, size));
        }
    }

    private static void DrawReconnectIconFallback(Graphics graphics, RectangleF bounds)
    {
        using var pen = new Pen(AccentBlue, 2);
        graphics.DrawArc(pen, bounds.X + 4, bounds.Y + 4, bounds.Width - 8, bounds.Height - 8, 30, 300);
    }

    private static void DrawExitIconFallback(Graphics graphics, RectangleF bounds)
    {
        using var pen = new Pen(AccentBlue, 2);
        graphics.DrawRectangle(pen, bounds.X + 4, bounds.Y + 4, bounds.Width - 8, bounds.Height - 8);
    }

    private static GraphicsPath RoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawCreateBoxIconFallback(Graphics graphics, RectangleF bounds)
    {
        var back = new RectangleF(
            bounds.X + 4,
            bounds.Y + 4,
            bounds.Width * 0.55f,
            bounds.Height * 0.55f);
        var front = new RectangleF(
            bounds.X + bounds.Width * 0.30f,
            bounds.Y + bounds.Height * 0.30f,
            bounds.Width * 0.55f,
            bounds.Height * 0.55f);
        using var backBrush = new SolidBrush(AccentBlueLight);
        using var frontBrush = new SolidBrush(AccentBlue);
        using var backPath = RoundedPath(back, 5);
        using var frontPath = RoundedPath(front, 5);
        graphics.FillPath(backBrush, backPath);
        graphics.FillPath(frontBrush, frontPath);
    }

    private static void DrawSettingsIconFallback(Graphics graphics, RectangleF bounds)
    {
        var center = new PointF(
            bounds.X + bounds.Width / 2,
            bounds.Y + bounds.Height / 2);
        var toothRadius = bounds.Width * 0.46f;
        var bodyRadius = bounds.Width * 0.30f;
        using var brush = new SolidBrush(AccentBlue);
        for (var index = 0; index < 8; index++)
        {
            var angle = index * Math.PI / 4;
            var x = center.X + (float)Math.Cos(angle) * toothRadius;
            var y = center.Y + (float)Math.Sin(angle) * toothRadius;
            graphics.FillRectangle(
                brush,
                x - bounds.Width * 0.07f,
                y - bounds.Width * 0.07f,
                bounds.Width * 0.14f,
                bounds.Width * 0.14f);
        }
        graphics.FillEllipse(
            brush,
            center.X - bodyRadius,
            center.Y - bodyRadius,
            bodyRadius * 2,
            bodyRadius * 2);
    }

    private static void DrawOrganizeIconFallback(Graphics graphics, RectangleF bounds)
    {
        using var brush = new SolidBrush(AccentBlue);
        var lineWidth = bounds.Width * 0.34f;
        var lineHeight = bounds.Height * 0.10f;
        for (var index = 0; index < 3; index++)
        {
            var y = bounds.Y + bounds.Height * (0.18f + index * 0.32f);
            graphics.FillRectangle(brush, bounds.X, y, lineWidth, lineHeight);
        }
        var arrowX = bounds.X + bounds.Width * 0.68f;
        var arrowY = bounds.Y + bounds.Height * 0.5f;
        graphics.FillPolygon(
            brush,
            [
                new PointF(arrowX, arrowY - bounds.Height * 0.22f),
                new PointF(arrowX + bounds.Width * 0.22f, arrowY),
                new PointF(arrowX, arrowY + bounds.Height * 0.22f)
            ]);
    }

    private static void DrawAiOrganizeIconFallback(Graphics graphics, RectangleF bounds)
    {
        var center = new PointF(
            bounds.X + bounds.Width / 2,
            bounds.Y + bounds.Height / 2);
        using var brush = new SolidBrush(AccentBlue);
        var sideHalf = bounds.Width * 0.16f;
        graphics.FillPolygon(
            brush,
            [
                new PointF(center.X, bounds.Y),
                new PointF(center.X + sideHalf, center.Y - sideHalf),
                new PointF(bounds.Right, center.Y),
                new PointF(center.X + sideHalf, center.Y + sideHalf),
                new PointF(center.X, bounds.Bottom),
                new PointF(center.X - sideHalf, center.Y + sideHalf),
                new PointF(bounds.X, center.Y),
                new PointF(center.X - sideHalf, center.Y - sideHalf)
            ]);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
