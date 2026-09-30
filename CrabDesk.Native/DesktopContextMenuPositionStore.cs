using System.Text.Json;

namespace CrabDesk.Native;

public readonly record struct DesktopContextMenuPosition(int X, int Y, long CapturedAtUtcTicks);

/// <summary>
/// Carries the desktop point from the low-level right-click hook to the
/// Explorer-launched create-box command. The command itself cannot receive the
/// original shell popup point through a registry command line.
/// </summary>
public static class DesktopContextMenuPositionStore
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(10);
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("CRABDESK_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrabDesk"),
        "context-menu-position.json");

    public static void Write(int x, int y)
    {
        var path = FilePath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        var payload = new DesktopContextMenuPosition(x, y, DateTime.UtcNow.Ticks);
        lock (Gate)
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload));
            File.Move(temporaryPath, path, true);
        }
    }

    public static bool TryRead(out DesktopContextMenuPosition position)
    {
        position = default;
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath))
                {
                    return false;
                }

                var loaded = JsonSerializer.Deserialize<DesktopContextMenuPosition>(
                    File.ReadAllText(FilePath));
                if (loaded.CapturedAtUtcTicks <= 0 ||
                    DateTime.UtcNow - new DateTime(loaded.CapturedAtUtcTicks, DateTimeKind.Utc) > MaximumAge)
                {
                    File.Delete(FilePath);
                    return false;
                }

                position = loaded;
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool TryConsume(out DesktopContextMenuPosition position)
    {
        if (!TryRead(out position))
        {
            return false;
        }

        try
        {
            lock (Gate)
            {
                File.Delete(FilePath);
            }
        }
        catch
        {
            // The position was still valid for this request; a stale file is
            // harmless because its timestamp is checked on the next read.
        }

        return true;
    }
}
