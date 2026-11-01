using System.Reflection;
using System.Runtime.CompilerServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Services;
using CrabDesk.WinUI.ViewModels;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class OrganizationTargetRegressionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RuleListShowsResolvedSubtabForNewStaleAndExistingBindings(int pin)
    {
        var tab = new DesktopBoxTab { Title = "图片" };
        var parent = new DesktopBox { Title = "资料", ManualTabs = [tab] };
        var rule = new OrganizationRule
        {
            Title = "图片", TargetBoxId = pin == 0 ? null : pin == 1 ? Guid.NewGuid() : parent.Id
        };

        Assert.Equal("放入「资料」· 图片", new OrganizationRuleListItem(rule, [parent]).DestinationText);
    }

    [Fact]
    public async Task ConfirmationPreviewNamesTheActualChildTab()
    {
        var tab = new DesktopBoxTab { Title = "图片" };
        var parent = new DesktopBox { Title = "资料", ManualTabs = [tab] };
        var state = new CrabDeskState { Boxes = [parent] };
        var service = new Mock<ICrabDeskService>();
        service.SetupGet(value => value.State).Returns(state);
        service.SetupGet(value => value.Boxes).Returns(state.Boxes);
        service.Setup(value => value.GetOrganizationRuleConflicts()).Returns(Array.Empty<OrganizationRuleConflict>());
        service.Setup(value => value.PreviewOrganizationRules()).Returns(
            new[] { new OrganizationDecision("one", "photo.png", Guid.NewGuid(), "图片", OrganizationRuleAction.AssignToBox, parent.Id, tab.Id) });
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(value => value.ConfirmAsync("整理预览", It.IsAny<string>(), "确认整理")).ReturnsAsync(false);
        var viewModel = new OrganizationViewModel(service.Object, dialogs.Object);

        await viewModel.ApplyCommand.ExecuteAsync(null);

        dialogs.Verify(value => value.ConfirmAsync("整理预览", It.Is<string>(text => text.Contains("放入「资料」· 图片")), "确认整理"), Times.Once);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void UndoRestoresOriginalGrouping(bool sameBox, bool originallyInTab, bool deleteOriginalTab)
    {
        var oldTab = new DesktopBoxTab { Title = "截图" };
        var newTab = new DesktopBoxTab { Title = "图片" };
        var source = new DesktopBox { Title = "来源", ManualTabs = [oldTab] };
        var target = sameBox ? source : new DesktopBox { Title = "资料" };
        target.ManualTabs.Add(newTab);
        var item = new DesktopItemRef
        {
            Key = new DesktopItemKey("file", "photo.png"), DisplayName = "photo.png",
            ParsingName = @"C:\Desktop\photo.png", FileSystemPath = @"C:\Desktop\photo.png", Kind = DesktopItemKind.File
        };
        var state = new CrabDeskState
        {
            Boxes = sameBox ? [source] : [source, target],
            OrganizationRules = [new OrganizationRule { Title = "图片", Extensions = ["png"] }],
            Organization = new OrganizationSettings { ReassignExistingItems = true }
        };
        state.Assignments[item.Key.ToString()] = source.Id;
        if (originallyInTab) source.ItemTabAssignments[item.Key.ToString()] = oldTab.Id;
        using var fixture = new RuntimeFixture(state, [item]);

        Assert.Equal(1, fixture.Runtime.ApplyOrganizationRules(false).Assigned);
        Assert.Equal(newTab.Id, target.ItemTabAssignments[item.Key.ToString()]);
        if (deleteOriginalTab) source.ManualTabs.Remove(oldTab);
        fixture.Runtime.UndoLastOrganization();

        Assert.Equal(source.Id, state.Assignments[item.Key.ToString()]);
        if (originallyInTab && !deleteOriginalTab)
            Assert.Equal(oldTab.Id, source.ItemTabAssignments[item.Key.ToString()]);
        else
            Assert.False(source.ItemTabAssignments.ContainsKey(item.Key.ToString()));
        if (!sameBox) Assert.False(target.ItemTabAssignments.ContainsKey(item.Key.ToString()));
    }

    [Fact]
    public void EditingAndDuplicatingRulesPreservesAnExplicitTarget()
    {
        var state = new CrabDeskState();
        using var fixture = new RuntimeFixture(state, []);
        var rule = new OrganizationRule { Title = "图片", TargetBoxId = Guid.NewGuid(), TargetBoxIsExplicit = true };

        fixture.Runtime.SaveOrganizationRule(rule);
        var copy = fixture.Runtime.DuplicateOrganizationRule(rule.Id)!;

        Assert.True(Assert.Single(state.OrganizationRules.Where(value => value.Id == rule.Id)).TargetBoxIsExplicit);
        Assert.True(copy.TargetBoxIsExplicit);
        Assert.Equal(rule.TargetBoxId, copy.TargetBoxId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedOrganizationKeepsTheChosenBoxAndItsTitle(bool builtIn)
    {
        var chosen = new DesktopBox { Title = "工作资料", IsAutoGenerated = true };
        var other = new DesktopBox { Title = "个人", ManualTabs = [new DesktopBoxTab { Title = "图片" }] };
        var rule = new OrganizationRule
        {
            Title = "图片", Extensions = ["png"], TargetBoxId = chosen.Id, TargetBoxIsExplicit = true,
            BuiltInId = builtIn ? BuiltInOrganizationRules.ImagesId : string.Empty
        };
        var item = new DesktopItemRef
        {
            Key = new DesktopItemKey("file", "photo.png"), DisplayName = "photo.png",
            ParsingName = @"C:\Desktop\photo.png", FileSystemPath = @"C:\Desktop\photo.png", Kind = DesktopItemKind.File
        };
        var state = new CrabDeskState { Boxes = [chosen, other], OrganizationRules = [rule] };
        using var fixture = new RuntimeFixture(state, [item]);

        Assert.Equal(1, fixture.Runtime.ApplyOrganizationRules(false).Assigned);
        fixture.Runtime.ApplyOrganizationRules(false);

        Assert.Equal(chosen.Id, state.Assignments[item.Key.ToString()]);
        Assert.True(rule.TargetBoxIsExplicit);
        Assert.Equal("工作资料", chosen.Title);
        Assert.Equal(2, state.Boxes.Count);
        Assert.Empty(other.ItemTabAssignments);
    }

    private sealed class RuntimeFixture : IDisposable
    {
        internal CrabDeskRuntime Runtime { get; } = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        private readonly RuntimeTimer _save = new(TimeSpan.FromHours(1), false, action => action(), () => { });
        internal RuntimeFixture(CrabDeskState state, IReadOnlyList<DesktopItemRef> items)
        {
            Set("<State>k__BackingField", state);
            Set("<Items>k__BackingField", items);
            Set("<Monitors>k__BackingField", new[] { new MonitorLayout
            {
                Id = "primary", DeviceName = "primary", IsPrimary = true,
                Bounds = new LayoutRect(0, 0, 1920, 1080), WorkArea = new LayoutRect(0, 0, 1920, 1040),
                PixelBounds = new LayoutRect(0, 0, 1920, 1080), PixelWorkArea = new LayoutRect(0, 0, 1920, 1040)
            } });
            Set("_organizationRuleEngine", new OrganizationRuleEngine());
            Set("_lastOrganizationCreatedBoxes", new HashSet<Guid>());
            Set("_saveTimer", _save);
        }
        private void Set(string name, object value) => typeof(CrabDeskRuntime)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Runtime, value);
        public void Dispose() => _save.Dispose();
    }
}
