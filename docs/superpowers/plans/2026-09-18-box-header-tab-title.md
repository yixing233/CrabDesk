# 盒子标题栏显示子标签名导致「重复盒子」 修复记录

日期：2026-09-18

## 现象

用户报告桌面上出现了重复的盒子：「图片」这个名字同时出现在两处。截图显示，其中一个盒子标题栏写着「图片」，其下方标签栏是 `全部 | 其它 | 图片 | 目录 | 压缩`；另一个盒子标题栏也是「图片」，没有标签栏，里面只有一项 `绘图12.bmp`。

## 排查

先按数据查：解析 `config.json` 后逐盒比对 `ManualTabs`，按码点级（NFKC + 去零宽/控制字符 + 折叠空白）折叠后查重，结果是**盒内没有任何重名子标签**——全配置里只有 `组合盒子` 有子标签，4 个：其它/图片/目录/压缩。

再看「图片」这个名字在整个配置里的出现位置，有三处：`组合盒子` 的 `图片` 子标签、自动生成的独立 `图片` 盒子、以及 `图片` 内置规则。也就是说，重复不在同一个盒子的标签列表里，而在「盒子标题」和「子标签标题」两个不同的命名空间之间。

## 根因

`DesktopBoxForm.Rendering.cs` 画标题栏时，把**当前激活的子标签名**当成了盒子名：

```csharp
var displayedTitle = geometry.ActiveManualTabId is { } activeTabId
    ? geometry.Box.ManualTabs.FirstOrDefault(tab => tab.Id == activeTabId)?.Title
    : null;
graphics.DrawString(
    string.IsNullOrWhiteSpace(displayedTitle) ? geometry.Box.Title : displayedTitle, ...);
```

这个覆盖在 `fed5d4c`（v20260913.01）随「拖拽经过标签区域时选中对应标签，并显示当前标签标题」一起进来。于是：

- `组合盒子` 激活了 `图片` 子标签 → 标题栏写「图片」。同时标签栏里本来就有个「图片」条目，同一个盒子里「图片」出现两次。
- 桌面上另有一个整理生成的独立 `图片` 盒子。
- 两者标题栏同名，从视觉上就是一个重复的盒子。

折叠状态下更糟：`CreateBoxGeometry` 里 `manualTabs = isCollapsed ? [] : availableManualTabs` 会让标签栏消失，但 `activeManualTabId` 仍然被解析出来（这是为了保留 `ItemViewKey`，避免展开后滚动位置丢失）。所以折叠的 `组合盒子` 只显示「图片」，连标签栏这个唯一线索都没有，和真正的 `图片` 盒子完全无法分辨。

数据本身没有重复，是显示层把一个盒子的名字「借」给了子标签。

## 修法

标题栏始终显示盒子名，不再被子标签名覆盖。当前激活的子标签改由标签栏自己的强调色 + 下划线表达——`DrawBoxTabs` 里本来就有这套标记（`geometry.ManualTabs[index].Id == geometry.ActiveManualTabId` 决定加粗与下划线），拖拽经过标签时的反馈也走同一条路径（`TrySelectBoxTab` 会 `InvalidateDip(box.TabBar)`），因此原有的拖拽提示不受影响，只是不再借标题栏表达。

没有改动任何配置数据：`图片` 子标签和 `图片` 盒子都保留原样，只是不再同名显示。

## 验证

新增 `CrabDesk.WinUI.Tests/DesktopBoxHeaderTitleTests.cs`（2 项）：

- 标题栏画的是 `geometry.Box.Title`，且源码里不再存在 `displayedTitle` 与那段取子标签标题的表达式；
- 标签栏仍保留激活标记（强调色 + 下划线），确保移除标题覆盖后仍有「当前是哪个标签」的指示。

先经测试失败确认复现：把旧代码还原后，`HeaderDrawsTheBoxTitleRatherThanTheActiveSubTabTitle` 报 `Assert.Contains() Failure: Sub-string not found`；改回修复版后转绿。

全量测试：`CrabDesk.Tests` 400、`CrabDesk.WinUI.Tests` 649（原 647 + 新增 2）、`CrabDesk.Bootstrapper.Tests` 46，全部通过。Release 构建 0 错误。

## 遗留

用户配置里那个独立的自动 `图片` 盒子（1 项 `绘图12.bmp`）与 `组合盒子` 的 `图片` 子标签装的是不同文件，本次修复不做合并：两者都有存活内容，且分属「盒子」与「子标签」两个层面。若要合并，把 `绘图12.bmp` 拖进 `组合盒子` 的 `图片` 子标签后删掉空盒即可，属于用户决定。

另外 `组合盒子` 的 `图片` 子标签里那两张 `Snipaste_*.png` 经 sha256 比对是**字节完全相同**的两个磁盘文件（均 24822 字节，`73de9f63c11bd9ab`），这是 Snipaste 自己产生的，与本次修复无关，需要用户自行决定是否删除其一。

## 附：修复过程中造成的一次环境事故（非代码缺陷）

重启实例时使用了 `taskkill /F` **强杀正在接管桌面的进程**，且此前在实例运行期间跑过会操作真实桌面的 WinUI 测试。结果 Explorer 于 17:38:31 重建，CrabDesk 的桌面附着失效：盒子绘制层（`CrabDesk Desktop Boxes`）掉到 explorer 图标层（`CrabDesk Desktop Icons`）之下，点击全部被图标层接走，表现为**所有盒子都无法交互**。日志里 `Surface mouse down ... box=<id>` 从上一会话的 20 次降为 0 次，只剩 `Icon surface mouse down ... item=<desktop>`；17:42 起持续报 `Acrylic desktop placement failed: The desktop host is hidden, minimized, or no longer attached to Explorer`。

排除过程确认与本次代码改动无关：改动只替换了标题栏 `DrawString` 的字符串实参，不涉及命中测试；`config.json` 完好（11 个盒子、122 条分配、边界未变）。

恢复方式：用 `CrabDesk.WinUI.exe --exit` 走干净退出通道（而非再次强杀），确认 `desktop-visibility.lock` 已清除、Progman 重新持有 `SHELLDLL_DefView` 后重启。新会话 0 条 placement 失败，盒子层 z=989 重新高于图标层 z=990。

防复发措施已写入 [`docs/DEVELOPMENT_NOTES.md`](../../DEVELOPMENT_NOTES.md)：接管桌面的进程必须用 `--exit` 停止，桌面相关测试必须在实例停止后运行（`Release` 输出目录被实例锁定的 `MSB3027/MSB3021` 报错正是同一前提问题，不要靠改 `Debug` 绕过）。
