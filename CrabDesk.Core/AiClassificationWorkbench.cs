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

    /// <summary>
    /// Folds the user's review into the model's preview: a manual label replaces the
    /// suggestion for that item (or supplies one where the model had none), and every
    /// excluded item is dropped so it stays on the desktop. Keys outside the preview's
    /// scope and labels outside <paramref name="allowedLabels"/> are ignored.
    /// </summary>
    public static IReadOnlyList<AiClassificationAssignment> MergeManualAssignments(
        AiClassificationPreview preview,
        IReadOnlyDictionary<string, string> manualLabels,
        IReadOnlyCollection<string> allowedLabels,
        IReadOnlyCollection<string>? excludedItemKeys = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(manualLabels);
        ArgumentNullException.ThrowIfNull(allowedLabels);

        var requestedKeys = preview.RequestedItemKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedKeys = (excludedItemKeys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var labels = allowedLabels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignments = preview.Assignments
            .Where(assignment => requestedKeys.Contains(assignment.ItemKey) &&
                                 !excludedKeys.Contains(assignment.ItemKey) &&
                                 labels.Contains(assignment.Label))
            .GroupBy(assignment => assignment.ItemKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(assignment => TryResolveManualLabel(assignment.ItemKey, out var label)
                ? assignment with { Label = label }
                : assignment)
            .ToList();
        var assignedKeys = assignments
            .Select(assignment => assignment.ItemKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var itemKey in preview.RequestedItemKeys)
        {
            if (assignedKeys.Contains(itemKey) ||
                excludedKeys.Contains(itemKey) ||
                !TryResolveManualLabel(itemKey, out var label))
            {
                continue;
            }

            assignments.Add(new AiClassificationAssignment(itemKey, itemKey, label));
            assignedKeys.Add(itemKey);
        }

        return assignments;

        bool TryResolveManualLabel(string itemKey, out string label)
        {
            label = string.Empty;
            if (!manualLabels.TryGetValue(itemKey, out var requestedLabel) ||
                string.IsNullOrWhiteSpace(requestedLabel) ||
                !labels.TryGetValue(requestedLabel.Trim(), out var resolved))
            {
                return false;
            }

            label = resolved;
            return true;
        }
    }
}
