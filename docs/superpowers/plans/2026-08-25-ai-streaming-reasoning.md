# AI Streaming Reasoning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show the provider's streamed reasoning and final structured JSON separately during AI organization, while preserving validation, cancellation, and confirmation-before-apply.

**Architecture:** Core emits typed stream events rather than ambiguous strings: `Reasoning` events are display-only and `Content` events remain the only data parsed into assignments. Runtime forwards the events unchanged; the dialog keeps independent, capped buffers so a verbose reasoning stream cannot hide or truncate the final JSON.

**Tech Stack:** .NET 8, `HttpClient` SSE parsing, WinUI 3 `ContentDialog`, xUnit, Moq.

---

### Task 1: Add a typed model-stream contract and test the SSE parser

**Files:**
- Modify: `CrabDesk.Core/Models.cs:299-308`
- Modify: `CrabDesk.Core/AiClassificationService.cs:88-376`
- Modify: `CrabDesk.Tests/AiClassificationServiceTests.cs:254-378`

- [ ] **Step 1: Write the failing tests for separated stream channels**

```csharp
[Fact]
public async Task StreamsReasoningAndStructuredContentThroughSeparateChannels()
{
    var updates = new List<AiClassificationModelStreamUpdate>();
    var sseBody = string.Concat(
        "data: ", JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { reasoning_content = "先分析文件名。" } } }
        }), "\n\n",
        "data: ", JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = "{\\\"items\\\":[{\\\"id\\\":\\\"0\\\",\\\"label\\\":\\\"工作\\\"}]}" } } }
        }), "\n\n",
        "data: [DONE]\n\n");

    using var client = new HttpClient(new StubHandler(_ => SseResponse(sseBody)));
    using var service = new AiClassificationService(client);

    var result = await service.ClassifyAsync(
        new AiClassificationSettings { BaseUrl = "https://models.example/v1", Model = "model-a" },
        [new AiClassificationInput("path:item", "文档")],
        ["工作"],
        modelStream: new RecordingProgress<AiClassificationModelStreamUpdate>(updates.Add));

    Assert.Single(result);
    Assert.Equal("工作", result[0].Label);
    Assert.Collection(
        updates,
        update => Assert.Equal(
            new AiClassificationModelStreamUpdate(AiClassificationModelStreamKind.Reasoning, "先分析文件名。"),
            update),
        update => Assert.Equal(
            new AiClassificationModelStreamUpdate(
                AiClassificationModelStreamKind.Content,
                "{\\\"items\\\":[{\\\"id\\\":\\\"0\\\",\\\"label\\\":\\\"工作\\\"}]}"),
            update));
}
```

- [ ] **Step 2: Run the new test and verify the current string-only callback cannot satisfy it**

Run: `dotnet test CrabDesk.Tests\\CrabDesk.Tests.csproj --no-restore --filter "FullyQualifiedName~StreamsReasoningAndStructuredContentThroughSeparateChannels"`

Expected: FAIL because `AiClassificationModelStreamUpdate` and the typed callback do not exist.

- [ ] **Step 3: Define the stream event and emit it at the parser boundary**

```csharp
public enum AiClassificationModelStreamKind
{
    Reasoning,
    Content
}

public sealed record AiClassificationModelStreamUpdate(
    AiClassificationModelStreamKind Kind,
    string Text);
```

Change `ClassifyAsync`, `RequestClassificationContentAsync`, and `ReadClassificationContentAsync` to receive `IProgress<AiClassificationModelStreamUpdate>?`. Parse `delta.reasoning_content` (and the OpenAI-compatible `delta.reasoning` alias) into `Reasoning` updates; parse `delta.content` into `Content` updates and the existing JSON buffer. Treat missing or `null` fields as empty events, but retain the existing strict validation for non-text values and error SSE payloads.

- [ ] **Step 4: Run Core tests**

Run: `dotnet test CrabDesk.Tests\\CrabDesk.Tests.csproj --no-restore`

Expected: all tests pass, including the prior `delta.content: null` regression test.

### Task 2: Preserve typed events through Runtime and WinUI service boundaries

**Files:**
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs:2252-2379`
- Modify: `CrabDesk.WinUI/Services/ICrabDeskService.cs:78-82`
- Modify: `CrabDesk.WinUI/Services/CrabDeskService.cs:87-92`
- Modify: `CrabDesk.WinUI/Services/AiOrganizationDialogContracts.cs:5-8`
- Modify: `CrabDesk.WinUI/ViewModels/AiClassificationViewModel.cs:183-206`
- Modify: `CrabDesk.WinUI.Tests/ViewModelTests.cs:411-456`

- [ ] **Step 1: Update the ViewModel dialog-session test first**

Replace the `IProgress<string>` mock matcher with `IProgress<AiClassificationModelStreamUpdate>`, invoke the captured dialog request using `new Progress<AiClassificationModelStreamUpdate>()`, and retain the assertion that the dialog request reaches `PreviewAiClassificationAsync` exactly once.

- [ ] **Step 2: Run the focused ViewModel test and verify the signature mismatch fails**

Run: `dotnet test CrabDesk.WinUI.Tests\\CrabDesk.WinUI.Tests.csproj --no-restore --filter "FullyQualifiedName~AiClassificationViewModelRunsOrganizationInsideDialogSession"`

Expected: FAIL until all delegate and service signatures use the typed stream update.

- [ ] **Step 3: Thread the exact typed progress object end-to-end**

Use this method shape at each boundary:

```csharp
Task<AiClassificationPreview> PreviewAiClassificationAsync(
    IProgress<AiClassificationProgress>? progress = null,
    CancellationToken cancellationToken = default,
    IProgress<AiClassificationModelStreamUpdate>? modelStream = null);
```

Make `AiOrganizationDialogRequest.PreviewAsync` accept `IProgress<AiClassificationModelStreamUpdate>` as its second argument. Runtime passes `modelStream` to `AiClassificationService.ClassifyAsync`; `CrabDeskService` forwards it; and `AiClassificationViewModel` forwards the dialog-provided stream reporter. Do not change the assignment validation or preview/apply gate.

- [ ] **Step 4: Run the focused ViewModel test again**

Run: `dotnet test CrabDesk.WinUI.Tests\\CrabDesk.WinUI.Tests.csproj --no-restore --filter "FullyQualifiedName~AiClassificationViewModelRunsOrganizationInsideDialogSession"`

Expected: PASS.

### Task 3: Render reasoning and JSON independently in the progress dialog

**Files:**
- Modify: `CrabDesk.WinUI/Services/AiOrganizationProgressDialog.cs:10-360`

- [ ] **Step 1: Replace the single raw-output buffer with two bounded buffers**

```csharp
private const int MaxVisibleReasoningLength = 48 * 1024;
private const int MaxVisibleStructuredOutputLength = 128 * 1024;
private readonly StringBuilder _reasoningOutput = new();
private readonly StringBuilder _structuredOutput = new();
```

Create two read-only, vertically scrollable text boxes: `模型推理（流式）` and `模型输出（结构化 JSON）`. The reasoning box begins with `正在等待模型开始推理…`; the JSON box begins with `正在等待结构化结果…`.

- [ ] **Step 2: Route stream events without allowing reasoning to replace JSON**

```csharp
private void AppendModelStream(AiClassificationModelStreamUpdate update) =>
    AppendToStreamBuffer(
        update.Kind == AiClassificationModelStreamKind.Reasoning ? _reasoningOutput : _structuredOutput,
        update.Kind == AiClassificationModelStreamKind.Reasoning
            ? MaxVisibleReasoningLength
            : MaxVisibleStructuredOutputLength,
        update.Text);
```

Batch UI updates through the existing `DispatcherQueue` mechanism. When the first reasoning event arrives, set status to `AI 正在推理分类结果…`; when the first content event arrives, set status to `AI 正在生成结构化分类结果…`. Never alter cancellation behavior, confirmation availability, or the result parser.

- [ ] **Step 3: Build the WinUI project**

Run: `dotnet build CrabDesk.WinUI\\CrabDesk.WinUI.csproj --no-restore`

Expected: `0 个错误`.

### Task 4: Run the complete verification suite and manually validate the live provider

**Files:**
- Modify only if tests expose a defect: files from Tasks 1-3.

- [ ] **Step 1: Run both test projects**

Run: `dotnet test CrabDesk.Tests\\CrabDesk.Tests.csproj --no-restore`

Expected: all Core tests pass.

Run: `dotnet test CrabDesk.WinUI.Tests\\CrabDesk.WinUI.Tests.csproj --no-restore`

Expected: all WinUI tests pass.

- [ ] **Step 2: Run formatting and build checks**

Run: `git diff --check`

Expected: no whitespace errors.

Run: `dotnet build CrabDesk.WinUI\\CrabDesk.WinUI.csproj --no-restore`

Expected: `0 个警告` and `0 个错误` when no executable is holding the output DLLs.

- [ ] **Step 3: Manually validate the configured SiliconFlow profile**

Start `CrabDesk.WinUI.exe`, begin AI organization, and confirm the reasoning box starts receiving text before the structured JSON box. Confirm the final JSON remains visible, `确认应用` stays disabled until a valid preview is created, and `终止` still cancels the request without applying layout changes.
