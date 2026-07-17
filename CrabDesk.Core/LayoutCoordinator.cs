namespace CrabDesk.Core;

public static class LayoutCoordinator
{
    public static void NormalizeForMonitors(CrabDeskState state, IReadOnlyList<MonitorLayout> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }

        var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
        foreach (var box in state.Boxes)
        {
            var monitor = monitors.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, box.MonitorId, StringComparison.OrdinalIgnoreCase)) ?? primary;
            box.MonitorId = monitor.Id;
            var localBounds = new LayoutRect(0, 0, monitor.WorkArea.Width, monitor.WorkArea.Height);
            var minimumWidth = DesktopItemLayoutEngine.GetMinimumBoxWidth(
                box.ViewMode,
                box.Appearance.IconSize,
                state.Settings.Appearance.IconHorizontalSpacing);
            box.Bounds = box.Bounds.Clamp(localBounds, minimumWidth);
        }
    }

    public static bool TryMoveBoxToMonitor(
        DesktopBox box,
        IReadOnlyList<MonitorLayout> monitors,
        double screenPixelX,
        double screenPixelY,
        double grabOffsetX,
        double grabOffsetY,
        double gridStep = 0)
    {
        var target = monitors.FirstOrDefault(monitor => monitor.PixelBounds.Contains(screenPixelX, screenPixelY));
        if (target is null || string.Equals(target.Id, box.MonitorId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var scale = target.DpiScale > 0 ? target.DpiScale : 1;
        var localCursorX = (screenPixelX - target.PixelBounds.X) / scale;
        var localCursorY = (screenPixelY - target.PixelBounds.Y) / scale;
        box.MonitorId = target.Id;
        box.Bounds = new LayoutRect(
            LayoutGrid.Snap(localCursorX - grabOffsetX, gridStep),
            LayoutGrid.Snap(localCursorY - grabOffsetY, gridStep),
            box.Bounds.Width,
            box.Bounds.Height).Clamp(new LayoutRect(0, 0, target.WorkArea.Width, target.WorkArea.Height));
        return true;
    }

    public static Guid ResolveBox(CrabDeskState state, DesktopItemRef item)
    {
        var key = item.Key.ToString();
        if (state.Assignments.TryGetValue(key, out var assigned) && state.Boxes.Any(box => box.Id == assigned))
        {
            return assigned;
        }

        var target = item.IsSystem ? state.SystemBox : state.UnassignedBox;
        target ??= state.Boxes.FirstOrDefault();
        if (target is null)
        {
            return Guid.Empty;
        }
        state.Assignments[key] = target.Id;
        return target.Id;
    }

    public static int ResetLayout(CrabDeskState state, string monitorId)
    {
        var defaults = JsonLayoutStore.CreateDefaultState(monitorId);
        state.Boxes = defaults.Boxes;
        state.Assignments.Clear();

        var validBoxIds = state.Boxes.Select(box => box.Id).ToHashSet();
        var disabledRules = 0;
        foreach (var rule in state.OrganizationRules.Where(rule =>
            rule.Action == OrganizationRuleAction.AssignToBox &&
            rule.TargetBoxId is { } target &&
            !validBoxIds.Contains(target)))
        {
            rule.Enabled = false;
            rule.TargetBoxId = null;
            disabledRules++;
        }
        return disabledRules;
    }

    public static bool ReorderItems(
        DesktopBox box,
        IReadOnlyList<string> currentKeys,
        IReadOnlyCollection<string> movingKeys,
        string? beforeKey)
    {
        if (currentKeys.Count == 0 || movingKeys.Count == 0)
        {
            return false;
        }
        var comparer = StringComparer.OrdinalIgnoreCase;
        var currentSet = currentKeys.ToHashSet(comparer);
        var movingSet = movingKeys.Where(currentSet.Contains).ToHashSet(comparer);
        if (movingSet.Count == 0 || (beforeKey is not null && movingSet.Contains(beforeKey)))
        {
            return false;
        }

        var normalized = box.SortMode == BoxSortMode.Manual
            ? box.ItemOrder
                .Where(currentSet.Contains)
                .Distinct(comparer)
                .ToList()
            : [];
        normalized.AddRange(currentKeys.Where(key => !normalized.Contains(key, comparer)));
        var moving = normalized.Where(movingSet.Contains).ToArray();
        normalized.RemoveAll(key => movingSet.Contains(key));
        var targetIndex = beforeKey is null
            ? normalized.Count
            : normalized.FindIndex(key => comparer.Equals(key, beforeKey));
        if (targetIndex < 0)
        {
            targetIndex = normalized.Count;
        }
        normalized.InsertRange(targetIndex, moving);
        var changed = box.SortMode == BoxSortMode.Manual
            ? !box.ItemOrder.SequenceEqual(normalized, comparer)
            : !currentKeys.SequenceEqual(normalized, comparer);
        if (!changed)
        {
            return false;
        }
        box.SortMode = BoxSortMode.Manual;
        box.ItemOrder = normalized;
        return true;
    }
}

public static class BoxTransferPolicy
{
    public static BoxTransferEffect Resolve(
        bool internalItems,
        bool sourceMapped,
        bool targetMapped,
        bool shiftPressed,
        bool controlPressed)
    {
        if (internalItems && !sourceMapped && !targetMapped)
        {
            return BoxTransferEffect.VirtualMove;
        }
        if (controlPressed)
        {
            return BoxTransferEffect.CopyFiles;
        }
        return shiftPressed ? BoxTransferEffect.MoveFiles : BoxTransferEffect.CopyFiles;
    }
}
