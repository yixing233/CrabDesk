using System.IO.Enumeration;

namespace CrabDesk.Core;

public sealed record OrganizationDecision(
    string ItemKey,
    string ItemName,
    Guid RuleId,
    string RuleTitle,
    OrganizationRuleAction Action,
    Guid? TargetBoxId);

public sealed record OrganizationApplyResult(
    int Assigned,
    int Unassigned,
    int Ignored,
    int InvalidTargets,
    IReadOnlyList<OrganizationDecision> Decisions);

public sealed record OrganizationRuleConflict(
    Guid FirstRuleId,
    string FirstRuleTitle,
    Guid SecondRuleId,
    string SecondRuleTitle);

public sealed class OrganizationRuleEngine : IOrganizationRuleEngine
{
    public IReadOnlyList<OrganizationDecision> Preview(
        CrabDeskState state,
        IReadOnlyList<DesktopItemRef> items,
        bool reassignExistingItems = false)
    {
        var rules = state.OrganizationRules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(entry => entry.Rule.Enabled)
            .OrderBy(entry => entry.Rule.Priority)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Rule)
            .ToArray();
        var decisions = new List<OrganizationDecision>();
        foreach (var item in items)
        {
            var key = item.Key.ToString();
            if (!reassignExistingItems && state.Assignments.ContainsKey(key))
            {
                continue;
            }

            var rule = rules.FirstOrDefault(candidate => MatchesRule(candidate, item));
            if (rule is null)
            {
                continue;
            }

            decisions.Add(new OrganizationDecision(
                key,
                item.DisplayName,
                rule.Id,
                rule.Title,
                rule.Action,
                rule.TargetBoxId));
        }
        return decisions;
    }

    public OrganizationApplyResult Apply(
        CrabDeskState state,
        IReadOnlyList<DesktopItemRef> items,
        bool reassignExistingItems = false)
    {
        var decisions = Preview(state, items, reassignExistingItems);
        var validBoxes = state.Boxes.Select(box => box.Id).ToHashSet();
        var assigned = 0;
        var unassigned = 0;
        var ignored = 0;
        var invalidTargets = 0;
        foreach (var decision in decisions)
        {
            switch (decision.Action)
            {
                case OrganizationRuleAction.AssignToBox:
                    if (decision.TargetBoxId is not { } target || !validBoxes.Contains(target))
                    {
                        invalidTargets++;
                        break;
                    }
                    state.Assignments[decision.ItemKey] = target;
                    assigned++;
                    break;
                case OrganizationRuleAction.KeepUnassigned:
                    if (reassignExistingItems && state.Assignments.Remove(decision.ItemKey))
                    {
                        unassigned++;
                    }
                    break;
                case OrganizationRuleAction.Ignore:
                    ignored++;
                    break;
            }
        }
        return new OrganizationApplyResult(assigned, unassigned, ignored, invalidTargets, decisions);
    }

    public IReadOnlyList<OrganizationRuleConflict> FindConflicts(CrabDeskState state)
    {
        var rules = state.OrganizationRules.Where(rule => rule.Enabled).ToArray();
        var conflicts = new List<OrganizationRuleConflict>();
        for (var first = 0; first < rules.Length; first++)
        {
            for (var second = first + 1; second < rules.Length; second++)
            {
                if ((BuiltInOrganizationRules.IsFallback(rules[first]) &&
                        rules[first].Priority > rules[second].Priority) ||
                    (BuiltInOrganizationRules.IsFallback(rules[second]) &&
                        rules[second].Priority > rules[first].Priority))
                {
                    continue;
                }
                if (!MayOverlap(rules[first], rules[second]))
                {
                    continue;
                }
                conflicts.Add(new OrganizationRuleConflict(
                    rules[first].Id,
                    rules[first].Title,
                    rules[second].Id,
                    rules[second].Title));
            }
        }
        return conflicts;
    }

    public static bool MatchesRule(OrganizationRule rule, DesktopItemRef item)
    {
        if (rule.ItemKinds.Count > 0 && !rule.ItemKinds.Contains(item.Kind))
        {
            return false;
        }

        if (!FileSystemName.MatchesSimpleExpression(
                string.IsNullOrWhiteSpace(rule.NamePattern) ? "*" : rule.NamePattern,
                item.DisplayName,
                true))
        {
            return false;
        }

        if (rule.Extensions.Count == 0)
        {
            return true;
        }

        var extension = Path.GetExtension(item.FileSystemPath ?? item.DisplayName);
        return rule.Extensions.Any(candidate => string.Equals(
            NormalizeExtension(candidate),
            NormalizeExtension(extension),
            StringComparison.OrdinalIgnoreCase));
    }

    private static bool MayOverlap(OrganizationRule first, OrganizationRule second)
    {
        var kindsOverlap = first.ItemKinds.Count == 0 || second.ItemKinds.Count == 0 ||
            first.ItemKinds.Intersect(second.ItemKinds).Any();
        if (!kindsOverlap)
        {
            return false;
        }

        var firstExtensions = first.Extensions.Select(NormalizeExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var secondExtensions = second.Extensions.Select(NormalizeExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extensionsOverlap = firstExtensions.Count == 0 || secondExtensions.Count == 0 ||
            firstExtensions.Overlaps(secondExtensions);
        if (!extensionsOverlap)
        {
            return false;
        }

        var firstPattern = string.IsNullOrWhiteSpace(first.NamePattern) ? "*" : first.NamePattern.Trim();
        var secondPattern = string.IsNullOrWhiteSpace(second.NamePattern) ? "*" : second.NamePattern.Trim();
        return firstPattern == "*" || secondPattern == "*" ||
            string.Equals(firstPattern, secondPattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string value)
    {
        var extension = value.Trim();
        if (extension.Length == 0)
        {
            return string.Empty;
        }
        return extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    }
}
