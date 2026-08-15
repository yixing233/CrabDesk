using CrabDesk.Core;
using Microsoft.Win32;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

public sealed class DesktopContextMenuRegistration : IDesktopContextMenuRegistration
{
    private const string DefaultKeyPath = @"Software\Classes\DesktopBackground\Shell\CrabDesk";
    private const int MenuIconSize = 32;
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
        key.SetValue("Position", "Bottom", RegistryValueKind.String);
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
            "icons");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetMenuIconPath(
        string directory,
        string fileName,
        Action<Graphics, RectangleF> draw)
    {
        var path = Path.Combine(directory, fileName);
        try
        {
            if (!File.Exists(path))
            {
                WriteMenuIcon(path, draw);
            }
        }
        catch
        {
            // Best-effort: the registration still works with the icon
            // path even if the file could not be written this time.
        }
        return path;
    }

    private static void WriteMenuIcon(string path, Action<Graphics, RectangleF> draw)
    {
        using var bitmap = new Bitmap(MenuIconSize, MenuIconSize);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            draw(graphics, new RectangleF(3, 3, MenuIconSize - 6, MenuIconSize - 6));
        }
        var hIcon = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            using var stream = File.Create(path);
            icon.Save(stream);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
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

    private static void DrawCreateBoxIcon(Graphics graphics, RectangleF bounds)
    {
        // Two stacked rounded panels: the front box in the accent color.
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

    private static void DrawSettingsIcon(Graphics graphics, RectangleF bounds)
    {
        // A simplified gear: eight teeth around a solid center disc.
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

    private static void DrawOrganizeIcon(Graphics graphics, RectangleF bounds)
    {
        // Three stacked lines with a right-pointing arrow: sorting rules.
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

    private static void DrawAiOrganizeIcon(Graphics graphics, RectangleF bounds)
    {
        // A four-point sparkle: AI organization.
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
}
