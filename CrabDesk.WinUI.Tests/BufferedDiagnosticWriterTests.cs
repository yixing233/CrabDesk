using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class BufferedDiagnosticWriterTests
{
    [Fact]
    public async Task WriterDrainsSeveralEntriesAsOneBatch()
    {
        var batches = new List<IReadOnlyList<string>>();
        await using var writer = new BufferedDiagnosticWriter(
            32,
            (lines, _) =>
            {
                batches.Add(lines.ToArray());
                return Task.CompletedTask;
            });

        Assert.True(writer.TryWrite("one"));
        Assert.True(writer.TryWrite("two"));
        await writer.FlushAsync(CancellationToken.None);

        Assert.Single(batches);
        Assert.Equal(new[] { "one", "two" }, batches[0]);
    }
}
