# AI Usage Metrics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show one AI-organization run's elapsed time, time to first token, input tokens, output tokens, and total tokens in the AI activity panel.

**Architecture:** Parse optional OpenAI-compatible `usage` data without treating missing values as zero. `AiClassificationService` reports per-request telemetry; `CrabDeskRuntime` aggregates it over all classification batches; the WinUI view model owns wall-clock elapsed time and binds a compact metrics row inside the single AI conversation container.

**Tech Stack:** .NET 8, `System.Text.Json`, WinUI 3/XAML, xUnit.

---

### Task 1: Define and test optional provider usage telemetry

**Files:**
- Modify: `CrabDesk.Core/Models.cs:325-339`
- Modify: `CrabDesk.Core/AiClassificationService.cs:93-420`
- Test: `CrabDesk.Tests/AiClassificationServiceTests.cs:360-393`

- [ ] **Step 1: Write the failing service test**

```csharp
var usage = new List<AiClassificationRequestUsage>();
var result = await service.ClassifyAsync(settings, items, labels,
    usageProgress: new RecordingProgress<AiClassificationRequestUsage>(usage.Add));

Assert.Single(result);
Assert.Single(usage);
Assert.Equal(12, usage[0].InputTokens);
Assert.Equal(8, usage[0].OutputTokens);
Assert.Equal(20, usage[0].TotalTokens);
Assert.NotNull(usage[0].FirstTokenLatency);
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --no-restore --filter FullyQualifiedName~ReportsStreamingUsage`

Expected: FAIL because `AiClassificationRequestUsage` and `usageProgress` do not exist.

- [ ] **Step 3: Add the telemetry contract and parse usage**

```csharp
public sealed record AiClassificationRequestUsage(
    TimeSpan Duration,
    TimeSpan? FirstTokenLatency,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens);
```

Return both response content and optional `usage` fields from `ReadClassificationContentAsync`. Read `prompt_tokens`, `completion_tokens`, and `total_tokens` from either a non-streaming response object or a final SSE event. Report `null` for unavailable token fields, never `0`.

- [ ] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --no-restore --filter FullyQualifiedName~ReportsStreamingUsage`

Expected: PASS.

### Task 2: Aggregate metrics across the complete organization run

**Files:**
- Modify: `CrabDesk.Core/Models.cs:325-339`
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs:2275-2527`
- Modify: `CrabDesk.WinUI/Services/ICrabDeskService.cs:75-86`
- Modify: `CrabDesk.WinUI/Services/CrabDeskService.cs:100-119`
- Test: `CrabDesk.Tests/CrabDeskRuntimeTests.cs`

- [ ] **Step 1: Write the failing aggregation test**

```csharp
Assert.Equal(30, update.InputTokens);
Assert.Equal(18, update.OutputTokens);
Assert.Equal(48, update.TotalTokens);
Assert.NotNull(update.FirstTokenLatency);
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --no-restore --filter FullyQualifiedName~AiUsage`

Expected: FAIL because runtime methods have no cumulative telemetry callback.

- [ ] **Step 3: Add cumulative telemetry progress**

```csharp
public sealed record AiClassificationUsageProgress(
    TimeSpan? FirstTokenLatency,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int CompletedRequests);
```

Pass a usage progress callback through `ICrabDeskService`. In `CrabDeskRuntime`, sum fields only while every completed provider response supplied that field; otherwise preserve `null`. Set first-token latency from the first successful model request only.

- [ ] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --no-restore --filter FullyQualifiedName~AiUsage`

Expected: PASS.

### Task 3: Render a bounded, live metrics row in the AI conversation

**Files:**
- Modify: `CrabDesk.WinUI/ViewModels/AiClassificationViewModel.cs:13-800`
- Modify: `CrabDesk.WinUI/Views/AiClassificationPage.xaml:244-290`
- Test: `CrabDesk.WinUI.Tests/ViewModelTests.cs`
- Test: `CrabDesk.WinUI.Tests/AiClassificationPageLayoutTests.cs`

- [ ] **Step 1: Write failing view-model and XAML tests**

```csharp
Assert.Equal("输入 12", viewModel.InputTokenText);
Assert.Equal("输出 8", viewModel.OutputTokenText);
Assert.Equal("总计 20", viewModel.TotalTokenText);
```

```csharp
Assert.Equal(5, metrics.Elements(Presentation + "StackPanel").Count());
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --no-restore --filter FullyQualifiedName~AiClassification`

Expected: FAIL because the metric text properties and metrics row do not exist.

- [ ] **Step 3: Implement UI state and bindings**

```csharp
public string TotalElapsedText => FormatDuration(_operationStopwatch.Elapsed);
public string FirstTokenText => FirstTokenLatency is { } value ? FormatDuration(value) : "—";
public string InputTokenText => InputTokens?.ToString("N0") ?? "—";
```

Start the stopwatch before requesting classification, update only at the existing 250 ms stream flush cadence, reset all fields on each run, and format unavailable provider data as `—`. Render five evenly sized label/value cells under the AI identity, before the transcript bubble.

- [ ] **Step 4: Run the focused tests and verify they pass**

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --no-restore --filter FullyQualifiedName~AiClassification`

Expected: PASS.

### Task 4: Run regression validation

**Files:**
- Modify: none
- Test: `CrabDesk.Tests/CrabDesk.Tests.csproj`
- Test: `CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj`

- [ ] **Step 1: Run the core test suite**

Run: `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --no-restore`

Expected: PASS.

- [ ] **Step 2: Run the WinUI test suite**

Run: `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --no-restore`

Expected: PASS with only existing MVVMTK0045 warnings.

## Self-review

- Spec coverage: total duration, first-token latency, input tokens, output tokens, and total tokens are covered by Tasks 1–3.
- Missing provider `usage` is explicitly represented as `—`, preventing false zero-cost reporting.
- The view model uses the existing 250 ms timer, so metrics do not reintroduce high-frequency UI work.
