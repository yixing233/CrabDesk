# AI 输出区 Activity Feed 重构 Implementation Plan

**Goal:** 将 AI 分类页右侧对话输出区重构为现代 agent 风格的 Activity Feed：全宽无气泡卡片、思考/工具调用/逐项分类/结果作为连续时间线、完成后过程自动收起为可展开摘要、分类结果用「分类标签 + 图标名 chips」呈现。

**用户决策（2026-09-14 确认）：**
1. 布局：彻底 Activity Feed（去嵌套卡片、事件流全宽）
2. 过程折叠：完成后自动收起为「过程轨迹 · 已处理 N/M 项」摘要行，点击展开
3. 结果形式：分类标签 + chips（图标名小圆角块）

---

### Task 1: 数据层扩展

**Files:**
- Modify: `CrabDesk.WinUI/ViewModels/AiConversationResultGroupViewModel.cs`
- Modify: `CrabDesk.WinUI/ViewModels/AiConversationMessageViewModel.cs`

- [x] `AiConversationResultGroupViewModel` 暴露 `ItemNames`（快照数组）供 chips 换行布局渲染。
- [x] `AiConversationMessageViewModel` 新增 `ShowSuccessMark`（完成且非错误且有 OutcomeText 时显示成功标记）与 `ToggleProcessCommand`。
- [x] `Complete` / `SetResultGroups` 触发 `ShowSuccessMark` 通知。

### Task 2: XAML 重建为 Activity Feed

**Files:**
- Rewrite: `CrabDesk.WinUI/Views/AiClassificationPage.xaml`

- [x] 删除 assistant 模板中的嵌套灰底卡片（原「智能体分类轨迹」Border、内联 outcome 卡片）。
- [x] Assistant 消息改为全宽 StackPanel：身份行（头像+名字+时间+状态图标）→ 正文缩进 31px 的事件流。
- [x] 定义 `AiActivityRowTemplate`：状态图标（Sparkles/Check/CircleAlert/Square）+ DisplayText + 运行 ProgressRing。
- [x] 运行中：当前进度摘要（ProgressRing + 「正在处理 N/M · 名称」）+ 最近活动行。
- [x] 完成后：折叠按钮（ChevronDown/Right + 「过程轨迹」+ 摘要），点击 ToggleProcessCommand 展开完整轨迹。
- [x] 结果区：每个分类一行（Folder/CircleAlert 图标 + 分类名 + 计数），下方 ItemNames 用 TagWrapLayout 渲染 chips。
- [x] 成功标记行：CircleCheck + 「分类完成」绿色。
- [x] 移除底部 Collapsed 的固定预览区块（Row 2），Composer 移至 Row 2；列宽 420→460，MinWidth 860→900。
- [x] 思考过程/JSON 保留为次级 Expander；Transport/Parsed/Metrics 为微元数据行。

### Task 3: 测试

**Files:**
- Modify: `CrabDesk.WinUI.Tests/AiTaskConversationTests.cs`

- [x] ItemNames 快照断言（源集合清空后仍保留）。
- [x] ToggleProcessCommand 展开/收起往返测试。
- [x] ShowSuccessMark 仅在「完成 + 非错误 + 有 OutcomeText」时为 true（含错误分支反例）。

### Task 4: 验证

- [x] `dotnet build CrabDesk.WinUI/CrabDesk.WinUI.csproj -c Debug -p:Platform=x64` — 0 错误
- [x] `dotnet test CrabDesk.sln` — 920 个测试全部通过（Bootstrapper 46 / Core 337 / WinUI 537）
- [ ] 启动新版本，执行一轮分类，确认：时间线连贯、chips 换行、完成后收起、展开可回看、JSON/思考可复制。

**测试修正记录：**
- 布局测试 `document.Root?.Elements().Single()` 因新增 `Page.Resources` 改为按 `Grid` 本地名筛选。
- `ShowSuccessMark` 语义定为 `!IsRunning && !IsError && HasResultGroups`（取消/错误/无结果均不显示绿色完成标记）。

### 修正（2026-09-14）：过程轨迹只剩 6 行

**现象：** 64 项分类完成后摘要显示「已处理 64/64 项」，但展开「过程轨迹」只有 6 行，其余 58 项无法回看。

**根因：** `AiClassificationViewModel.AppendClassificationActivity` 为了让运行中的视图保持紧凑，直接把 `ClassificationActivities` 裁到 6 条；而完成后的可展开轨迹绑定的是同一个集合，历史随之丢失。

**修法：** 把「完整轨迹」和「最近窗口」拆成两个集合：
- `ClassificationActivities`：完整轨迹，按首次出现顺序，永不裁剪；完成后展开即为全量。
- `RecentClassificationActivities`：最近更新的 `RecentActivityWindow`（6）行滑动窗口，同一行更新后移到尾部；仅供运行中视图绑定。
- 两者共享同一行实例，`Complete()` 把未完成行标为「已停止」时两边同步。
- 完成后展开的完整轨迹套在 `MaxHeight=132` 的 ScrollViewer 里（约等于运行中 6 行窗口的高度），长轨迹在内部滚动，不再把整条消息撑到 65 行（2026-09-15 补）。
- 视图模型不再裁剪集合；查找改为按 key 的字典，64+ 项不再线性扫描。

### 左侧图标网格卡片重排（2026-09-14）

**现象：** 复选框叠在图标左上角遮住图标；分类后的绿色标签被卡片底边裁掉只露一半（名称两行时更明显）。

**根因：** 卡片 `MinHeight=136` 但 UniformGridLayout 的槽位固定 136，标签出现后卡片长高、超出槽位被圆角裁剪；复选框与图标按钮共用同一格。

**修法（`AiClassificationPage.xaml` 工作区 DataTemplate）：**
- 卡片固定 132×140，与槽位一致；内部三行固定高度：图标 56 / 名称 36（两行 18px）/ 标签 24，标签行在分类前就预留，分类后不再回流。
- 复选框缩为 20px 紧凑框，放在卡片右上角，独立于图标按钮所在的格；未选中时内容区通过 `BooleanToOpacityConverter`（新增）降为 45% 透明度，复选框保持清晰。
- AI 标签：绿色胶囊 + Check 图标 + 文字，超宽省略，完整名称进 tooltip。
- 待确认：原「待确认」徽标 + 独立 ComboBox 合并为一个 24px 高的琥珀色胶囊 ComboBox，占位文字「待确认」，展开即选标签（局部覆盖 `ComboBoxMinHeight` 资源以压到 24）。
- 名称字号 14→13，`LineHeight=18` + `BlockLineHeight` 固定两行高度，tooltip 显示完整名称。

**测试：** 新增两条 XAML 布局断言（卡片尺寸=槽位尺寸、标签行固定高、复选框右上角且在降透明子树之外）与 `BooleanToOpacityConverter` 的 5 组 Theory。

### 窗口右侧空白（2026-09-14）

**现象：** 无论窗口多宽，AI 整理窗口右侧总有一条约 100px 的空白，左右内边距不对称。

**根因：** `AiOrganizationWindow.xaml` 用 `ContentControl` 承载页面，WinUI 默认样式的 `HorizontalContentAlignment=Left` / `VerticalContentAlignment=Top` 让页面只占自身期望宽度（能放下的整列图标数 × 142 + 右栏 460），不足一列的余量堆到窗口右边。`MainWindow` 的 `Frame` 默认 Stretch，所以设置页没有这个问题。

**修法：** 宿主 `ContentControl` 显式 `HorizontalContentAlignment="Stretch"` `VerticalContentAlignment="Stretch"`，右栏贴齐窗口右内边距。`AiOrganizationWindowTests.AiOrganizationWindowHostsTheWorkbenchPage` 增加两条对齐断言。

**图标网格自适应（同日追加）：** 页面撑满后，余量转移到了左侧图标网格右边（`ItemsStretch=None` 只排整列）。改为 `ItemsStretch="Fill"`：列槽位均匀加宽填满行宽，卡片保持固定 132px 并在槽位内居中（`HorizontalAlignment="Center"`），余量平均分到列间距；最后一行不满时仍与上方列对齐（Fill 只改槽宽，不改排布顺序；`SpaceBetween` 类对齐会把不满的末行拉散，故不用）。布局测试改名为 `WorkspaceCardsKeepAFixedWidthWhileTheirColumnsFillTheAvailableWidth`。

### 整理完成后的按钮状态与结果可编辑（2026-09-14）

**现象：** 分类完成后底部主按钮仍是「开始 AI 分类」；结果只能看，不能改标签，也没法说「这个不归类」（原来只有待确认项有一个下拉框）。

**修法：**
- 底部动作条：有预览时「开始 AI 分类」变为「重新分类」并退为次要样式，「确认应用」接过强调色（`PrimaryActionStyleConverter` 按 `HasPreview` 选 `AccentButtonStyle`/`DefaultButtonStyle`，`invert` 参数用于运行按钮）；「终止」按钮仅在运行中显示。
- 条目视图模型 `AiWorkbenchItemViewModel` 增加 `IsExcluded`（不归类）、`IsManuallyLabeled`、`ShowsAiLabel`、`ClassificationToolTip`；`EffectiveLabel` 改为「用户决定优先」：不归类→null，手动标签→手动，否则 AI 建议。`IsUncertain` 只在有预览、未排除且完全没有标签时为真。
- 卡片底部的标签胶囊变成菜单按钮：四种状态（AI 建议绿/手动指定蓝/不归类灰/待确认琥珀）各自一层填充与前导图标，尾部下拉箭头。点击由 `ClassificationChip_OnClick` 现场构建 `MenuFlyout`（`MenuFlyout` 没有 `ItemsSource`）：所有分类标签 + 「不归类」为一组单选项，用户改过后再多一项「恢复 AI 建议」。选择走 `ChooseWorkspaceItemLabelCommand`，再次选中 AI 自己的标签视为恢复而非覆盖。
- `AiClassificationWorkbench.MergeManualAssignments` 新增 `excludedItemKeys`：手动标签可覆盖 AI 建议（保留原 `ItemName`），排除项整体剔除；仍只接受配置内的标签。运行时校验（范围/标签）不需改动。
- 状态行：审阅期间每次改动把 `Status` 更新为 `ResultSummaryText`（「确认后将归入盒子 N/M 项（x 项待确认，y 项手动调整，z 项不归类）」）；应用后的汇报把「按你的选择保留在桌面」的数量从「未识别」里拆出来。

**测试：** Core 两条合并用例（未知标签/键被忽略；覆盖 + 排除），WinUI 新增 `AiWorkbenchItemViewModelTests`（5 条）、视图模型端到端审阅用例、`PrimaryActionStyleConverter` Theory、卡片胶囊与动作条布局断言。

### 图标搜索（2026-09-15）

- 左侧「桌面图标」标题行下方新增 `AutoSuggestBox`（放大镜图标，占位「搜索图标名称或分类」），每次击键经 `WorkspaceSearchBox_OnTextChanged` 写入 `WorkspaceFilter`。
- 网格改绑 `VisibleWorkspaceItems`：按名称或当前分类文本大小写不敏感子串匹配；`RefreshVisibleWorkspaceItems` 只在可见集合真正变化时才改集合，避免运行中逐项通知让网格反复重建。
- 勾选状态留在条目本身，过滤不改变已选；「全选」「清除」只作用于当前可见项，过滤因此可当批量勾选工具。摘要在过滤时显示「已选择 N 项 · 匹配 x/y 项」；无匹配时网格中央显示「没有匹配「…」的图标」。
