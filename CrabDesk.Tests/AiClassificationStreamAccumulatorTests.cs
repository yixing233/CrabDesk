using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class AiClassificationStreamAccumulatorTests
{
    [Fact]
    public void SeparatesReasoningAndStructuredOutputAndTruncatesBoth()
    {
        var accumulator = new AiClassificationStreamAccumulator(4, 5);
        accumulator.Append(new AiClassificationModelStreamUpdate(
            AiClassificationModelStreamKind.Reasoning,
            "abcdef"));
        accumulator.Append(new AiClassificationModelStreamUpdate(
            AiClassificationModelStreamKind.Content,
            "123456"));

        var snapshot = accumulator.Flush();

        Assert.StartsWith("abcd", snapshot.Reasoning);
        Assert.Contains("…", snapshot.Reasoning);
        Assert.StartsWith("12345", snapshot.StructuredOutput);
        Assert.Contains("…", snapshot.StructuredOutput);
    }

    [Fact]
    public void ClearsBufferedOutput()
    {
        var accumulator = new AiClassificationStreamAccumulator();
        accumulator.Append(new AiClassificationModelStreamUpdate(
            AiClassificationModelStreamKind.Content,
            "{}"));

        accumulator.Clear();

        Assert.Equal(new AiClassificationStreamSnapshot(string.Empty, string.Empty), accumulator.Flush());
    }

    [Fact]
    public void TryFlushOnlyReturnsWhenNewOutputArrives()
    {
        var accumulator = new AiClassificationStreamAccumulator();

        Assert.False(accumulator.TryFlush(out _));

        accumulator.Append(new AiClassificationModelStreamUpdate(
            AiClassificationModelStreamKind.Content,
            "{}"));

        Assert.True(accumulator.TryFlush(out var first));
        Assert.Equal("{}", first.StructuredOutput);
        Assert.False(accumulator.TryFlush(out _));
    }
}
