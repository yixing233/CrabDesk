# AI Organization Workbench Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide a dedicated AI organization workbench window that lets users choose desktop items, inspect streamed AI activity, correct uncertain classifications, and explicitly apply the preview.

**Architecture:** The runtime exposes a revision-bound snapshot of eligible desktop items and accepts an explicit selection for preview generation. A dedicated WinUI window owns transient workbench state (selection, streamed output, result badges, and manual labels), while the runtime remains the authority that validates and atomically applies preview assignments. External entry points open or focus the single workbench window.

**Tech Stack:** .NET 8, WinUI 3, CommunityToolkit.Mvvm, CrabDesk.Core/Runtime models, existing streamed `AiClassificationModelStreamUpdate` pipeline.

---

### Task 1: Add revision-bound workbench snapshots and safe preview selection

**Files:**
- Modify: `CrabDesk.Core/Models.cs`
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs`
- Modify: `CrabDesk.WinUI/Services/ICrabDeskService.cs`
- Modify: `CrabDesk.WinUI/Services/CrabDeskService.cs`
- Test: `CrabDesk.Tests/AiClassificationWorkbenchTests.cs`

- [ ] **Step 1: Write failing core tests for selection and preview validation**

```csharp
[Fact]
public void SelectsOnlyKnownWorkspaceItemsInWorkspaceOrder()
{
    var workspace = new AiClassificationWorkspace(12,
        [new AiClassificationWorkspaceItem("one", "One", DesktopItemKind.File, "C:\\One.txt"),
         new AiClassificationWorkspaceItem("two", "Two", DesktopItemKind.Shortcut, "C:\\Two.lnk")]);

    var selected = AiClassificationWorkbench.Select(workspace, ["two", "missing", "two"]);

    Assert.Equal(["two"], selected.Select(item => item.ItemKey));
}

[Fact]
public void MergesManualLabelsWithoutOverridingAiAssignments()
{
    var preview = new AiClassificationPreview(4, 2,
        [new AiClassificationAssignment("one", "One", "工作")], [])
    { RequestedItemKeys = ["one", "two"] };

    var assignments = AiClassificationWorkbench.MergeManualAssignments(
        preview, new Dictionary<string, string> { ["two"] = "学习", ["one"] = "游戏" }, ["工作", "学习"]);

    Assert.Equal(["工作", "学习"], assignments.Select(item => item.Label));
}
```

- [ ] **Step 2: Run the focused test and verify it fails because the workbench types do not exist**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter AiClassificationWorkbenchTests --no-restore`

Expected: compile failure mentioning `AiClassificationWorkspace` or `AiClassificationWorkbench`.

- [ ] **Step 3: Add explicit workspace and preview contracts in Core**

```csharp
public sealed record AiClassificationWorkspace(
    long WorkspaceRevision,
    IReadOnlyList<AiClassificationWorkspaceItem> Items);

public sealed record AiClassificationWorkspaceItem(
    string ItemKey,
    string DisplayName,
    DesktopItemKind Kind,
    string ParsingName);

public sealed record AiClassificationPreview(/* existing positional members */)
{
    public IReadOnlyList<string> RequestedItemKeys { get; init; } = [];
}
```

Implement `AiClassificationWorkbench.Select` to de-duplicate requested keys and return only keys present in the supplied snapshot, in snapshot order. Implement `MergeManualAssignments` to retain first-pass AI results, accept manual labels only for requested-but-unassigned keys, and accept only labels from the supplied tag collection.

- [ ] **Step 4: Make the runtime own snapshot revision and selection validation**

```csharp
public AiClassificationWorkspace GetAiClassificationWorkspace();

public Task<AiClassificationPreview> PreviewAiClassificationAsync(
    long expectedWorkspaceRevision,
    IReadOnlyCollection<string> selectedItemKeys,
    IProgress<AiClassificationProgress>? progress = null,
    CancellationToken cancellationToken = default,
    IProgress<AiClassificationModelStreamUpdate>? modelStream = null);
```

`GetAiClassificationWorkspace` returns only current desktop items that are neither shell/system items nor already assigned to a box. The preview method must reject a mismatched revision before any AI or Tavily request, select only snapshot keys, and populate `RequestedItemKeys` on the resulting preview. Keep `ApplyAiClassificationPreviewAsync` revision validation and add an allow-list check: every assignment must target a requested, currently eligible key and a currently configured category label before state mutation.

- [ ] **Step 5: Run the focused tests**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter AiClassificationWorkbenchTests --no-restore`

Expected: PASS.

### Task 2: Move streamed progress into reusable page state

**Files:**
- Create: `CrabDesk.Core/AiClassificationStreamAccumulator.cs`
- Test: `CrabDesk.Tests/AiClassificationStreamAccumulatorTests.cs`
- Modify: `CrabDesk.WinUI/ViewModels/AiClassificationViewModel.cs`

- [ ] **Step 1: Write failing accumulator tests for throttled page rendering data**

```csharp
[Fact]
public void SeparatesReasoningAndStructuredOutputAndTruncatesBoth()
{
    var accumulator = new AiClassificationStreamAccumulator(4, 5);
    accumulator.Append(new(AiClassificationModelStreamKind.Reasoning, "abcdef"));
    accumulator.Append(new(AiClassificationModelStreamKind.Content, "123456"));

    var snapshot = accumulator.Flush();

    Assert.Contains("…", snapshot.Reasoning);
    Assert.Contains("…", snapshot.StructuredOutput);
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter AiClassificationStreamAccumulatorTests --no-restore`

Expected: compile failure because `AiClassificationStreamAccumulator` is absent.

- [ ] **Step 3: Implement bounded, thread-safe stream accumulation**

```csharp
public sealed class AiClassificationStreamAccumulator
{
    public void Append(AiClassificationModelStreamUpdate update);
    public AiClassificationStreamSnapshot Flush();
    public void Clear();
}

public sealed record AiClassificationStreamSnapshot(string Reasoning, string StructuredOutput);
```

Use a private lock and separate `StringBuilder` instances. Never update WinUI controls from the model callback. In the view model, flush this accumulator at most every 100 ms with a `DispatcherQueueTimer`; flush once synchronously when preview generation ends. Bind `ReasoningOutput`, `StructuredOutput`, bounded progress log entries, status text, and `IsReasoningPanelExpanded` to the page.

- [ ] **Step 4: Run the accumulator tests**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter AiClassificationStreamAccumulatorTests --no-restore`

Expected: PASS.

### Task 3: Build the AI workbench surface and interaction model

**Files:**
- Modify: `CrabDesk.WinUI/ViewModels/AiClassificationViewModel.cs`
- Create: `CrabDesk.WinUI/ViewModels/AiWorkbenchItemViewModel.cs`
- Create: `CrabDesk.WinUI/Services/DesktopItemIconSourceFactory.cs`
- Modify: `CrabDesk.WinUI/Views/AiClassificationPage.xaml`
- Modify: `CrabDesk.WinUI/Views/AiClassificationPage.xaml.cs`
- Test: `CrabDesk.WinUI.Tests/ViewModelTests.cs`

- [ ] **Step 1: Replace dialog-session view-model tests with workbench tests**

```csharp
[Fact]
public async Task RunsPreviewForOnlySelectedWorkspaceItems()
{
    service.Setup(item => item.GetAiClassificationWorkspace()).Returns(new AiClassificationWorkspace(
        7, [new("one", "One", DesktopItemKind.File, "C:\\One.txt"), new("two", "Two", DesktopItemKind.File, "C:\\Two.txt")]));
    viewModel.WorkspaceItems[1].IsSelected = false;

    await viewModel.ClassifyCommand.ExecuteAsync(null);

    service.Verify(item => item.PreviewAiClassificationAsync(7, It.Is<IReadOnlyCollection<string>>(keys => keys.SequenceEqual(["one"])), It.IsAny<IProgress<AiClassificationProgress>>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<AiClassificationModelStreamUpdate>>()), Times.Once);
}

[Fact]
public async Task AppliesManualLabelOnlyToAnUncertainSelectedItem()
{
    // Return a preview with one AI label and one unassigned key, choose a tag for the latter, then verify the composed preview.
}
```

- [ ] **Step 2: Run the focused WinUI tests and verify they fail with the old dialog flow**

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --filter AiClassificationViewModel --no-restore`

Expected: failure because `ClassifyAsync` still invokes `RunAiOrganizationAsync`.

- [ ] **Step 3: Implement item cards, selection, result badges, and manual labels**

```csharp
public partial class AiWorkbenchItemViewModel : ObservableObject
{
    public required string ItemKey { get; init; }
    public required string DisplayName { get; init; }
    public required ImageSource Icon { get; init; }
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string? _aiLabel;
    [ObservableProperty] private string? _manualLabel;
    public bool IsUncertain => AiLabel is null && HasPreview;
    public string? EffectiveLabel => AiLabel ?? ManualLabel;
}
```

Load all workspace cards selected by default. `ClassifyCommand` saves settings, clears previous preview state, creates a per-run cancellation source, and calls the new explicit-selection preview API. On completion, map AI assignments to cards; cards without an assignment become `IsUncertain`, retain no label, and expose a tag picker. `ApplyCommand` merges manual labels through `AiClassificationWorkbench.MergeManualAssignments`, applies the composed preview, and refreshes the workspace only after a successful mutation. `CancelCommand` cancels both the local token and runtime operation. `SelectAll`, `ClearSelection`, and `RefreshWorkspace` must not alter an in-flight request.

Implement `DesktopItemIconSourceFactory` with `ShellIconProvider`: encode a copy of the native icon as PNG into an `InMemoryRandomAccessStream`, then create a `BitmapImage`. Load icons asynchronously with a bounded concurrency of four and preserve a neutral file icon fallback, so opening the page does not block the UI thread.

- [ ] **Step 4: Build the workbench layout for its dedicated window**

Build the page as a complete workspace, not a dialog replica:

```xml
<Grid RowSpacing="12">
  <Grid.RowDefinitions>
    <RowDefinition Height="Auto" />
    <RowDefinition Height="*" />
    <RowDefinition Height="Auto" />
  </Grid.RowDefinitions>
  <!-- toolbar: selected count, refresh, select all/none, classify/cancel/apply -->
  <!-- GridView: desktop icon cards, checkboxes, AI label badges, uncertain tag ComboBox -->
  <!-- Expander: streamed reasoning, structured JSON and bounded activity log -->
</Grid>
```

Keep endpoint, model, tags, prompt and Tavily settings inside a closed-by-default `Expander` titled “AI 设置与分类规则”. The results area is always visible. Use real 48 px desktop icons, a 136 px card width, text trimming, explicit selected visual state, green AI-label badges, and amber “待确认” badges. The confirmation button remains disabled until a successful preview provides at least one effective assignment.

- [ ] **Step 5: Run the focused view-model tests**

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --filter AiClassificationViewModel --no-restore`

Expected: PASS.

### Task 4: Remove dialog-only entry points and preserve external activation

**Files:**
- Modify: `CrabDesk.WinUI/App.xaml.cs`
- Modify: `CrabDesk.WinUI/Services/IDialogService.cs`
- Delete: `CrabDesk.WinUI/Services/AiOrganizationDialogContracts.cs`
- Delete: `CrabDesk.WinUI/Services/AiOrganizationProgressDialog.cs`
- Modify: `CrabDesk.WinUI.Tests/ViewModelTests.cs`

- [ ] **Step 1: Write the failing application-entry test or inspection assertion**

Ensure no production call sites reference `RunAiOrganizationAsync`, and assert the view model no longer needs `IDialogService` to start AI organization. Keep `IDialogService` only for category tag input and other independent dialogs.

- [ ] **Step 2: Replace external AI-organize behavior with window activation**

```csharp
OpenAiOrganizationWorkbench();
```

Use this method for command-line activation and the named event listener. Remove `RunAiOrganizationAsync` from the dialog service contract and delete the two modal-only source files. Do not automatically run classification when the window opens.

- [ ] **Step 3: Verify no dialog-only symbols remain**

Run: `rg -n "RunAiOrganizationAsync|AiOrganizationProgressDialog|AiOrganizationDialog" CrabDesk.WinUI CrabDesk.WinUI.Tests`

Expected: no matches.

### Task 5: Full validation and interaction regression checks

**Files:**
- Modify: `docs/superpowers/plans/2026-08-25-ai-organization-workbench.md`

- [ ] **Step 1: Run all automated tests**

Run:

```powershell
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --no-restore
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --no-restore
```

Expected: all tests pass.

- [ ] **Step 2: Build the complete solution**

Run: `dotnet build CrabDesk.sln --no-restore`

Expected: 0 errors. Record any pre-existing WinRT AOT warnings separately from new warnings.

- [ ] **Step 3: Perform manual workbench smoke checks**

1. Open AI 整理: unassigned non-shell desktop items appear as actual icon cards and all are selected.
2. Clear one checkbox: only the remaining selected keys enter the preview call.
3. Start classification: the page remains interactive except the execution controls; progress and buffered model reasoning update without a modal dialog.
4. Inspect a returned label: it appears as an AI badge; inspect an omitted item: it is amber, unassigned, and offers only configured category tags.
5. Select a manual tag, then apply: only effective labels move into boxes; the item list refreshes after success.
6. Start again and cancel: no layout mutation occurs and controls recover.
7. Trigger AI organize from tray/context entry: Settings opens directly on the workbench without starting a request.

- [ ] **Step 4: Mark completed plan items and leave the current worktree intact**

Do not commit, push, merge, reset, or delete files unless the user explicitly requests it.

## Self-review

- The plan covers the requested grid, default selection, inline streamed activity, inline result marking, uncertain-item protection, manual label assignment, and removal of the modal workflow.
- Runtime selection and apply validation retain the existing revision, cancellation, global mutual-exclusion, JSON-schema, encrypted-key, and Tavily privacy protections.
- The workbench only exposes unassigned non-shell desktop items, matching the requested “desktop (non-box)” scope; it does not silently reclassify boxed items.
