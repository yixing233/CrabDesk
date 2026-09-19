# 流式分类对话流 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 将 AI 分类过程呈现为连贯的逐项智能体活动流，例如“正在分析 Cockpit Tools”→“归类为专业工具”，同时保留现有模型思考、工具调用和 JSON 结果。

**Architecture:** 在 Core/Runtime 增加轻量的分类活动事件，Runtime 在批次输入和解析出 assignments 后报告事件；WinUI 将事件追加到当前 assistant 消息的活动列表。活动列表是可观察集合，完成项冻结，当前项显示 ProgressRing，避免把模型原始 JSON 当作用户可读进度。

**Tech Stack:** .NET 8, C#, CommunityToolkit.Mvvm, WinUI 3 XAML, xUnit.

---

### Task 1: 定义可测试的分类活动事件

**Files:**
- Modify: `CrabDesk.Core/Models.cs`
- Create: `CrabDesk.Core/AiClassificationActivity.cs`
- Create: `CrabDesk.Tests/AiClassificationActivityTests.cs`

- [ ] 添加 `AiClassificationActivityPhase`（Analyzing、Classified、Uncertain、Failed）和不可变 `AiClassificationActivity(ItemKey, ItemName, Phase, Label)`；Label 仅在 Classified 时使用。
- [ ] 添加纯函数/工厂方法，将项目名和 assignment 映射成对应活动文本，文本固定为 `正在分析 {name}`、`归类为 {label}`、`暂无法确定分类`，不得暴露模型内部推理。
- [ ] 编写 xUnit 覆盖四个 phase 的中文显示文本及空标签回退。
- [ ] 运行 `dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj --filter AiClassificationActivityTests`，确认通过。

### Task 2: 从 Runtime 报告逐项目活动

**Files:**
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs`（PreviewAiClassificationAsync 参数、每批处理循环）
- Modify: `CrabDesk.Core/Models.cs`
- Modify: `CrabDesk.WinUI/ViewModels/AiClassificationViewModel.cs`

- [ ] 在分类预览 API 增加可选 `IProgress<AiClassificationActivity>`，沿调用链透传，保持现有调用者兼容。
- [ ] 每个 batch 请求前按输入顺序报告 Analyzing；请求完成后建立 assignment lookup，逐项报告 Classified 或 Uncertain；联网补充分类完成后再次报告最终状态，保证同一 ItemKey 的后续事件覆盖而不是重复堆积。
- [ ] ViewModel 建立当前运行的 item-key 到活动行映射；收到 Analyzing 时插入活动行，收到结果时更新同一行，更新 Status 为当前活动文本并保留批次进度。
- [ ] 运行现有 Core/Runtime/WinUI 测试，修正所有因新增可选参数导致的编译错误。

### Task 3: 在 assistant 气泡中渲染连贯活动流

**Files:**
- Modify: `CrabDesk.WinUI/ViewModels/AiConversationMessageViewModel.cs`
- Modify: `CrabDesk.WinUI/Views/AiClassificationPage.xaml`
- Modify: `CrabDesk.WinUI.Tests/AiClassificationPageLayoutTests.cs`

- [ ] 添加 `AiClassificationActivityViewModel`（ItemName、DisplayText、Phase、IsRunning、IsCompleted、IsUncertain），并让 `AiConversationMessageViewModel` 暴露 `ObservableCollection<AiClassificationActivityViewModel> ClassificationActivities` 与 `HasClassificationActivities`。
- [ ] 在 assistant 消息正文之后、Tool calls 之前添加活动 ItemsControl：左侧 Sparkles/Check/CircleAlert 图标，文本绑定 DisplayText，活动项之间用细分隔线；运行项显示 ProgressRing，完成项不闪烁。
- [ ] 将活动区限制为最近可视范围（运行中优先、已完成保留最近 12 项），防止 62 项批次撑爆气泡；JSON、思考过程仍使用现有 Expander。
- [ ] 更新布局测试，断言 ItemsSource、活动文本绑定和最近项容量相关结构存在；补一个 ViewModel 测试验证同一 ItemKey 从 analyzing 更新为 classified 而不是新增重复行。
- [ ] 运行 `dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj --filter AiClassificationPageLayoutTests`。

### Task 4: 完整验证与构建

**Files:**
- No new files.

- [ ] 运行 `dotnet test CrabDesk.sln --configuration Debug --no-restore`。
- [ ] 运行 `dotnet build CrabDesk.WinUI/CrabDesk.WinUI.csproj --configuration Debug --no-restore`。
- [ ] 手动启动现有 CrabDesk GUI，执行一轮分类，确认活动顺序连续、当前项滚动可见、完成后仍可展开思考和 JSON。
