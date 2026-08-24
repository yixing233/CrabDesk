# AI Classification Progress Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show safe, batch-level progress while AI classification builds a preview, without exposing model reasoning or response text.

**Architecture:** The runtime owns truthful progress because it knows the candidate count and batch boundaries. It reports a small value object through `IProgress<T>` while preserving the existing operation gate and cancellation behavior. The WinUI view model converts these reports into bindable text and a determinate progress bar; the UI never displays filenames, prompt text, or model chain-of-thought.

**Tech Stack:** .NET 8, C#, `IProgress<T>`, CommunityToolkit.Mvvm, WinUI 3 XAML, xUnit.

---

### Task 1: Add a safe progress contract

**Files:**
- Modify: `CrabDesk.Core/Models.cs`
- Test: `CrabDesk.Tests/AiClassificationProgressTests.cs`

- [x] **Step 1: Write the failing test**

```csharp
[Fact]
public void ClassificationProgressCarriesOnlyAggregateBatchState()
{
    var progress = new AiClassificationProgress(25, 100, 1, 2, true, "正在请求 AI 分类");

    Assert.Equal(25, progress.CompletedItems);
    Assert.Equal(100, progress.TotalItems);
    Assert.Equal(1, progress.CompletedBatches);
    Assert.Equal(2, progress.TotalBatches);
    Assert.True(progress.IsIndeterminate);
    Assert.DoesNotContain("文件", progress.Message, StringComparison.OrdinalIgnoreCase);
}
```

- [x] **Step 2: Run the test to verify it fails**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AiClassificationProgressTests`

Expected: FAIL because `AiClassificationProgress` does not exist.

- [x] **Step 3: Add the immutable transport type**

```csharp
public sealed record AiClassificationProgress(
    int CompletedItems,
    int TotalItems,
    int CompletedBatches,
    int TotalBatches,
    bool IsIndeterminate,
    string Message);
```

- [x] **Step 4: Run the focused test**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AiClassificationProgressTests`

Expected: PASS.

### Task 2: Report preview progress from the runtime and facade

**Files:**
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs`
- Modify: `CrabDesk.WinUI/Services/ICrabDeskService.cs`
- Modify: `CrabDesk.WinUI/Services/CrabDeskService.cs`

- [x] **Step 1: Extend the preview method signatures**

```csharp
public Task<AiClassificationPreview> PreviewAiClassificationAsync(
    IProgress<AiClassificationProgress>? progress = null,
    CancellationToken cancellationToken = default);
```

Propagate the optional `progress` argument unchanged through the WinUI service facade.

- [x] **Step 2: Report aggregate-only stages in the runtime**

```csharp
var totalBatches = (int)Math.Ceiling(candidates.Length / (double)AiClassificationService.MaxItemsPerRequest);
progress?.Report(new AiClassificationProgress(0, candidates.Length, 0, totalBatches, true, "正在准备分类项目"));
foreach (var batch in candidates.Chunk(AiClassificationService.MaxItemsPerRequest))
{
    progress?.Report(new AiClassificationProgress(completedItems, candidates.Length, completedBatches, totalBatches, true, "正在请求 AI 分类"));
    var result = await _aiClassificationService.ClassifyAsync(settings, batch, labels, operationToken);
    classifications.AddRange(result);
    completedItems += batch.Length;
    completedBatches++;
    progress?.Report(new AiClassificationProgress(completedItems, candidates.Length, completedBatches, totalBatches, false, "已完成一批分类"));
}
progress?.Report(new AiClassificationProgress(candidates.Length, candidates.Length, totalBatches, totalBatches, true, "正在生成整理预览"));
```

Do not report filenames, prompt content, API responses, hidden reasoning, or error details.

- [x] **Step 3: Build the affected projects**

Run: `dotnet build CrabDesk.sln -c Release --no-restore`

Expected: success with no errors.

### Task 3: Bind the progress surface in the AI settings page

**Files:**
- Modify: `CrabDesk.WinUI/ViewModels/AiClassificationViewModel.cs`
- Modify: `CrabDesk.WinUI/Views/AiClassificationPage.xaml`

- [x] **Step 1: Add view-model state and update it from the `Progress<T>` callback**

```csharp
[ObservableProperty] private int _progressCompleted;
[ObservableProperty] private int _progressTotal;
[ObservableProperty] private string _progressText = string.Empty;
[ObservableProperty] private bool _isProgressIndeterminate;
[ObservableProperty] private bool _isClassificationInProgress;

var progress = new Progress<AiClassificationProgress>(value =>
{
    ProgressCompleted = value.CompletedItems;
    ProgressTotal = value.TotalItems;
    IsProgressIndeterminate = value.IsIndeterminate;
    ProgressText = value.TotalItems == 0
        ? value.Message
        : $"{value.Message}（{value.CompletedItems}/{value.TotalItems}，第 {value.CompletedBatches}/{value.TotalBatches} 批）";
});
var preview = await _service.PreviewAiClassificationAsync(progress, cancellation.Token);
```

Reset the values at operation start and preserve a concise final message during confirmation and apply. `IsClassificationInProgress` is separate from the shared `IsBusy` flag so model loading and connectivity checks never show an empty classification progress bar. The cancel command remains available while classification is running.

- [x] **Step 2: Render a determinate progress bar and status text only while classification is active**

```xml
<StackPanel Visibility="{Binding IsClassificationInProgress, Converter={StaticResource BooleanToVisibilityConverter}}" Spacing="6">
    <ProgressBar Minimum="0" Maximum="{Binding ProgressTotal}" Value="{Binding ProgressCompleted}"
                 IsIndeterminate="{Binding IsProgressIndeterminate}" />
    <TextBlock Text="{Binding ProgressText}" Style="{StaticResource SecondaryBodyStyle}" />
</StackPanel>
```

Place it in the existing AI action card above the buttons so it does not change the configuration form layout.

- [x] **Step 3: Build the WinUI project**

Run: `dotnet build CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Release --no-restore`

Expected: success with no errors.

### Task 4: Run regression tests and manually validate the interaction

**Files:**
- Test: `CrabDesk.Tests/AiClassificationProgressTests.cs`

- [x] **Step 1: Run the full automated suite**

Run: `dotnet test CrabDesk.sln -c Release --no-restore`

Expected: all Core and WinUI test projects pass.

- [x] **Step 2: Start the isolated Release build**

Run: `Start-Process 'CrabDesk.WinUI/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/CrabDesk.WinUI.exe' -ArgumentList '--show-settings'`

Expected: Settings opens; AI classification shows a progress bar for multi-batch requests, then a confirmation dialog; cancelling stops the request and leaves layout unchanged.
