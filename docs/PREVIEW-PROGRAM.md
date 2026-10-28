# Preview / Program 双通道播控架构

参照 Hirender、vMix、ProPresenter：所有媒体始终在 EveryStage 内部解码与预览，预监（Preview）与正式输出（Program）分离。
普通播放路径不再调用 Windows 默认程序。

## 1. 改造前的调用链（2026-09-27，分支基线 211e14e）

```
FilesPanel 双击 / "播放·预览" / 右键"循环/顺序播放" / 音频条"投屏"
  └─ FilePlayRequested(file)
       └─ MainWindow.OnFilePlayRequested
            ├─ _playback == null（无扩展屏）           → OpenLocalPreview → Process.Start(UseShellExecute)  ← 跳出到系统程序
            ├─ !CastSwitchOn（投屏开关关）             → OpenLocalPreview → Process.Start(UseShellExecute)  ← 跳出到系统程序
            └─ PlaybackEngine.RequestPlay(file)
                 └─ PlayFile
                      ├─ OutputStateMachine.RequestLocalFilePlayback()   ← 开关关闭则拒绝（PlaybackDeclinedByCastSwitch）
                      ├─ Image    → ImageContentRenderer → OverlayWindow.ContentSurface（扩展屏）
                      ├─ Document → PdfContentRenderer / WpsDocumentController（扩展屏）
                      ├─ Video    → VideoContentController → VideoSurface(OverlayWindow.VideoHost) → SwapChainPresenter（扩展屏）
                      └─ Audio    → AudioContentController（WASAPI）+ AudioVisualRenderer → OverlayWindow.ContentSurface

ActivitiesPanel 播放 → PlaybackEngine.RequestPlay(activity, index)（同上，只能上扩展屏）
SharedPreviewDock（"本地预览/播控窗口"）→ 每 500ms 读 PlaybackEngine.CurrentThumbnail —— 只是 Program 的缩略镜像，不是预监
Program.cs：只有绑定了扩展屏才创建 OverlayWindow / VideoSurface / PlaybackEngine；否则 _playback 为 null
```

问题：
1. 无扩展屏或投屏开关关闭时，唯一的"播放"是调系统默认程序。
2. PlaybackEngine 只有一个输出目标（扩展屏），"本地预览"没有自己的解码/呈现。
3. 投屏开关在 PlayFile 里直接拒绝播放，预览与输出耦合。
4. `VideoDecodeSource` 构造时强制取音频流类型，无音轨视频直接构造失败。
5. `VideoContentController` 依赖音频时钟定速、无跳转无音量；每帧只补一块音频（AAC≈21ms < 帧≈33ms），存在欠载风险。
6. 白名单含 `.webp`，但 GDI+（`Image.FromStream`）不带 WebP 解码器。
7. `SwapChainPresenter` 把视频拉伸铺满输出，不保持宽高比。

## 2. 目标模型

```
媒体文件 ─ 内部解码/加载 ─┬─ Preview 通道：PlaybackEngine(Preview) → PreviewSurface（主窗口内"本地预览/播控窗口"）
                          │      始终存在；不依赖扩展屏、不受投屏开关影响
                          └─ Take（投到屏幕）→ Program 通道：PlaybackEngine(Program) → OverlayWindow（扩展屏）
                                 仅在扩展屏存在且投屏开关开启时工作
```

* 两个通道是同一个 `PlaybackEngine` 类的两个实例，共享同一套状态模型（当前文件、所属活动、位置、暂停、循环、音量、完成动作、失败）。
  输出目标抽象为 `IPlaybackOutput`（`OverlayWindow` 与新的 `PreviewSurface` 各自实现）。
* 解码实例各自独立（Preview 与 Program 各一套 D3D11 设备、解码源、音频时钟）。这是需求第 8 条允许的过渡方案；
  后续优化项：Program 复用 Preview 已解码帧（共享纹理）以省去第二次解码。
* Take：Program 从 Preview 的当前文件/活动位置/播放位置接续播放；随后 Preview 暂停并保留画面与位置（可再次投出）。
* Program 直播期间 Preview 自动静音，防止预监声音混入现场扩声；Program 停止后恢复。
* 投屏开关只控制本地 Program；关闭时若本地内容正在输出则收回这一路投屏并恢复扩展屏原状态，Preview 不受影响。
* 设备来投与本地 Program 互斥：设备来投前停止本地 Program 输出，Preview 保留。
* "使用系统默认程序打开"只作为显式命令（文件右键菜单、错误卡片按钮）。

## 3. 状态

| 通道 | 状态 |
|---|---|
| Preview | PreviewIdle / PreviewLoading / PreviewPlaying / PreviewPaused / PreviewFailed |
| Program | ProgramDisconnected（无扩展屏）/ ProgramStandby / ProgramLive（来源：本地媒体 / 设备来投）/ ProgramFailed |


## 4. 实现（分支 feature/preview-program）

| 层 | 文件 | 要点 |
|---|---|---|
| 输出抽象 | `Display/IPlaybackOutput.cs`（新）、`Display/OverlayWindow.cs` | 引擎只依赖「内容画布 + 视频宿主 + 投递到 UI 线程」；扩展屏窗口实现它 |
| 预览画布 | `Display/PreviewSurface.cs`（新） | 主窗口内的预监器：内容横向铺满预览区（保持宽高比，纵向超出部分上下居中裁切，不足则上下留黑边）；视频默认「满宽」，可切换适应/填充；Office 文档显示内嵌封面；错误卡片覆盖层 |
| 引擎 | `Playback/PlaybackEngine.cs`、`Playback/PlaybackState.cs`（新） | 同一类的两个实例（Preview/Program）；统一状态 Idle/Loading/Playing/Paused/Failed、位置/时长/跳转/循环/音量/静音/完成；Take 快照；统一失败处理与错误卡片数据；投屏开关只拦 Program |
| 输出状态 | `StateMachine/OutputStateMachine.cs` | 新增当前输出源（本地媒体/设备来投）；投屏开关只约束本地媒体，不管理网络内投屏机请求 |
| 视频 | `ContentEngine/VideoContentController.cs`、`Rendering/Decode/VideoDecodeSource.cs`、`Rendering/SwapChainPresenter.cs`、`Display/VideoSurface.cs` | 音轨可选（无音轨按墙钟定速）；音频保持 250ms 预缓冲（原每帧只补一块 AAC 会欠载）；暂停/恢复/跳转（解码到目标点）/音量/静音/淡入淡出；视频适应/填充/拉伸；分阶段错误 + 编码信息 |
| 音频 | `ContentEngine/AudioContentController.cs`、`Rendering/Decode/AudioDecodeSource.cs`、`ContentEngine/PcmTrim.cs`（新） | 时长与跳转改用强类型 MF 调用（原 `dynamic` 猜测的签名在 Vortice 3.6.2 上不存在，跳转与时长一直静默失效）；解码到目标点；静音；暂停中跳转不再竞态；**完成事件改为缓冲播完后才发出（原来提前约 1 秒，顺序播放会切掉每首最后一秒）** |
| 图片 | `ContentEngine/ImageContentRenderer.cs`、`Data/FileLibraryStore.cs` | GIF/多页 TIFF 标注「仅显示首帧/首页」；WebP 移出导入白名单（GDI+ 无 WebP 解码器） |
| UI | `UI/SharedPreviewDock.cs`、`UI/MainWindow.cs`、`UI/Panels/FilesPanel.cs`、`UI/Panels/AudioPlayerBar.cs`、`UI/ModernUi.cs` | 预览坞成为 Preview 监视器 + 传输条（上一项/播放暂停/停止/下一项/时间码/进度/音量/循环/适应填充/投到屏幕/停止输出）；信号源卡片显示当前输出源；双击=内部预览；右键菜单：在本地预览/投到屏幕/循环/顺序/停止预览/停止输出/加入活动/从文件库移除/使用系统默认程序打开；音频条播放走内部 Preview；禁用按钮视觉淡化 |
| 显式外部打开 | `UI/ExternalOpener.cs`（新） | 全仓唯一调用系统默认程序的地方，仅由右键菜单与错误卡片的显式按钮进入；`OpenLocalPreview` 已删除 |
| 测试 | `Tests/EveryStage.WindowsSelfTests/PreviewProgramTests.cs`、`MediaFixtures.cs`（新） | 用 Windows 自带编码器现场生成样本（无 FFmpeg、无二进制入库），静音运行、Program 窗口不显示 |

交互约定：单击=选中；双击/「在本地预览」=在 Preview 播放；「投到屏幕」=Preview 当前内容（文件、活动位置、播放位置）接续到 Program，随后 Preview 暂停保留；「停止预览」只停 Preview；「停止输出」只停 Program，Preview 保留内容与位置；Program 在播时 Preview 自动静音；设备来投前停止本地 Program，Preview 不清空。

### 设备投屏：预览 / 投屏 / 回退（2026-10-05）

设备来投不再直接抢占 Program。已配对设备发起投屏后，终端先在主窗口「信号源」区域展开设备源卡片，卡片的视频区直接显示设备预览画面，右下角浮动「投屏」按钮；预览阶段音频静音，Program 原内容保持不变。

点击「投屏」后，终端捕获 Program 回退点（当前文件、活动位置、PDF/PPT 页、播放位置和暂停状态；若 Program 原本空闲则记为空闲），停止本地 Program，设备流同时呈现到扩展屏并取消静音。设备卡片按钮切为「回退」，状态显示「大屏输出中」。

点击「回退」、设备断开、接收 10 秒无包或解码/呈现坏死时，如果设备正在 Program 上，终端先 `DetachProgram()` 并重新静音设备预览，再恢复回退点：有快照则 `PlaybackEngine.RequestPlay(snapshot)`，必要时补 `Pause()`；原本空闲则断开输出。设备仍连接时会继续留在设备源窗口预览。

本地「投到屏幕」、停止输出或扩展屏断开期间，如果设备画面正在 Program 上，只把设备流退回设备源窗口并丢弃回退点，不主动断开设备。投屏开关不管理网络内投屏机请求：关闭期间投屏机仍可请求进入设备源窗口；但开关关闭会收回已经上大屏的设备画面，并按回退点恢复设备投屏前的大屏内容或空闲状态。第二台设备请求投屏时，终端先回签名 `Pending=true`，弹出操作屏确认框；接受后断开旧设备并让新设备进入设备源窗口，仍需操作员按「投屏」才上大屏。

实现上，设备投屏编排集中在 `DeviceCastController`。控制器只依赖 `IDeviceCastStream` 和 `IDeviceCastTransport`：生产路径用 `CastReceiverStream` 适配真实 `CastReceiver`，并由 `DiscoveryService` 负责状态、NACK、ping 和 CastStatus；测试路径使用 fake stream/transport，不占用发现端口即可验证预览、上屏、投屏开关不管理网络投屏机请求但会撤回已上大屏的设备流、CastStatus `OnProgram` / 终端 `DeviceId`、回退、同设备重连、本地 Take 接管、第二台设备 Pending/拒绝/接受、断开、10 秒无包和解码/呈现坏死状态转换。

## 5. 测试结果（Windows 10 Enterprise 19045，单显示器 1280×1024，Debug 与 Release 均通过）

全部 6 个项目 Debug/Release 构建 0 警告 0 错误；CoreSelfTests 全通过；WindowsSelfTests 全通过，含：

- 无扩展屏内部预览：图片、带/不带音轨视频、音频（格式矩阵见下）
- 预览内容横向铺满且保持宽高比：2:1 图片上下留黑边；1:2 图片横向铺满、上下裁切；视频满宽矩形计算（宽画面、竖画面）
- 带音轨 MP4 音画同步：90/90 帧呈现、0 丢帧，3.0 秒片段用时 3.06–3.08 秒
- 无音轨 MP4 正常播放（墙钟定速）
- 视频、音频：暂停/恢复/播放中跳转/暂停中跳转/循环
- 音频完成在尾部播完之后（3.0 秒文件 3.10 秒完成）
- 损坏/缺失文件：内部错误详情（阶段、扩展名、建议），不启动外部程序，下一个文件照常播放
- 投屏开关关闭：只拦/收回本地 Program，恢复扩展屏原状态；Preview 照常播放
- 投到屏幕：Program 从 Preview 当前位置接续；停止 Program 后 Preview 内容与位置不变
- 设备来投与本地 Program 互斥；投屏开关不管理网络投屏机请求，但关闭时会把已上大屏的设备流退回静音预览，并恢复设备投屏前的大屏状态；Preview 始终不被清空
- `DeviceCastController`：设备连接先留在设备源窗口预览且静音；按「投屏」才接入 Program 并取消静音；「回退」、设备断开、10 秒无包和解码/呈现坏死都会恢复投屏前内容并释放异常会话；同设备重连不弹确认、不闪回本地内容，直接替换流并保留原回退点；本地 Take 接管时设备退回静音预览且不断开；第二台设备先回 Pending，拒绝不影响当前设备，接受会恢复当前设备投屏前内容并让新设备只进预览；投屏前 Program 空闲时回退仍为空闲
- CastStatus：设备只在预览窗口时 `OnProgram=false`，上大屏后 `OnProgram=true`，`DeviceId` 使用终端自身身份；Caster 据此显示「设备窗口预览中 / 大屏显示中」
- `CastReceiver` shared-GPU 冒烟：两个 `VideoSurface` 共用同一个 `D3D11Device`，无帧时 `AttachProgram` / `DetachProgram` 应立即返回；视频解码器延迟到 `Start()` 初始化，因此测试可在无 H.264 硬件 MFT 的机器上实际构造接收器并验证无帧互斥
- 「使用系统默认程序打开」只在显式调用时执行
- 视频适应/填充/拉伸矩形计算

格式矩阵（白名单每项均有结果）：

| 格式 | 结果 |
|---|---|
| .jpg .jpeg .png .bmp | 通过 |
| .gif | 通过（动图仅显示首帧，UI 标注） |
| .tif .tiff | 通过（多页仅显示首页，UI 标注） |
| .mp4 | 通过（H.264+AAC；无音轨） |
| .m4v .mov | 通过（H.264+AAC；.mov 为 ISO BMFF 封装样本） |
| .wmv | 通过（WMV9+WMA） |
| .mp3 .m4a .aac .wma .flac .wav | 通过 |
| .mkv .avi | **未验证**：Windows 自带组件无法生成样本，需真实文件真机验证；失败时显示内部错误卡片 |
| .webp | 已移出白名单 |

### CI 覆盖范围

GitHub 托管 Windows 机器没有 GPU 视频处理能力和音频输出设备。自检会先探测这两项：缺少的能力对应的格式与场景标为 SKIP 并注明原因，其余照常执行（图片、错误卡片、投屏开关、投到屏幕、停止输出、设备来投互斥、显式外部打开；Program 相关用例改用图片投送）。**视频/音频播放本身只能在有显卡和声卡的机器上验证**——修改播放代码后须在本地运行 WindowsSelfTests。可用环境变量 `ES_SELFTEST_NO_VIDEO=1` / `ES_SELFTEST_NO_AUDIO=1` 在本地模拟 CI 环境。

模拟 CI 时发现并修复：无可用显卡时双击视频，异常会越过错误卡片直接抛到 UI 线程（`PlayFile` 在 `try` 之外访问了视频控制器）。

## 6. 残余风险与后续

1. Preview 与 Program 各自解码（两套 D3D11 设备/解码器/音频时钟）；后续可让 Program 复用 Preview 已解码帧。
2. 未做真机双显示器验证（本机单显示器）：扩展屏实际输出、中途拔出扩展屏、Windows 11 均未实测；Program 通道的正确性目前由不可见窗口的自动测试覆盖。
3. .mkv/.avi 未验证（见上）；HEVC 等依赖系统扩展的编码在未安装扩展的机器上会显示错误卡片。
4. ~~Preview 中的 Office 文档只显示内嵌封面卡片~~ 已解决（2026-10-03）：PDF / Word / PPT 都在 Preview 与 Program 中真实呈现，见 [DOCUMENT-PLAYBACK.md](DOCUMENT-PLAYBACK.md)。
5. WASAPI 暂停后报告位置约 50ms 后才稳定，时间码会一次性跳动约 0.05 秒。
6. 活动页播放现在进入 Preview；上 Program 需点「投到屏幕」（快照包含活动与位置，Program 接着按活动顺序推进）。旧的悬浮预览窗仍跟随 Program 引擎。
