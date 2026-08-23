using System.Text;

namespace CrabDesk.Runtime;

internal static class DiagnosticLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int QueueCapacity = 2048;
    private static readonly Lazy<BufferedDiagnosticWriter> Writer = new(CreateWriter);
    private static readonly bool VerboseEnabled = string.Equals(
        Environment.GetEnvironmentVariable("CRABDESK_VERBOSE_LOG"),
        "1",
        StringComparison.Ordinal);

    internal static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrabDesk",
        "logs",
        "crabdesk.log");

    internal static void Initialize() => _ = Writer.Value;

    internal static void Info(string message) => Write("INFO", message, null);

    internal static void Verbose(string message)
    {
        if (VerboseEnabled)
        {
            Write("VERBOSE", message, null);
        }
    }

    internal static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    internal static void Flush(TimeSpan timeout)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            Writer.Value.FlushAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    private static BufferedDiagnosticWriter CreateWriter() => new(QueueCapacity, AppendBatchAsync);

    private static async Task AppendBatchAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
            {
                File.Move(LogPath, LogPath + ".previous", true);
            }
            await File.AppendAllTextAsync(LogPath, string.Concat(lines), Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void Write(string level, string message, Exception? exception)
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
        _ = Writer.Value.TryWrite(builder.ToString());
    }
}
