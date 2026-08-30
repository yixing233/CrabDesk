# Box Scroll And List Rename Visuals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 消除盒子平滑滚动时的合成闪烁，并让列表模式的普通标签与重命名输入框保持相同的文字位置和对齐方式。

**Architecture:** 继续复用现有父级分层窗口的局部更新通道，把“纯滚动动画”重新纳入该通道，避免盒子像素在父层与子覆盖层之间交接。列表重命名继续复用 `DesktopRenameEditor`，但由编辑器根据单行模式选择左对齐，并把列表编辑窗口限制为单行标签高度后垂直居中。

**Tech Stack:** .NET 8、WinForms、GDI+、layered windows、xUnit

---

## File map

- Modify: `CrabDesk.Runtime/DesktopBoxForm.cs` — 暴露同时覆盖高度与纯滚动动画的局部合成资格。
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs` — 将图标表面的局部动画提供器接到新的盒子级资格。
- Modify: `CrabDesk.Runtime/DesktopRenameEditor.cs` — 统一单行列表编辑器的左对齐与单行高度计算。
- Modify: `CrabDesk.Runtime/DesktopBoxForm.ContextMenus.cs` — 使用单行高度并在列表行内垂直居中编辑器。
- Modify: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs` — 覆盖滚动合成资格和重命名样式策略。

### Task 1: 固定滚动合成回归

**Files:**
- Test: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.cs`
- Modify: `CrabDesk.Runtime/DesktopSurfaceManager.cs`

- [ ] **Step 1: 添加失败测试**

```csharp
[Theory]
[InlineData(false, false, false, false, false, false)]
[InlineData(true, false, false, false, false, true)]
[InlineData(false, false, false, true, false, true)]
[InlineData(false, true, false, true, false, false)]
[InlineData(false, false, true, true, false, false)]
[InlineData(false, false, false, true, true, false)]
public void PureScrollAndHeightAnimationsStayOnTheParentLayer(
    bool heightAnimationActive,
    bool transformActive,
    bool selectionActive,
    bool scrollAnimationActive,
    bool otherDynamicVisualActive,
    bool expected)
{
    Assert.Equal(
        expected,
        DesktopBoxForm.CanUsePartialBoxAnimationComposition(
            heightAnimationActive,
            transformActive,
            selectionActive,
            scrollAnimationActive,
            otherDynamicVisualActive));
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~PureScrollAndHeightAnimationsStayOnTheParentLayer"
```

Expected: FAIL，因为 `CanUsePartialBoxAnimationComposition` 尚不存在。

- [ ] **Step 3: 实现盒子级局部动画资格并更新接线**

```csharp
internal bool UsesPartialBoxAnimationComposition =>
    CanUsePartialBoxAnimationComposition(
        _heightAnimations.Count > 0,
        IsTransformActive,
        _selectionBox is not null,
        IsScrollAnimationActive,
        IsTransformActive || _dragStarted || _dropPreview is not null || _selectionBox is not null);

internal static bool CanUsePartialBoxAnimationComposition(
    bool heightAnimationActive,
    bool transformActive,
    bool selectionActive,
    bool scrollAnimationActive,
    bool otherDynamicVisualActive) =>
    CanUsePartialHeightAnimationComposition(
        heightAnimationActive,
        transformActive,
        selectionActive,
        scrollAnimationActive) ||
    IsPartialBoxAnimationOnly(
        heightAnimationActive,
        scrollAnimationActive,
        otherDynamicVisualActive);
```

在 `DesktopSurfaceManager.ConfigureBoxIconLayerComposition` 中将 `UsesPartialHeightAnimationComposition` 替换为 `UsesPartialBoxAnimationComposition`。

- [ ] **Step 4: 再次运行定向测试**

Expected: PASS；纯滚动进入父级局部更新通道，滚动叠加其它动态视觉时仍使用原有完整动态路径。

### Task 2: 统一列表标签和重命名布局

**Files:**
- Test: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`
- Modify: `CrabDesk.Runtime/DesktopRenameEditor.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.ContextMenus.cs`

- [ ] **Step 1: 添加失败测试**

```csharp
[Theory]
[InlineData(true, Forms.HorizontalAlignment.Center)]
[InlineData(false, Forms.HorizontalAlignment.Left)]
public void InlineRenameAlignmentMatchesWrappedAndListLabels(
    bool wordWrap,
    Forms.HorizontalAlignment expected)
{
    Assert.Equal(expected, DesktopRenameEditor.ResolveTextAlignment(wordWrap));
}

[Theory]
[InlineData(20, 88, 22)]
[InlineData(20, 18, 18)]
public void SingleLineRenameEditorUsesLabelHeight(
    float lineHeight,
    float rowHeight,
    float expected)
{
    Assert.Equal(
        expected,
        DesktopRenameEditor.CalculateSingleLineEditorHeight(lineHeight, rowHeight));
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~InlineRenameAlignmentMatchesWrappedAndListLabels|FullyQualifiedName~SingleLineRenameEditorUsesLabelHeight"
```

Expected: FAIL，因为两个策略方法尚不存在。

- [ ] **Step 3: 实现单行列表样式**

```csharp
internal static Forms.HorizontalAlignment ResolveTextAlignment(bool wordWrap) =>
    wordWrap
        ? Forms.HorizontalAlignment.Center
        : Forms.HorizontalAlignment.Left;

internal static float CalculateSingleLineEditorHeight(float lineHeight, float availableHeight) =>
    CalculateEditorHeight(lineHeight, lineHeight, availableHeight);
```

`ShowAsync` 使用 `ResolveTextAlignment(wordWrap)`；列表分支使用 `CalculateSingleLineEditorHeight(lineHeight, item.Bounds.Height)`，再沿行中心计算 `listTop`。

- [ ] **Step 4: 再次运行定向测试**

Expected: PASS；网格/桌面重命名继续居中，列表重命名左对齐且文字垂直居中。

### Task 3: 回归验证

**Files:**
- No additional code changes.

- [ ] **Step 1: 运行交互测试项目**

```powershell
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Debug --no-restore
```

Expected: PASS。

- [ ] **Step 2: 运行核心测试项目**

```powershell
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Debug --no-restore
```

Expected: PASS。

- [ ] **Step 3: 构建运行时项目**

```powershell
dotnet build CrabDesk.Runtime/CrabDesk.Runtime.csproj -c Debug --no-restore
```

Expected: 构建成功且没有新增警告或错误。

- [ ] **Step 4: 检查变更范围**

```powershell
git diff --check
git status --short
```

Expected: 只包含本计划、滚动合成、列表重命名和相应测试；不创建提交。
