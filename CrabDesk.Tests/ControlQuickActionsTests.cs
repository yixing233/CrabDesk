using System.Text.Json;
using CrabDesk.Core;
using QuickAction = CrabDesk.Core.ControlCenterQuickAction;
using Target = CrabDesk.Core.ControlCenterPresentation.ControlCenterTarget;

namespace CrabDesk.Tests;

public sealed class ControlQuickActionsTests
{
    private static readonly QuickAction[] ExpectedDefaults =
        [QuickAction.WiFi, QuickAction.Bluetooth, QuickAction.NightLight, QuickAction.Display];

    [Fact]
    public void NetworkAdaptersCanBeSelectedAndSavedWithoutRenumberingExistingActions()
    {
        Assert.Equal(8, (int)QuickAction.Settings);
        Assert.Equal(9, (int)QuickAction.NetworkAdapters);
        Assert.Equal("网卡", ControlQuickActions.Label(QuickAction.NetworkAdapters));
        Assert.Equal("启用 / 禁用", ControlQuickActions.Status(QuickAction.NetworkAdapters, null));
        Assert.Null(ControlQuickActions.Toggle(QuickAction.NetworkAdapters));
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.ControlCenter,
            ControlCenterQuickActions = [QuickAction.NetworkAdapters, QuickAction.WiFi]
        };
        var restored = JsonSerializer.Deserialize<DesktopWidget>(JsonSerializer.Serialize(widget))!;
        Assert.Equal(widget.ControlCenterQuickActions, ControlQuickActions.For(restored));
    }

    [Theory]
    [InlineData(true, "深色", "系统主题")]
    [InlineData(false, "浅色", "系统主题")]
    [InlineData(null, "系统主题", "不可用")]
    public void ThemeActionDescribesTheCurrentMode(bool? dark, string label, string status)
    {
        var snapshot = new ControlCenterSnapshot { NightLightEnabled = dark };
        Assert.Equal(label, ControlQuickActions.Label(QuickAction.NightLight, snapshot));
        Assert.Equal(status, ControlQuickActions.Status(QuickAction.NightLight, snapshot));
        Assert.Equal("系统主题", ControlQuickActions.Label(QuickAction.NightLight));
    }

    [Fact]
    public void NewControlCenterUsesTheFourDefaultActionsInOrder()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter };

        Assert.Equal(ExpectedDefaults, ControlQuickActions.Defaults);
        Assert.Equal(ExpectedDefaults, ControlQuickActions.For(widget));

        DesktopWidgets.Normalize(new CrabDeskState { Widgets = [widget] });

        Assert.Equal(ExpectedDefaults, widget.ControlCenterQuickActions);
    }

    public static IEnumerable<object[]> NormalizationCases()
    {
        yield return [null!, ExpectedDefaults];
        yield return [Array.Empty<QuickAction>(), ExpectedDefaults];
        yield return [new QuickAction[] { (QuickAction)(-1), (QuickAction)1000 }, ExpectedDefaults];
        yield return [new QuickAction[] { QuickAction.Cast }, new QuickAction[] { QuickAction.Cast }];
        yield return [new QuickAction[] { (QuickAction)1000, QuickAction.Settings, QuickAction.WiFi, (QuickAction)(-1), QuickAction.Display },
            new QuickAction[] { QuickAction.Settings, QuickAction.WiFi, QuickAction.Display }];
        yield return [new QuickAction[] { QuickAction.Bluetooth, QuickAction.Display, QuickAction.Bluetooth, QuickAction.WiFi, QuickAction.Display },
            new QuickAction[] { QuickAction.Bluetooth, QuickAction.Display, QuickAction.WiFi }];
        yield return [new QuickAction[] { (QuickAction)1000, QuickAction.Settings, QuickAction.Settings, QuickAction.ScreenCapture,
                QuickAction.ApplicationVolume, QuickAction.Microphone, QuickAction.Cast, QuickAction.Display, QuickAction.NightLight,
                QuickAction.Bluetooth, QuickAction.WiFi },
            new QuickAction[] { QuickAction.Settings, QuickAction.ScreenCapture, QuickAction.ApplicationVolume,
                QuickAction.Microphone, QuickAction.Cast, QuickAction.Display }];
    }

    [Theory]
    [MemberData(nameof(NormalizationCases))]
    public void NormalizationRepairsSavedChoicesAndKeepsTheFirstSixDistinctValidActions(
        QuickAction[]? saved, QuickAction[] expected)
    {
        Assert.Equal(expected, ControlQuickActions.Normalize(saved));
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, ControlCenterQuickActions = saved?.ToList() };
        var state = new CrabDeskState { Widgets = [widget] };

        DesktopWidgets.Normalize(state);

        Assert.Equal(expected, widget.ControlCenterQuickActions);
        Assert.Equal(expected, ControlQuickActions.For(widget));
        DesktopWidgets.Normalize(state);
        Assert.Equal(expected, widget.ControlCenterQuickActions);
    }

    [Fact]
    public void CustomizingOneDefaultWidgetDoesNotChangeAnotherWidgetOrTheDefaults()
    {
        var first = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter };
        var second = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter };
        DesktopWidgets.Normalize(new CrabDeskState { Widgets = [first, second] });

        first.ControlCenterQuickActions!.RemoveAt(0);
        first.ControlCenterQuickActions.Add(QuickAction.Settings);

        Assert.Equal(ExpectedDefaults, second.ControlCenterQuickActions);
        Assert.Equal(ExpectedDefaults, ControlQuickActions.Defaults);
    }

    [Fact]
    public void JsonRoundTripPreservesTheSelectedActionsAndTheirOrder()
    {
        QuickAction[] selected = [QuickAction.ScreenCapture, QuickAction.Bluetooth, QuickAction.ApplicationVolume, QuickAction.Display];
        var state = new CrabDeskState
        {
            Widgets = [new() { Kind = DesktopWidgetKind.ControlCenter, ControlCenterQuickActions = selected.ToList() }]
        };
        DesktopWidgets.Normalize(state);

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<CrabDeskState>(json)!;
        DesktopWidgets.Normalize(restored);

        var widget = Assert.Single(restored.Widgets);
        Assert.Equal(selected, widget.ControlCenterQuickActions);
        Assert.Equal(selected, ControlQuickActions.For(widget));
        Assert.DoesNotContain(QuickAction.WiFi, ControlQuickActions.For(widget));
        Assert.DoesNotContain(QuickAction.NightLight, ControlQuickActions.For(widget));
    }

    public static IEnumerable<object[]> LayoutCases()
    {
        for (var count = 1; count <= 6; count++)
            foreach (var cornerRadius in new[] { 0d, 8d, 20d }) yield return [count, cornerRadius];
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void OneToSixActionsFitTheCardWithoutOverlappingEachOtherOrTheSliders(int count, double cornerRadius)
    {
        var body = WidgetMonitorPresentation.Body(ControlCenterPresentation.CardWidth, ControlCenterPresentation.CardHeight, cornerRadius);
        var tiles = Enumerable.Range(0, count).Select(index => ControlCenterPresentation.QuickAction(body, index, count)).ToArray();
        var sliderArea = ControlCenterPresentation.SliderArea(body);

        Assert.Equal(body.X, tiles[0].X, 8);
        Assert.Equal(body.X + body.Width, tiles[^1].X + tiles[^1].Width, 8);
        for (var index = 0; index < tiles.Length; index++)
        {
            var tile = tiles[index];
            Assert.True(tile.Width > 0 && tile.Height > 0);
            AssertInside(body, tile);
            AssertInside(tile, ControlCenterPresentation.QuickActionDetails(tile));
            Assert.False(tile.Intersects(sliderArea));
            for (var other = index + 1; other < tiles.Length; other++) Assert.False(tile.Intersects(tiles[other]));
        }
    }

    [Fact]
    public void DefaultHitTestingMatchesTheDefaultActionsAndLeavesGapsInactive()
    {
        var body = Body();
        Target[] expected = [Target.ToggleWiFi, Target.ToggleBluetooth, Target.ToggleNightLight, Target.DisplayControls];
        for (var index = 0; index < expected.Length; index++)
        {
            var tile = ControlCenterPresentation.QuickAction(body, index, expected.Length);
            Assert.Equal(expected[index], ControlCenterPresentation.TargetAt(body, tile.X + 8, tile.Y + tile.Height - 8));
            if (index == expected.Length - 1) continue;
            var next = ControlCenterPresentation.QuickAction(body, index + 1, expected.Length);
            var gapCenter = (tile.X + tile.Width + next.X) / 2;
            Assert.Equal(Target.None, ControlCenterPresentation.TargetAt(body, gapCenter, tile.Y + tile.Height / 2));
        }
    }

    [Theory]
    [InlineData(QuickAction.WiFi, Target.ToggleWiFi, Target.WiFiDetails)]
    [InlineData(QuickAction.Bluetooth, Target.ToggleBluetooth, Target.BluetoothDetails)]
    public void RadioMainButtonAndDetailsArrowRemainDistinctAtEverySupportedCount(QuickAction radio, Target main, Target details)
    {
        var body = Body();
        QuickAction[] otherActions = [QuickAction.Display, QuickAction.Cast, QuickAction.Microphone, QuickAction.ApplicationVolume, QuickAction.Settings];
        for (var count = 1; count <= 6; count++)
        {
            var actions = otherActions.Take(count - 1).Append(radio).ToArray();
            var tile = ControlCenterPresentation.QuickAction(body, count - 1, count);
            var arrow = ControlCenterPresentation.QuickActionDetails(tile);

            Assert.Equal(main, ControlCenterPresentation.TargetAt(body, tile.X + 8, tile.Y + 14, actions));
            Assert.Equal(main, ControlCenterPresentation.TargetAt(body, tile.X + tile.Width / 2, tile.Y + 14, actions));
            Assert.Equal(details, ControlCenterPresentation.TargetAt(body, arrow.X + arrow.Width / 2, arrow.Y + arrow.Height / 2, actions));
            Assert.Equal(details, ControlCenterPresentation.TargetAt(body, tile.X + tile.Width - 22, tile.Y + 14, actions));
            Assert.Equal(details, ControlCenterPresentation.TargetAt(body, tile.X + tile.Width - 10, tile.Y + 30, actions));
            Assert.Equal(details, ControlCenterPresentation.TargetAt(body, tile.X + tile.Width - 10, tile.Y + 1, actions));
            Assert.Equal(main, ControlCenterPresentation.TargetAt(body, arrow.X + arrow.Width / 2, tile.Y + tile.Height - 8, actions));
        }
    }

    [Fact]
    public void HidingAndReorderingActionsUpdatesBothMainAndDetailsTargets()
    {
        var body = Body();
        QuickAction[] actions = [QuickAction.ScreenCapture, QuickAction.Display, QuickAction.Bluetooth];
        Target[] expected = [Target.ScreenCapture, Target.DisplayControls, Target.ToggleBluetooth];
        for (var index = 0; index < actions.Length; index++)
        {
            var tile = ControlCenterPresentation.QuickAction(body, index, actions.Length);
            Assert.Equal(expected[index], ControlCenterPresentation.TargetAt(body, tile.X + 8, tile.Y + 14, actions));
        }

        var arrow = ControlCenterPresentation.QuickActionDetails(ControlCenterPresentation.QuickAction(body, 2, actions.Length));
        Assert.Equal(Target.BluetoothDetails,
            ControlCenterPresentation.TargetAt(body, arrow.X + arrow.Width / 2, arrow.Y + arrow.Height / 2, actions));
        // Sweep the whole row so a hidden action cannot leave an old clickable region.
        var found = Enumerable.Range(0, (int)body.Width + 1)
            .Select(offset => ControlCenterPresentation.TargetAt(body, body.X + offset, body.Y + 14, actions)).ToHashSet();
        Assert.DoesNotContain(Target.ToggleWiFi, found);
        Assert.DoesNotContain(Target.WiFiDetails, found);
        Assert.DoesNotContain(Target.ToggleNightLight, found);
        Assert.Contains(Target.ScreenCapture, found);
        Assert.Contains(Target.BluetoothDetails, found);
    }

    [Theory]
    [InlineData(QuickAction.NightLight, Target.ToggleNightLight)]
    [InlineData(QuickAction.Display, Target.DisplayControls)]
    [InlineData(QuickAction.Cast, Target.Cast)]
    [InlineData(QuickAction.Microphone, Target.Microphone)]
    [InlineData(QuickAction.ApplicationVolume, Target.VolumeMixer)]
    [InlineData(QuickAction.ScreenCapture, Target.ScreenCapture)]
    [InlineData(QuickAction.Settings, Target.SystemSettings)]
    [InlineData(QuickAction.NetworkAdapters, Target.NetworkAdapters)]
    public void NonRadioActionsUseTheEntireTileForTheirOwnCommand(QuickAction action, Target expected)
    {
        var body = Body();
        QuickAction[] actions = [QuickAction.Bluetooth, action, QuickAction.WiFi];
        var tile = ControlCenterPresentation.QuickAction(body, 1, actions.Length);
        var upperRight = ControlCenterPresentation.QuickActionDetails(tile);

        Assert.Equal(expected, ControlCenterPresentation.TargetAt(body, tile.X + 8, tile.Y + 14, actions));
        Assert.Equal(expected, ControlCenterPresentation.TargetAt(body,
            upperRight.X + upperRight.Width / 2, upperRight.Y + upperRight.Height / 2, actions));
    }

    private static LayoutRect Body() => WidgetMonitorPresentation.Body(
        ControlCenterPresentation.CardWidth, ControlCenterPresentation.CardHeight, 8);

    private static void AssertInside(LayoutRect outer, LayoutRect inner)
    {
        Assert.True(inner.X >= outer.X && inner.Y >= outer.Y);
        Assert.True(inner.X + inner.Width <= outer.X + outer.Width + .001);
        Assert.True(inner.Y + inner.Height <= outer.Y + outer.Height + .001);
    }
}
