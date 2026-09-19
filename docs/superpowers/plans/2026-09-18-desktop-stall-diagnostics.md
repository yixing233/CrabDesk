# 桌面卡顿诊断（诊断页）实现记录

日期：2026-09-18

## 目标

把 `2026-09-15-desktop-drop-and-watcher-fixes.md` 末尾那套「归属实验」做进 CrabDesk，让用户能在设置里自己跑一次，判断「文件落到桌面后卡住数秒」该由谁负责，并看到具体是哪些第三方 Shell 扩展。

范围是**只读诊断**：测量、列清单、给结论和建议，不代用户改系统。

## 为什么不代改注册表

禁用图标覆盖处理程序要重命名 `HKLM\...\ShellIconOverlayIdentifiers` 下的项，需要管理员权限，而且改动的是别的软件的注册信息。CrabDesk 目前从不写 HKLM（所有写入都是 HKCU），为了这个功能引入提权路径不划算。诊断页把具体 DLL 和产品名列出来，由用户决定是否用 ShellExView 处理。

## 结构

| 层 | 文件 | 职责 |
|---|---|---|
| Core | `DesktopStallAnalysis.cs` | 纯逻辑：四项耗时 → 结论；DLL 路径 → 产品名；过滤与计数 |
| Core | `Models.cs` | `DesktopStallVerdict`、`ShellExtensionKind`、`ShellExtensionEntry`、`DesktopStallReport` |
| Native | `DesktopStallProbe.cs` | `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)` 往返计时；单窗口与**交错**双窗口采样 |
| Native | `ShellExtensionInventory.cs` | 读 overlay 注册项并解析到 DLL；列出 explorer 进程内非微软模块 |
| Runtime | `DesktopStallDiagnostics.cs` | 编排：定位桌面 → 测基线 → 对照组 → 桌面目录 + 交错测 CrabDesk → 出报告 |
| Runtime | `DesktopSurfaceManager.GetIconSurfaceHandle` | 暴露「CrabDesk 是否还在响应」的探测目标 |
| WinUI | `DiagnosticsViewModel` / `Views/DiagnosticsPage.xaml` | 导航项「诊断」、结果展示、复制报告 |

## 关键实现决定

**交错采样。** 早先的验证脚本是先测完 30 次 Explorer、再测 30 次 CrabDesk，两者相隔约 4.5 秒。那样测出来的「Explorer 卡 1200 ms 时 CrabDesk 只用 2 ms」可能只是因为 CrabDesk 的采样落在卡顿结束之后。现在 `MeasurePairedWorstResponse` 在同一轮里背靠背测两个窗口，读数取自同一时刻，结论才站得住。重测结果：Explorer 1206 ms 的那一轮，CrabDesk 是 0 ms——旧结论成立，且证据更强。

**探针文件必须是可见文件。** 最初想用 `FileOptions.DeleteOnClose` + 隐藏属性，但 `using` 结束文件就没了、根本测不到，而且隐藏文件会被 Explorer 视图过滤掉，overlay handler 不会跑，测量会静默报「桌面很健康」。现在创建普通可见文件、在 `finally` 删除，页面上也写明「会短暂创建临时文件」。

**窗口句柄在 UI 线程解析。** `DesktopSurfaceManager` 的表面列表在 UI 线程被修改，测量在线程池。若把 `Func<IntPtr>` 传进去在后台调用会与表面重建竞态，所以句柄先解析好再交给 `DesktopStallDiagnostics`。

**阈值只在 Core 一处。** `ClassifyVerdict` 是纯函数，判定规则（桌面目录 ≥ 250 ms 且比对照组高 250 ms 以上；CrabDesk ≥ 200 ms 视为被阻塞）集中在 `DesktopStallAnalysis`，Runtime 不再重复定义，源码测试也断言了这一点。

## 判定规则

| 条件 | 结论 |
|---|---|
| 桌面目录 < 250 ms | `NoStall` |
| 桌面目录比对照组高不足 250 ms | `Inconclusive`（整机卡顿，不能赖桌面命名空间） |
| 桌面目录高且 CrabDesk < 200 ms | `ThirdPartyShellExtensions` |
| 桌面目录高且 CrabDesk ≥ 200 ms | `CrabDeskBlocked`（解附着失效，是我们的 bug） |
| 缺任一测量值或没有桌面 | `Inconclusive` |

`CrabDeskBlocked` 这一支是特意留的：它让这个功能同时成为第四版解附着修复的回归探测器。

## 验证

**本机实测（诊断页真实运行）**：空闲基线 3 ms、普通目录对照 3 ms、桌面目录 2140 ms、同时刻 CrabDesk 51 ms → 结论「第三方 Shell 扩展导致资源管理器卡顿」。扩展清单与机器实际情况一致：Windhawk 14 项、百度网盘 8 项、WPS Office 5 项、Autodesk 2 项、微信输入法 2 项、Bandizip 1 项、Bonjour 1 项、Listary 1 项、breeze-shell 1 项、深信服 1 项。

**交错采样重测**：40 轮中仅 1 轮 Explorer ≥ 500 ms（1206 ms），该轮 CrabDesk 为 0 ms；Explorer ≥ 200 ms 的轮次里 CrabDesk 全部 ≤ 1 ms。

**探针文件不残留**：诊断跑完后桌面目录与文档目录均无 `CrabDesk-stall-probe-*`。

**测试**：`CrabDesk.Tests` 400、`CrabDesk.WinUI.Tests` 639、`CrabDesk.Bootstrapper.Tests` 46，全部通过。新增覆盖：
- `DesktopStallAnalysisTests`：判定矩阵（含 `CrabDeskBlocked` 与整机卡顿两个反例）、阈值边界、产品名映射、微软路径过滤、去重、按产品计数。
- `ShellExtensionInventoryTests`：注入 HKCU 的 GUID 临时子树读写 overlay 项、CLSID → DLL 解析、`InprocServer32` 缺失时回退到 CLSID 默认值、无法解析时不抛异常、键不存在返回空列表；`finally` 里 `DeleteSubKeyTree(..., false)` 清理。
- `DiagnosticsViewModelTests`：四项耗时与结论文案、未测量显示占位符、扩展汇总、未跑先复制、复制内容、失败不抛异常、运行中重复点击只跑一次。
- `DesktopStallDiagnosticsSourceTests`：测量在 `Task.Run` 内、探针文件在 `finally` 清理、探针不是隐藏/DeleteOnClose 文件、双窗口采样确实在同一循环体内、阈值不在 Runtime 重复定义、导航与 DI 接线、Native 层不含任何 `SetValue`/`DeleteSubKey`。
- `DesktopStallDiagnosticsEndToEndTests`：对真实桌面跑完整测量、探针文件不残留、无 CrabDesk 表面时结论为 `Inconclusive`、无桌面时给出解释。

## 已知边界

- 列出 explorer 进程内模块需要读取该进程，权限不足时只返回 overlay 清单（主证据仍在）。
- 测量会短暂在桌面创建一个临时文件，属于可观察的副作用，页面已说明。
- 桌面目录与普通目录耗时接近时判为 `Inconclusive` 而非硬给结论，宁可不说也不误判。
