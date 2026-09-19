# 规则整理残留空盒子 修复记录

日期：2026-09-18

## 现象

用户反馈：规则整理之后，盒子里已经没有任何可见项目，盒子本身却仍然留在桌面上。此前的 `bug-u2yrcu` 修的是「同名盒子被重复创建」，与这条不是同一个问题——那次修完，重复创建的路径确实不再触发了（用真实配置跑整理，新建盒子数为 0），但空盒子依然会出现。

## 复现

先用用户真实的 `config.json` 加真实桌面跑 `EnsureSmartOrganizationStructure`（配置复制到沙箱目录后经 `JsonLayoutStore` 正常加载，迁移与归一化都会执行），再逐项核对状态。

核对结果确认了关键事实：**122 个 `Assignments` 条目中有 33 个指向桌面上已不存在的文件**。核对方式是按 CrabDesk 自己的规则重算稳定 key（volume serial + file index，即 `FileIdentity.GetStableId` 的算法）再与 `Assignments` 的键比对，不是按文件名猜。

## 根因

### 1. 失效分配让空盒子被判为「非空」

`OrganizationRuleEngine.CanRemoveEmptyBox` 用 `state.Assignments.Values.Contains(box.Id)` 判断盒子是否非空，`CountProjectedItems` 也把 `Assignments` 里指向该盒子的条目全部计入。但指向已删除文件的失效分配不会被清理：只有 `SmartOrganize` 开头会清理陈旧键，而自动整理走的是 `ApplyOrganizationRules`，那个清理步骤不执行。

于是「只装着已删除文件」的自动盒被判为非空而保留，而渲染层（`GetItemsForBox`）是按 `Items` 过滤的，该盒子一个项目都显示不出来——正是用户看到的空盒子。

### 2. 自定义规则的空盒从不清理

内置规则的循环里有 `itemCount == 0` 时调用 `CanRemoveEmptyBox` 删除空盒的分支，而自定义规则（非内置规则）的循环在同一条件下只 `continue`，从不删除。因此自定义规则创建的空盒永远不会被移除。

## 修法

- `CanRemoveEmptyBox` 与 `CountProjectedItems` 新增可选的 `liveItemKeys` 参数，只把存活项目算作占位。
- `EnsureSmartOrganizationStructure` 用 `Items` 构造该集合并传入两处调用点（内置规则循环与自定义规则循环）。
- 自定义规则循环的 `itemCount == 0` 分支补上内置规则同样的清理逻辑。

选用 `Items` 而不是 `_allDesktopItems` 作为存活来源，是为了让「盒子是否为空」的判断与渲染层严格一致：`GetItemsForBox` 也是从 `Items` 过滤的，所以被判为非空的盒子一定有东西显示出来。

## 验证

新增 `OrganizationEmptyBoxTests`（8 项）：

- 只装失效分配的自动盒会被删除；
- 装存活项的自动盒会被保留；
- 失效分配不会凭自身创建盒子；
- **整理创建的每个盒子都真的收到了项目**——这是既有测试的盲区：`OrganizationBoxReuseTests` 只断言「盒子存在且规则指向它」，即使盒子建完什么都不装也能通过，因此空盒子问题在测试上是隐形的；
- 内置规则与自定义规则在文件被删除后都会移除空盒（均按「先建盒、后删文件、再整理」的真实两轮序列构造）；
- 用户手工创建的盒子即使为空也不移除。

两条修复各自都先经测试失败确认复现，再改代码转绿。全量测试：`CrabDesk.Tests` 400、`CrabDesk.WinUI.Tests` 647、`CrabDesk.Bootstrapper.Tests` 46，全部通过。

## 遗留

用户现有配置里那个重复的「文档」盒子（手工 34 项 + 自动 6 项存活）是修复前遗留的，不会自动合并：两个盒子都还有存活项目，本次修复不删除任何一个。需要用户手动删掉多余的那个。清理失效分配只在 `SmartOrganize` 路径上发生，本次没有扩大它的调用范围——那是独立的行为改动，不混进这个修复。

## 核查过但不算问题：「目录」「压缩」规则的 target 为空

整理后这两条内置规则的 `TargetBoxId` 仍是空的，看起来像"规则没生效"，实际是正确行为：桌面上那个文件夹（`新建文件夹 (2)`）和压缩包（`NapCatQQ-...zip`）已经手工分到了 `组合盒子` 里，而 `ReassignExistingItems` 为 `false`，`Preview` 对已有分配的项目直接 `continue`，所以两规则没有可收的项目、不建盒子、也不设 target。

核对方式是按 `FileIdentity.GetStableId` 的算法（volume serial + file index）重算桌面上 186 个真实项目的稳定 key，再与 `Assignments` 比对，而不是按文件名猜。结论：不存在"该收没收到"的项目。
