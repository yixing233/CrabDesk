namespace CrabDesk.Core;

/// <summary>
/// Pure workbench operations shared by the page and runtime validation path.
/// </summary>
public static class AiClassificationWorkbench
{
    public static IReadOnlyList<AiClassificationWorkspaceItem> Select(
        AiClassificationWorkspace workspace,
        IReadOnlyCollection<string> selectedItemKeys)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(selectedItemKeys);

        var selected = selectedItemKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return workspace.Items
            .Where(item => selected.Contains(item.ItemKey))
            .ToArray();
    }

    public static IReadOnlyList<AiClassificationAssignment> MergeManualAssignments(
        AiClassificationPreview preview,
        IReadOnlyDictionary<string, string> manualLabels,
        IReadOnlyCollection<string> allowedLabels)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(manualLabels);
        ArgumentNullException.ThrowIfNull(allowedLabels);

        var requestedKeys = preview.RequestedItemKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var labels = allowedLabels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignments = preview.Assignments
            .Where(assignment => requestedKeys.Contains(assignment.ItemKey) &&
                                 labels.Contains(assignment.Label))
            .GroupBy(assignment => assignment.ItemKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var assignedKeys = assignments
            .Select(assignment => assignment.ItemKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var itemKey in preview.RequestedItemKeys)
        {
            if (assignedKeys.Contains(itemKey) ||
                !manualLabels.TryGetValue(itemKey, out var requestedLabel) ||
                string.IsNullOrWhiteSpace(requestedLabel) ||
                !labels.TryGetValue(requestedLabel.Trim(), out var label))
            {
                continue;
            }

            assignments.Add(new AiClassificationAssignment(itemKey, itemKey, label));
            assignedKeys.Add(itemKey);
        }

        return assignments;
    }
}
