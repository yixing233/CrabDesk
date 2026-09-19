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
    public void UnmatchedItemsStayOnDesktopAfterFallbackRuleRemoval()
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

        Assert.Equal(2, decisions.Count);
        Assert.Equal("文档", decisions.Single(decision => decision.ItemName == "report.pdf").RuleTitle);
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
        Assert.Equal(["文档", "目录", "图片", "压缩"], rules.Select(rule => rule.Title));
        Assert.Equal([10, 20, 30, 40], rules.Select(rule => rule.Priority));
    }

    [Fact]
    public void MappedBoxIsNotAValidRuleTarget()
    {
        var mappedBox = new DesktopBox
        {
            Title = "Mapped",
            MappedFolder = new MappedFolderSettings { Path = "C:\\Mirror", IsReadOnly = true }
        };
        var state = new CrabDeskState
        {
            Boxes = [mappedBox],
            OrganizationRules =
            [
                new OrganizationRule
                {
                    Title = "IntoMapped",
                    Extensions = ["txt"],
                    TargetBoxId = mappedBox.Id
                }
            ]
        };
        var item = Item("notes.txt", DesktopItemKind.File);

        var result = _engine.Apply(state, [item]);

        Assert.Equal(1, result.InvalidTargets);
        Assert.Empty(state.Assignments);
    }

    [Fact]
    public void OverlappingWildcardPatternsAreReportedAsConflicts()
    {
        var state = new CrabDeskState
        {
            OrganizationRules =
            [
                new OrganizationRule { Title = "Reports", NamePattern = "report*" },
                new OrganizationRule { Title = "Year2026", NamePattern = "*2026" }
            ]
        };

        var conflicts = _engine.FindConflicts(state);

        Assert.Single(conflicts);
    }

    [Fact]
    public void DisjointWildcardPatternsAreNotReportedAsConflicts()
    {
        var state = new CrabDeskState
        {
            OrganizationRules =
            [
                new OrganizationRule { Title = "Text", NamePattern = "*.txt" },
                new OrganizationRule { Title = "Png", NamePattern = "*.png" }
            ]
        };

        Assert.Empty(_engine.FindConflicts(state));
    }

    [Fact]
    public void PatternThatNeedsBothSidesOfWildcardsIsReported()
    {
        var state = new CrabDeskState
        {
            OrganizationRules =
            [
                new OrganizationRule { Title = "StartsReport", NamePattern = "report*" },
                new OrganizationRule { Title = "EndsPdf", NamePattern = "*.pdf" }
            ]
        };

        // "report.pdf" satisfies both patterns.
        Assert.Single(_engine.FindConflicts(state));
    }

    [Fact]
    public void ProjectedCountGivesShadowedItemToTheWinningRuleOnly()
    {
        var images = new OrganizationRule { Title = "图片", Priority = 10, Extensions = ["png"] };
        var allFiles = new OrganizationRule { Title = "文件", Priority = 20, ItemKinds = [DesktopItemKind.File] };
        var state = new CrabDeskState { OrganizationRules = [images, allFiles] };

        var decisions = _engine.Preview(state, [Item("photo.png", DesktopItemKind.File)]);

        Assert.Equal(1, OrganizationRuleEngine.CountProjectedItems(decisions, state.Assignments, images.Id, null));
        // "文件" matches photo.png on its own, but "图片" claims it first, so a
        // box created for "文件" would stay empty.
        Assert.Equal(0, OrganizationRuleEngine.CountProjectedItems(decisions, state.Assignments, allFiles.Id, null));
    }

    [Fact]
    public void ProjectedCountKeepsItemsAlreadyInTheBoxAndSkipsThoseInOthers()
    {
        var ownBox = new DesktopBox { Title = "图片" };
        var otherBox = new DesktopBox { Title = "手动" };
        var rule = new OrganizationRule { Title = "图片", Extensions = ["png"], TargetBoxId = ownBox.Id };
        var retained = Item("kept.png", DesktopItemKind.File);
        var elsewhere = Item("moved.png", DesktopItemKind.File);
        var state = new CrabDeskState
        {
            Boxes = [ownBox, otherBox],
            OrganizationRules = [rule],
            Assignments = new Dictionary<string, Guid>
            {
                [retained.Key.ToString()] = ownBox.Id,
                [elsewhere.Key.ToString()] = otherBox.Id
            }
        };

        var decisions = _engine.Preview(state, [retained, elsewhere, Item("new.png", DesktopItemKind.File)]);

        Assert.Equal(2, OrganizationRuleEngine.CountProjectedItems(decisions, state.Assignments, rule.Id, ownBox.Id));
    }

    [Fact]
    public void ProjectedCountDropsItemsThatReassignmentMovesOut()
    {
        var imagesBox = new DesktopBox { Title = "图片" };
        var documentsBox = new DesktopBox { Title = "文档" };
        var images = new OrganizationRule { Title = "图片", Priority = 10, Extensions = ["png"], TargetBoxId = imagesBox.Id };
        var documents = new OrganizationRule { Title = "文档", Priority = 20, Extensions = ["txt"], TargetBoxId = documentsBox.Id };
        var misplaced = Item("notes.txt", DesktopItemKind.File);
        var state = new CrabDeskState
        {
            Boxes = [imagesBox, documentsBox],
            OrganizationRules = [images, documents],
            Assignments = new Dictionary<string, Guid> { [misplaced.Key.ToString()] = imagesBox.Id }
        };

        var decisions = _engine.Preview(state, [misplaced], reassignExistingItems: true);

        Assert.Equal(0, OrganizationRuleEngine.CountProjectedItems(decisions, state.Assignments, images.Id, imagesBox.Id));
        Assert.Equal(1, OrganizationRuleEngine.CountProjectedItems(decisions, state.Assignments, documents.Id, documentsBox.Id));
    }

    [Fact]
    public void ResolveTargetBoxReusesAnExistingNormalBoxWithTheSameTitle()
    {
        // The box came from the AI classifier or the user, not from an earlier
        // organize pass, so IsAutoGenerated is false.
        var manual = new DesktopBox { Title = "图片", MonitorId = "primary" };
        var rule = new OrganizationRule { Title = "图片", Extensions = ["png"] };
        var state = new CrabDeskState { Boxes = [manual], OrganizationRules = [rule] };

        Assert.Same(manual, OrganizationRuleEngine.ResolveTargetBox(state, rule));
    }

    [Fact]
    public void ResolveTargetBoxMatchesTitleIgnoringSurroundingWhitespaceAndCase()
    {
        var box = new DesktopBox { Title = "  Images  " };
        var rule = new OrganizationRule { Title = "images" };
        var state = new CrabDeskState { Boxes = [box] };

        Assert.Same(box, OrganizationRuleEngine.ResolveTargetBox(state, rule));
    }

    [Fact]
    public void ResolveTargetBoxPrefersItsOwnGeneratedBoxOverAUserBoxOfTheSameTitle()
    {
        var userBox = new DesktopBox { Title = "图片" };
        var generated = new DesktopBox { Title = "图片", IsAutoGenerated = true };
        var rule = new OrganizationRule { Title = "图片" };
        var state = new CrabDeskState { Boxes = [userBox, generated] };

        Assert.Same(generated, OrganizationRuleEngine.ResolveTargetBox(state, rule));
    }

    [Fact]
    public void ResolveTargetBoxKeepsAnExplicitTargetAndIgnoresMappedFolders()
    {
        var chosen = new DesktopBox { Title = "资料" };
        var sameTitle = new DesktopBox { Title = "图片" };
        var mapped = new DesktopBox
        {
            Title = "图片",
            MappedFolder = new MappedFolderSettings { Path = "C:\\Mirror" }
        };
        var rule = new OrganizationRule { Title = "图片", TargetBoxId = chosen.Id };
        var state = new CrabDeskState { Boxes = [sameTitle, mapped, chosen] };

        Assert.Same(chosen, OrganizationRuleEngine.ResolveTargetBox(state, rule));

        // With no explicit target the mapped folder must not be adopted either,
        // because rules may not move items into a live directory mirror.
        var untargeted = new OrganizationRule { Title = "图片" };
        Assert.Same(sameTitle, OrganizationRuleEngine.ResolveTargetBox(state, untargeted));
    }

    [Fact]
    public void ResolveTargetBoxReturnsNullWhenNoBoxMatches()
    {
        var state = new CrabDeskState { Boxes = [new DesktopBox { Title = "文档" }] };

        Assert.Null(OrganizationRuleEngine.ResolveTargetBox(
            state,
            new OrganizationRule { Title = "图片" }));
    }

    [Fact]
    public void EmptyUserBoxIsNeverRemovedByAnOrganizePass()
    {
        var userBox = new DesktopBox { Title = "图片" };
        var rule = new OrganizationRule { Title = "图片", Extensions = ["png"], TargetBoxId = userBox.Id };
        var state = new CrabDeskState { Boxes = [userBox], OrganizationRules = [rule] };

        Assert.False(OrganizationRuleEngine.CanRemoveEmptyBox(state, userBox, rule));
    }

    [Fact]
    public void EmptyGeneratedBoxIsRemovedOnlyWhenNoOtherRuleTargetsIt()
    {
        var generated = new DesktopBox { Title = "图片", IsAutoGenerated = true };
        var rule = new OrganizationRule { Title = "图片", Extensions = ["png"], TargetBoxId = generated.Id };
        var state = new CrabDeskState { Boxes = [generated], OrganizationRules = [rule] };

        Assert.True(OrganizationRuleEngine.CanRemoveEmptyBox(state, generated, rule));

        // A second rule pinned to the same box keeps it alive; deleting it would
        // leave that rule pointing at a target that no longer exists.
        var sharer = new OrganizationRule
        {
            Title = "更多图片",
            Extensions = ["jpg"],
            TargetBoxId = generated.Id
        };
        state.OrganizationRules.Add(sharer);

        Assert.False(OrganizationRuleEngine.CanRemoveEmptyBox(state, generated, rule));
    }

    [Fact]
    public void GeneratedBoxHoldingAssignedItemsIsNeverRemoved()
    {
        var generated = new DesktopBox { Title = "图片", IsAutoGenerated = true };
        var rule = new OrganizationRule { Title = "图片", Extensions = ["png"], TargetBoxId = generated.Id };
        var kept = Item("kept.png", DesktopItemKind.File);
        var state = new CrabDeskState
        {
            Boxes = [generated],
            OrganizationRules = [rule],
            Assignments = new Dictionary<string, Guid> { [kept.Key.ToString()] = generated.Id }
        };

        Assert.False(OrganizationRuleEngine.CanRemoveEmptyBox(state, generated, rule));
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
