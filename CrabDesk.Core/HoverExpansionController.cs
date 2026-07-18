namespace CrabDesk.Core;

public readonly record struct HoverExpansionTransition(Guid? ExpandedBoxId, Guid? CollapsedBoxId)
{
    public bool Changed => ExpandedBoxId.HasValue || CollapsedBoxId.HasValue;
}

public sealed class HoverExpansionController
{
    private readonly TimeSpan _expandDelay;
    private readonly TimeSpan _collapseDelay;
    private Guid? _candidateBoxId;
    private DateTimeOffset? _candidateSince;
    private DateTimeOffset? _outsideSince;

    public HoverExpansionController(TimeSpan expandDelay, TimeSpan collapseDelay)
    {
        _expandDelay = expandDelay;
        _collapseDelay = collapseDelay;
    }

    public Guid? ExpandedBoxId { get; private set; }

    public void AdoptExpanded(Guid boxId)
    {
        ExpandedBoxId = boxId;
        _candidateBoxId = null;
        _candidateSince = null;
        _outsideSince = null;
    }

    public HoverExpansionTransition Update(
        Guid? collapsedHeaderBoxId,
        bool pointerInsideExpandedBox,
        DateTimeOffset now)
    {
        if (ExpandedBoxId is { } expandedBoxId)
        {
            _candidateBoxId = null;
            _candidateSince = null;
            if (pointerInsideExpandedBox)
            {
                _outsideSince = null;
                return default;
            }

            _outsideSince ??= now;
            if (now - _outsideSince < _collapseDelay)
            {
                return default;
            }

            ExpandedBoxId = null;
            _outsideSince = null;
            return new HoverExpansionTransition(null, expandedBoxId);
        }

        _outsideSince = null;
        if (collapsedHeaderBoxId is not { } candidateBoxId)
        {
            _candidateBoxId = null;
            _candidateSince = null;
            return default;
        }

        if (_candidateBoxId != candidateBoxId)
        {
            _candidateBoxId = candidateBoxId;
            _candidateSince = now;
            return default;
        }

        _candidateSince ??= now;
        if (now - _candidateSince < _expandDelay)
        {
            return default;
        }

        ExpandedBoxId = candidateBoxId;
        _candidateBoxId = null;
        _candidateSince = null;
        return new HoverExpansionTransition(candidateBoxId, null);
    }

    public Guid? Reset()
    {
        var expandedBoxId = ExpandedBoxId;
        ExpandedBoxId = null;
        _candidateBoxId = null;
        _candidateSince = null;
        _outsideSince = null;
        return expandedBoxId;
    }
}
