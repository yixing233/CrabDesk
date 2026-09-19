using CrabDesk.Core;
using CrabDesk.WinUI.Converters;
using CrabDesk.WinUI.Services;
using CrabDesk.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;
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
        var viewModel = new GeneralViewModel(service.Object, theme.Object, Mock.Of<IBackdropService>());

        viewModel.StartWithWindows = true;

        service.Verify(item => item.SetStartWithWindows(true), Times.Once);
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
    public async Task BackupViewModelOpensPreviewForSelectedBackup()
    {
        var state = CreateState();
        var service = CreateService(state);
        var backup = new LayoutBackupInfo(
            "C:\\Backups\\CrabDesk-20260824-1845.crabdesk.json",
            DateTimeOffset.Parse("2026-08-24T18:45:00+08:00"),
            state.SchemaVersion,
            1,
            0,
            1,
            new LayoutBackupSnapshot(new LayoutRect(0, 0, 1920, 1080), []));
        service.Setup(item => item.GetBackupsAsync())
            .ReturnsAsync([backup]);
        var dialogs = new Mock<IDialogService>();
        var viewModel = new BackupViewModel(
            service.Object,
            Mock.Of<IFilePickerService>(),
            dialogs.Object);

        await viewModel.PreviewCommand.ExecuteAsync(null);

        dialogs.Verify(item => item.ShowBackupPreviewAsync(backup), Times.Once);
    }

    [Fact]
    public void AppearanceViewModelPreservesBoxSizeWhenChangingColor()
    {
        var state = CreateState();
        var service = CreateService(state);
        var viewModel = new AppearanceViewModel(service.Object, Mock.Of<IFontCatalogService>());
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

    [Theory]
    [InlineData(true, null, 1d)]
    [InlineData(false, null, BooleanToOpacityConverter.DimmedOpacity)]
    [InlineData(false, "0.3", 0.3)]
    [InlineData(false, "not-a-number", BooleanToOpacityConverter.DimmedOpacity)]
    [InlineData(null, null, BooleanToOpacityConverter.DimmedOpacity)]
    public void BooleanToOpacityConverterDimsDeselectedContent(object? value, string? parameter, double expected)
    {
        var converter = new BooleanToOpacityConverter();

        var opacity = Assert.IsType<double>(converter.Convert(value!, typeof(double), parameter!, string.Empty));

        Assert.Equal(expected, opacity, precision: 6);
    }

    [Theory]
    [InlineData(true, null, PrimaryActionStyleConverter.PrimaryStyleKey)]
    [InlineData(false, null, PrimaryActionStyleConverter.SecondaryStyleKey)]
    [InlineData(true, "invert", PrimaryActionStyleConverter.SecondaryStyleKey)]
    [InlineData(false, "invert", PrimaryActionStyleConverter.PrimaryStyleKey)]
    public void PrimaryActionStyleConverterSwapsWhichButtonReadsAsPrimary(bool flag, string? parameter, string expectedKey)
    {
        Assert.Equal(expectedKey, PrimaryActionStyleConverter.GetStyleKey(flag, parameter));
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
        fonts.SetupGet(item => item.FontFamilies).Returns(["Segoe UI", "Microsoft YaHei UI", "Consolas"]);
        var viewModel = new AppearanceViewModel(service.Object, fonts.Object);

        // Deliberately a font other than the default: the setters skip a value
        // equal to the current one, so picking the default would prove nothing.
        viewModel.TitleFontFamily = "Consolas";
        viewModel.LabelFontFamily = "Consolas";
        viewModel.LabelFontSize = 12.5;

        service.Verify(item => item.SetBoxTitleFontFamily(null, "Consolas"), Times.Once);
        service.Verify(item => item.SetBoxLabelFontFamily(null, "Consolas"), Times.Once);
        service.Verify(item => item.SetBoxLabelFontSize(null, 12.5), Times.Once);
    }

    [Fact]
    public void AppearanceViewModelDefaultsBoxFontsToMicrosoftYaHeiUi()
    {
        var state = CreateState();
        var service = CreateService(state);
        var fonts = new Mock<IFontCatalogService>();
        fonts.SetupGet(item => item.FontFamilies).Returns(["Segoe UI", "Microsoft YaHei UI"]);
        var viewModel = new AppearanceViewModel(service.Object, fonts.Object);

        Assert.Equal("Microsoft YaHei UI", viewModel.TitleFontFamily);
        Assert.Equal("Microsoft YaHei UI", viewModel.LabelFontFamily);
        Assert.Equal("Microsoft YaHei UI", viewModel.IconLabelFontFamily);
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

    [Fact]
    public async Task AboutViewModelCopiesDiagnosticsAndReportsSuccess()
    {
        var service = CreateService(CreateState());
        service.Setup(item => item.GetDesktopHostDiagnosticsText()).Returns("diagnostic text");
        var clipboard = new Mock<IClipboardService>();
        clipboard.Setup(item => item.SetTextAsync("diagnostic text")).Returns(Task.CompletedTask);
        var viewModel = new AboutViewModel(
            service.Object,
            Mock.Of<IDialogService>(),
            clipboard.Object);

        await viewModel.CopyDiagnosticsCommand.ExecuteAsync(null);

        clipboard.Verify(item => item.SetTextAsync("diagnostic text"), Times.Once);
        Assert.Equal("诊断信息已复制", viewModel.MaintenanceStatus);
        Assert.Equal(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success, viewModel.MaintenanceInfoSeverity);
    }

    [Fact]
    public async Task AboutViewModelReportsClipboardFailureWithoutThrowing()
    {
        var service = CreateService(CreateState());
        service.Setup(item => item.GetDesktopHostDiagnosticsText()).Returns("diagnostic text");
        var clipboard = new Mock<IClipboardService>();
        clipboard.Setup(item => item.SetTextAsync(It.IsAny<string>()))
            .Returns(Task.FromException(new System.Runtime.InteropServices.COMException(
                "Clipboard is busy",
                unchecked((int)0x800401D0))));
        var viewModel = new AboutViewModel(
            service.Object,
            Mock.Of<IDialogService>(),
            clipboard.Object);

        await viewModel.CopyDiagnosticsCommand.ExecuteAsync(null);

        Assert.Contains("复制诊断失败", viewModel.MaintenanceStatus);
        Assert.Equal(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, viewModel.MaintenanceInfoSeverity);
    }

    [Fact]
    public async Task AiClassificationViewModelShowsInlineConnectivityProgressAndSuccessResult()
    {
        var service = CreateService(CreateState());
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Setup(item => item.TestAiModelConnectivityAsync(It.IsAny<CancellationToken>()))
            .Returns(completion.Task);
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());

        var task = viewModel.TestConnectivityCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsConnectivityTestInProgress);
        Assert.True(viewModel.HasConnectivityTestResult);
        Assert.Equal(InfoBarSeverity.Informational, viewModel.ConnectivityTestSeverity);
        Assert.Equal("正在测试模型连接", viewModel.ConnectivityTestTitle);
        Assert.Equal("正在测试…", viewModel.ConnectivityTestButtonText);

        completion.SetResult();
        await task;

        Assert.False(viewModel.IsConnectivityTestInProgress);
        Assert.True(viewModel.HasConnectivityTestResult);
        Assert.Equal(InfoBarSeverity.Success, viewModel.ConnectivityTestSeverity);
        Assert.Equal("模型连接正常", viewModel.ConnectivityTestTitle);
        Assert.Contains("可以开始 AI 分类", viewModel.ConnectivityTestMessage);
    }

    [Fact]
    public async Task AiClassificationViewModelShowsSafeInlineConnectivityFailure()
    {
        var service = CreateService(CreateState());
        service.Setup(item => item.TestAiModelConnectivityAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiClassificationRequestException("服务端返回的敏感错误"));
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());

        await viewModel.TestConnectivityCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsConnectivityTestInProgress);
        Assert.True(viewModel.HasConnectivityTestResult);
        Assert.Equal(InfoBarSeverity.Error, viewModel.ConnectivityTestSeverity);
        Assert.Equal("模型连接失败", viewModel.ConnectivityTestTitle);
        Assert.Equal(AiClassificationRequestException.SafeMessage, viewModel.ConnectivityTestMessage);
        Assert.DoesNotContain("敏感错误", viewModel.ConnectivityTestMessage);
    }

    [Fact]
    public async Task AiClassificationViewModelClearsConnectivityResultWhenConnectionSettingsChange()
    {
        var service = CreateService(CreateState());
        service.Setup(item => item.TestAiModelConnectivityAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());

        await viewModel.TestConnectivityCommand.ExecuteAsync(null);
        viewModel.Model = "另一模型";

        Assert.False(viewModel.HasConnectivityTestResult);
    }

    [Fact]
    public void AiClassificationViewModelLoadsAndRemovesCategoryTags()
    {
        var state = CreateState();
        state.Settings.AiClassification.CategoryLabels = "工作\n游戏\n工作";
        var viewModel = new AiClassificationViewModel(
            CreateService(state).Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());

        Assert.Equal(["工作", "游戏"], viewModel.CategoryTags);

        viewModel.RemoveCategoryTagCommand.Execute("工作");

        Assert.Equal(["游戏"], viewModel.CategoryTags);
    }

    [Fact]
    public async Task AiClassificationViewModelAddsWhitespaceSeparatedCategoryTags()
    {
        var service = CreateService(CreateState());
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(item => item.PromptAsync(
                "添加分类标签",
                It.IsAny<string>(),
                ""))
            .ReturnsAsync("工作  游戏\n开发工具 工作");
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            dialogs.Object);

        await viewModel.AddCategoryTagsCommand.ExecuteAsync(null);

        Assert.Equal(["工作", "游戏", "开发工具"], viewModel.CategoryTags);
        service.Verify(item => item.ConfigureAiClassification(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            "工作\n游戏\n开发工具",
            It.IsAny<string>(),
            It.IsAny<bool>()), Times.AtLeastOnce);
    }

    [Fact]
    public void AiClassificationViewModelSavesWebSearchSettingsSeparately()
    {
        var service = CreateService(CreateState());
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());

        viewModel.WebSearchEnabled = true;
        viewModel.WebSearchApiKey = "tvly-test";

        service.Verify(item => item.ConfigureAiWebSearch(true, "tvly-test"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AiClassificationViewModelRunsPreviewForOnlySelectedWorkspaceItems()
    {
        var state = CreateState();
        state.Settings.AiClassification.CategoryLabels = "工作\n学习";
        var service = CreateService(state);
        service.Setup(item => item.GetAiClassificationWorkspace()).Returns(new AiClassificationWorkspace(
            7,
            [
                new AiClassificationWorkspaceItem("one", "One", DesktopItemKind.File, "C:\\One.txt"),
                new AiClassificationWorkspaceItem("two", "Two", DesktopItemKind.File, "C:\\Two.txt")
            ]));
        service.Setup(item => item.PreviewAiClassificationAsync(
                7,
                It.Is<IReadOnlyCollection<string>>(keys => HasOnlyKey(keys, "one")),
                It.IsAny<IProgress<AiClassificationProgress>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<string>>(),
                It.IsAny<IProgress<AiClassificationModelStreamUpdate>>(),
                It.IsAny<IProgress<AiClassificationUsageProgress>>(),
                It.IsAny<IProgress<AiClassificationTransportProgress>>(),
                It.IsAny<IProgress<AiWebSearchProgress>>(),
                 It.IsAny<IProgress<AiClassificationActivity>>()))
            .ReturnsAsync(new AiClassificationPreview(7, 1, [], []) { RequestedItemKeys = ["one"] });
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());
        viewModel.WorkspaceItems[1].IsSelected = false;

        await viewModel.ClassifyCommand.ExecuteAsync(null);

        service.Verify(item => item.PreviewAiClassificationAsync(
            7,
            It.Is<IReadOnlyCollection<string>>(keys => HasOnlyKey(keys, "one")),
            It.IsAny<IProgress<AiClassificationProgress>>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<string>>(),
            It.IsAny<IProgress<AiClassificationModelStreamUpdate>>(),
            It.IsAny<IProgress<AiClassificationUsageProgress>>(),
            It.IsAny<IProgress<AiClassificationTransportProgress>>(),
            It.IsAny<IProgress<AiWebSearchProgress>>(),
             It.IsAny<IProgress<AiClassificationActivity>>()), Times.Once);
    }

    [Fact]
    public void AiClassificationViewModelTogglesWorkspaceSelectionFromIcon()
    {
        var service = CreateService(CreateState());
        service.Setup(item => item.GetAiClassificationWorkspace()).Returns(new AiClassificationWorkspace(
            7,
            [new AiClassificationWorkspaceItem("one", "One", DesktopItemKind.File, "C:\\One.txt")]));
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());
        var workspaceItem = Assert.Single(viewModel.WorkspaceItems);

        viewModel.ToggleWorkspaceItemSelectionCommand.Execute(workspaceItem);

        Assert.False(workspaceItem.IsSelected);
        Assert.Equal(0, viewModel.SelectedItemCount);
    }

    [Fact]
    public void AiClassificationViewModelFiltersTheVisibleGridByNameOrLabelWithoutTouchingSelection()
    {
        var service = CreateService(CreateState());
        service.Setup(item => item.GetAiClassificationWorkspace()).Returns(new AiClassificationWorkspace(
            7,
            [
                new AiClassificationWorkspaceItem("one", "Docker Desktop", DesktopItemKind.File, "C:\\One.txt"),
                new AiClassificationWorkspaceItem("two", "QQ音乐", DesktopItemKind.File, "C:\\Two.txt"),
                new AiClassificationWorkspaceItem("three", "Obsidian", DesktopItemKind.File, "C:\\Three.txt")
            ]));
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());
        Assert.Equal(viewModel.WorkspaceItems, viewModel.VisibleWorkspaceItems);
        Assert.False(viewModel.IsWorkspaceFiltered);

        viewModel.WorkspaceFilter = "  desk ";
        Assert.Equal(["one"], viewModel.VisibleWorkspaceItems.Select(item => item.ItemKey));
        Assert.Equal("已选择 3 项 · 匹配 1/3 项", viewModel.SelectedItemSummary);
        Assert.False(viewModel.HasNoFilterMatches);

        // "清除" only clears what is visible; the other two stay selected.
        viewModel.ClearSelectionCommand.Execute(null);
        Assert.False(viewModel.WorkspaceItems[0].IsSelected);
        Assert.Equal(2, viewModel.SelectedItemCount);

        viewModel.WorkspaceFilter = "不存在";
        Assert.Empty(viewModel.VisibleWorkspaceItems);
        Assert.True(viewModel.HasNoFilterMatches);
        Assert.Equal("没有匹配「不存在」的图标", viewModel.NoFilterMatchesText);

        // Labels are searchable too once the item has a classification.
        viewModel.WorkspaceItems[2].HasPreview = true;
        viewModel.WorkspaceItems[2].AiLabel = "专业工具";
        viewModel.WorkspaceFilter = "专业";
        Assert.Equal(["three"], viewModel.VisibleWorkspaceItems.Select(item => item.ItemKey));

        viewModel.WorkspaceFilter = string.Empty;
        Assert.Equal(viewModel.WorkspaceItems, viewModel.VisibleWorkspaceItems);
        Assert.False(viewModel.HasNoFilterMatches);
        Assert.Equal("已选择 2 项", viewModel.SelectedItemSummary);
    }

    [Fact]
    public async Task AiClassificationViewModelAppliesAManualLabelToAnUncertainItem()
    {
        var state = CreateState();
        state.Settings.AiClassification.CategoryLabels = "工作\n学习";
        var service = CreateService(state);
        service.Setup(item => item.GetAiClassificationWorkspace()).Returns(new AiClassificationWorkspace(
            7,
            [
                new AiClassificationWorkspaceItem("one", "One", DesktopItemKind.File, "C:\\One.txt"),
                new AiClassificationWorkspaceItem("two", "Two", DesktopItemKind.File, "C:\\Two.txt")
            ]));
        service.Setup(item => item.PreviewAiClassificationAsync(
                7,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IProgress<AiClassificationProgress>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<string>>(),
                It.IsAny<IProgress<AiClassificationModelStreamUpdate>>(),
                It.IsAny<IProgress<AiClassificationUsageProgress>>(),
                It.IsAny<IProgress<AiClassificationTransportProgress>>(),
                It.IsAny<IProgress<AiWebSearchProgress>>(),
                 It.IsAny<IProgress<AiClassificationActivity>>()))
            .ReturnsAsync(new AiClassificationPreview(
                7,
                2,
                [new AiClassificationAssignment("one", "One", "工作")],
                []) { RequestedItemKeys = ["one", "two"] });
        service.Setup(item => item.ApplyAiClassificationPreviewAsync(
                It.IsAny<AiClassificationPreview>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiClassificationApplyResult(2, 2, 2, 0, 0, []));
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());

        await viewModel.ClassifyCommand.ExecuteAsync(null);
        viewModel.WorkspaceItems.Single(item => item.ItemKey == "two").ManualLabel = "学习";
        await viewModel.ApplyCommand.ExecuteAsync(null);

        service.Verify(item => item.ApplyAiClassificationPreviewAsync(
            It.Is<AiClassificationPreview>(preview =>
                preview.Assignments.Count == 2 &&
                preview.Assignments[0].ItemKey == "one" &&
                preview.Assignments[0].Label == "工作" &&
                preview.Assignments[1].ItemKey == "two" &&
                preview.Assignments[1].Label == "学习"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AiClassificationViewModelLetsTheUserAdjustAReviewedPreviewBeforeApplyingIt()
    {
        var state = CreateState();
        state.Settings.AiClassification.CategoryLabels = "工作\n学习";
        var service = CreateService(state);
        service.Setup(item => item.GetAiClassificationWorkspace()).Returns(new AiClassificationWorkspace(
            7,
            [
                new AiClassificationWorkspaceItem("one", "One", DesktopItemKind.File, "C:\\One.txt"),
                new AiClassificationWorkspaceItem("two", "Two", DesktopItemKind.File, "C:\\Two.txt"),
                new AiClassificationWorkspaceItem("three", "Three", DesktopItemKind.File, "C:\\Three.txt")
            ]));
        service.Setup(item => item.PreviewAiClassificationAsync(
                7,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IProgress<AiClassificationProgress>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<string>>(),
                It.IsAny<IProgress<AiClassificationModelStreamUpdate>>(),
                It.IsAny<IProgress<AiClassificationUsageProgress>>(),
                It.IsAny<IProgress<AiClassificationTransportProgress>>(),
                It.IsAny<IProgress<AiWebSearchProgress>>(),
                It.IsAny<IProgress<AiClassificationActivity>>()))
            .ReturnsAsync(new AiClassificationPreview(
                7,
                3,
                [
                    new AiClassificationAssignment("one", "One", "工作"),
                    new AiClassificationAssignment("two", "Two", "工作"),
                    new AiClassificationAssignment("three", "Three", "工作")
                ],
                []) { RequestedItemKeys = ["one", "two", "three"] });
        AiClassificationPreview? applied = null;
        service.Setup(item => item.ApplyAiClassificationPreviewAsync(
                It.IsAny<AiClassificationPreview>(),
                It.IsAny<CancellationToken>()))
            .Callback<AiClassificationPreview, CancellationToken>((preview, _) => applied = preview)
            .ReturnsAsync(new AiClassificationApplyResult(3, 1, 1, 0, 2, []));
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());
        Assert.Equal("开始 AI 分类", viewModel.ClassifyButtonText);

        await viewModel.ClassifyCommand.ExecuteAsync(null);

        Assert.Equal("重新分类", viewModel.ClassifyButtonText);
        var one = viewModel.WorkspaceItems.Single(item => item.ItemKey == "one");
        var two = viewModel.WorkspaceItems.Single(item => item.ItemKey == "two");
        var three = viewModel.WorkspaceItems.Single(item => item.ItemKey == "three");

        // The chip menu lists every category with the AI suggestion checked, plus "不归类".
        var choices = viewModel.GetLabelChoices(one);
        Assert.Equal(
            [AiWorkbenchLabelChoiceKind.Category, AiWorkbenchLabelChoiceKind.Category, AiWorkbenchLabelChoiceKind.KeepOnDesktop],
            choices.Select(choice => choice.Kind));
        Assert.Equal("工作", Assert.Single(choices, choice => choice.IsCurrent).Label);

        // Override the suggestion; a restore entry appears.
        viewModel.ChooseWorkspaceItemLabelCommand.Execute(choices.Single(choice => choice.Label == "学习"));
        Assert.True(one.IsManuallyLabeled);
        Assert.Equal("学习", one.EffectiveLabel);
        var restore = Assert.Single(
            viewModel.GetLabelChoices(one),
            choice => choice.Kind == AiWorkbenchLabelChoiceKind.RestoreAiSuggestion);
        Assert.Equal("工作", restore.Label);

        // Picking the model's own label again is a restore rather than an override.
        viewModel.ChooseWorkspaceItemLabelCommand.Execute(viewModel.GetLabelChoices(one)
            .Single(choice => choice.Kind == AiWorkbenchLabelChoiceKind.Category && choice.Label == "工作"));
        Assert.True(one.ShowsAiLabel);
        Assert.Null(one.ManualLabel);

        viewModel.ChooseWorkspaceItemLabelCommand.Execute(viewModel.GetLabelChoices(one)
            .Single(choice => choice.Label == "学习"));
        viewModel.ChooseWorkspaceItemLabelCommand.Execute(viewModel.GetLabelChoices(two)
            .Single(choice => choice.Kind == AiWorkbenchLabelChoiceKind.KeepOnDesktop));
        Assert.True(two.IsExcluded);
        Assert.Null(two.EffectiveLabel);
        Assert.False(two.IsUncertain);
        three.IsSelected = false;

        Assert.Equal(1, viewModel.EffectiveAssignmentCount);
        Assert.Equal("确认后将归入盒子 1/2 项（1 项手动调整，1 项不归类）。", viewModel.Status);

        await viewModel.ApplyCommand.ExecuteAsync(null);

        // Only the reviewed plan reaches the runtime: the override, minus the excluded and unchecked items.
        var assignment = Assert.Single(applied!.Assignments);
        Assert.Equal(("one", "学习"), (assignment.ItemKey, assignment.Label));
        Assert.Contains(viewModel.Conversation, message =>
            message.IsAssistant && message.Text == "已分类 1/3 项，2 项按你的选择保留在桌面。");
    }

    [Fact]
    public async Task AiClassificationViewModelGroupsPreviewResultsIntoCards()
    {
        var state = CreateState();
        state.Settings.AiClassification.CategoryLabels = "工作\n学习";
        var service = CreateService(state);
        service.Setup(item => item.GetAiClassificationWorkspace()).Returns(new AiClassificationWorkspace(
            7,
            [
                new AiClassificationWorkspaceItem("one", "One", DesktopItemKind.File, "C:\\One.txt"),
                new AiClassificationWorkspaceItem("two", "Two", DesktopItemKind.File, "C:\\Two.txt"),
                new AiClassificationWorkspaceItem("three", "Three", DesktopItemKind.File, "C:\\Three.txt")
            ]));
        service.Setup(item => item.PreviewAiClassificationAsync(
                7,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IProgress<AiClassificationProgress>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<string>>(),
                It.IsAny<IProgress<AiClassificationModelStreamUpdate>>(),
                It.IsAny<IProgress<AiClassificationUsageProgress>>(),
                It.IsAny<IProgress<AiClassificationTransportProgress>>(),
                It.IsAny<IProgress<AiWebSearchProgress>>(),
                 It.IsAny<IProgress<AiClassificationActivity>>()))
            .ReturnsAsync(new AiClassificationPreview(
                7,
                3,
                [
                    new AiClassificationAssignment("one", "One", "工作"),
                    new AiClassificationAssignment("two", "Two", "工作")
                ],
                []) { RequestedItemKeys = ["one", "two", "three"] });

        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());

        await viewModel.ClassifyCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasResultGroups);
        Assert.Equal(2, viewModel.ResultGroups.Count);

        var workGroup = Assert.Single(viewModel.ResultGroups.Where(g => g.CategoryName == "工作"));
        Assert.False(workGroup.IsUncertainGroup);
        Assert.Equal(2, workGroup.ItemCount);

        var uncertainGroup = Assert.Single(viewModel.ResultGroups.Where(g => g.IsUncertainGroup));
        Assert.Equal("待确认项目", uncertainGroup.CategoryName);
        Assert.Equal(1, uncertainGroup.ItemCount);
        Assert.Equal("three", uncertainGroup.Items[0].ItemKey);
    }

    [Fact]
    public async Task AiClassificationViewModelKeepsEveryActivityRowForTheFinishedTurn()
    {
        // Progress<T> posts through the ambient SynchronizationContext; run the
        // callbacks inline so the assertions below are deterministic.
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            const int itemCount = AiConversationMessageViewModel.RecentActivityWindow + 4;
            var items = Enumerable.Range(1, itemCount)
                .Select(index => new AiClassificationWorkspaceItem(
                    $"item-{index}", $"Item {index}", DesktopItemKind.File, $"C:\\Item{index}.txt"))
                .ToArray();
            var state = CreateState();
            state.Settings.AiClassification.CategoryLabels = "工作";
            var service = CreateService(state);
            service.Setup(item => item.GetAiClassificationWorkspace())
                .Returns(new AiClassificationWorkspace(7, items));
            service.Setup(item => item.PreviewAiClassificationAsync(
                    7,
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<IProgress<AiClassificationProgress>>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<IProgress<string>>(),
                    It.IsAny<IProgress<AiClassificationModelStreamUpdate>>(),
                    It.IsAny<IProgress<AiClassificationUsageProgress>>(),
                    It.IsAny<IProgress<AiClassificationTransportProgress>>(),
                    It.IsAny<IProgress<AiWebSearchProgress>>(),
                    It.IsAny<IProgress<AiClassificationActivity>>()))
                .Callback(new InvocationAction(invocation =>
                {
                    // Mirror the runtime: the whole batch is marked analyzing, then classified one by one.
                    var activities = Assert.IsAssignableFrom<IProgress<AiClassificationActivity>>(invocation.Arguments[9]);
                    foreach (var item in items)
                    {
                        activities.Report(new AiClassificationActivity(
                            item.ItemKey, item.DisplayName, AiClassificationActivityPhase.Analyzing));
                    }
                    foreach (var item in items)
                    {
                        activities.Report(new AiClassificationActivity(
                            item.ItemKey, item.DisplayName, AiClassificationActivityPhase.Classified, "工作"));
                    }
                }))
                .ReturnsAsync(new AiClassificationPreview(
                    7,
                    itemCount,
                    items.Select(item => new AiClassificationAssignment(item.ItemKey, item.DisplayName, "工作")).ToArray(),
                    []) { RequestedItemKeys = items.Select(item => item.ItemKey).ToArray() });
            var viewModel = new AiClassificationViewModel(
                service.Object,
                Mock.Of<IInfoBarService>(),
                Mock.Of<IDialogService>());

            await viewModel.ClassifyCommand.ExecuteAsync(null);

            var turn = viewModel.Conversation.Last(message => message.IsAssistant);
            Assert.False(turn.IsRunning);
            // Every processed item stays reviewable in the finished turn's trace...
            Assert.Equal(
                items.Select(item => item.ItemKey),
                turn.ClassificationActivities.Select(row => row.ItemKey));
            Assert.All(turn.ClassificationActivities, row => Assert.True(row.IsClassified));
            Assert.Equal($"已处理 {itemCount}/{itemCount} 项", turn.ClassificationActivitySummary);
            // ...while the live window stays bounded.
            Assert.Equal(AiConversationMessageViewModel.RecentActivityWindow, turn.RecentClassificationActivities.Count);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public void AiClassificationViewModelSwitchesCardAndJsonViewModes()
    {
        var viewModel = new AiClassificationViewModel(
            CreateService(CreateState()).Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());

        Assert.False(viewModel.IsJsonViewMode);

        viewModel.ShowJsonViewCommand.Execute(null);
        Assert.True(viewModel.IsJsonViewMode);

        viewModel.ShowCardViewCommand.Execute(null);
        Assert.False(viewModel.IsJsonViewMode);
    }

    [Fact]
    public void AiClassificationViewToggleSwitchesBothWays()
    {
        var viewModel = new AiClassificationViewModel(
            CreateService(CreateState()).Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());

        Assert.False(viewModel.IsJsonViewMode);
        viewModel.ShowJsonViewCommand.Execute(null);
        Assert.True(viewModel.IsJsonViewMode);
        viewModel.ShowCardViewCommand.Execute(null);
        Assert.False(viewModel.IsJsonViewMode);
    }

    [Fact]
    public async Task AiClassificationViewModelCopiesStructuredOutputToClipboard()
    {
        var clipboard = new Mock<IClipboardService>();
        var notifications = new Mock<IInfoBarService>();
        var viewModel = new AiClassificationViewModel(
            CreateService(CreateState()).Object,
            notifications.Object,
            Mock.Of<IDialogService>(),
            clipboard: clipboard.Object);

        await viewModel.CopyStructuredOutputCommand.ExecuteAsync(null);
        clipboard.Verify(c => c.SetTextAsync("尚未生成分类结果。"), Times.Once);
        notifications.Verify(n => n.Show("分类结果已复制到剪贴板", InfoBarSeverity.Success, It.IsAny<TimeSpan?>()), Times.Once);
    }

    private static bool HasOnlyKey(IReadOnlyCollection<string> keys, string expected) =>
        keys.Count == 1 && keys.Contains(expected, StringComparer.Ordinal);

    /// <summary>Runs posted callbacks on the calling thread so Progress&lt;T&gt; reports synchronously.</summary>
    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private static Mock<ICrabDeskService> CreateService(CrabDeskState state)
    {
        var service = new Mock<ICrabDeskService>();
        service.SetupGet(item => item.State).Returns(state);
        service.SetupGet(item => item.Boxes).Returns(state.Boxes);
        service.SetupGet(item => item.DesktopConnected).Returns(true);
        service.SetupGet(item => item.BackupDirectory).Returns("C:\\Backups");
        service.SetupGet(item => item.LastUpdateCheck)
            .Returns(new UpdateCheckResult(UpdateCheckStatus.NotChecked, "0.6.0"));
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
