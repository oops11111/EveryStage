# WPS COM Interop Spike（阶段1：WPS互操作验证）

对应 `docs/PLANNING.md` §14.1 与 §16 第9项："WPS COM互操作的静默模式实现与异常弹窗预案需要实测验证
（不同WPS版本行为可能不同）"。这是一个**一次性验证脚本，不是产品代码**——目的只是尽快拿到"WPS到底能
不能被自动化、能不能做到真正静默"的真实答案，好让 Content Engine 里 Office 文档渲染那部分的设计基于
事实而不是假设。

## 用法（仅限装有 WPS 的 Windows 机器）

```powershell
cd src/Poc/WpsComInteropSpike
dotnet run -- --app writer --file C:\path\to\test.docx
dotnet run -- --app spreadsheet --file C:\path\to\test.xlsx
dotnet run -- --app presentation --file C:\path\to\test.pptx
dotnet run -- --app writer --self-test-dir .\out
dotnet run -- --app spreadsheet --self-test-dir .\out
dotnet run -- --app presentation --self-test-dir .\out
```

跑完后**去任务管理器确认没有残留的 `wps*.exe` 进程**——控制台最后一行会提醒你这件事，这本身就是
一个需要验证的失败模式（COM automation 常见的"进程没退干净"问题）。

## 这个脚本回答的问题，以及怎么解读结果

| 现象 | 说明 |
|---|---|
| 正常打印到 "silent open + basic object-model access succeeded" | 基本可行，静默参数(`Visible=false`等)生效 |
| 某个 `TrySet` 报 failed | 说明 WPS 的这个自动化属性名称/行为和 Office 不完全一致，需要单独确认替代方案 |
| 进程卡住很久才报 COM 错误 | 大概率是弹出了不可见的模态对话框（更新提示/授权提醒），需要人工去看一眼弹窗内容，再决定怎么处理 |
| 跑完任务管理器还有残留进程 | `Quit()` 没生效或 COM 引用没放干净，这是"禁用热更新+无人值守"场景下必须解决的问题，不能上线 |

## 已知不确定项

1. **ProgID 猜测**：`Program.cs` 里 `ProgIdCandidates` 列的 `KWPS.Application` /
   `KET.Application` / `KWPP.Application`（以及各自的非K前缀备选）是从 WPS 官方自动化文档印象中
   转录的，未在真实安装上核实过，不同 WPS 版本/渠道（个人版/专业版/林格分发版）注册的 ProgID 可能不同。
   如果全部候选都不注册，用 `regedit` 查 `HKEY_CLASSES_ROOT` 里实际叫什么，把真名加进候选列表。
2. **`Documents.Open` / `Presentations.Open` / `Workbooks.Open` 的具名参数**（`ReadOnly:` /
   `WithWindow:` 等）：照抄 Office VBA 自动化的习惯写法，WPS 是否逐一支持同名参数未经验证。
3. **`ComputeStatistics(2)` 的枚举值**：`2` 是照 Word 的 `wdStatisticPages` 抄的，WPS 对应枚举值
   如果不同，这里会拿到错的统计类型而不是抛异常，结果需要人工核对（比如打印出来的页数是否和文档实际
   页数一致）。

## 这个验证之后要做什么

把跑出来的真实行为记录下来（哪个 ProgID 能用、哪些静默参数生效、有没有弹窗、进程退出是否干净），
回填到 `docs/PLANNING.md` §16 的待确认清单里，再决定 Content Engine 里 Office 文档那部分要不要按
这个方案继续做，或者需要绕开哪些已发现的坑（比如锁定某个已验证可用的 WPS 版本、预先关闭自动更新等，
§14.1 已经预见到这类代价）。
