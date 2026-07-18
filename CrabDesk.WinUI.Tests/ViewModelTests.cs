using CrabDesk.Core;
using CrabDesk.WinUI.Converters;
using CrabDesk.WinUI.Services;
using CrabDesk.WinUI.ViewModels;
using Moq;
using Windows.UI;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class ViewModelTests
{
    [Fact]
    public void GeneralViewModelWritesStartupSettingThroughFacade()
    {
        var state = CreateState();
        var service = CreateService(state);
        var theme = new Mock<IThemeService>();
        var viewModel = new GeneralViewModel(service.Object, theme.Object, Mock.Of<IDialogService>());

        viewModel.StartWithWindows = true;

        service.Verify(item => item.SetStartWithWindows(true), Times.Once);
    }

    [Fact]
    public async Task GeneralViewModelRepairsDesktopIconsAfterConfirmation()
    {
        var service = CreateService(CreateState());
        service.Setup(item => item.RepairDesktopIconsAsync()).ReturnsAsync(true);
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(item => item.ConfirmAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        var viewModel = new GeneralViewModel(service.Object, Mock.Of<IThemeService>(), dialogs.Object);

        await viewModel.RepairDesktopIconsCommand.ExecuteAsync(null);

        service.Verify(item => item.RepairDesktopIconsAsync(), Times.Once);
        Assert.Equal("桌面图标已修复", viewModel.DesktopIconRepairStatus);
    }

    [Fact]
    public void HotkeyViewModelKeepsModifierAndKeyWhenEnablingBinding()
    {
        var state = CreateState();
        state.Settings.Hotkeys.ShowDesktop.Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;
        state.Settings.Hotkeys.ShowDesktop.Key = HotkeyKey.D;
        var service = CreateService(state);
        var viewModel = new HotkeysViewModel(service.Object);

        viewModel.ShowDesktopEnabled = true;

        service.Verify(item => item.SetHotkey(
            HotkeyAction.ShowDesktop,
            true,
            HotkeyModifiers.Control | HotkeyModifiers.Alt,
            HotkeyKey.D), Times.Once);
    }

    [Fact]
    public void HotkeyViewModelAcceptsTypedLetterKey()
    {
        var state = CreateState();
        var service = CreateService(state);
        var viewModel = new HotkeysViewModel(service.Object);

        viewModel.ShowDesktopKeyText = "q";

        service.Verify(item => item.SetHotkey(
            HotkeyAction.ShowDesktop,
            state.Settings.Hotkeys.ShowDesktop.Enabled,
            state.Settings.Hotkeys.ShowDesktop.Modifiers,
            HotkeyKey.Q), Times.Once);
    }

    [Fact]
    public void BoxesViewModelRenamesSelectedBoxWithoutChangingIdentity()
    {
        var state = CreateState();
        var service = CreateService(state);
        var viewModel = new BoxesViewModel(
            service.Object,
            Mock.Of<IDialogService>(),
            Mock.Of<IFilePickerService>());
        var id = viewModel.SelectedBox!.Id;

        viewModel.Title = "工作";

        Assert.Equal(id, viewModel.SelectedBox.Id);
        Assert.Equal("工作", viewModel.SelectedBox.Title);
        Assert.Equal("普通盒子", viewModel.BoxTypeText);
        Assert.Equal("DISPLAY1", viewModel.MonitorId);
        service.Verify(item => item.BoxChanged(viewModel.SelectedBox, true), Times.Once);
    }

    [Fact]
    public async Task BoxesViewModelHonorsDeleteConfirmationSetting()
    {
        var state = CreateState();
        state.Settings.ConfirmDeleteBox = false;
        var service = CreateService(state);
        var dialogs = new Mock<IDialogService>();
        var viewModel = new BoxesViewModel(service.Object, dialogs.Object, Mock.Of<IFilePickerService>());

        await viewModel.DeleteCommand.ExecuteAsync(null);

        dialogs.Verify(item => item.ConfirmAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        service.Verify(item => item.DeleteBox(viewModel.SelectedBox!), Times.Once);
    }

    [Fact]
    public void AppearanceViewModelPreservesBoxSizeWhenChangingColor()
    {
        var state = CreateState();
        var service = CreateService(state);
        var backdrop = new Mock<IBackdropService>();
        var viewModel = new AppearanceViewModel(service.Object, backdrop.Object, Mock.Of<IFontCatalogService>());
        var bounds = state.Boxes[0].Bounds;

        viewModel.Background = "#FF112233";

        Assert.Equal(bounds, state.Boxes[0].Bounds);
        service.Verify(item => item.SetBoxBackground(null, "#FF112233"), Times.Once);
    }

    [Theory]
    [InlineData("#112233", "#FF112233")]
    [InlineData("#80112233", "#80112233")]
    public void HexColorConverterPreservesConfigurationFormat(string input, string expected)
    {
        var converter = new HexColorConverter();

        var color = Assert.IsType<Color>(converter.Convert(input, typeof(Color), null!, string.Empty));
        var output = converter.ConvertBack(color, typeof(string), null!, string.Empty);

        Assert.Equal(expected, output);
    }

    [Fact]
    public void UserFacingEnumsHaveChineseLabels()
    {
        object[] values =
        [
            ApplicationThemeMode.System,
            ApplicationThemeMode.Light,
            ApplicationThemeMode.Dark,
            OrganizationRuleAction.AssignToBox,
            OrganizationRuleAction.KeepUnassigned,
            OrganizationRuleAction.Ignore,
            BackdropKind.Mica,
            BackdropKind.MicaAlt,
            BackdropKind.Acrylic,
            BoxTitleAlignment.Left,
            BoxTitleAlignment.Center,
            BoxViewMode.Grid,
            BoxViewMode.List,
            BoxSortMode.Manual,
            BoxSortMode.Name,
            BoxSortMode.Type,
            BoxSortMode.Modified,
            UpdateChannel.Stable,
            UpdateChannel.Preview
        ];

        Assert.All(values, value =>
            Assert.NotEqual(value.ToString(), EnumDisplayConverter.GetLabel(value)));
        Assert.Equal("跟随系统", EnumDisplayConverter.GetLabel(ApplicationThemeMode.System));
        Assert.Equal("放入盒子", EnumDisplayConverter.GetLabel(OrganizationRuleAction.AssignToBox));
    }

    [Fact]
    public void AppearanceViewModelRestoresManualTitleColorAfterAutomaticMode()
    {
        var state = CreateState();
        state.Boxes[0].Appearance.TitleColor = "#FF102030";
        var service = CreateService(state);
        service.Setup(item => item.SetBoxTitleColor(It.IsAny<Guid?>(), It.IsAny<string>()))
            .Callback<Guid?, string>((_, color) => state.Boxes[0].Appearance.TitleColor = color);
        var viewModel = new AppearanceViewModel(
            service.Object,
            Mock.Of<IBackdropService>(),
            Mock.Of<IFontCatalogService>());

        viewModel.UseAutomaticTitleColor = true;
        Assert.True(viewModel.UseAutomaticTitleColor);

        viewModel.UseAutomaticTitleColor = false;

        Assert.Equal("#FF102030", viewModel.TitleColor);
        service.Verify(item => item.SetBoxTitleColor(null, "Auto"), Times.Once);
        service.Verify(item => item.SetBoxTitleColor(null, "#FF102030"), Times.Once);
    }

    [Fact]
    public void AppearanceViewModelRefreshesAfterDesktopContextMenuChange()
    {
        var state = CreateState();
        var service = CreateService(state);
        var viewModel = new AppearanceViewModel(
            service.Object,
            Mock.Of<IBackdropService>(),
            Mock.Of<IFontCatalogService>());

        state.Boxes[0].ViewMode = BoxViewMode.List;
        state.Boxes[0].SortMode = BoxSortMode.Modified;
        service.Raise(item => item.Changed += null, EventArgs.Empty);

        Assert.Equal(BoxViewMode.List, viewModel.ViewMode);
        Assert.Equal(BoxSortMode.Modified, viewModel.SortMode);
    }

    [Fact]
    public void AppearanceViewModelUpdatesTitleAndApplicationFonts()
    {
        var state = CreateState();
        var service = CreateService(state);
        var fonts = new Mock<IFontCatalogService>();
        fonts.SetupGet(item => item.FontFamilies).Returns(["Segoe UI", "Microsoft YaHei UI"]);
        var viewModel = new AppearanceViewModel(service.Object, Mock.Of<IBackdropService>(), fonts.Object);

        viewModel.TitleFontFamily = "Microsoft YaHei UI";
        viewModel.LabelFontFamily = "Microsoft YaHei UI";
        viewModel.LabelFontSize = 12.5;
        viewModel.BoxMaterial = BoxMaterialKind.AcrylicPreview;

        service.Verify(item => item.SetBoxTitleFontFamily(null, "Microsoft YaHei UI"), Times.Once);
        service.Verify(item => item.SetBoxLabelFontFamily(null, "Microsoft YaHei UI"), Times.Once);
        service.Verify(item => item.SetBoxLabelFontSize(null, 12.5), Times.Once);
        service.Verify(item => item.SetBoxMaterial(null, BoxMaterialKind.AcrylicPreview), Times.Once);
    }

    [Fact]
    public void OrganizationRuleListItemDescribesExtensionsAndAutomaticTarget()
    {
        var rule = BuiltInOrganizationRules.CreateDefaults().Single(candidate =>
            candidate.BuiltInId == BuiltInOrganizationRules.DocumentsId);

        var item = new OrganizationRuleListItem(rule, []);

        Assert.Contains("扩展名", item.CriteriaText);
        Assert.Contains(".docx", item.CriteriaText);
        Assert.Contains(".pdf", item.CriteriaText);
        Assert.Equal("整理时创建「文档」", item.DestinationText);
    }

    [Fact]
    public void OrganizationRuleListItemUsesTargetBoxTitleInsteadOfInternalActionName()
    {
        var box = new DesktopBox { Title = "资料" };
        var rule = new OrganizationRule
        {
            Title = "报告",
            ItemKinds = [DesktopItemKind.File],
            Extensions = [".pdf"],
            TargetBoxId = box.Id
        };

        var item = new OrganizationRuleListItem(rule, [box]);

        Assert.Equal("放入「资料」", item.DestinationText);
        Assert.DoesNotContain(nameof(OrganizationRuleAction.AssignToBox), item.DestinationText);
    }

    [Fact]
    public async Task AboutViewModelDownloadsVerifiedUpdateAndLaunchesInstaller()
    {
        var state = CreateState();
        var service = CreateService(state);
        var check = new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            "0.6.0",
            "0.7.0",
            InstallerUrl: "https://download.test/CrabDesk-Setup-x64.exe",
            Sha256Url: "https://download.test/SHA256SUMS.txt");
        service.SetupGet(item => item.LastUpdateCheck).Returns(check);
        service.Setup(item => item.DownloadUpdateAsync(It.IsAny<IProgress<UpdateDownloadProgress>>()))
            .ReturnsAsync(new UpdateDownloadResult(
                true,
                "C:\\Updates\\CrabDesk-Setup-x64.exe",
                new string('a', 64),
                true,
                "CN=CrabDesk"));
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(item => item.ConfirmAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(true);
        var viewModel = new AboutViewModel(service.Object, dialogs.Object, Mock.Of<IClipboardService>());

        await viewModel.DownloadAndInstallUpdateCommand.ExecuteAsync(null);

        service.Verify(item => item.DownloadUpdateAsync(It.IsAny<IProgress<UpdateDownloadProgress>>()), Times.Once);
        service.Verify(item => item.LaunchUpdateInstaller("C:\\Updates\\CrabDesk-Setup-x64.exe"), Times.Once);
    }

    private static Mock<ICrabDeskService> CreateService(CrabDeskState state)
    {
        var service = new Mock<ICrabDeskService>();
        service.SetupGet(item => item.State).Returns(state);
        service.SetupGet(item => item.Boxes).Returns(state.Boxes);
        service.SetupGet(item => item.DesktopConnected).Returns(true);
        service.SetupGet(item => item.BackupDirectory).Returns("C:\\Backups");
        return service;
    }

    private static CrabDeskState CreateState()
    {
        var state = new CrabDeskState();
        state.Boxes.Add(new DesktopBox
        {
            Id = Guid.NewGuid(),
            Title = "新盒子",
            MonitorId = "DISPLAY1",
            Bounds = new LayoutRect(40, 40, 420, 310)
        });
        return state;
    }
}
