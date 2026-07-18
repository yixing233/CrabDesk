namespace CrabDesk.Core;

public static class OrganizationRuleOrdering
{
    public static bool Move(List<OrganizationRule> rules, Guid ruleId, int direction)
    {
        if (direction == 0 || rules.Count < 2)
        {
            return false;
        }

        var ordered = rules.OrderBy(rule => rule.Priority).ToList();
        var index = ordered.FindIndex(rule => rule.Id == ruleId);
        if (index < 0)
        {
            return false;
        }

        var target = Math.Clamp(index + Math.Sign(direction), 0, ordered.Count - 1);
        if (target == index)
        {
            return false;
        }

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var priorityIndex = 0; priorityIndex < ordered.Count; priorityIndex++)
        {
            ordered[priorityIndex].Priority = (priorityIndex + 1) * 10;
        }

        rules.Clear();
        rules.AddRange(ordered);
        return true;
    }
}
