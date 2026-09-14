# EveryStage.Terminal — 终端机主程序框架（阶段1，第一部分）

对应 `docs/PLANNING.md` 第15章阶段1的第一项："终端机主程序框架（覆盖式窗口、投屏开关/断状态机、
数据持久化）"。**不包含**本地内容引擎（图片/视频/PDF/WPS）、设备发现配对、传输接收端、正式UI四大
面板与悬浮预览窗——这些是阶段1剩余部分与阶段3/4的工作。

当前是一个只有托盘图标的最小宿主：能开关"投屏开关"、能进出"待机中/扩展屏输出中"两个状态、有了状态
就会显示/隐藏绑定的扩展屏覆盖窗口并接管/归还系统音频。没有真正的内容渲染——覆盖窗口目前只是纯黑背景，
真正显示图片/视频/文档是本地内容引擎的工作（下一步）。

## 已实现

| 模块 | 对应 PLANNING.md | 说明 |
|---|---|---|
| `Data/` | §6 数据结构 | Scenario/Activity/MediaFile 模型 + JSON持久化（原子写，损坏时降级为空白方案而不是崩溃循环） |
| `Display/MonitorService.cs` | §5, §7 | 用 WinForms `Screen` 枚举显示器，选取非主屏作为"绑定扩展屏" |
| `Display/OverlayWindow.cs` | §5, §9.2 | 无边框置顶覆盖窗口，常驻创建、仅隐藏/显示，定期重申TOPMOST z-order |
| `StateMachine/OutputStateMachine.cs` | §9, §10 | 投屏开关与待机/输出状态两个独立维度；设备请求不受开关约束；"断"不改变开关状态 |
| `Audio/AudioTakeoverService.cs` | §9.3 | 媒体键暂停 + 静音兜底两段式，断开时对称恢复 |
| `Tray/TrayIconController.cs` | §10 | 托盘图标 + 开关菜单 + 退出（"关闭程序"的唯一入口） |

## 已知风险 / 待验证事项

同样地：本项目在 Linux 沙箱中编写，从未在 Windows 上编译过。相对 `src/Poc/ZeroCopyRenderDemo`，这里
用到的都是成熟、常见的 WinForms/Win32 API（`Screen`、`SetWindowPos`、`keybd_event`），风险明显更低，
但仍需在真实环境验证：

1. **NAudio.CoreAudioApi 的 `AudioSessionControl.GetProcessID`**（`Audio/AudioTakeoverService.cs`）
   ——不确定是属性还是方法、返回 `uint` 还是 `int`，需要对照实际安装的 NAudio 版本改一下调用形式，
   语义（拿到该会话所属进程PID）不变。
2. **投屏开关的默认状态**（`OutputStateMachine.CastSwitchOn` 默认 `true`）——PLANNING.md 未明确
   开机默认值，产品侧确认后可能需要改成默认关闭、或做成持久化配置项。
3. **单一扩展屏假设**：`MonitorService.GetBoundExtendedDisplay()` 只返回"第一个非主屏"，多扩展屏
   场景（如果产品后续要支持）需要扩展为"选定用而非取第一个"。
4. **NotifyIcon 图标**：目前用 `SystemIcons.Application` 占位，阶段5视觉设计落地前需要替换成产品图标。

## 尚未开始（阶段1剩余 + 后续阶段）

- 本地内容引擎：图片(WIC)、视频(Media Foundation，可复用 `ZeroCopyRenderDemo` 的解码/渲染管线)、
  PDF(PDFium)
- WPS COM互操作验证（PLANNING.md 标记为"风险仅次于阶段0"，需要尽早独立验证，不依赖本框架代码）
- 设备发现/配对、传输接收端（阶段2/3）
- 正式UI（四大面板 + 悬浮预览窗，阶段4）
- 三类日志系统（§14.4）——当前完全没有实现，`ScenarioRepository` 目前只是把损坏的旧文件重命名保留，
  不构成正式的"文件操作日志"
