# 活动页核验记录（AP-01–09）

DEV-25：活动页设计审查（`EveryStage-活动页设计审查.md`，AP-01–09）的三态核验记录。
状态只有三种：**通过**（代码证据确立）、**未通过**（未做，或做了但不成立）、**未核**
（需要运行中的程序或真机硬件，静态读码无法确立）。

核验基线：main@b02c6015（PR #15 已合入）+ 开发清单第二批（DEV-03/04/05/15/16/19/20/21/22/23/24，
分支 `batch2-panel-ftb`）。行号是该基线下的行号，改动后以符号名为准。
**与实现同步更新**：实现改动涉及下表行为时，本表随同一批改动更新，不得只改代码不改表。

核验方法：只认静态代码证据。凡是需要点开程序、看真屏、接扩展屏或真机 Office 的，
一律记未核并写明原因，不声称已验证。

## AP-01–09 三态表

| 条目 | 状态 | 代码证据（文件：行 / 符号） | 备注 |
|---|---|---|---|
| AP-01 多选加入文件 | 通过 | `UI/Panels/ActivitiesPanel.Editing.cs:17,25`（`picker.SelectedFiles` 逐个加入、一次保存）；`UI/LibraryFilePickerDialog.cs:21`（`SelectedFiles` 按显示顺序快照） | 加入后选中最后一个；失败整批回滚（见审查完成记录） |
| AP-02 移除文件撤销 | 通过 | `UI/Panels/ActivitiesPanel.Editing.cs:228,231-238`（`OfferUndoFileRemove`，通知栏 10 秒撤销、原对象原位插回） | 未加确认框——撤销即替代确认，属拍板偏差，已记录 |
| AP-03 环节排序与跨环节移动 | 通过 | `UI/Panels/ActivitiesPanel.BatchEditing.cs:163`（「移动到环节」子菜单，打开时填充）；`ActivitiesPanel.Editing.cs:259-286`（同一对象移动、属性全保留）；`ActivitiesPanel.BatchEditing.cs:322-323`（环节上移/下移菜单项，按位置灰化） | 分两步均已落地 |
| AP-04 复制环节 | 通过 | `UI/Panels/ActivitiesPanel.Scenarios.cs:241`（「复制环节」，插在原环节后并选中）；`:107`（`CloneActivity` 深拷贝，`MediaFile.Clone` 生成新 Id） | 副本名「原名 副本」，重名叠序号 |
| AP-05 检查结果窗定位 | 通过 | `UI/Panels/ActivitiesPanel.PreShow.cs` 的 `ShowPreShowResult`（条目 `Tag` 挂 `PreShowFinding` 引用；单击选中对应行并滚动入可视区；双击定位并关窗；失效条目安静不动作） | 需真机点选确认手感，见下节未核项 |
| AP-06 搜索/过滤 | 通过 | `UI/Panels/ActivitiesPanel.cs:51-53`（`IsFiltering`）、`:187-195`（页头搜索框即时过滤）、`:217`（提示文案）；过滤态禁拖排/禁上下移/禁折叠切换（见 R10-3 一节） | 零命中空状态、Esc 清除搜索均已落地；环节头只显示命中后项数（拍板偏差） |
| AP-07 多选补全 | 通过 | `UI/Panels/ActivitiesPanel.cs:59`（Shift 锚点存 `MediaFile` 对象）、`:594`（Shift+Click 按树扁平序成段）、`:312`（「批量移除」）；批量移动到环节在 `ActivitiesPanel.BatchEditing.cs` | 批量移除一次保存、一条 Toast 撤销；批量移动选择跟随 |
| AP-08 编辑入口 | 通过 | `UI/Panels/ActivitiesPanel.cs:197`（页头「编辑 ▾」按钮）；`:279`（与右键共用同一菜单）；`:994-1005`（菜单键/Shift+F10 打开同一菜单；Delete 走 AP-02 撤销移除文件、选中环节时不动作；F2 重命名环节） | 键盘路径的实际手感需真机确认，见下节 |
| AP-09a 窄屏时长入第二行 | 通过 | `UI/Panels/ActivityRowView.cs:107`（`wide = width >= 700`）、`:415`（宽屏时长列）、`:492-499`（窄屏第二行复用同一 `DurationText`，成「类型 · 时长 · 过场」；背景音频不拼、问题提示优先） | 呈现层，窄屏实际观感见下节未核项 |
| AP-09b 环节总时长 | 未通过 | 未做，且核查后不成立：`UI/Panels/ActivityRowView.cs:520-526` 的 `DurationText` 对视频/音频只能写「播完」——`Data/MediaFile.cs` 不存媒体时长（只有停留/淡入秒数）；全仓唯一的时长查询是播放时对已打开解码源的 best-effort `TryGetDuration`（`ContentEngine/AudioContentController.cs:70`），绘制时无可同步取到的数据 | 只统计图片/文档停留秒数会成误导数字，故整条跳过；将来导入侧加时长探测落盘后可按原口径补 |

## DEV-03/04 后的再审（均为未核，真机/运行中程序才能确立）

DEV-03（危险属性保护）与 DEV-04（详情面板新增演示文稿字段、过场与进场音乐入口）改动了活动页
同一块区域，下列各项在静态读码后仍须真机确认，**不得以代码审查代替**：

- 未核：窄窗口下详情面板（含 DEV-03 保护条、DEV-04 新增组）的可用性——保护条与新增控件是否
  都能滚到、是否挤占编辑区。原因：布局在窄窗下的实际表现只能看真屏。
- 未核：Esc / F12 不被保护条或新增控件遮挡、延迟或截获。原因：焦点与按键路径需在运行中验证；
  代码上保护条不注册任何按键处理、不覆盖行区，但这只是必要条件。
- 未核：保护条不遮挡 on-air / next 行（保护条在详情面板内、行区之外）。原因：行区与详情面板的
  实际叠放需看真屏。
- 未核：DEV-03 解锁后、NEXT 项上屏时立即重锁并丢弃未提交编辑的实际表现。原因：涉及时序与焦点，
  静态读码只能确立重锁钩子（`OnFileStarted` / `OnStateChanged` / `MainWindow.OnProgramFileStarted`
  均调用 `RefreshDetailProtection`），不能确立真机表现。
- 未核：DEV-04 对话框与侧栏的保存值往返（改一处、另一处所见一致，隐藏字段的值不丢失）。
  原因：需在运行中打开对话框点确定再重开；代码证据（`DocumentSettingsDialog.Collect` 的空守卫、
  详情面板填充只读）见实现，不代替运行验证。
- 未核：AP-05 单击定位与 AP-08 键盘路径（菜单键/Shift+F10、Delete、F2）的真机手感。原因：同上。

## R10-3 筛选/搜索时禁拖排（代码核验，仍成立）

V1 发布闸门第 25 条（R10-3）“筛选/搜索时禁拖排”在本基线代码中仍然成立，核验于 2026-11-27：

- 行的 ⋮⋮ 拖拽唯一入口 `IActivityRowHost.MoveFile`（`UI/Panels/ActivitiesPanel.Editing.cs:283`，
  `IsFiltering` 守卫在 `:288`）在过滤时直接返回，拒绝一切拖拽排序。
- 菜单的上移/下移项在过滤时灰化：`UI/Panels/ActivitiesPanel.BatchEditing.cs:335-336`
  （`_moveUpButton` / `_moveDownButton` 的 `Enabled` 含 `!filtering`）；环节上移/下移同理（`:322-323`）。
- 折叠切换在过滤时冻结：`IActivityRowHost.ToggleCollapsed`（`UI/Panels/ActivitiesPanel.RunControls.cs:228`）
  在 `IsFiltering` 时返回，且过滤全程不写 `Activity.IsCollapsed`（`ActivitiesPanel.cs:738-739` 的
  全部展开/收起亦然），退出过滤后原折叠状态天然还原。
- 展开/收起按钮在过滤时同样冻结（`ActivitiesPanel.cs:738-739` 的守卫）。

以上为静态代码证据；拖拽手感本身（拖到一半松手、跨环节拖拽）需真机确认，不在本表声称之列。
