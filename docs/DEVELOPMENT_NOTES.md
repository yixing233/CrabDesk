# 开发注意事项（DEVELOPMENT_NOTES）

本文件记录开发过程中容易踩、且后果不易察觉的坑。与
[`RELEASING.md`](RELEASING.md)（发布流程）和 [`DEVELOPMENT_PLAN.md`](DEVELOPMENT_PLAN.md)
（迭代规划）互补：这里只写「做某件事会悄悄弄坏什么」。

---

## 1. 接管桌面的进程不能强杀

CrabDesk 的盒子不是悬浮置顶窗口，而是通过 `DesktopAcrylicHost` 附着在 Explorer 桌面层上。
进程持有这份附着时，**`taskkill /F`、任务管理器「结束任务」、调试器终止都属于危险操作**。

### 后果

强杀会让进程来不及还原桌面状态，通常接着出现两类现象：

- **Explorer 重建桌面**：`Progman` 被重建，CrabDesk 记着的桌面视图句柄失效。日志里出现
  `Acrylic desktop placement failed: The desktop host is hidden, minimized, or no longer
  attached to Explorer`。
- **盒子层掉到图标层下面**：盒子窗口（`CrabDesk Desktop Boxes`）仍在，也可见，但
  z 序落到了 `CrabDesk Desktop Icons` 之下。图标层接管全部点击，表现为
  **所有盒子都无法交互**（点不动、右键无菜单），而桌面图标本身看着正常。

此时从外观很难判断：盒子照常绘制、配置完好、进程响应正常，只有输入进不去。

### 判断方法

比对日志里的输入事件来源。正常时盒子窗口会记录

```
[P<pid> T1] Surface mouse down monitor=... button=Left x=.. y=.. box=<盒子Id> itemKind=..
```

（由 `DesktopBoxForm.Input.cs` 打印）。若点击盒子位置却只出现

```
[P<pid> T1] Icon surface mouse down monitor=... item=<desktop>
```

（由 `DesktopIconSurface.cs` 打印），说明输入被图标层接管了，`box=` 一次都不出现。

### 正确做法

用程序自带的干净退出通道，它会走完整的还原流程：

```powershell
& "$PWD\CrabDesk.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\CrabDesk.WinUI.exe" --exit
```

`--exit`（等价别名 `--exit-existing`）通过单实例命令通道通知已在运行的实例退出，
见 `CrabDesk.WinUI/App.xaml.cs` 的 `OnLaunched`。

退出后确认还原干净，再启动新实例：

```powershell
# 进程应已不在；锁文件应已清除；Progman 应重新持有 SHELLDLL_DefView
Test-Path "$env:LOCALAPPDATA\CrabDesk\desktop-visibility.lock"   # 期望 False
```

---

## 2. 不要在实例运行时跑桌面相关测试

`CrabDesk.WinUI.Tests` 里有会操作**真实桌面**的测试，它们创建真实窗口并调用
`ShowAtDesktop` / `DesktopAcrylicHost`：

- `DesktopAcrylicTests`（含 `AcrylicHostRestoresExternalHideWhileDesktopDisplayIsRequested`
  等，使用 `Monitor(0, 0, 1)` 构造名为 `acrylic-test` 的假显示器）
- `DesktopDropForwardingTests`
- `DesktopStallDiagnosticsEndToEndTests`（会往真实桌面写探针文件）

在正式实例运行期间跑这些测试，两边的桌面附着会互相干扰，效果与强杀类似（Explorer 重建、
盒子失去交互）。`DesktopProbeCollection` 只串行化了测试之间的探针写入，管不到「测试 vs
运行中的实例」。

### 正确做法

```powershell
# 1. 先干净停掉实例
& "...\CrabDesk.WinUI.exe" --exit
# 2. 再跑测试
dotnet test CrabDesk.WinUI.Tests/CrabDesk.WinUI.Tests.csproj -c Release -p:Platform=x64
# 3. 测试完再启动
```

另外，`Release` 输出目录在实例运行时被锁定，`-c Release` 构建会报
`MSB3027 / MSB3021 文件被 CrabDesk.WinUI (pid) 锁定`。遇到这个报错，先停实例，
不要改用 `Debug` 绕过——它掩盖的是同一个「实例在跑」的前提问题。

---

## 3. Windows 上写临时脚本的两个坑

排查问题时经常需要临时脚本，注意：

- **PowerShell 脚本里的中文会被编码破坏**。用 Git Bash 里的 heredoc 写 `.ps1` 时，
  中文常被写成乱码，导致 `哈希文本不完整` 之类的语法错误。临时脚本尽量用 ASCII 标识，
  或改用 Python。
- **Python 一行式命令经 Git Bash 传递会被破坏**（出现 `|| goto :error` 之类的内容）。
  把脚本写成 `.py` 文件再执行，不要用 `python -c "..."`。
- 通过 `.NET` 产物核对字符串时用 UTF-16：
  `[System.Text.Encoding]::Unicode.GetString($bytes)`，直接 `strings` 看不到。

临时脚本用完即删，不要留在 `build/` 里。
