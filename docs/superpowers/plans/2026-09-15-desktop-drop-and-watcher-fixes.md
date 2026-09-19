# 桌面外部拖放失效与保存后不刷新 修复记录

日期：2026-09-15

## 现象

1. 从资源管理器或其他程序往桌面空白处拖文件，光标显示禁止号，无法放下；拖进盒子却可以。
2. 保存文件到桌面后图标不出现，要点托盘的「重新接管」才刷出来。

## 根因

### 1. 亚克力宿主截住了跨进程的 OLE 命中测试

`UseAcrylicBoxes` 打开后（2026-09-11 引入），`DesktopAcrylicHost` 是一个盖住全部工作区、紧贴桌面根窗口之上的**顶层**窗口，只靠 `WM_NCHITTEST` 返回 `HTTRANSPARENT` 把输入让给下面的图标层。这对鼠标点击和 CrabDesk 自己发起的拖拽都有效，因为 `HTTRANSPARENT` 只在同一线程的窗口之间传递。资源管理器发起的 OLE 拖拽在它自己的进程里跑 `WindowFromPoint`，根本不会问我们的 `WM_NCHITTEST`，于是命中宿主本身；宿主没有注册放置目标，又是顶层没有父窗口可回溯，OLE 判定无目标。盒子窗口是宿主的子窗口且各自注册了放置目标，所以拖进盒子正常。`DesktopDragOverlay` 早就为同一问题留了注释（"HTTRANSPARENT alone cannot"）。日志印证：当前日志自 09-06 起没有一条「External drag entered」，而内部拖放（`Surface drag drop … VirtualMove`）照常。

### 2. 桌面目录监听的三个盲区

在桌面上做了三组探针（隐藏创建→取消隐藏、独占锁定写入→关闭、普通创建/删除）：

- 文件先以隐藏属性创建、写完再取消隐藏：创建时刷新一次（此时被当作隐藏文件过滤掉），**取消隐藏没有任何事件**——`NotifyFilter` 不含 `Attributes`，于是永远不出现，直到手动刷新。
- 默认 8 KB 的 `FileSystemWatcher` 缓冲区在一小串变更后就会溢出，溢出时排队的通知全部丢弃、只触发 `Error`，而原代码没有 `Error` 处理，静默丢失。
- 250 ms 合并只保留最后一条事件：临时文件写入后改名的保存流程会被合并成一条「改名」，而运行时对改名有「改名后刷新」开关，关闭时整个新文件就被压掉。另外 `_pendingChange` 的读写没有加锁，存在极小概率的丢事件窗口。

## 修法

- `DesktopDropForwarding.cs`：`IDesktopDropForwardTarget` + `DesktopDropForwarder`。宿主 `SetDropForwarding` 后 `AllowDrop = true`，重写 `OnDragEnter/Over/Leave/Drop`，按指针所在显示器把事件交给对应 `DesktopIconSurface`（实现了该接口，直接复用原有处理函数，`DragEventArgs` 里是屏幕像素，`PointToClient` 对哪个窗口调用都正确）。跨显示器时给上一块表面补一个 DragLeave。
- `DesktopItemProvider`：过滤器加 `Attributes`；缓冲区 64 KB；`Error` 时重新武装并上报 `WatcherChangeTypes.All`，运行时对 `All` 一律走全量刷新（不进入「自有变更」定向路径）；合并逻辑加锁，突发中含非改名事件时以「最终路径的 Changed」上报，`DescribeBurst` 为纯函数可测。

## 验证

- `DesktopDropForwardingTests`（转发/切换/离开/放下、像素包含、接口与源码断言）、`DesktopWatcherCoalescingTests`（合并策略与源码断言）。
- 重新拉起后用 `GetProp(hwnd, "OleDropTargetInterface")` 确认亚克力宿主已注册为 OLE 放置目标。
- 真实拖放需人工验证：从资源管理器拖文件到桌面空白处应出现复制/移动光标并落到桌面目录；跨屏拖动时幽灵图标跟随。

## 追加：拖到桌面应为移动而非复制（2026-09-15）

**现象：** 从资源管理器往桌面拖文件，落下后源文件仍在，桌面上多了一份副本。

**根因（两处叠加）：**
1. `OnExternalFileDragOver` 沿用盒子之间的 `BoxTransferPolicy`：同卷才移动，跨卷默认复制。这是 Explorer 在两个普通文件夹之间的规则，但用户对"拖到桌面"的预期是移动，无论文件来自哪个盘。
2. `OnDragDrop` 用 `eventArgs.Effect == Move` 决定 `move`。OLE 调 `IDropTarget::Drop` 时传入的初始 effect 是源的 allowed 掩码（Explorer 为 Copy|Move|Link），不是上一次 DragOver 协商出的值；WinForms 直接把它塞进 `DragEventArgs.Effect`，于是这个比较永远为假，即使同卷也一律复制。

**修法：** 新增纯函数 `DesktopIconSurface.ResolveExternalFileDropEffect(allowed, keyState, overRecycleBin)`：默认 Move，Ctrl 请求 Copy，Shift 强制 Move，回收站只 Move，最终受源 allowed 掩码约束（只允许 Copy 的源仍复制）。DragOver 与 Drop 都调用它，Drop 不再信任传入的 `Effect`；解析为 None 时直接返回不做操作。日志加印 `allowed=`。

**验证：** `ExternalDesktopDropTests` 增加 9 组 Theory 与卷无关性断言。真实行为需人工：从 D: 拖文件到桌面，源文件应消失；按住 Ctrl 拖则保留。

## 追加：从桌面拖到资源管理器也应为移动（2026-09-15）

**现象：** 把桌面图标拖进资源管理器的其他盘目录，桌面上原文件仍在。

**根因：** 桌面/盒子发起的 OLE 拖拽只提供 `FileDrop` 并允许 `Copy|Move`，操作由目标（Explorer）裁决，它套用"跨卷默认复制"。

**修法：** `FileClipboardCodec.WritePreferredDropEffect(DataObject, move)` 从 `Create` 中拆出；`TryStartDesktopOleDrag` 与盒子的 `DesktopBoxForm.Input` 在填好 `FileDrop` 后写入 `Preferred DropEffect = Move`（只读映射盒子写 Copy）。Explorer 对拖拽同样读取该格式，于是默认移动；allowed 仍含 Copy，Ctrl 或只接受复制的目标（浏览器上传）照旧复制。既有的 `ReconcileExternalDesktopMoveAsync` 在完成 effect 含 Move 时移除桌面图标。

**验证：** `FileOperationServiceTests.WritePreferredDropEffectStampsAnExistingDragPayload`；`DesktopItemReleaseRefreshTests` 源码断言。真实拖到另一盘目录需人工确认源文件消失。

## 追加：拖放后点击图标卡顿数秒（2026-09-16）

**现象：** 从资源管理器拖文件到桌面后，CrabDesk 数秒无响应，看起来像在传文件。

**日志证据：** 文件导入本身很快（`ImportAsync` 在线程池，`Refresh items … totalMs=17`）。卡的是随后的第一次点击：`UI thread stall began scope=icon window msg=0x0201`，`Slow icon window message msg=0x0201 elapsedMs=6495`（20:14）/ `1596`（16:19），且「Icon surface mouse down」日志行在卡顿结束后才打出，说明阻塞在 `OnMouseDown` 打日志之前的三个调用里；`CommitActiveDesktopInlineRename` 与 `GetItemAt` 都是纯内存操作，剩下 `ActivateDesktopKeyboardInput → SetForegroundWindow(Explorer 桌面根窗口)`。

**根因：** `SetForegroundWindow` 会同步向目标线程投递激活消息。拖放刚完成时 Explorer 桌面线程正在消化 Shell 变更通知（重建视图、缩略图），CrabDesk 的 UI 线程就在这里等它。之前记忆里的「阻塞在 COM/跨线程调用盲区」与此吻合。

**修法：** `DesktopWindowTools.TryActivateDesktopInput` 先判断已是前台则直接返回；再用 `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 50 ms)` 探测桌面线程，忙则跳过激活（F2 作用域只是锦上添花，冻住桌面不可接受）。新增 `IsWindowThreadResponsive` 供复用。

**第一版失败（06:20 复现，8.5 s，Explorer 同时无响应）：** 探测的是桌面根窗口的线程，它其实空闲；`SetForegroundWindow` 等的是**被取消激活的前一个前台窗口**，即发起拖拽、正在刷新源目录的资源管理器窗口。探测对象错了。

**第二版：** `CrabDeskRuntime.ActivateDesktopKeyboardInput` 改为在线程池线程上调用 `SetForegroundWindow`（前台权限按进程判定，工作线程仍可拿到），用 `_desktopInputActivationPending` 合并连点；不再探测（无论谁忙，都不再拖住 UI 线程），耗时 ≥100 ms 记 `Desktop input activation slow`。`OnMouseDown` 加阶段计时，≥100 ms 记 `Slow desktop mouse down stages commitMs/hitTestMs/activateMs`，下次若仍卡，日志直接点名。

**验证：** `DesktopUiThreadStallTests.ActivatingExplorerForKeyboardInputNeverWaitsOnABusyWindowFromTheUiThread`。真实效果需人工：拖入文件后立刻点击图标；若仍卡，看 `Slow desktop mouse down stages` 哪一段大。

**第二版也失败（07:17 复现，7 s，资源管理器同卡）：** 阶段计时没有任何输出，说明 `OnMouseDown` 三步都很快；卡的是 `WM_LBUTTONDOWN` 之后的**跨进程窗口链**——图标层是 Explorer `SHELLDLL_DefView` 的子窗口（`SetParent`），按下鼠标时系统的激活/捕获处理会同步走到父窗口所在的 Explorer 线程，而那条线程此时正忙。为什么忙：

**真正的根因（第三版）：** CrabDesk 的 `Drop` 自己把文件**移动**了，却返回 `DROPEFFECT_MOVE` 且没有做 Shell 的 optimized-move 握手（`IDataObject::SetData(CFSTR_PERFORMEDDROPEFFECT)`）。按 [Shell 数据传输文档](https://learn.microsoft.com/en-us/windows/win32/shell/datascenarios#handling-optimized-move-operations)，`DoDragDrop` 返回 MOVE 且 Performed DropEffect 未设为 NONE 时，源认为这是"非优化移动"，要由源删除原文件——而原文件已被我们移走，Explorer 的删除操作在重试/等待路径里耗数秒，期间它的桌面线程被占，我们的子窗口一按就跟着卡。此前"拖到桌面变复制"的现象也与此相关：以前返回 COPY 时反而没有这一步。

**修法：** 新增 `CrabDesk.Native/ShellDropEffectProtocol.cs`：`TryReportOptimizedMove(IDataObject)` 用 `GlobalAlloc` 写入 `Performed DropEffect = NONE`、`Logical Performed DropEffect = MOVE` 并 `SetData`。图标层 `OnDragDrop` 与盒子 `DesktopBoxForm.DragDrop` 的三条外部导入分支在 move 时先握手，再把返回 effect 改为 COPY（文档要求返回非 MOVE）。CrabDesk 自己发起的拖拽（带 session/key-list 格式）不走握手，仍靠运行时 reconcile。

**验证：** `FileOperationServiceTests.OptimizedMoveHandshake*`（记录型/拒绝型假 IDataObject）、`DesktopDropForwardingTests.EveryExternalMoveDropCompletesTheShellOptimizedMoveHandshake`。真实效果需人工：拖入后立刻点击不应再卡；日志应出现 `optimized-move handshake accepted=True`。

**第三版握手生效（`accepted=True`）但仍卡（08:07 复现）。** 于是做了对照实验，结论改变了问题的归属：

| 实验 | Explorer 桌面线程阻塞 |
|---|---|
| 仅 `File.Move` 一个文件进桌面目录，不经拖放，CrabDesk 运行中 | 7.0 s |
| 同上，**CrabDesk 已退出** | 6.3 s（.txt 6.8 / .pdf 4.5 / .bin 4.5 / .xls 0.5） |
| 同一文件放进「文档」目录 | 0.25 s |
| Wait Chain Traversal 看 Explorer 桌面线程在等什么 | `ComActivation`（跨进程 COM 激活） |

即：**本机 Explorer 的桌面线程每来一个新文件就被第三方 shell 扩展挂住数秒**，与 CrabDesk 无关。本机 explorer.exe 里挂着百度网盘 7 个 overlay handler（`D:\BaiduNetdisk\YunShellExtV164.dll`，百度网盘进程未运行，激活其服务要等超时）、小米云同步、WPS、Windhawk 等；SyncRootManager 里注册了百度/小米/OneDrive 三个云同步根。整个探测期间 CrabDesk 自己的两个窗口对 `SendMessageTimeout(WM_NULL)` 始终秒回。

**CrabDesk 为什么会连坐：** 图标层和盒子窗口通过 `SetParent` 挂在 Explorer 的 `SHELLDLL_DefView` 下，跨线程 `SetParent` 会把两个线程的输入队列附着在一起（等价 `AttachThreadInput`），Explorer 一卡，我们的鼠标消息处理就跟着等；watchdog 看到的 `scope=icon window msg=0x0201` 8 s 就是这个。前三版都在追"我们自己哪一步慢"，方向错了。

**第四版修法：** `DesktopWindowTools.AttachAsDesktopChild` 在 `SetParent` 之后立刻 `AttachThreadInput(childThread, parentThread, FALSE)` 解除附着（新增 `DetachInputQueueFromParentThread`）。解除后本线程的键盘状态不再镜像 Explorer，`Forms.Control.ModifierKeys`（基于 `GetKeyState`）会读不到 Ctrl/Shift，所以运行时 8 处修饰键读取全部改为 `DesktopWindowTools.GetAsyncModifierKeys()`（`GetAsyncKeyState`）。CrabDesk 的窗口本来就是 `WS_EX_NOACTIVATE`、键盘命令走底层钩子，不依赖焦点共享。

**验证：** `DesktopUiThreadStallTests.DesktopSurfacesDetachTheirInputQueueFromExplorersThread`。真实效果需人工：拖入文件后 Explorer 仍会卡几秒（那是它自己的事），但 CrabDesk 的图标应能立刻点击、拖动。Ctrl/Shift 多选需要回归确认。

**给用户的建议（环境层面）：** 桌面新文件触发的 COM 激活超时大概率来自未运行的百度网盘的 overlay handler，可用 ShellExView 之类工具禁用 `YunShellExt` 的 7 个 overlay 项，或启动/卸载百度网盘后再测 `File.Move` 到桌面是否仍阻塞。

**另一个独立卡顿（未修）：** 23:50 的 11 s 卡顿是 `RebuildSurfaces`（工作区高度在 1380/1440 之间反复切换触发，`msg=0x001A` WM_SETTINGCHANGE），与拖放无关，记忆里已有记录。

## 结案：归属实验确认残留卡顿在 Explorer 一侧（2026-09-18）

用户复核「CrabDesk 不卡了，但资源管理器还是会卡死」后，做了一组对照实验来判定这段残留卡顿该由谁负责。探测手段是 `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)` 打 Explorer 桌面线程（`Progman > SHELLDLL_DefView`），取最差往返耗时。

| 场景 | Explorer 桌面线程最差耗时 |
|---|---|
| 空闲基线 | 8 ms |
| CrabDesk 未运行，向桌面目录写 1 个文件 | 1248 / 1287 / 1332 ms（3/3 复现） |
| 同一操作写入普通目录 `E:\Documents` | 2 ms |
| 同一操作写入桌面目录 | 1161 ms |
| CrabDesk 运行中，向桌面目录写文件 | 1425 ms |
| 同一时刻 CrabDesk 挂在桌面视图下的子窗口 | 2 ms |

三条结论：

1. **CrabDesk 不是原因。** CrabDesk 完全退出时，新文件落到桌面目录仍让 Explorer 桌面线程卡 1.2–1.3 秒，稳定复现。
2. **是桌面命名空间特有的。** 同样一次文件创建，写普通目录 2 ms、写桌面目录 1161 ms。差别不在文件操作本身，而在桌面视图会为每个新文件跑一遍第三方 shell 扩展（overlay/同步 handler）。
3. **解附着修复有效。** CrabDesk 运行中，Explorer 卡 1425 ms 的同时，CrabDesk 那个 `SetParent` 到 `SHELLDLL_DefView` 下的子窗口（`WindowsForms10.Window.8`）只用 2 ms 响应。第四版的 `AttachThreadInput(childThread, parentThread, FALSE)` 确实切断了连坐；watchdog 里 `msg=0x0201` 的 `icon window` 停顿自 09-16 06:20 之后不再出现，此后的停顿全在 `scope=idle` 且伴随 `RebuildSurfaces`，与拖放无关。

Explorer 侧来源（`explorer.exe` 桌面线程进程内实际加载）：百度网盘 `D:\BaiduNetdisk\YunShellExtV164.dll` 的 7 个 overlay handler（注册表 `ShellIconOverlayIdentifiers` 的 `.WorkspaceExt0-6`）、WPS（`kwpsshellext64` / `qingshellext64`）、Bandizip（`bdzshl.x64`）、Listary（`ListaryHook64`）、Windhawk 14 个 mod、breeze-shell；`SyncRootManager` 下另有百度/小米/OneDrive 三个云同步根。百度网盘进程当前未运行，激活其服务要等超时，与之前 Wait Chain Traversal 看到的 `ComActivation` 等待吻合。

**验收脚本：** `build/verify-desktop-freeze-attribution.ps1`。在 CrabDesk 关闭与运行两种情形下都能跑，自动对比「普通目录 / 桌面目录 / CrabDesk 子窗口」三项延迟并给出归属判定。本次输出 `control 1ms / desktop 1425ms / crabdesk 2ms`。

**给用户的环境层建议：** 用 ShellExView 禁用百度网盘 `YunShellExt` 的 7 个 overlay 项，或启动/卸载百度网盘后复测；预期「新文件落桌面卡 1 秒以上」随之消失。CrabDesk 侧已无可改之处。

**全量回归：** `CrabDesk.Tests` 368、`CrabDesk.WinUI.Tests` 619、`CrabDesk.Bootstrapper.Tests` 46，全部通过。
