# AI Category Tag Chips Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the free-form AI category label textarea with removable tag chips and a dialog that supports whitespace-separated bulk additions.

**Architecture:** Keep `AiClassificationSettings.CategoryLabels` as the persisted newline-delimited value so existing profiles and classification behavior remain compatible. `AiClassificationViewModel` will expose an observable tag collection, normalize/de-duplicate additions, serialize it on every change, and use `IDialogService.PromptAsync` for input. The page will render a compact wrapping chip list with an adjacent add button.

**Tech Stack:** WinUI 3 XAML, CommunityToolkit.Mvvm, .NET 8, xUnit/Moq.

---

### Task 1: Add tag collection behavior to the AI settings view model

**Files:**

- Modify: `CrabDesk.WinUI/ViewModels/AiClassificationViewModel.cs`
- Test: `CrabDesk.WinUI.Tests/ViewModelTests.cs`

- [ ] **Step 1: Write failing tests for loading, batch addition, duplicate filtering, and removal**

```csharp
[Fact]
public void AiClassificationViewModelLoadsAndRemovesCategoryTags()
{
    var state = CreateState();
    state.Settings.AiClassification.CategoryLabels = "工作\n游戏\n工作";
    var viewModel = new AiClassificationViewModel(CreateService(state).Object, Mock.Of<IInfoBarService>(), Mock.Of<IDialogService>());

    Assert.Equal(["工作", "游戏"], viewModel.CategoryTags);
    viewModel.RemoveCategoryTagCommand.Execute("工作");
    Assert.Equal(["游戏"], viewModel.CategoryTags);
}

[Fact]
public async Task AiClassificationViewModelAddsWhitespaceSeparatedCategoryTags()
{
    var dialogs = new Mock<IDialogService>();
    dialogs.Setup(item => item.PromptAsync("添加分类标签", It.IsAny<string>(), ""))
        .ReturnsAsync("工作  游戏\n开发工具 工作");
    var service = CreateService(CreateState());
    var viewModel = new AiClassificationViewModel(service.Object, Mock.Of<IInfoBarService>(), dialogs.Object);

    await viewModel.AddCategoryTagsCommand.ExecuteAsync(null);

    Assert.Equal(["工作", "游戏", "开发工具"], viewModel.CategoryTags);
    service.Verify(item => item.ConfigureAiClassification(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), "工作\n游戏\n开发工具", It.IsAny<string>(), It.IsAny<bool>()), Times.AtLeastOnce);
}
```

- [ ] **Step 2: Run the focused tests and confirm they fail because the collection and commands do not yet exist**

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --no-restore --filter "FullyQualifiedName~AiClassificationViewModelLoadsAndRemovesCategoryTags|FullyQualifiedName~AiClassificationViewModelAddsWhitespaceSeparatedCategoryTags"`

Expected: FAIL with missing `CategoryTags`, `RemoveCategoryTagCommand`, and `AddCategoryTagsCommand` members.

- [ ] **Step 3: Implement normalized tag parsing and commands**

```csharp
public ObservableCollection<string> CategoryTags { get; } = [];

[RelayCommand]
private async Task AddCategoryTagsAsync()
{
    var input = await _dialogs.PromptAsync("添加分类标签", "多个标签请用空格分隔");
    if (input is not null)
    {
        AddCategoryTags(input);
    }
}

[RelayCommand]
private void RemoveCategoryTag(string? tag)
{
    if (!string.IsNullOrWhiteSpace(tag) && CategoryTags.Remove(tag))
    {
        SaveCategoryTags();
    }
}

private void AddCategoryTags(string text)
{
    foreach (var tag in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!CategoryTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            CategoryTags.Add(tag);
        }
    }
    SaveCategoryTags();
}

private void SaveCategoryTags()
{
    _categoryLabels = string.Join("\n", CategoryTags);
    OnPropertyChanged(nameof(CategoryLabels));
    SaveSettings();
}
```

Initialize `CategoryTags` using the same whitespace tokenization in the constructor and update the existing `CategoryLabels` setter to repopulate the collection when an imported/programmatic setting is applied.

- [ ] **Step 4: Run the focused tests and confirm they pass**

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --no-restore --filter "FullyQualifiedName~AiClassificationViewModelLoadsAndRemovesCategoryTags|FullyQualifiedName~AiClassificationViewModelAddsWhitespaceSeparatedCategoryTags"`

Expected: PASS.

### Task 2: Replace the textarea with accessible chip UI

**Files:**

- Create: `CrabDesk.WinUI/Controls/WrapPanel.cs`
- Modify: `CrabDesk.WinUI/Views/AiClassificationPage.xaml`

- [ ] **Step 1: Replace the classification-label `TextBox` with a wrapping chip surface and add button**

```xml
<StackPanel Spacing="8">
    <TextBlock Text="分类标签" FontWeight="SemiBold" />
    <ItemsRepeater ItemsSource="{Binding CategoryTags}">
        <ItemsRepeater.Layout>
                                <controls:WrapPanel HorizontalSpacing="8" VerticalSpacing="8" />
        </ItemsRepeater.Layout>
        <ItemsRepeater.ItemTemplate>
            <DataTemplate>
                <Border CornerRadius="14" Padding="10,5" Background="{ThemeResource AccentFillColorSecondaryBrush}">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <TextBlock Text="{Binding}" VerticalAlignment="Center" />
                        <Button Content="×" Command="{Binding DataContext.RemoveCategoryTagCommand, ElementName=RootPage}" CommandParameter="{Binding}" />
                    </StackPanel>
                </Border>
            </DataTemplate>
        </ItemsRepeater.ItemTemplate>
    </ItemsRepeater>
    <Button Content="添加标签" Command="{Binding AddCategoryTagsCommand}" HorizontalAlignment="Left" />
</StackPanel>
```

Name the root `Page` as `RootPage`; use an icon button style available in the application resources if one exists, otherwise use a text button with `AutomationProperties.Name="移除标签"`.

- [ ] **Step 2: Build and correct XAML binding or control-namespace errors**

Run: `dotnet build CrabDesk.sln --no-restore`

Expected: build succeeds without new errors.

### Task 3: Run regression tests and validate persistence

**Files:**

- Test: `CrabDesk.WinUI.Tests/ViewModelTests.cs`
- Test: `CrabDesk.Tests/AiClassificationServiceTests.cs`

- [ ] **Step 1: Run automated suites**

Run:

```powershell
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --no-restore --no-build
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --no-restore --no-build
```

Expected: all tests pass.

- [ ] **Step 2: Manually verify the settings page**

Launch the WinUI debug executable, open AI 分类 settings, and verify: existing newline-delimited labels render as individual chips; the add dialog accepts `工作 游戏` and line breaks; duplicates are ignored case-insensitively; each chip's remove button persists removal after restarting; the AI request still receives the newline-delimited labels.

## Review checklist

- The persisted string format remains compatible with existing settings and imports.
- Empty tokens, repeated whitespace, and case-insensitive duplicates do not create duplicate chips.
- Every remove button has an accessible name and only removes its own tag.
- The input dialog is the only text entry surface for adding tags and supports bulk additions.
