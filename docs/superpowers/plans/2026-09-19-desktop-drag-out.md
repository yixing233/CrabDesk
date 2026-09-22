# 桌面文件无法拖出到其他程序 修复记录

日期：2026-09-19

## 现象

用户报告：CrabDesk 接管后的桌面上，文件**无法拖拽到其他程序**（资源管理器、浏览器、聊天窗口等）。盒子里项目的拖出不受影响，只有桌面上的图标拖不出去。

## 排查

日志里拖拽全部记成内部路径：

```
[P8764 T1] Desktop icon drag using non-OLE pointer path
[P8764 T1] Desktop icon drag settled partially monitor=\\.\DISPLAY19 dirtyRegions=2 dragged=1
```

`non-OLE pointer path` 只出现在 `DesktopIconSurface.OnMouseMove` 的拖动起始处。检查该函数发现：

```csharp
BeginDesktopDrag(_pressedItem.Key.ToString());
// Keep desktop-to-desktop movement on CrabDesk's own pointer state machine...
if (_dragStarted)
{
    DiagnosticLog.Info("Desktop icon drag using non-OLE pointer path");
}
```

`TryStartDesktopOleDrag()` 的调用不见了。再全仓库检索：该函数**只余定义（`DesktopIconSurface.cs:4623`），没有任何调用点**。

## 根因

`TryStartDesktopOleDrag()` 内部调用 `Forms.DoDragDrop`，这是把 `FileDrop` 负载发布给系统、让其他进程能接收的**唯一**途径。CrabDesk 自己的指针状态机只在进程内记录拖动与落点，从不向系统注册拖拽数据，因此：

- **桌面内部**拖动（桌面↔盒子、桌面内移动）照常工作——它只依赖自己的状态机；
- **拖出到外部程序**必然失败——目标进程从未收到 OLE 拖拽通知，连"禁止放置"光标都不会出现，表现为拖动毫无反应。

这与用户现象完全吻合：只有桌面图标拖不出去。

## 引入原因（自省）

这是上一个提交 `0bb9975` 引入的回归，且属于**误删**：

- 同一个提交为 `TryStartDesktopOleDrag` 内部**新增**了埋点 `UiThreadWatchdog.Enter("icon OLE DoDragDrop")`，却把它唯一的调用点删掉了。给一条路径加监视的同时禁用它，本身自相矛盾。
- 保留的注释理由是「External export remains handled by the explicit desktop drop forwarding path」，这个判断是错的：`DesktopDropForwarder` 处理的是外部**拖入**桌面（Explorer → CrabDesk），与拖**出**方向相反，不能互相替代。
- 改动动机有真实来源：`DoDragDrop` 会进 WinForms/OLE 模态循环，曾观察到阻塞。但按 `docs/superpowers/plans/2026-09-15-desktop-drop-and-watcher-fixes.md` 的第三版结论，那次卡顿的**真正根因**是 `Drop` 返回 `DROPEFFECT_MOVE` 却没做 Shell optimized-move 握手，真正的修法是新增 `ShellDropEffectProtocol.TryReportOptimizedMove`——**那项修复仍在**。删除 OLE 拖拽调用是当时多做的、有害的一步，不是卡顿的解药。

## 修法

在 `OnMouseMove` 的拖动起始处恢复原有交接：`BeginDesktopDrag` 之后调用 `TryStartDesktopOleDrag()`，成功则 `return` 交给系统拖拽循环。

桌面内移动不受影响：OLE 拖拽负载里带有 CrabDesk 私有的 `DesktopIconDragSession` 与 item-key 列表，图标层在 `Drop` 时读回同一 session 完成吸附与分配。`TryStartDesktopOleDrag` 里写 `Preferred DropEffect = Move` 的那段逻辑（使拖离桌面为移动而非跨卷复制）也重新生效。

## 验证

新增 `DesktopItemReleaseRefreshTests.StartingADesktopDragHandsOffToTheOleDragLoop`：断言 `OnMouseMove` 体内同时出现 `BeginDesktopDrag(` 与 `TryStartDesktopOleDrag()`，且 `TryStartDesktopOleDrag` 定义与 `WritePreferredDropEffect(data, move: true)` 依然存在。

**回归网之所以漏掉**：既有测试 `ExplorerMoveOutOfDesktopReconcilesOnlyTheMovedItems` 只断言 `TryStartDesktopOleDrag` **函数体内部**的内容，不检查它是否被调用，所以函数被"孤儿化"后测试照常通过。新测试补上了"必须被调用"这一层。

先经失败确认复现：还原旧代码后该测试报 `Assert.Contains() Failure: Sub-string not found`；改回修复版转绿。

全量测试：`CrabDesk.Tests` 400、`CrabDesk.WinUI.Tests` 650（原 649 + 新增 1）、`CrabDesk.Bootstrapper.Tests` 46，全部通过。Release 构建 0 错误；核对构建产物已不含 `non-OLE pointer path` 字符串，且 `icon OLE DoDragDrop` 埋点仍在（正对照）。

## 遗留

`DoDragDrop` 的模态循环开销是真实存在的，本次恢复的是既有行为、未做优化。若日后再次观察到拖拽后 UI 阻塞，应按 `2026-09-15` 文档的方法定位到具体阻塞点（当时是 Shell 握手与 `SetForegroundWindow`），而不是再次牺牲拖出能力。
