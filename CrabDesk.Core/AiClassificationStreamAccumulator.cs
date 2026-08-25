using System.Text;

namespace CrabDesk.Core;

/// <summary>
/// Buffers model stream updates off the UI thread. Consumers decide when to
/// call <see cref="Flush"/>, which keeps high-frequency SSE chunks from
/// forcing a layout pass for every token.
/// </summary>
public sealed class AiClassificationStreamAccumulator
{
    private const string TruncationMarker = "…";
    private readonly object _gate = new();
    private readonly StringBuilder _reasoning = new();
    private readonly StringBuilder _structuredOutput = new();
    private readonly int _maximumReasoningLength;
    private readonly int _maximumStructuredOutputLength;
    private bool _reasoningTruncated;
    private bool _structuredOutputTruncated;
    private bool _hasUnflushedChanges;

    public AiClassificationStreamAccumulator(
        int maximumReasoningLength = 48 * 1024,
        int maximumStructuredOutputLength = 128 * 1024)
    {
        _maximumReasoningLength = Math.Max(1, maximumReasoningLength);
        _maximumStructuredOutputLength = Math.Max(1, maximumStructuredOutputLength);
    }

    public void Append(AiClassificationModelStreamUpdate update)
    {
        if (string.IsNullOrEmpty(update.Text))
        {
            return;
        }

        lock (_gate)
        {
            var changed = update.Kind switch
            {
                AiClassificationModelStreamKind.Reasoning => AppendBounded(
                    _reasoning,
                    update.Text,
                    _maximumReasoningLength,
                    ref _reasoningTruncated),
                AiClassificationModelStreamKind.Content => AppendBounded(
                    _structuredOutput,
                    update.Text,
                    _maximumStructuredOutputLength,
                    ref _structuredOutputTruncated),
                _ => false
            };
            _hasUnflushedChanges |= changed;
        }
    }

    /// <summary>
    /// Returns a snapshot only when output has changed since the previous call.
    /// This lets a UI timer avoid repeatedly measuring and rendering an unchanged
    /// stream buffer while a model is waiting for its next chunk.
    /// </summary>
    public bool TryFlush(out AiClassificationStreamSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_hasUnflushedChanges)
            {
                snapshot = default!;
                return false;
            }

            snapshot = new AiClassificationStreamSnapshot(
                _reasoning.ToString(),
                _structuredOutput.ToString());
            _hasUnflushedChanges = false;
            return true;
        }
    }

    public AiClassificationStreamSnapshot Flush()
    {
        lock (_gate)
        {
            return new AiClassificationStreamSnapshot(
                _reasoning.ToString(),
                _structuredOutput.ToString());
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _reasoning.Clear();
            _structuredOutput.Clear();
            _reasoningTruncated = false;
            _structuredOutputTruncated = false;
            _hasUnflushedChanges = false;
        }
    }

    private static bool AppendBounded(
        StringBuilder buffer,
        string text,
        int maximumLength,
        ref bool truncated)
    {
        if (truncated)
        {
            return false;
        }

        var remaining = maximumLength - buffer.Length;
        if (remaining <= 0)
        {
            buffer.Append(TruncationMarker);
            truncated = true;
            return true;
        }

        if (text.Length <= remaining)
        {
            buffer.Append(text);
            return true;
        }

        buffer.Append(text, 0, remaining);
        buffer.Append(TruncationMarker);
        truncated = true;
        return true;
    }
}

public sealed record AiClassificationStreamSnapshot(string Reasoning, string StructuredOutput);
