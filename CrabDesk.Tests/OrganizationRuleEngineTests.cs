using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class OrganizationRuleEngineTests
{
    private readonly OrganizationRuleEngine _engine = new();

    [Fact]
    public void LowerPriorityNumberWinsWhenRulesOverlap()
    {
        var firstBox = new DesktopBox { Title = "文档" };
        var secondBox = new DesktopBox { Title = "其他" };
        var state = new CrabDeskState
        {
            Boxes = [firstBox, secondBox],
            OrganizationRules =
            [
                new OrganizationRule
                {
                    Title = "后置规则",
                    Priority = 20,
                    Extensions = [".txt"],
                    TargetBoxId = secondBox.Id
                },
                new OrganizationRule
                {
                    Title = "优先规则",
                    Priority = 10,
                    Extensions = ["txt"],
                    TargetBoxId = firstBox.Id
                }
            ]
        };

        var decision = Assert.Single(_engine.Preview(state, [Item("notes.txt", DesktopItemKind.File)]));

        Assert.Equal("优先规则", decision.RuleTitle);
        Assert.Equal(firstBox.Id, decision.TargetBoxId);
    }

    [Fact]
    public void RuleMatchesKindNamePatternAndNormalizedExtension()
    {
        var box = new DesktopBox { Title = "图片" };
        var rule = new OrganizationRule
        {
            Title = "截图",
            ItemKinds = [DesktopItemKind.File],
            NamePattern = "Screenshot*",
            Extensions = ["PNG"],
            TargetBoxId = box.Id
        };
        var state = new CrabDeskState { Boxes = [box], OrganizationRules = [rule] };

        var decisions = _engine.Preview(state,
        [
            Item("Screenshot 01.PNG", DesktopItemKind.File),
            Item("Photo.PNG", DesktopItemKind.File),
            Item("Screenshot folder.PNG", DesktopItemKind.Folder)
        ]);

        Assert.Single(decisions);
        Assert.Equal("Screenshot 01.PNG", decisions[0].ItemName);
    }

    [Fact]
    public void ExistingAssignmentsAreSkippedUnlessReassignmentIsEnabled()
    {
        var sourceBox = new DesktopBox { Title = "原盒子" };
        var targetBox = new DesktopBox { Title = "新盒子" };
        var item = Item("report.pdf", DesktopItemKind.File);
        var state = new CrabDeskState
        {
            Boxes = [sourceBox, targetBox],
            Assignments = new Dictionary<string, Guid> { [item.Key.ToString()] = sourceBox.Id },
            OrganizationRules =
            [
                new OrganizationRule
                {
                    Extensions = ["pdf"],
                    TargetBoxId = targetBox.Id
                }
            ]
        };

        Assert.Empty(_engine.Preview(state, [item]));
        var result = _engine.Apply(state, [item], true);

        Assert.Equal(1, result.Assigned);
        Assert.Equal(targetBox.Id, state.Assignments[item.Key.ToString()]);
    }

    [Fact]
    public void InvalidTargetDoesNotChangeAssignments()
    {
        var state = new CrabDeskState
        {
            Boxes = [new DesktopBox()],
            OrganizationRules = [new OrganizationRule { TargetBoxId = Guid.NewGuid() }]
        };
        var item = Item("anything.txt", DesktopItemKind.File);

        var result = _engine.Apply(state, [item]);

        Assert.Equal(1, result.InvalidTargets);
        Assert.Empty(state.Assignments);
    }

    [Fact]
    public void KeepUnassignedRuleRemovesAssignmentOnlyDuringReassignment()
    {
        var box = new DesktopBox();
        var item = Item("keep.tmp", DesktopItemKind.File);
        var state = new CrabDeskState
        {
            Boxes = [box],
            Assignments = new Dictionary<string, Guid> { [item.Key.ToString()] = box.Id },
            OrganizationRules =
            [
                new OrganizationRule
                {
                    Extensions = ["tmp"],
                    Action = OrganizationRuleAction.KeepUnassigned
                }
            ]
        };

        var result = _engine.Apply(state, [item], true);

        Assert.Equal(1, result.Unassigned);
        Assert.Empty(state.Assignments);
    }

    [Fact]
    public void ConflictDetectionFindsOverlappingEnabledRules()
    {
        var state = new CrabDeskState
        {
            OrganizationRules =
            [
                new OrganizationRule { Title = "所有文件", ItemKinds = [DesktopItemKind.File] },
                new OrganizationRule { Title = "PDF", ItemKinds = [DesktopItemKind.File], Extensions = ["pdf"] },
                new OrganizationRule { Title = "图片", ItemKinds = [DesktopItemKind.File], Extensions = ["png"] },
                new OrganizationRule { Title = "已禁用", Enabled = false }
            ]
        };

        var conflicts = _engine.FindConflicts(state);

        Assert.Equal(2, conflicts.Count);
        Assert.All(conflicts, conflict => Assert.Equal("所有文件", conflict.FirstRuleTitle));
    }

    [Fact]
    public void BuiltInFallbackOnlyHandlesItemsNotMatchedByEarlierRules()
    {
        var state = JsonLayoutStore.CreateDefaultState();
        foreach (var rule in state.OrganizationRules)
        {
            var box = new DesktopBox { Title = rule.Title };
            state.Boxes.Add(box);
            rule.TargetBoxId = box.Id;
        }

        var decisions = _engine.Preview(state,
        [
            Item("report.pdf", DesktopItemKind.File),
            Item("tool.exe", DesktopItemKind.File),
            Item("Projects", DesktopItemKind.Folder)
        ]);

        Assert.Equal(3, decisions.Count);
        Assert.Equal("文档", decisions.Single(decision => decision.ItemName == "report.pdf").RuleTitle);
        Assert.Equal("其它", decisions.Single(decision => decision.ItemName == "tool.exe").RuleTitle);
        Assert.Equal("目录", decisions.Single(decision => decision.ItemName == "Projects").RuleTitle);
        Assert.Empty(_engine.FindConflicts(state));
    }

    [Fact]
    public void MovingRuleRewritesPrioritiesInSwappedOrder()
    {
        var rules = BuiltInOrganizationRules.CreateDefaults();
        var directory = rules.Single(rule => rule.BuiltInId == BuiltInOrganizationRules.DirectoryId);

        var moved = OrganizationRuleOrdering.Move(rules, directory.Id, 1);

        Assert.True(moved);
        Assert.Equal(["文档", "目录", "图片", "压缩", "其它"], rules.Select(rule => rule.Title));
        Assert.Equal([10, 20, 30, 40, 50], rules.Select(rule => rule.Priority));
    }

    private static DesktopItemRef Item(string name, DesktopItemKind kind) => new()
    {
        Key = new DesktopItemKey("test", Guid.NewGuid().ToString("N")),
        DisplayName = name,
        ParsingName = name,
        FileSystemPath = Path.Combine("C:\\Desktop", name),
        Kind = kind
    };
}
