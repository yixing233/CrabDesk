using System.Xml.Linq;
using CrabDesk.Core;
using CrabDesk.WinUI.Controls;
using CrabDesk.WinUI.Services;
using CrabDesk.WinUI.ViewModels;
using CrabDesk.WinUI.Views;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetCenterSettingsTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AppearanceOffersIndependentSystemLightAndDarkCenterThemes()
    {
        var document = ReadPage("WidgetsPage.xaml");
        var picker = Named(document, "WidgetCenterThemePicker");
        Assert.Contains(picker, Named(document, "AppearanceView").Descendants());
        Assert.Equal("组件中心主题", (string?)picker.Attribute("Header"));
        Assert.Equal("WidgetCenterThemePicker_SelectionChanged", (string?)picker.Attribute("SelectionChanged"));
        Assert.Equal(new[] { "System", "Light", "Dark" },
            picker.Elements(Ui + "ComboBoxItem").Select(element => (string?)element.Attribute("Tag")));
        Assert.Equal(new[] { "跟随系统", "浅色", "深色" },
            picker.Elements(Ui + "ComboBoxItem").Select(element => (string?)element.Attribute("Content")));
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var service = new CrabDeskService(fixture.Runtime);
        service.SetWidgetCenterThemeMode(ApplicationThemeMode.Dark);
        Assert.Equal(ApplicationThemeMode.Dark, service.State.Settings.WidgetCenter.ThemeMode);
        Assert.True(fixture.Runtime.IsWidgetCenterDarkTheme);
        Assert.Equal(ApplicationThemeMode.System, service.State.Settings.ThemeMode);
    }

    [Fact]
    public void OverviewOffersOptInCenterCardWithoutReplacingTheGallery()
    {
        var document = ReadPage("WidgetsPage.xaml");
        var overview = Named(document, "OverviewView");
        var card = Named(document, "WidgetCenterCard");
        Assert.Contains(card, overview.Descendants());
        Assert.Equal("{StaticResource SectionCardStyle}", (string?)card.Attribute("Style"));
        Assert.Contains(card.Descendants(Ui + "TextBlock"), element => (string?)element.Attribute("Text") == "组件中心");
        Assert.Contains(card.Descendants(Ui + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "将组件集中在悬浮面板，开启后单击托盘图标打开");
        var toggle = Named(document, "WidgetCenterEnabledToggle");
        Assert.Equal("False", (string?)toggle.Attribute("IsOn"));
        Assert.Equal("WidgetCenterEnabled_Toggled", (string?)toggle.Attribute("Toggled"));
        var open = Named(document, "OpenWidgetCenterButton");
        Assert.Equal("False", (string?)open.Attribute("IsEnabled"));
        Assert.Equal("OpenWidgetCenter_Click", (string?)open.Attribute("Click"));
        Assert.Contains(Named(document, "WidgetGallery"), overview.Descendants());
        Assert.DoesNotContain(card.DescendantsAndSelf().Attributes(), attribute =>
            (attribute.Name.LocalName is "Background" or "Foreground") && attribute.Value.StartsWith('#'));
    }

    [Fact]
    public void PlacementPickerIsCommonToAllWidgetEditorsAndUsesTheMigrationHandler()
    {
        var document = ReadPage("WidgetsPage.xaml");
        var picker = Named(document, "WidgetPlacementPicker");
        Assert.Contains(picker, Named(document, "EditorScroll").Descendants());
        Assert.Equal("WidgetPlacementPicker_SelectionChanged", (string?)picker.Attribute("SelectionChanged"));
        Assert.Equal(new[] { "Desktop", "Center" }, picker.Elements(Ui + "ComboBoxItem").Select(element => (string?)element.Attribute("Tag")));
        Assert.Equal(new[] { "桌面", "组件中心" }, picker.Elements(Ui + "ComboBoxItem").Select(element => (string?)element.Attribute("Content")));
        Assert.DoesNotContain(picker.Ancestors(), element => (string?)element.Attribute(Xaml + "Name") == "LyricsEditor");
        var source = File.ReadAllText(Path.Combine(SolutionDirectory(), "CrabDesk.WinUI", "Views", "WidgetsPage.WidgetCenter.cs"));
        Assert.Contains("ApplyWidgetPlacement(_widgetCenterService, widget, placement)", source);
        Assert.Contains("_widgetCenterService.SetWidgetCenterEnabled(WidgetCenterEnabledToggle.IsOn)", source);
        Assert.Contains("Try(_widgetCenterService.ShowWidgetCenter)", source);
        Assert.DoesNotContain("AddWidgetToCenter(", source);
    }

    [Fact]
    public void CenterEditorShowsItsThemeWhileRestrictingBackgroundStyleControlsToDesktopPlacement()
    {
        var document = ReadPage("WidgetsPage.xaml");
        var hint = Named(document, "WidgetCenterMaterialHint");
        var material = Named(document, "WidgetMaterialPicker");
        Assert.Same(hint.Parent, material.Parent);
        Assert.True(hint.ElementsBeforeSelf().Count() < material.ElementsBeforeSelf().Count());
        Assert.Contains("配色跟随组件中心主题", (string?)hint.Attribute("Text"));
        Assert.Null(material.Attribute("IsEnabled"));
        var lyricsHint = Named(document, "WidgetCenterLyricsMaterialHint");
        Assert.Same(lyricsHint.Parent, Named(document, "LyricsTransparentBackground").Parent);
        Assert.Contains("配色跟随组件中心主题", (string?)lyricsHint.Attribute("Text"));
        Assert.Null(Named(document, "LyricsTransparentBackground").Attribute("IsEnabled"));
        var source = File.ReadAllText(Path.Combine(SolutionDirectory(), "CrabDesk.WinUI", "Views", "WidgetsPage.WidgetCenter.cs"));
        Assert.Contains("WidgetCenterMaterialHint.Visibility = Show(inCenter)", source);
        Assert.Contains("WidgetCenterLyricsMaterialHint.Visibility = Show(inCenter)", source);
        Assert.Contains("WidgetMaterialPicker.Visibility = Show(!inCenter)", source);
        Assert.Contains("LyricsTransparentBackground.Visibility = Show(!inCenter)", source);
        var appearance = File.ReadAllText(Path.Combine(SolutionDirectory(), "CrabDesk.WinUI", "Views", "WidgetsPage.Appearance.cs"));
        Assert.Contains("WidgetThemePicker.Visibility = Show(!inCenter &&", appearance);
        Assert.Contains("中心仅使用实色，原桌面材质设置保留，移回桌面后恢复。", source);
        Assert.DoesNotContain("widget.BackgroundMaterial =", source);
        Assert.DoesNotContain("widget.LyricsTransparentBackground =", source);
    }

    [Fact]
    public void SharedPreviewForcesSolidOnlyForCenterInstancesWithoutMutatingTheirModels()
    {
        var source = File.ReadAllText(Path.Combine(SolutionDirectory(), "CrabDesk.WinUI", "Controls", "WidgetPreview.xaml.cs"));
        Assert.Contains("ForceSolidPreview => !IsCatalogPreview && Widget?.Placement == WidgetPlacement.Center", source);
        Assert.Contains("IsSolidPreview(DesktopWidget widget) => ForceSolidPreview || widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid", source);
        Assert.Contains("forceSolid: ForceSolidPreview", source);
        Assert.Contains("colorTheme: ForceSolidPreview ? appearance.CenterColorTheme : null", source);
        Assert.Contains("var hasBackground = ForceSolidPreview || DesktopWidgets.HasBackground(widget)", source);
        Assert.Contains("var solid = IsSolidPreview(widget)", source);
        Assert.Contains("ContentWash(asset, IsSolidPreview(widget)", source);
        Assert.DoesNotContain("var solid = widget.BackgroundMaterial", source);
        Assert.DoesNotMatch(@"\bwidget\.BackgroundMaterial\s*=(?!=)", source);
        Assert.DoesNotMatch(@"\bwidget\.LyricsTransparentBackground\s*=(?!=)", source);
    }

    [Theory]
    [InlineData(DesktopWidgetKind.Note)]
    [InlineData(DesktopWidgetKind.Lyrics)]
    public void SolidCenterSurfacePreservesOriginalDesktopMaterialAndTransparency(DesktopWidgetKind kind)
    {
        var widget = new DesktopWidget
        {
            Kind = kind, Placement = WidgetPlacement.Center,
            BackgroundMaterial = WidgetBackgroundMaterial.Acrylic, LyricsTransparentBackground = true
        };
        var item = new WidgetGalleryItem(widget, new(new(), new()), false, "组件中心");
        var surface = WidgetAppearance.Resolve(item.Widget, new BoxAppearance(), acrylic: true, forceSolid: true);
        Assert.False(surface.Acrylic);
        Assert.Equal(1, surface.Opacity);
        Assert.Equal(WidgetBackgroundMaterial.Acrylic, widget.BackgroundMaterial);
        Assert.True(widget.LyricsTransparentBackground);
        Assert.Same(widget, item.Widget);
    }

    [Fact]
    public void SettingsFacadeEnablesWithoutMovingAndDisablesWithoutLosingCenterOwnership()
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var service = new CrabDeskService(fixture.Runtime);
        var widget = fixture.Runtime.AddWidget(DesktopWidgetKind.Note);
        widget.Text = "保留的内容";
        var bounds = widget.Bounds;
        service.SetWidgetCenterEnabled(true);
        Assert.Equal(WidgetPlacement.Desktop, widget.Placement);
        Assert.False(service.IsWidgetCenterVisible);
        WidgetsPage.ApplyWidgetPlacement(service, widget, WidgetPlacement.Center);
        service.SetWidgetCenterEnabled(false);
        Assert.Equal(WidgetPlacement.Center, widget.Placement);
        Assert.Equal(bounds, widget.Bounds);
        Assert.Equal("保留的内容", widget.Text);
        Assert.False(service.IsWidgetCenterVisible);
        Assert.Same(widget, Assert.Single(service.State.Widgets));
        WidgetsPage.ApplyWidgetPlacement(service, widget, WidgetPlacement.Desktop);
        Assert.Equal(WidgetPlacement.Desktop, widget.Placement);
        Assert.Equal(bounds, widget.Bounds);
        Assert.False(service.State.Settings.WidgetCenter.Enabled);
    }

    [Fact]
    public void PlacementHandlerRejectsCenterWhileDisabledWithoutChangingTheWidgetOrFeature()
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var service = new CrabDeskService(fixture.Runtime);
        var widget = fixture.Runtime.AddWidget(DesktopWidgetKind.Note);
        Assert.Throws<InvalidOperationException>(() => WidgetsPage.ApplyWidgetPlacement(service, widget, WidgetPlacement.Center));
        Assert.False(service.State.Settings.WidgetCenter.Enabled);
        Assert.Equal(WidgetPlacement.Desktop, widget.Placement);
        Assert.Same(widget, Assert.Single(service.State.Widgets));
    }

    [Fact]
    public void PlacementHandlerRoutesTheOriginalInstanceAndIgnoresSamplesAndNoOpSelections()
    {
        var state = new CrabDeskState();
        state.Settings.WidgetCenter.Enabled = true;
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note };
        state.Widgets.Add(widget);
        var service = new Mock<ICrabDeskService>();
        service.SetupGet(item => item.State).Returns(state);
        WidgetsPage.ApplyWidgetPlacement(service.Object, new DesktopWidget(), WidgetPlacement.Center);
        WidgetsPage.ApplyWidgetPlacement(service.Object, widget, WidgetPlacement.Desktop);
        service.Verify(item => item.MoveWidgetToCenter(It.IsAny<DesktopWidget>()), Times.Never);
        service.Verify(item => item.MoveWidgetToDesktop(It.IsAny<DesktopWidget>()), Times.Never);
        WidgetsPage.ApplyWidgetPlacement(service.Object, widget, WidgetPlacement.Center);
        service.Verify(item => item.MoveWidgetToCenter(widget), Times.Once);
        widget.Placement = WidgetPlacement.Center;
        WidgetsPage.ApplyWidgetPlacement(service.Object, widget, WidgetPlacement.Desktop);
        service.Verify(item => item.MoveWidgetToDesktop(widget), Times.Once);
        service.Verify(item => item.AddWidgetToCenter(It.IsAny<DesktopWidgetKind>()), Times.Never);
        service.Verify(item => item.SetWidgetCenterEnabled(It.IsAny<bool>()), Times.Never);
        Assert.Same(widget, Assert.Single(state.Widgets));
    }

    [Fact]
    public void FacadeCenterAddRetainsTheRuntimeSingleInstanceContract()
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var service = new CrabDeskService(fixture.Runtime);
        service.SetWidgetCenterEnabled(true);
        var widget = service.AddWidgetToCenter(DesktopWidgetKind.Note);
        Assert.Equal(WidgetPlacement.Center, widget.Placement);
        Assert.Same(widget, service.AddWidgetToCenter(DesktopWidgetKind.Note));
        Assert.Same(widget, Assert.Single(service.State.Widgets));
        Assert.False(service.IsWidgetCenterVisible);
    }

    [Fact]
    public void DisabledFacadeShowAndToggleDoNotCreateOrEnableTheCenter()
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var service = new CrabDeskService(fixture.Runtime);
        service.ShowWidgetCenter();
        service.ToggleWidgetCenter();
        service.HideWidgetCenter();
        Assert.False(service.State.Settings.WidgetCenter.Enabled);
        Assert.False(service.IsWidgetCenterVisible);
        Assert.Empty(service.State.Widgets);
    }

    [Fact]
    public void GalleryCenterCardsRemainAddedAndNotifyPlacementChangesWithoutRecreatingPreviews()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note };
        var appearance = new WidgetPreviewAppearance(new(), new());
        var item = new WidgetGalleryItem(widget, appearance, false, "小", 1);
        var changes = new List<string?>();
        item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        widget.Placement = WidgetPlacement.Center;
        item.Refresh(appearance, "小", 1, refreshPreview: false);
        Assert.True(item.IsAdded);
        Assert.True(item.IsInCenter);
        Assert.Contains("中心", item.StatusLabel);
        Assert.Equal("组件中心", item.PlacementLabel);
        Assert.Contains("组件中心", item.AccessibleName);
        Assert.True(item.Matches("组件中心", true));
        Assert.False(item.Matches(null, false));
        Assert.Contains(nameof(item.StatusLabel), changes);
        Assert.Contains(nameof(item.PlacementLabel), changes);
        Assert.Contains(nameof(item.IsInCenter), changes);
        Assert.Contains(nameof(item.AccessibleName), changes);
        Assert.Equal(0, item.PreviewRevision);
        Assert.Same(widget, item.Widget);
        widget.Placement = WidgetPlacement.Desktop;
        item.Refresh(appearance, "小", 1, refreshPreview: false);
        Assert.False(item.IsInCenter);
        Assert.Equal("已添加", item.StatusLabel);
    }

    [Fact]
    public void CatalogStillAddsToDesktopAndIsNotShownAsAnAddedCenterInstance()
    {
        var item = new WidgetGalleryItem(new() { Kind = DesktopWidgetKind.Note, Placement = WidgetPlacement.Center }, new(new(), new()), true, "记录想法");
        Assert.False(item.IsAdded);
        Assert.False(item.IsInCenter);
        Assert.Equal("未添加", item.StatusLabel);
        Assert.Equal("添加到桌面", item.ActionLabel);
        var source = File.ReadAllText(Path.Combine(SolutionDirectory(), "CrabDesk.WinUI", "Views", "WidgetsPage.xaml.cs"));
        Assert.Contains("item.IsCatalog ? _runtime.AddWidget(item.Widget.Kind) : item.Widget", source);
    }

    [Fact]
    public void HotkeyCardBindsCenterConfigurationAndAvailability()
    {
        var card = Named(ReadPage("HotkeysPage.xaml"), "WidgetCenterHotkeyCard");
        Assert.Contains(card.Descendants(Ui + "ToggleSwitch"), element =>
            (string?)element.Attribute("IsOn") == "{Binding WidgetCenterEnabled, Mode=TwoWay}");
        Assert.Contains(card.Descendants(Ui + "ComboBox"), element =>
            (string?)element.Attribute("SelectedItem") == "{Binding WidgetCenterModifiers, Mode=TwoWay}");
        Assert.Contains(card.Descendants(Ui + "TextBox"), element =>
            (string?)element.Attribute("Text") == "{Binding WidgetCenterKeyText, Mode=TwoWay}");
        foreach (var controlType in new[] { "ToggleSwitch", "ComboBox", "TextBox" })
            Assert.Equal("{Binding WidgetCenterAvailable}", (string?)Assert.Single(card.Descendants(Ui + controlType)).Attribute("IsEnabled"));
        Assert.All(card.Descendants(Ui + "StackPanel"), panel => Assert.Null(panel.Attribute("IsEnabled")));
        Assert.Contains(card.Descendants(Ui + "TextBlock"), element => (string?)element.Attribute("Text") == "{Binding WidgetCenterStatus}");
    }

    [Fact]
    public void HotkeyViewModelPreservesConfigurationAndUsesRuntimeRegistrationStatus()
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var service = new CrabDeskService(fixture.Runtime);
        var viewModel = new HotkeysViewModel(service);
        Assert.False(viewModel.WidgetCenterAvailable);
        Assert.False(viewModel.WidgetCenterHasStatus);
        service.SetWidgetCenterEnabled(true);
        viewModel.WidgetCenterModifiers = viewModel.ModifierOptions.Single(item => item.Value == (HotkeyModifiers.Control | HotkeyModifiers.Shift));
        viewModel.WidgetCenterKeyText = "f8";
        viewModel.WidgetCenterEnabled = true;
        var binding = service.State.Settings.Hotkeys.WidgetCenter;
        Assert.True(binding.Enabled);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, binding.Modifiers);
        Assert.Equal(HotkeyKey.F8, binding.Key);
        Assert.Equal("已注册", viewModel.WidgetCenterStatus);
        Assert.True(viewModel.WidgetCenterHasStatus);
        service.SetWidgetCenterEnabled(false);
        Assert.False(viewModel.WidgetCenterAvailable);
        Assert.True(binding.Enabled);
        Assert.False(fixture.Hotkeys.Bindings[HotkeyAction.WidgetCenter].Enabled);
        Assert.False(viewModel.WidgetCenterHasStatus);
        Assert.Equal(string.Empty, viewModel.WidgetCenterStatus);
        viewModel.WidgetCenterKeyText = "q";
        Assert.Equal(HotkeyKey.Q, binding.Key);
        Assert.False(service.State.Settings.WidgetCenter.Enabled);
        Assert.False(fixture.Hotkeys.Bindings[HotkeyAction.WidgetCenter].Enabled);
    }

    [Fact]
    public void DisabledFeatureCannotImplicitlyEnableItsHotkeyFromTheViewModel()
    {
        var state = new CrabDeskState();
        state.Settings.Hotkeys.WidgetCenter.Enabled = false;
        var service = new Mock<ICrabDeskService>();
        service.SetupGet(item => item.State).Returns(state);
        var viewModel = new HotkeysViewModel(service.Object);
        viewModel.WidgetCenterEnabled = true;
        service.Verify(item => item.SetHotkey(It.IsAny<HotkeyAction>(), It.IsAny<bool>(), It.IsAny<HotkeyModifiers>(), It.IsAny<HotkeyKey>()), Times.Never);
        service.Verify(item => item.SetWidgetCenterEnabled(It.IsAny<bool>()), Times.Never);
        Assert.False(viewModel.WidgetCenterEnabled);
        Assert.False(state.Settings.WidgetCenter.Enabled);
    }

    [Theory]
    [InlineData(HotkeyRegistrationStatus.Conflict, "与其他应用冲突")]
    [InlineData(HotkeyRegistrationStatus.Failed, "注册失败")]
    public void CenterHotkeyStatusComesFromTheRuntimeFacade(HotkeyRegistrationStatus status, string expected)
    {
        var service = new Mock<ICrabDeskService>();
        service.SetupGet(item => item.State).Returns(new CrabDeskState());
        service.Setup(item => item.GetHotkeyStatus(HotkeyAction.WidgetCenter)).Returns(status);
        var viewModel = new HotkeysViewModel(service.Object);
        Assert.Equal(expected, viewModel.WidgetCenterStatus);
        Assert.True(viewModel.WidgetCenterHasStatus);
    }

    private static XDocument ReadPage(string name) => XDocument.Load(Path.Combine(SolutionDirectory(), "CrabDesk.WinUI", "Views", name));
    private static XElement Named(XDocument document, string name) => document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);
    private static string SolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln"))) return directory.FullName;
        throw new InvalidOperationException("CrabDesk.sln not found.");
    }
}
