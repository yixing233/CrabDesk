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
        fonts.SetupGet(item => item.FontFamilies).Returns(["Segoe UI", "Microsoft YaHei UI"]);
        var viewModel = new AppearanceViewModel(service.Object, fonts.Object);

        viewModel.TitleFontFamily = "Microsoft YaHei UI";
        viewModel.LabelFontFamily = "Microsoft YaHei UI";
        viewModel.LabelFontSize = 12.5;

        service.Verify(item => item.SetBoxTitleFontFamily(null, "Microsoft YaHei UI"), Times.Once);
        service.Verify(item => item.SetBoxLabelFontFamily(null, "Microsoft YaHei UI"), Times.Once);
        service.Verify(item => item.SetBoxLabelFontSize(null, 12.5), Times.Once);
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
                It.IsAny<IProgress<AiWebSearchProgress>>()))
            .ReturnsAsync(new AiClassificationPreview(7, 1, [], []) { RequestedItemKeys = ["one"] });
        var viewModel = new AiClassificationViewModel(
            service.Object,
            Mock.Of<IInfoBarService>(),
            Mock.Of<IDialogService>());
        viewModel.WorkspaceItems[1].IsSelected = false;

        Assert.Equal(0, viewModel.SelectedInspectorTabIndex);

        await viewModel.ClassifyCommand.ExecuteAsync(null);

        Assert.Equal(1, viewModel.SelectedInspectorTabIndex);

        service.Verify(item => item.PreviewAiClassificationAsync(
            7,
            It.Is<IReadOnlyCollection<string>>(keys => HasOnlyKey(keys, "one")),
            It.IsAny<IProgress<AiClassificationProgress>>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<string>>(),
            It.IsAny<IProgress<AiClassificationModelStreamUpdate>>(),
            It.IsAny<IProgress<AiClassificationUsageProgress>>(),
            It.IsAny<IProgress<AiClassificationTransportProgress>>(),
            It.IsAny<IProgress<AiWebSearchProgress>>()), Times.Once);
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
    public async Task AiClassificationViewModelAppliesManualLabelOnlyToAnUncertainItem()
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
                It.IsAny<IProgress<AiWebSearchProgress>>()))
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
                It.IsAny<IProgress<AiWebSearchProgress>>()))
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
    public async Task AiClassificationViewModelCopiesReasoningAndStructuredOutputToClipboard()
    {
        var clipboard = new Mock<IClipboardService>();
        var notifications = new Mock<IInfoBarService>();
        var viewModel = new AiClassificationViewModel(
            CreateService(CreateState()).Object,
            notifications.Object,
            Mock.Of<IDialogService>(),
            clipboard: clipboard.Object);

        await viewModel.CopyReasoningCommand.ExecuteAsync(null);
        clipboard.Verify(c => c.SetTextAsync("尚未开始 AI 分类。"), Times.Once);
        notifications.Verify(n => n.Show("思考过程已复制到剪贴板", InfoBarSeverity.Success, It.IsAny<TimeSpan?>()), Times.Once);

        await viewModel.CopyStructuredOutputCommand.ExecuteAsync(null);
        clipboard.Verify(c => c.SetTextAsync("尚未生成分类结果。"), Times.Once);
        notifications.Verify(n => n.Show("分类结果已复制到剪贴板", InfoBarSeverity.Success, It.IsAny<TimeSpan?>()), Times.Once);
    }

    private static bool HasOnlyKey(IReadOnlyCollection<string> keys, string expected) =>
        keys.Count == 1 && keys.Contains(expected, StringComparer.Ordinal);

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
