using System.Text;

namespace CrabDesk.WinUI;

internal static class AppDiagnostic
{
    private static readonly object Sync = new();
    internal static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrabDesk",
        "logs",
        "winui.log");

    internal static void Info(string message) => Write("INFO", message, null);
    internal static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var builder = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append(" [").Append(level).Append("] ")
                    .Append(message);
                if (exception is not null) builder.AppendLine().Append(exception);
                builder.AppendLine();
                File.AppendAllText(Path, builder.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
