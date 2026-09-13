using System.Text;
using System.Text.RegularExpressions;

namespace CrabDesk.Bootstrapper;

/// <summary>
/// Local, opt-in-by-execution installer logging. Log entries are deliberately
/// allow-listed/redacted because installer logs may be shared for support.
/// </summary>
internal static class InstallerLogger
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxFiles = 5;
    private static readonly object Sync = new();
    private static string? _path;
    private static int _sequence;

    internal static string? LogPath => _path;

    internal static void Initialize()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrabDesk", "Logs");
            Directory.CreateDirectory(root);
            _path = Path.Combine(root, $"bootstrapper-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            Log("INFO", "startup", new Dictionary<string, object?>
            {
                ["version"] = typeof(InstallerLogger).Assembly.GetName().Version?.ToString(),
                ["osBuild"] = Environment.OSVersion.Version.Build,
                ["architecture"] = Environment.Is64BitProcess ? "x64" : "x86"
            });
        }
        catch
        {
            // Logging must never prevent installation.
            _path = null;
        }
    }

    internal static void Log(string level, string eventName, IReadOnlyDictionary<string, object?>? fields = null)
    {
        var path = _path;
        if (path is null) return;
        try
        {
            var values = new List<string>
            {
                $"ts={DateTimeOffset.UtcNow:O}",
                $"level={SafeToken(level)}",
                $"event={SafeToken(eventName)}"
            };
            if (fields is not null)
            {
                foreach (var field in fields.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    values.Add($"{SafeToken(field.Key)}={Sanitize(field.Key, Convert.ToString(field.Value) ?? "")}");
                }
            }

            var line = string.Join(' ', values) + Environment.NewLine;
            lock (Sync)
            {
                RotateIfNeeded(path, line.Length);
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging is best effort and must not alter installer behavior.
        }
    }

    internal static void LogException(string eventName, Exception exception, IReadOnlyDictionary<string, object?>? fields = null)
    {
        var data = fields is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(fields, StringComparer.Ordinal);
        data["exceptionType"] = exception.GetType().FullName;
        data["message"] = exception.Message;
        data["stack"] = exception.StackTrace;
        Log("ERROR", eventName, data);
    }

    internal static string Sanitize(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var lowerKey = key.ToLowerInvariant();
        if (lowerKey.Contains("url") || lowerKey.Contains("uri") || lowerKey.Contains("token") ||
            lowerKey.Contains("secret") || lowerKey.Contains("password") || lowerKey.Equals("key") || lowerKey.EndsWith("key"))
            return "[REDACTED]";

        // Prevent newlines/control characters from forging log records.
        value = Regex.Replace(value, @"[\r\n\t\0-\x1F\x7F]", " ");
        // Do not expose user names or absolute filesystem locations in support logs.
        value = Regex.Replace(value, @"(?i)(?:[A-Z]:|\\\\)[^ ]+", "[PATH]");
        return value.Length > 512 ? value[..512] + "…" : value;
    }

    internal static string SafeToken(string value) =>
        Regex.Replace(value ?? "", @"[^A-Za-z0-9_.:-]", "_");

    private static void RotateIfNeeded(string path, int incomingLength)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingLength <= MaxFileBytes) return;
        var rotated = path + "." + (++_sequence);
        File.Move(path, rotated, true);
        var files = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(MaxFiles - 1);
        foreach (var file in files)
        {
            try { File.Delete(file); } catch { }
        }
    }
}
