# Complete Lucide Icon Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace every CrabDesk-owned operation and status icon with glyphs from the bundled Lucide font while preserving shell file icons, CrabDesk branding, and framework-owned control chrome.

**Architecture:** Keep the generated WinUI `LucideIcon` control and font map for XAML. Add a Runtime-only renderer backed by the same `Assets/lucide.ttf`; use it for GDI header/menu rendering and WPF desktop dialogs so Runtime does not depend on WinUI. Mark ToolStrip items with typed Lucide metadata and let the themed renderer draw them at the current color and DPI.

**Tech Stack:** .NET 8, WinUI 3 XAML, WinForms ToolStrip rendering, WPF `ElementHost`, `System.Drawing.Text.PrivateFontCollection`, xUnit.

---

### Task 1: Runtime Lucide glyph service

**Files:**
- Create: `CrabDesk.Runtime/LucideRuntimeIcons.cs`
- Modify: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`

- [ ] **Step 1: Add a failing glyph-contract test**

```csharp
[Theory]
[InlineData(LucideRuntimeIcon.Menu, "\uE115")]
[InlineData(LucideRuntimeIcon.Expand, "\uE21A")]
[InlineData(LucideRuntimeIcon.Check, "\uE06C")]
[InlineData(LucideRuntimeIcon.ChevronRight, "\uE06F")]
[InlineData(LucideRuntimeIcon.TriangleAlert, "\uE193")]
[InlineData(LucideRuntimeIcon.CircleAlert, "\uE077")]
public void RuntimeLucideIconsExposeExpectedFontGlyphs(LucideRuntimeIcon icon, string expected)
{
    Assert.Equal(expected, LucideRuntimeIcons.GetGlyph(icon));
}
```

- [ ] **Step 2: Run the targeted test and verify it fails**

```powershell
dotnet test CrabDesk.WinUI.Tests\CrabDesk.WinUI.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~RuntimeLucideIconsExposeExpectedFontGlyphs"
```

Expected: compilation fails because the Runtime Lucide types do not exist.

- [ ] **Step 3: Implement the Runtime glyph service**

Create an internal enum containing the exact icons used by Runtime UI and this service contract:

```csharp
internal static class LucideRuntimeIcons
{
    internal static string GetGlyph(LucideRuntimeIcon icon);
    internal static void Draw(Graphics graphics, LucideRuntimeIcon icon, RectangleF bounds, Color color, float emSize);
    internal static void SetMenuIcon(ToolStripMenuItem item, LucideRuntimeIcon icon);
    internal static bool TryGetMenuIcon(ToolStripItem item, out LucideRuntimeIcon icon);
    internal static System.Windows.Media.FontFamily CreateWpfFontFamily();
}
```

Load `Assets/lucide.ttf` once with `PrivateFontCollection`, keep it alive for the process lifetime, center glyphs with `StringFormat.GenericTypographic`, and use anti-aliased text rendering. If the font cannot load, skip the glyph instead of falling back to a non-Lucide icon font.

- [ ] **Step 4: Run the targeted test and verify it passes**

Run the command from Step 2. Expected: every glyph row passes.

### Task 2: Runtime hand-drawn controls and menu chrome

**Files:**
- Modify: `CrabDesk.Runtime/DesktopBoxForm.Rendering.cs`
- Modify: `CrabDesk.Runtime/FluentMenuRenderer.cs`
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs`

- [ ] **Step 1: Replace hand-drawn header icons**

Replace the three-line hamburger implementation with `LucideRuntimeIcons.Draw(..., LucideRuntimeIcon.Menu, ...)`. Keep the existing active/hover background of the auto-expand button and replace its custom `GraphicsPath` with `LucideRuntimeIcon.Expand`.

- [ ] **Step 2: Replace menu checkmarks and submenu arrows**

Draw `LucideRuntimeIcon.Check` from `OnRenderItemCheck` and `LucideRuntimeIcon.ChevronRight` from `OnRenderArrow`. In `OnRenderItemText`, read typed icon metadata, draw the action glyph immediately before the text rectangle, and render text unchanged.

- [ ] **Step 3: Reserve the ToolStrip image column**

```csharp
menu.ShowImageMargin = true;
dropDownMenu.ShowImageMargin = true;
```

Keep the independent check margin so checked choices can display a Lucide check.

- [ ] **Step 4: Run Runtime interaction tests**

```powershell
dotnet test CrabDesk.WinUI.Tests\CrabDesk.WinUI.Tests.csproj -c Debug --no-restore
```

Expected: all tests pass.

### Task 3: All CrabDesk-owned menu commands

**Files:**
- Modify: `CrabDesk.Runtime/DesktopBoxForm.ContextMenus.cs`
- Modify: `CrabDesk.Runtime/CrabDeskRuntime.cs`

- [ ] **Step 1: Add and use a typed menu-item factory**

```csharp
private static ToolStripMenuItem CreateLucideMenuItem(
    string text,
    LucideRuntimeIcon icon,
    EventHandler? onClick = null)
{
    var item = new ToolStripMenuItem(text);
    LucideRuntimeIcons.SetMenuIcon(item, icon);
    if (onClick is not null) item.Click += onClick;
    return item;
}
```

- [ ] **Step 2: Mark every box-menu action**

Assign Lucide metadata to open-folder, paste, rename, display mode, sub-tabs, layer order, palette, view, sort, settings, delete, new-tab, rename-tab, delete-tab, and move-to-tab entries. Choice rows use the Lucide checkmark rather than redundant images.

- [ ] **Step 3: Mark every tray-menu action**

Assign `AppWindow`, `Sparkles`, `Plus`, `Pause`/`Play`, `RefreshCw`, `Power`, `SunMoon`, and `LogOut` to tray commands. Update the pause glyph together with its dynamic text.

- [ ] **Step 4: Run the menu test suite**

```powershell
dotnet test CrabDesk.WinUI.Tests\CrabDesk.WinUI.Tests.csproj -c Debug --no-restore
```

Expected: all tests pass and menu creation does not throw.

### Task 4: Runtime stock error and warning icons

**Files:**
- Modify: `CrabDesk.Runtime/DesktopConfirmationDialog.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.ContextMenus.cs`
- Modify: `CrabDesk.Runtime/DesktopBoxForm.DragDrop.cs`
- Modify: `CrabDesk.Runtime/DesktopIconSurface.cs`

- [ ] **Step 1: Generalize the owner-bound desktop dialog**

```csharp
internal enum DesktopDialogKind
{
    Confirmation,
    Warning,
    Error
}
```

Map confirmation to `Trash2`, warning to `TriangleAlert`, and error to `CircleAlert`. Render the badge through `LucideRuntimeIcons.CreateWpfFontFamily()` and `GetGlyph`. For message dialogs hide Cancel, label the primary button `确定`, focus it, and accept Enter.

- [ ] **Step 2: Add the message API**

```csharp
internal static void ShowMessage(
    Forms.IWin32Window owner,
    bool isDarkTheme,
    string title,
    string message,
    DesktopDialogKind kind)
```

- [ ] **Step 3: Replace every Runtime stock message icon**

Replace all `MessageBox.Show(... MessageBoxIcon.Error/Warning)` calls with `DesktopConfirmationDialog.ShowMessage(...)`, preserving the existing owner, title, message, and severity. Keep Explorer file/context-menu icons, CrabDesk branding, and OS tray-notification identity unchanged.

- [ ] **Step 4: Verify no owned legacy references remain**

```powershell
rg -n -i "FluentIcons|FluentIcon|SymbolIcon|Segoe MDL2 Assets|MessageBoxIcon\.(Error|Warning)" CrabDesk.Runtime CrabDesk.WinUI -g "*.cs" -g "*.xaml" -g "*.csproj" -g "!CrabDesk.WinUI/Controls/LucideGlyphs.cs" -g "!**/bin/**" -g "!**/obj/**"
```

Expected: no matches.

### Task 5: WinUI coverage and full validation

**Files:**
- Modify: `CrabDesk.WinUI.Tests/DesktopIconInteractionTests.cs`

- [ ] **Step 1: Add WinUI glyph coverage**

Add theory rows for every XAML glyph: `House`, `LayoutGrid`, `Sparkles`, `Archive`, `Keyboard`, `Info`, `Download`, `ExternalLink`, `RefreshCw`, `FolderOpen`, `Plus`, `Upload`, `RotateCcw`, `Trash2`, `FolderGit2`, `Pencil`, `Copy`, `ArrowUp`, `ArrowDown`, `Eye`, `ArrowRight`, and `X`. Assert `LucideGlyphs.ToGlyph(icon)` is non-empty.

- [ ] **Step 2: Run all verification commands**

```powershell
dotnet test CrabDesk.Tests\CrabDesk.Tests.csproj -c Debug --no-restore
dotnet test CrabDesk.WinUI.Tests\CrabDesk.WinUI.Tests.csproj -c Debug --no-restore
dotnet build CrabDesk.sln -c Debug --no-restore
git diff --check
```

Expected: both suites pass, the solution builds with zero errors, and the diff check has no whitespace errors.

- [ ] **Step 3: Exercise affected surfaces**

Launch Debug with `--show-settings`; navigate through boxes, smart organization, backup, and about. Open a box menu and tray menu, inspect the header menu/expand buttons, and open one safe warning/error dialog path. Confirm Lucide glyph rendering and text alignment at current DPI, retain the menu owner/tool-window behavior, and verify no new errors in `winui.log` or `crabdesk.log`.
