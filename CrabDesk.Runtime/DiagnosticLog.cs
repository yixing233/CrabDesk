using System.Text;

namespace CrabDesk.Runtime;

internal static class DiagnosticLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static bool _initialized;

    internal static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrabDesk",
        "logs",
        "crabdesk.log");

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                {
                    File.Move(LogPath, LogPath + ".previous", true);
                }
                _initialized = true;
            }
            catch
            {
            }
        }
    }

    internal static void Info(string message) => Write("INFO", message, null);

    internal static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        Initialize();
        lock (Sync)
        {
            try
            {
                var builder = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append(" [")
                    .Append(level)
                    .Append("] [T")
                    .Append(Environment.CurrentManagedThreadId)
                    .Append("] ")
                    .Append(message);
                if (exception is not null)
                {
                    builder.AppendLine().Append(exception);
                }
                builder.AppendLine();
                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
