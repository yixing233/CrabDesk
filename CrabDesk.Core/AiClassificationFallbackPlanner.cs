namespace CrabDesk.Core;

public static class AiClassificationFallbackPlanner
{
    public static IReadOnlyList<AiClassificationInput> SelectForWebSearch(
        IReadOnlyList<AiClassificationInput> items,
        IReadOnlyList<AiClassificationAssignment> assignments,
        int maximumItems)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(assignments);

        if (maximumItems <= 0)
        {
            return [];
        }

        var assignedItemKeys = assignments
            .Select(assignment => assignment.ItemKey)
            .ToHashSet(StringComparer.Ordinal);
        return items
            .Where(item => !assignedItemKeys.Contains(item.ItemKey))
            .Take(maximumItems)
            .ToArray();
    }
}
