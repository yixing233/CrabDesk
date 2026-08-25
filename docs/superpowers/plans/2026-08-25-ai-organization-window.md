# AI Organization Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide AI organization as a dedicated resizable window launched from the single-page Smart Organization settings screen and from existing external commands.

**Architecture:** The settings navigation opens `OrganizationPage` directly. That page contains a single AI workbench entry, while `App` owns at most one `AiOrganizationWindow`, restores and focuses it on repeated requests, and releases it after close. The window creates window-scoped dialog, notification, and view-model instances so prompts and feedback stay attached to the AI workbench.

**Tech Stack:** C# 12, .NET 8, WinUI 3 XAML, CommunityToolkit.Mvvm, xUnit

---

### Task 1: Lock the new navigation and window surfaces with failing tests

**Files:**
- Create: `CrabDesk.WinUI.Tests/AiOrganizationWindowTests.cs`
- Modify: `CrabDesk.WinUI.Tests/AiClassificationPageLayoutTests.cs`

- [x] **Step 1: Add source-structure tests**

Create tests that load the XAML files from the solution and assert these concrete invariants:

```csharp
[Fact]
public void OrganizationPageExposesTheAiWorkbenchEntry()
{
    var document = LoadXaml("CrabDesk.WinUI", "Views", "OrganizationPage.xaml");
    var entry = document.Descendants(Presentation + "Button")
        .Single(element => (string?)element.Attribute("Click") == "OpenAiOrganization_OnClick");

    Assert.Equal("打开 AI 整理", entry.Value.Trim());
}

[Fact]
public void AiOrganizationWindowHostsTheWorkbenchPage()
{
    var document = LoadXaml("CrabDesk.WinUI", "Windows", "AiOrganizationWindow.xaml");

    Assert.Contains(document.Descendants(Presentation + "ContentControl"),
        element => (string?)element.Attribute(XName.Get("Name", Xaml)) == "WorkbenchHost");
}
```

Update the page layout test so it asserts a two-row workbench (`Auto`, `*`) with the activity expander inside the right-side scroll viewer rather than as an additional root row.

- [x] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --no-restore --filter "FullyQualifiedName~AiOrganizationWindowTests|FullyQualifiedName~AiClassificationPageLayoutTests"
```

Expected: failure because `AiOrganizationWindow.xaml` and the `OpenAiOrganization_OnClick` entry do not exist.

### Task 2: Make Smart Organization a single settings page

**Files:**
- Modify: `CrabDesk.WinUI/MainWindow.xaml.cs`
- Modify: `CrabDesk.WinUI/Views/OrganizationPage.xaml`
- Modify: `CrabDesk.WinUI/Views/OrganizationPage.xaml.cs`
- Delete: `CrabDesk.WinUI/Views/SmartOrganizationPage.xaml`
- Delete: `CrabDesk.WinUI/Views/SmartOrganizationPage.xaml.cs`

- [x] **Step 1: Navigate directly to the rules page**

Change the navigation mapping to:

```csharp
"smart-organization" or "organization" => typeof(OrganizationPage),
```

The `ai` route is removed because AI organization opens a window instead of navigating the settings frame.

- [x] **Step 2: Add the AI workbench entry card**

Add this card after the general organization settings card:

```xml
<Border Style="{StaticResource SectionCardStyle}" Padding="16">
    <Grid ColumnSpacing="16">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <StackPanel Spacing="3">
            <TextBlock Text="AI 整理" FontWeight="SemiBold" />
            <TextBlock Text="选择桌面图标并生成分类建议。"
                       Style="{StaticResource SecondaryBodyStyle}" />
        </StackPanel>
        <Button Grid.Column="1"
                Click="OpenAiOrganization_OnClick"
                Style="{StaticResource AccentButtonStyle}">
            <StackPanel Orientation="Horizontal" Spacing="6">
                <lucide:LucideIcon Icon="Sparkles" FontSize="16" />
                <TextBlock Text="打开 AI 整理" />
            </StackPanel>
        </Button>
    </Grid>
</Border>
```

The click handler calls `App.CurrentApp.OpenAiOrganizationWorkbench()`.

- [x] **Step 3: Remove the selector wrapper**

Delete both `SmartOrganizationPage` files after all production references are removed.

### Task 3: Add the dedicated window and its lifecycle

**Files:**
- Create: `CrabDesk.WinUI/Windows/AiOrganizationWindow.xaml`
- Create: `CrabDesk.WinUI/Windows/AiOrganizationWindow.xaml.cs`
- Modify: `CrabDesk.WinUI/App.xaml.cs`
- Modify: `CrabDesk.WinUI/Views/AiClassificationPage.xaml.cs`
- Modify: `CrabDesk.WinUI/ViewModels/AiClassificationViewModel.cs`

- [x] **Step 1: Add a window-scoped workbench host**

Create the window surface:

```xml
<Window x:Class="CrabDesk.WinUI.Windows.AiOrganizationWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid x:Name="RootGrid">
        <ContentControl x:Name="WorkbenchHost" />
        <InfoBar x:Name="NotificationBar"
                 Width="420"
                 Margin="0,16,24,0"
                 HorizontalAlignment="Right"
                 VerticalAlignment="Top"
                 IsOpen="False"
                 IsClosable="True" />
    </Grid>
</Window>
```

The code-behind creates local `DialogService` and `InfoBarService` instances, constructs `AiClassificationViewModel` from application services, assigns `new AiClassificationPage(viewModel)` to `WorkbenchHost.Content`, registers the local dialog root on load, applies the current backdrop and theme, and opens at 1260×820 DIP centered in the monitor work area.

- [x] **Step 2: Make the page accept a window-scoped view model**

Add an internal constructor:

```csharp
internal AiClassificationPage(AiClassificationViewModel viewModel)
{
    InitializeComponent();
    DataContext = viewModel;
    ApiKeyBox.Password = viewModel.ApiKey;
    WebSearchApiKeyBox.Password = viewModel.WebSearchApiKey;
}
```

Keep the public parameterless constructor delegating to the DI-created view model for XAML tooling and tests.

- [x] **Step 3: Dispose transient workbench state on close**

Implement `IDisposable` on `AiClassificationViewModel`. Disposal unsubscribes `_service.Changed`, cancels and disposes the run and icon cancellation sources, stops the stream timer, and removes every item property-change handler. The window calls `Dispose` exactly once from `Closed`.

- [x] **Step 4: Own one AI window in App**

Add `_aiOrganizationWindow` and implement:

```csharp
internal void OpenAiOrganizationWorkbench()
{
    if (_aiOrganizationWindow is null)
    {
        _aiOrganizationWindow = new AiOrganizationWindow();
        _aiOrganizationWindow.Closed += (_, _) => _aiOrganizationWindow = null;
    }

    if (_aiOrganizationWindow.AppWindow.Presenter is OverlappedPresenter
        {
            State: OverlappedPresenterState.Minimized
        } presenter)
    {
        presenter.Restore();
    }

    _aiOrganizationWindow.AppWindow.Show();
    _aiOrganizationWindow.Activate();
}
```

Use this method for startup `--ai-organize`, the named event listener, and the settings-page entry. Close the AI window during application shutdown before disposing services.

### Task 4: Lay out the workbench for the dedicated window

**Files:**
- Modify: `CrabDesk.WinUI/Views/AiClassificationPage.xaml`
- Modify: `CrabDesk.WinUI/ViewModels/AiClassificationViewModel.cs`
- Modify: `CrabDesk.WinUI.Tests/AiClassificationPageLayoutTests.cs`
- Modify: `CrabDesk.WinUI.Tests/ViewModelTests.cs`

- [x] **Step 1: Use a height-safe two-row page**

The page root contains a compact header row and a `*` content row. The content row uses `*` and `360` columns. The left item canvas and right inspector each own a vertical scroll viewer; the root has no minimum content height.

- [x] **Step 2: Keep secondary content in the inspector**

Order the right inspector as `整理流程`, `AI 活动`, `分类设置`, and `模型与联网设置`. The last three sections are expanders. `AI 活动` contains the bounded run log, reasoning stream, and structured output; it starts collapsed and opens when `ResetActivity` begins a run.

- [x] **Step 3: Add activity-state tests**

Add these assertions to the existing view-model tests:

```csharp
Assert.False(viewModel.IsActivityPanelExpanded);
await viewModel.ClassifyCommand.ExecuteAsync(null);
Assert.True(viewModel.IsActivityPanelExpanded);
```

- [x] **Step 4: Run focused tests**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --no-restore --filter "FullyQualifiedName~AiOrganizationWindowTests|FullyQualifiedName~AiClassificationPageLayoutTests|FullyQualifiedName~AiClassificationViewModel"
```

Expected: all focused tests pass.

### Task 5: Verify and launch

**Files:**
- Verify: `CrabDesk.WinUI/App.xaml.cs`
- Verify: `CrabDesk.WinUI/Windows/AiOrganizationWindow.xaml.cs`
- Verify: `CrabDesk.WinUI/Views/OrganizationPage.xaml`
- Verify: `CrabDesk.WinUI/Views/AiClassificationPage.xaml`

- [x] **Step 1: Build the solution**

Run `dotnet build CrabDesk.sln --no-restore`.

Expected: build succeeds with no errors; existing MVVMTK AOT warnings may remain.

- [x] **Step 2: Run all tests**

Run `dotnet test CrabDesk.sln --no-build --no-restore`.

Expected: all core and WinUI tests pass.

- [x] **Step 3: Verify external activation and visual startup**

Run the built executable with `--ai-organize`, verify the dedicated window remains running, then invoke `--ai-organize` again and verify the existing process remains single-instance.

- [x] **Step 4: Check patch hygiene**

Run `git diff --check` and `rg -n "SmartOrganizationPage" CrabDesk.WinUI CrabDesk.WinUI.Tests`.

Expected: no whitespace errors and no production or test references to the selector wrapper.

Commits are intentionally omitted because the user has not authorized committing or pushing the current dirty workspace.
