using System.Threading.Channels;

namespace CrabDesk.Runtime;

internal sealed class BufferedDiagnosticWriter : IAsyncDisposable
{
    private readonly Channel<Entry> _entries;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _append;
    private readonly Task _worker;

    internal BufferedDiagnosticWriter(
        int capacity,
        Func<IReadOnlyList<string>, CancellationToken, Task> append)
    {
        _append = append;
        _entries = Channel.CreateBounded<Entry>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _worker = Task.Run(DrainAsync);
    }

    internal bool TryWrite(string line) => _entries.Writer.TryWrite(new Entry(line, null));

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _entries.Writer.WriteAsync(new Entry(null, completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        var lines = new List<string>(128);
        var flushCompletions = new List<TaskCompletionSource<bool>>();
        try
        {
            while (await _entries.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                DrainAvailableEntries(lines, flushCompletions);
                await Task.Delay(10).ConfigureAwait(false);
                DrainAvailableEntries(lines, flushCompletions);
                await FlushBatchAsync(lines, flushCompletions).ConfigureAwait(false);
            }

            DrainAvailableEntries(lines, flushCompletions);
            await FlushBatchAsync(lines, flushCompletions).ConfigureAwait(false);
        }
        catch
        {
            CompleteFlushes(flushCompletions);
        }
    }

    private void DrainAvailableEntries(
        ICollection<string> lines,
        ICollection<TaskCompletionSource<bool>> flushCompletions)
    {
        while (_entries.Reader.TryRead(out var entry))
        {
            if (entry.Line is not null)
            {
                lines.Add(entry.Line);
            }
            if (entry.FlushCompletion is not null)
            {
                flushCompletions.Add(entry.FlushCompletion);
            }
        }
    }

    private async Task FlushBatchAsync(
        List<string> lines,
        List<TaskCompletionSource<bool>> flushCompletions)
    {
        try
        {
            if (lines.Count > 0)
            {
                await _append(lines, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            lines.Clear();
            CompleteFlushes(flushCompletions);
        }
    }

    private static void CompleteFlushes(ICollection<TaskCompletionSource<bool>> flushCompletions)
    {
        foreach (var completion in flushCompletions)
        {
            completion.TrySetResult(true);
        }
        flushCompletions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _entries.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private readonly record struct Entry(string? Line, TaskCompletionSource<bool>? FlushCompletion);
}
