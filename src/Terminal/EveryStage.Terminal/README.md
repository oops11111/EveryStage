# EveryStage.Terminal — 终端机主程序框架 + 本地内容引擎 + 设备发现配对/投屏接收 + 部分主界面

对应 `docs/PLANNING.md` 第15章阶段1的前两项："终端机主程序框架（覆盖式窗口、投屏开关/断状态机、
数据持久化）"+"本地内容引擎（图片/视频/PDF）"，阶段3的"设备发现/配对"部分（§7），阶段2"传输接收端"
（真正接收Caster的RTP/H.264流并解码显示，见下方 `Receiving/`），以及阶段4正式四大面板主界面（§8.2）
的全部四个面板：**文件**、**活动**、**设备**、**设置**。**不包含**WPS正式集成（有独立验证脚本，
见下）、正式视觉设计（阶段5）。

现在有一个真正意义上的操作界面（`UI/MainWindow.cs`）：左侧固定导航（投屏开关、断、文件/活动/设备/
设置四个入口、状态指示）+ 右侧内容区。文件面板能导入/拖拽文件、按类型筛选、双击播放——这是
`PlaybackEngine.RequestPlay(MediaFile)` 第一次有真实调用方。活动面板能新建/另存为/删除方案，
新建/重命名/删除活动，从文件库添加/移除文件到活动、上移下移排序，双击活动或文件按
`PlaybackEngine.RequestPlay(Activity, int)` 播放——这是它第一次有真实调用方。设备面板能看已配对
设备列表并移除配对。设置面板这一轮也做完了（`UI/Panels/SettingsPanel.cs`，见下方"已实现"表格）——
PLANNING.md §8.2只给了"通用/显示/播放行为/网络与设备/关于"五个分类的名字，没有规定分类里具体有
什么字段，这里选的字段都是本仓库其他地方已经标注过"随手定的、没有依据"的真实配置项（投屏开关默认
状态、扩展屏选择、默认停留时长、设备名称），而不是凭空发明的占位字段。覆盖窗口 + 悬浮预览窗 +
配对确认弹窗这些阶段1/3的产出维持不变。

## 已实现

| 模块 | 对应 PLANNING.md | 说明 |
|---|---|---|
| `Data/` | §6 数据结构 | Scenario/Activity/MediaFile 模型 + JSON持久化（原子写，损坏时降级为空白方案而不是崩溃循环）；`FileLibraryStore` 是这次新加的——独立于任何活动之外的"文件库"，§6原模型没有覆盖，文件面板需要它 |
| `Display/MonitorService.cs` | §5, §7 | 用 WinForms `Screen` 枚举显示器，选取非主屏作为"绑定扩展屏" |
| `Display/OverlayWindow.cs` | §5, §9.2 | 无边框置顶覆盖窗口，常驻创建、仅隐藏/显示，定期重申TOPMOST z-order |
| `Display/VideoSurface.cs` | §9.2 | 绑定`OverlayWindow.VideoHost`HWND的唯一D3D11设备+交换链，`VideoContentController`(本地视频)和`Receiving/CastReceiver`(设备投屏)共用，避免两者各建一个绑到同一HWND |
| `StateMachine/OutputStateMachine.cs` | §9, §10 | 投屏开关与待机/输出状态两个独立维度；设备请求不受开关约束；"断"不改变开关状态 |
| `Audio/AudioTakeoverService.cs` | §9.3 | 媒体键暂停 + 静音兜底两段式，断开时对称恢复 |
| `Tray/TrayIconController.cs` | §10 | 托盘图标 + 开关菜单 + 退出（"关闭程序"的唯一入口） |
| `ContentEngine/` | §3 | 图片(GDI+，WIC编解码器) + PDF(PdfiumViewer) 渲染器、画面呈现控件（等比缩放、黑边）、视频播放控制器(`VideoContentController`，复用 `EveryStage.Rendering` 的D3D11零拷贝管线，通过共享的 `Display/VideoSurface` 呈现到 `OverlayWindow.VideoHost`) |
| `Playback/PlaybackEngine.cs` | §6, §9 | 把上面三种渲染器接到 Scenario/Activity/MediaFile 数据模型和投屏开关/断状态机上："点文件"→(开关判断)→选渲染器播放→按停留时长/完成动作(NextItem/Loop/HoldOnLastFrame)推进；提供悬浮预览窗按钮要用的手动上一项/下一项 |
| `Logging/` | §14.4 | 三类物理独立的按天滚动日志：`FileOperationLogger`(文件操作)、`PlaybackLogger`(播放/投屏记录，已接入`PlaybackEngine`)、`DeviceConnectionLogger`(设备连接，已接入`DiscoveryService`)；JSON-lines格式 + 自动清理过期文件 |
| `Devices/` | §7 | 设备发现(UDP广播 `DiscoveryService`)、配对(信任/手动确认、被投放/被监看权限分离)、配对设备列表持久化(`PairedDeviceStore`)。设备指纹(`DeviceIdentity`)与协议格式(`DiscoveryProtocol`)现在都在 `src/Shared/EveryStage.Discovery/`，因为 `src/Caster/EveryStage.Caster/` 也要用同一套。`DiscoveryService` 现在还处理 `CastStartMessage`/`CastStopMessage`（只信任 `AllowCast` 的已配对设备），驱动下面的 `Receiving/`；新增 `SendCastStatusAsync`，配合 `Program.cs` 里每秒一次的 `SendCastStatus()` 把接收状态报回给正在投屏的Caster（`DiscoveryProtocol.CastStatusMessage`，见该README"已知风险"新增小节） |
| `Receiving/` | 阶段2"传输接收端" | `H264HardwareDecoder` 直接驱动一个（假设是同步的）H.264解码器MFT，把推入的Annex-B访问单元解码成D3D11 NV12纹理；`CastReceiver` 把 `RtpReceiver`(EveryStage.Transport)接收到的NAL单元用RTP marker位重新拼回Annex-B访问单元喂给解码器，再通过共享的 `Display/VideoSurface` 呈现到 `OverlayWindow.VideoHost`（不再自建独立的D3D11设备/交换链，见该类README条目）——这是这个仓库第一次让 Caster 和 Terminal 真的通过网络传视频（而不是各自的自检）。`CastReceiver`现在还有音频侧：`RawRtpReceiver`收PCM，喂给`EveryStage.Rendering.Audio.AudioPlaybackClock`播放，构造失败会独立降级成纯视频（不影响视频侧）；新增`LastPacketReceivedAt`，配合`Program.cs`的`CheckCastLiveness()`在Caster连续10秒无数据包时自动断开 |
| `UI/FloatingPreviewWindow.cs` | §8.3 | 悬浮预览窗：LIVE标识、缩略图(仅图片/PDF，视频暂无)、文件名、上一项/暂停/下一项/断 四个按钮、置顶开关；拖动位置靠"常驻同一个Form实例、只隐藏不销毁"天然记住 |
| `UI/PairingConfirmationDialog.cs` | §7 | 配对请求的弹窗确认（接受/拒绝 + 被投放/被监看/信任三个独立勾选项）；不含PIN码交换，`DiscoveryProtocol`目前没有PIN字段 |
| `UI/MainWindow.cs` | §8.1 | 主界面外壳：左侧导航(投屏开关/断/四个面板入口/状态) + 右侧内容区；关闭窗口只隐藏不退出进程（终端机要常驻），托盘菜单"打开主界面"或双击托盘图标可以召回 |
| `UI/Panels/FilesPanel.cs` | §8.2 | 文件面板：`ListView`缩略图网格 + 类型筛选(全部/图片/视频/文档/音频) + 导入对话框 + 从资源管理器拖拽导入 + 移除(二次确认) + 双击播放(`PlaybackEngine.RequestPlay`)，导入/移除都接入`FileOperationLogger` |
| `UI/Panels/DevicesPanel.cs` | §8.2 | 设备面板：已配对设备列表(信任状态/被投放/被监看/配对时间) + 移除配对 |
| `UI/Panels/ActivitiesPanel.cs` | §8.2 | 活动面板：方案选择器(切换/新建/另存为/删除) + `TreeView`活动/文件层级(可折叠) + 新建/重命名/删除活动 + 从文件库添加/移除文件 + 上移/下移排序 + 输出状态条；双击播放，接入`FileOperationLogger`记录方案/活动的增删改 |
| `UI/TextInputDialog.cs`, `UI/LibraryFilePickerDialog.cs` | — | 活动面板用到的两个小弹窗：单行文本输入(方案/活动命名)、从文件库选一个文件 |
| `UI/Panels/SettingsPanel.cs` | §8.2 | 设置面板：`TabControl`五个分类(通用/显示/播放行为/网络与设备/关于)；`Data/AppSettings.cs`+`Data/SettingsStore.cs`是这次新加的数据模型和JSON持久化(同样是atomic write) |

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
5. **`PdfiumViewer` 包名/版本与其 `Render()` 重载签名**（`ContentEngine/PdfContentRenderer.cs`）
   ——未对照真实 NuGet 源核实，且该库依赖单独的原生 pdfium.dll 包（架构需匹配 x64）。
6. **图片解码用 GDI+ 而非直接调用 WIC COM 接口**：满足常见格式（JPEG/PNG/BMP/GIF/TIFF）没问题，
   但不支持 WIC 能处理的部分高位深/HDR 格式——如果产品需要展示这类图片，需要换成直接的 WIC interop。
7. **`VideoContentController` 的 D3D11/Media Foundation 部分**：继承自 `EveryStage.Rendering`，
   已知风险清单见该库的 README.md（NuGet版本、GUID字面量、COM重载签名等），这里不重复列。这个类
   自己新增的部分——独立播放线程的启动/取消/Join、`_presenterLock` 保护并发 Present/Resize——
   逻辑上是新代码，同样没有在真实环境跑过。
8. **`PlaybackEngine` 假设所有公开方法都在 UI 线程调用**：目前成立是因为
   `ImageContentRenderer`/`PdfContentRenderer` 的 `LoadAsync` 恰好同步完成，`await` 之后天然还在
   UI 线程的 `SynchronizationContext` 上——一旦这两个 `LoadAsync` 改成真正异步的文件I/O，这个假设
   需要重新审视（`ContentSurface.SetFrame`/`System.Windows.Forms.Timer` 都要求UI线程）。
9. **PlaybackEngine 里没有定义的产品行为**（照 PLANNING.md 现状，这些确实没写清楚，不是漏做）：
   一个活动的文件列表播完最后一项后 `NextItem` 该不该自动跳到下一个活动；投屏开关关闭时"仅本地预览"
   应该渲染到哪个界面——文件面板现在存在了，双击文件仍然会正确遵守开关状态(`RequestLocalFilePlayback`
   返回`false`就不播)，但关闭状态下双击目前是彻底无反馈的静默无操作，用户会以为点击没生效，这本身
   也是需要在真正实现"本地预览"之前先解决的可用性问题；音频类文件(`MediaKind.Audio`)的播放目前完全
   没接（§6"音频特殊性"的背景音轨叠加规则本身在文档§16第3项里也还标着"待细化"）。
10. **`Logging/`**：`DailyRollingLogWriter` 用反射把匿名对象的属性摊平进日志行，日志量在这个阶段
    很小，没考虑过性能。`VideoContentController` 后台播放线程里的解码异常现在会被捕获并通过新增的
    `PlaybackFailed` 事件上报给 `PlaybackEngine`（记入 `LogAbnormalInterruption`），但恢复行为
    （重试/跳到下一项/停留）PLANNING.md 没有定义，目前是原地不动，不代表这是正确的产品行为。
11. **`DiscoveryProtocol` 已经搬到 `src/Shared/EveryStage.Discovery/`**（`DeviceIdentity` 一起搬了），
    因为 `src/Caster/EveryStage.Caster/` 现在也需要说同一种协议——这仍然是本仓库自己拍的草案，只是
    从"另一端根本不存在"变成了"另一端存在了，但两边都还没在真实网络上互相验证过"，风险性质变了但
    没有消失。`DiscoveryProtocol.Port = 47990` 仍然是随手选的端口号，没检查过是否与常见软件冲突。
12. **`DiscoveryService` 的具体 API 细节**：`UdpClient.ReceiveAsync(CancellationToken)` 重载、
    `UdpClient.SendAsync(byte[], int, IPEndPoint)` 重载都是 .NET 6+ 的标准API，比D3D11/MF那套风险
    低很多，但仍未在真实网络环境跑过——尤其是"同一局域网多网卡/多网段时广播地址怎么选"完全没处理，
    当前用的是全局 `IPAddress.Broadcast`（255.255.255.255），部分路由器/网络配置下可能收不到。
13. **`PlaybackEngine.Pause()` 只对图片/PDF真实有效**：靠冻结停留时长计时器实现，视频调用它是文档化
    的空操作——`VideoContentController` 没有"原地暂停/从暂停位置继续"的能力（`Stop()`是整体拆除解码
    源），伪造一个会重头播放的"暂停"按钮比明确不支持更糟，所以悬浮预览窗的暂停按钮在播放视频时会被
    禁用（`FloatingPreviewWindow.RefreshFromEngine` 里 `_pauseButton.Enabled` 那行）。
14. **`Program.cs` 显式安装 `WindowsFormsSynchronizationContext`**：这是为了保证 `DiscoveryService`
    后台线程触发配对弹窗时一定有地方 `Post` 回UI线程，不依赖"WinForms会在第一个Control构造时自动装
    好同步上下文"这种隐式时机（尤其是没有扩展屏、`OverlayWindow`都不会被创建的情况下）。这个写法本身
    风险不高，但同样没有在真实环境验证过。
15. **`FloatingPreviewWindow` 的缩略图直接引用 `PlaybackEngine.CurrentThumbnail` 返回的 `Bitmap`**：
    没有做拷贝。`ImageContentRenderer`/`PdfContentRenderer` 切换文件时会 `Dispose()` 旧的 `Bitmap`
    再赋新的——只要两者都在UI线程上跑（现有假设，见上面第8条），`PictureBox.Image` 引用切换和旧图
    释放不会真的并发，但如果以后 `LoadAsync` 变成真异步就需要重新检查这条。
16. **配对确认弹窗只做了"接受/拒绝"，没有PIN码**：PLANNING.md §7 原话是"弹窗/PIN码"（二选一的口吻），
    这里只实现了弹窗那一半——`DiscoveryProtocol` 的 `PairRequestMessage` 也没有PIN字段，要加PIN需要
    先扩展协议本身，而协议本身还是草案（见上面第11条），不适合在弹窗UI里单方面加。
17. **`FilesPanel` 的视频/文档/音频缩略图都是同一个占位图标**（`SystemIcons.Application`），不是真的
    解码出来的预览画面——视频需要解一帧、PDF需要渲染首页、音频没有画面概念，这些都不是"标准WinForms
    风险"而是明确没做的功能，等真正做缩略图时优先级最高的应该是视频（用户最容易靠缩略图分辨内容）。
18. **`FileLibraryStore.InferKind` 是一个写死的扩展名列表**，不认识的扩展名会被拒绝导入而不是猜测——
    这是有意的（宁可拒绝也不要把 `.heic` 之类的当成`Image`结果渲染器加载失败），但意味着列表本身需要
    随着产品实际支持的格式范围维护，目前只覆盖了最常见的几种。
19. **`MainWindow` 关闭窗口只隐藏、`Dispose()` 才真正释放**：这个"隐藏而不是销毁"的模式在本仓库里已经
    用过好几次(`OverlayWindow`、`FloatingPreviewWindow`)，`MainWindow`延续同样的做法是一致的，但
    `FormClosing` 里判断 `CloseReason != ApplicationExitCall` 才拦截关闭这一行为，具体触发时机
    （尤其是 Windows 关机/注销时系统会用什么 `CloseReason` 广播关闭）没有在真实环境验证过。
20. **`FilesPanel` 用标准 `ListView`(`View.LargeIcon`) + `ImageList` + `AllowDrop`/`DragEnter`/
    `DragDrop`**：这些是非常成熟、低风险的WinForms API（比D3D11/MF那一类风险低得多），但同样没有
    在真实Windows环境跑过，第一次使用时仍然值得跑一遍：导入对话框多选、拖拽多文件、切换分类筛选、
    双击播放各种文件类型。
21. **`ActivitiesPanel` 没有实现拖拽排序**：PLANNING.md §8.2 原话是"活动列表...拖拽排序"，这里换成了
    "上移/下移"按钮达到同样的最终效果——在`TreeView`上正确实现拖拽（区分拖到条目上=追加、拖到间隙=
    新建，§11那套语义）比按钮复杂得多，第一次搭这层UI时选择了功能对等但风险更低的方案，不是疏漏。
22. **"另存为"是深拷贝，"添加文件到活动"也是拷贝而非引用**：`ActivitiesPanel.CloneActivity`/
    `CloneFile` 复制了 `MediaFile` 的所有字段（含一个新 `Guid`）——这是有意的：如果两个活动共享同一个
    `MediaFile` 实例，编辑一处的停留时长会意外改到另一处。跟 `MediaFile.cs` 类文档注释里"同一物理
    文件出现在两个活动里 = 两个独立MediaFile"的既有设计是一致的，不是这次新引入的假设。
23. **`ActivitiesPanel`/`MainWindow` 直接读写 `Scenario.Activities`/`Activity.Files` 这些公开可变
    集合，每次操作后手动调用 `_repository.Save(_store)`**：没有事务/撤销机制——如果两个地方"同时"
    改（目前只有这一个UI入口，不会真的并发，但结构上没有防护），后保存的会覆盖先保存的。
24. **`MainWindow.Dispose()` 需要显式 `Dispose()` 四个面板**：`ShowPanel()` 用
    `_contentHost.Controls.Clear()` 切换当前显示的面板，`Clear()` 只是把不显示的面板从父子关系里
    摘掉、不会自动`Dispose()`它们——写代码时已经发现并修了这个点（否则`ActivitiesPanel`/`FilesPanel`
    对`PlaybackEngine`/`OutputStateMachine`的事件订阅会永远不被取消），但这类"容器控件切换导致遗漏
    Dispose"的模式，以后如果加更多面板需要留意同样的坑。

### `Receiving/` — 投屏接收/解码，这次新加的部分

25. **【已修复】本地播放与设备投屏互抢`VideoHost`HWND**：上一轮`CastReceiver`和
    `VideoContentController`各自独立创建`D3D11Device`+`SwapChainPresenter`，都绑定到同一个
    `OverlayWindow.VideoHost`句柄——如果本地正在播放视频时来了一个设备投屏请求，两个独立的swap
    chain会同时存在，行为未定义。这一轮改为共享：新增 `Display/VideoSurface.cs` 持有*唯一*的
    `D3D11Device`+`SwapChainPresenter`，由 `TerminalApplicationContext` 创建一次并分别传给
    `PlaybackEngine`(转给`VideoContentController`)和每一次新建的`CastReceiver`。光是共享同一个
    presenter对象还不够——`SwapChainPresenter`自己并不是为并发调用设计的(`EnsureProcessor`会修改
    `_processor`/`_enumerator`/`_outputView`字段)，所以真正的互斥还是要靠调用方保证同一时刻只有
    一边在跑：`PlaybackEngine`新增`StopForDeviceCast()`(停止本地视频解码但不经过
    `OutputStateMachine`，因为PLANNING.md §9.1认为设备投屏请求应该视为继续输出而非回到待机)和
    `LocalPlaybackStarting`事件(本地播放要开始前通知外部先停掉正在跑的设备投屏)，
    `TerminalApplicationContext`在`OnCastStartRequested`里调用前者、订阅后者调用
    `_castReceiver?.Dispose()`——两个方向都补上了。**残留风险**：`CastReceiver`的构造函数原本用
    投屏画面的分辨率(来自`CastStartMessage.Width/Height`)去创建swap chain本身，这其实一直是个更
    早就存在的独立bug(swap chain应该按`VideoHost`的实际客户区尺寸创建，视频处理器的
    `VideoProcessorBlt`本来就会做缩放)——这次重构顺带修掉了它，因为`VideoSurface`现在只用
    `_overlay.VideoHost.ClientSize`创建一次，`width`/`height`参数现在只用于`H264HardwareDecoder`
    协商输入类型，不再影响swap chain尺寸。另外，"断"(`OutputStateMachine.Disconnect()`)现在也会
    顺带停掉活跃的`_castReceiver`(`TerminalApplicationContext.OnOutputStateChanged`的Idle分支)——
    这同样是自我审查时才发现的:之前"断"只停本地播放，设备投屏会在后台无限期继续接收/解码/呈现到一个
    已经隐藏的覆盖窗口。以上所有改动都没有真正在Windows机器上验证过，只是消除了"两个独立swap
    chain绑定同一HWND"这个结构性问题本身。
26. **`H264HardwareDecoder` 假设目标H.264解码器MFT是同步的**（该类doc comment里详细写了这个假设
    的依据和风险）——如果真机上遇到的是异步解码器MFT，这个类的整个控制流（没有事件循环、没有后台
    线程）都是错的，需要按 `H264HardwareEncoder` 的异步模式重写。
27. **`H264HardwareDecoder.ConfigureNv12OutputType` 用 `GetOutputAvailableType` 循环查找NV12
    类型，用广义 `catch (Exception)` 判断"枚举完了"**：真实失败信号应该是HRESULT
    `MF_E_NO_MORE_TYPES`，但这个仓库无法核实Vortice对失败HRESULT具体抛出什么异常类型（甚至是否
    抛异常而不是返回值），所以用了broad catch——如果解码器MFT真的支持NV12输出但顺序靠后、
    异常发生在还没枚举到它之前的某次调用，这个循环会误判为"没有NV12输出"而提前失败。
28. **`IMFMediaType.Get<Guid>(MF_MT_SUBTYPE)` 的类型参数未经验证**：`VideoDecodeSource` 已经验证
    过（至少是"跟着抄"过）`Get<uint>`/`Get<ulong>`的用法，这里外推到 `Get<Guid>`，是否是同一个
    泛型方法支持的类型参数完全没有把握。
29. **`MFT_CATEGORY_VIDEO_DECODER` GUID 是凭记忆重构的**（`DecoderGuids.cs` 里已经标注），置信度
    低于本文件其他从 `WellKnownGuids`/`EncoderGuids` 抄来的字面量——如果 `MFTEnumEx` 找不到任何
    解码器MFT，这个GUID是第一嫌疑对象。
30. **`CastReceiver` 完全没有处理丢包/乱序**：直接依赖 `RtpReceiver`/`H264RtpDepacketizer` 已有的
    行为（丢包会导致该NAL被丢弃，不会拼出损坏帧，但也不会恢复，见 `EveryStage.Transport` 的
    README）——在真实局域网上（不像 `TransportSelfTest` 的本机回环）确实可能丢包，`BuildAnnexBAccessUnit`
    会因此偶尔拼出"缺了一个或几个NAL"的访问单元喂给解码器；解码器大概率能容忍这种情况（跳过/输出
    带伪影的一帧），但没有实测过会不会直接报错整个会话崩掉。
31. **【已实现，原为已知缺口】现在有超时自动断开了**：`CastReceiver` 新增
    `LastPacketReceivedAt`（视频NAL单元和音频payload收到时都会更新），`Program.cs` 的
    `CheckCastLiveness()` 跟着状态回报同一个1秒定时器一起跑，超过10秒没收到任何视频或音频数据包
    就视为"Caster已经消失"（崩溃/断网/被强制结束，没来得及发`cast_stop`），自动调用`StopCasting()`
    +`_stateMachine.Disconnect()`回到待机，而不是永远冻结在最后一帧。10秒这个数字是凭感觉定的、
    没有真机测过（同类问题见本文件其他"随手定的"数字），够容忍几秒网络抖动，又不至于让用户等太久。
    这解决的是"Caster消失了Terminal却不知道"，跟`CastStatusMessage`解决的"Caster不知道Terminal是否
    收到了"是反方向的两个问题——现在两个方向都有兜底了，尽管都只是尽力而为的超时/心跳，不是真正的
    连接状态协议。
32. **同一时间只支持一路投屏**：`RtpReceiver` 绑定固定端口 `DiscoveryProtocol.VideoRtpPort`，
    `Program.cs` 也只维护一个 `_castReceiver` 字段——第二个设备的 `cast_start` 到达时会直接顶掉
    第一个（`_castReceiver?.Dispose()` 后新建），没有排队或拒绝逻辑，也没有UI提示"已经有人在投屏"。
33. **【已实现，原为已知缺口】投屏接收端现在有音频了**：`CastReceiver` 新增了音频侧——
    `RawRtpReceiver`(`EveryStage.Transport`，音频专用、不经过H.264那套NAL重组) 收到PCM chunk后
    直接喂给 `EveryStage.Rendering.Audio.AudioPlaybackClock`(跟本地视频文件播放音轨复用同一个
    WASAPI播放类)。音频构造失败(`AudioPlaybackClock`/`WasapiOut`初始化失败)会被单独捕获并记到
    `AudioError`，退化成纯视频接收，不会连累视频一起失败——这跟Caster端`LiveCastSession`的
    "音频尽力而为"原则对称，是这次实现时特意做成一致的。具体的音频相关风险见下面新的一节。

### `UI/Panels/SettingsPanel.cs` / `Data/AppSettings.cs` / `Data/SettingsStore.cs` — 设置面板，这次新加的部分

34. **字段是本仓库自己挑的，不是PLANNING.md规定的**：PLANNING.md §8.2只给了五个分类名字（通用/
    显示/播放行为/网络与设备/关于），没有规定任何具体字段。这里选的四个字段（投屏开关默认状态、
    扩展屏选择、默认停留时长、设备名称）都是照着本文件"已知风险"里已经存在的"...随手定的，没有
    依据"条目选的，而不是凭空发明——但选哪些字段进设置面板本身仍然是产品决策，值得在真正的产品
    设计阶段重新核实这四个是不是用户真正需要调的。
35. **"投屏开关默认状态"和"扩展屏选择"都需要重启终端机才能生效**：`MonitorService.GetBoundExtendedDisplay`
    和 `OutputStateMachine.SetCastSwitch` 的应用时机都在 `TerminalApplicationContext` 构造函数里，
    只在进程启动时跑一次——这是诚实的限制而不是遗漏：真正做到"改了扩展屏立即生效"需要处理
    `OverlayWindow.Rebind`之后重新创建`VideoSurface`/重新绑定`PlaybackEngine`等一整套热切换逻辑，
    这个仓库目前完全没有涉足"运行时显示器热插拔"这个话题（`OverlayWindow.Rebind`本身也从未被任何
    调用方实际调用过，见`Display/OverlayWindow.cs`）。设置面板里已经用文字提示了这一点，而不是假装
    立即生效。
36. **"默认停留时长"是唯一立即生效的设置**：因为`PlaybackEngine`持有的是`SettingsStore`本身（不是
    某次读取的快照），每次`ArmStayDurationTimer`都重新读一次`Current.DefaultStayDurationSeconds`——
    这个字段选它作为"立即生效"的示范是有意的，用来验证"设置存储可以被多处共享读取、不需要额外的
    变更通知机制"这个模式本身是否够用；如果以后有更多设置项也需要立即生效，这个模式（持有store而
    非缓存值）应该继续沿用。
37. **设备名称的持久化跟其他三个设置字段完全独立**（走`DeviceIdentity.Save()`，不是`SettingsStore`）：
    面板上因此有两个独立的保存按钮，而不是一个——这是有意的诚实展示，而不是应该合并的UI缺陷：
    合并成一个按钮会让人误以为设备名称和`AppSettings`存在同一个文件里。顺带修了一个更早就存在的
    独立bug：`DiscoveryService.BeaconLoopAsync`原来在循环开始前只构造一次beacon消息，之后每次都
    发送同一个缓存对象——如果`DeviceIdentity.DeviceName`运行时被改了（现在通过设置面板真的可以了），
    beacon广播出去的名字永远不会更新，直到下次重启。现在改成每次循环都重新读取
    `_identity.DeviceName`构造新的beacon消息。
38. **【已实现，原为已知缺口】`SettingsPanel`"显示"标签页的显示器列表现在会在每次切换到本面板时
    重新枚举**：新增`SettingsPanel.Refresh_()`，跟`FilesPanel`/`DevicesPanel`已有的
    "构造一次、靠`Refresh_()`按需刷新"是同一个约定，`MainWindow.ShowPanel`导航到设置面板时调用它。
    上一轮这里的诊断（"真正实现需要让`MainWindow`不再对所有面板都永久复用同一个实例"）其实想复杂
    了——不需要放弃实例复用，只需要把其他三个面板已经在用的`Refresh_()`约定也套到这一个面板上，
    单独刷新"显示"标签页这一小块依赖实时硬件状态的UI，而不是整个面板重新构造。`Refresh_()`刻意
    没有直接拿`_settingsStore.Current.PreferredMonitorDeviceName`（已保存的值）去重新填充下拉框
    ——那样每次导航离开再回来，都会把用户已经选中但还没点"保存设置"的下拉框选择静默重置掉；改成
    优先保留下拉框里*当前选中*的项（如果对应显示器还在），只有选中的显示器真的被拔掉了才回退成
    "自动选择"。**遗留的未验证点**：这个修复的前提是`MonitorService.GetAll()`（底层是WinForms的
    `Screen.AllScreens`）在同一个进程里被反复调用时真的会返回新鲜枚举结果，而不是某种只在收到
    `WM_DISPLAYCHANGE`时才失效的内部缓存——.NET文档描述`Screen`确实监听这个消息来失效缓存，
    在一个正常跑消息循环的WinForms应用里应该没问题，但这个沙箱没有办法在真机上插拔显示器验证。

### `Receiving/CastReceiver.cs` 的音频侧 — 这次新加的部分

39. **【已实现，原为已知缺口】音视频同步**：`CastReceiver`不再把解码出的每一帧立即呈现——新增
    一个专门的后台呈现线程（`RunPresentLoop`），把解码结果先放进一个队列，只有当
    `AudioPlaybackClock.PositionTicks`（真实WASAPI硬件播放位置）追上这一帧自己的呈现时间才真正
    调用`PresentFrame`，跟`ContentEngine.VideoContentController`本地文件播放用的是同一套"音频为
    主时钟"模式（PLANNING.md §4.2）。两条流现在能比较，是因为Caster端`LiveCastSession`把音频的
    RTP时间戳也改成了跟视频同一套墙钟推导方式（见`EveryStage.Caster`README）；`CastReceiver`把
    两边的时间戳都用`RtpVideoClock.ToElapsedTicks`换算回"投屏开始后经过的时间"，再用
    第一个音频包建立的`_audioSyncOffsetTicks`对齐两条时间线的零点。这次实现特意加了一个**墙钟
    兜底超时**（等待循环里`waitStopwatch.Elapsed.Ticks < MaxWaitTicks`，1秒）：如果同步换算本身
    有错，导致目标音频位置永远"看起来在未来"，这个等待循环原本会永远卡住、把视频画面冻结在原地
    ——这比"完全不同步"这个已知缺口本身还要严重的一种回归，兜底超时保证等待循环无论如何都会在
    1秒内放弃并把这一帧呈现出去，把最坏情况限制在"呈现时机不准"而不是"画面冻结"。这次实现仍然
    没有解决/没有测量的：Desktop Duplication（视频）和WASAPI loopback（音频）各自从采集到真正
    送上RTP之间的延迟差；RTP 32位时间戳约13小时后的回绕；第一个音频包本身若被延迟/丢包/乱序，
    会让`_audioSyncOffsetTicks`从一开始就建立在一个不准的基准上，且此后永远不会重新校准。
40. **`H264HardwareDecoder`新增的时间戳FIFO（`_pendingTimestamps`）完全依赖"解码器不重排序、
    不缓冲超过一个访问单元"这个假设**：`SubmitAccessUnit`传入的`sampleTimeTicks`被放进一个
    `Queue<long>`，`DrainOutput`每产出一帧就从队头取一个——这是因为这个仓库没有办法验证Media
    Foundation的解码器MFT是否真的会把输入sample的时间戳原样保留/传递到输出sample上，只能自己维护
    这个FIFO作为替代方案。正确性完全依赖H.264编码器（Caster端`H264HardwareEncoder`）配置的
    "无B帧、低延迟"设置——如果解码器出于任何原因重排序、丢帧、或者在第一次产出前缓冲了不止一个
    访问单元，这个FIFO会从那一刻起永久错位（此后每次取出的都是别的访问单元的时间戳，通常偏差不大
    但永久存在），且这个仓库没有办法在真实硬件上验证这个假设是否成立。
41. **`BufferedWaveProvider`(`AudioPlaybackClock`内部)的`DiscardOnBufferOverflow=true`+
    5秒缓冲区，是为本地文件播放场景调的，没有针对网络抖动重新评估过**：网络场景下包到达的节奏比
    本地文件解码更不稳定（可能成串到达而不是均匀节奏），5秒缓冲区/丢弃满溢策略是否合适，只有真机
    联网测试才能知道。
42. **`CastReceiver`构造函数里音频初始化失败会被单独捕获，视频初始化失败则会让整个构造函数抛出**：
    这是有意的不对称（没有视频就没有可投的东西，没有音频只是体验降级），跟`LiveCastSession`在
    Caster端的处理原则一致，但值得注意`Program.cs`里`OnCastStartRequested`的`catch`块现在只应该
    捕获到视频侧的失败——写完这段代码后已经把注释更新为准确反映这个区分，但两边独立实现同一个
    "尽力而为"原则、没有共享代码或测试验证两边真的对称，是这个仓库到处存在的"协议/约定靠约定俗成
    而不是类型系统强制"的又一个例子。

### `Program.cs` 的 `CastStatusMessage` 应答机制 — 这次新加的部分

`DiscoveryProtocol`新增`CastStatusMessage`，`TerminalApplicationContext`每秒通过
`SendCastStatus()`把`_castReceiver`的已解码帧数/收到的视频音频字节数/两边各自的出错信息报回给
正在投屏的Caster——这是Caster端README里记录的"完全没有应答机制"缺口的另一半实现，第一次让
Caster知道终端机确实收到了东西。

43. **状态回报走discovery socket的现有基础设施，没有新开专门的控制通道**：`DiscoveryService`
    新增`SendCastStatusAsync`直接复用了已有的`_socket`和`SendAsync`私有方法——这跟发beacon/配对
    响应用的是同一个逻辑通道，如果discovery协议以后要加更多"高频周期性消息"，可能需要重新考虑
    要不要跟低频的beacon/配对消息分开，现在还看不出真的有必要。
44. **【已实现，原为已知缺口】`SendCastStatus`本身仍然不知道`_castingCasterEndPoint`是否还指向
    一个真实在线的Caster，但反方向的检测现在由下面第46条`CheckCastLiveness()`补上了**：Caster
    进程崩溃/被强制结束后，Terminal确实还是会往一个不存在的地址空发几次状态包（最多约10秒，直到
    `CheckCastLiveness`的超时触发），但不会再无限期发下去——这条残留的"最多浪费10秒状态包"本身
    不值得单独修，是超时机制生效前的正常延迟，不是一个独立的bug。
45. **`StopCasting()`重构消除了"三处重复的清理逻辑各自维护"的风险，但这次修改没有为此新增任何
    自动化验证**：这类"把重复逻辑收敛到一个方法"的重构在没有编译器/测试的环境下，风险是重构本身
    引入新bug（比如某个调用点其实需要跳过其中一步）而没有被发现——这次审查过三个调用点(§9.1"设备
    投屏"接管、`cast_stop`收到、"断"点击)确实都应该做完全一样的清理，但这个判断本身没有测试佐证。
46. **【已实现，原为已知缺口】`CheckCastLiveness()` 补上了反方向的超时检测**：Terminal现在会在
    连续10秒收不到任何视频/音频包时自动断开——跟Caster端`LiveCastSession.IsTerminalAlive`的5秒
    窗口是两个独立选的数字，没有共享定义或互相校准，纯粹因为分别针对不同问题（这边判断"媒体流是否
    还在流动"，那边判断"状态回报是否还在到达"，本来就是不同性质的信号，数字不同本身不是bug，但
    两个数字都是凭感觉定的，没有依据支撑10秒/5秒这两个具体值本身，也没考虑过它们要不要保持某种
    比例关系。`CheckCastLiveness`和`SendCastStatus`共用同一个`_castStatusTimer`（1秒一次）—这个
    设计选择本身没问题，但意味着以后如果要分别调整"状态回报频率"和"超时检测频率"，需要先把两者
    从共享的定时器里拆开。

### `FilesPanel.cs` 的"移除"按钮 — 这次新加的部分

47. **【已实现，原为已知缺口】文件面板现在有真正的"移除"操作了，`FileOperationLogger`的
    `LogFileImported`/`LogFileRemoved`也终于有了调用方**：之前`FilesPanel`只有"导入"，完全没有
    从文件库移除文件的UI入口——`FileLibraryStore.Remove(Guid)`这个方法本身早就存在，只是没人调用
    它，这比"漏记日志"更严重，是一个完整功能缺失。这次新增的"移除"按钮跟`DevicesPanel`的"移除配对"
    是同一个交互模式：选中一项才启用、点击后二次确认、确认后调用`_library.Remove`并刷新列表。
    确认提示里特意说明"活动里的副本不受影响"，因为这依赖`ActivitiesPanel.CloneFile`深拷贝而非
    按`MediaFile.Id`引用库条目这个前提（见风险#22）——这次没有改这个前提本身，只是第一次真正利用
    了它：如果以后"添加文件到活动"改成按引用而不是深拷贝，这里的移除操作和confirm文案都需要重新
    评估是否还安全。
    另外把`MainWindow`里原来"每个面板各自`new FileOperationLogger()`"的写法改成了一个共享实例
    传给`FilesPanel`和`ActivitiesPanel`两个面板——两者最终都写同一个物理日志文件
    （`file-operations/file-ops-{日期}.log`），而`DailyRollingLogWriter`的追加锁是每个实例各自
    持有的，两个独立实例同时写同一个文件理论上有小概率因为共享冲突导致某次追加静默失败（这个类
    本身的设计就是"写失败不崩溃、直接吞掉"，所以不会是一次崩溃，但会是一条丢失的日志）——共享一个
    实例、也就共享同一把锁，从根上排除这个可能性，而不是继续接受这个小概率风险。

## 尚未开始（阶段1剩余 + 后续阶段）

- 音视频同步的残余误差补偿（见"已知风险"第39-40条）——基础的"音频为主时钟+呈现线程等待"已经实现，
  但采集延迟差、RTP时间戳回绕、解码器FIFO假设这几项仍然是接受的已知限制，没有计划中的进一步方案
- WPS COM互操作：验证脚本见 `src/Poc/WpsComInteropSpike/`（PLANNING.md 标记为"风险仅次于阶段0"，
  这里只验证了"能否静默打开+翻页"，真正的编辑/保存集成到 Content Engine 仍未开始）
- 显示器热插拔/运行时重新绑定扩展屏（见"已知风险"第35条）——`OverlayWindow.Rebind`存在但从未被
  调用过，`VideoSurface`/`PlaybackEngine`也没有为"运行中途换显示器"设计
- 悬浮预览窗、文件面板、活动面板三者之间没有联动（比如从悬浮预览窗"下一项"切换后，文件/活动面板
  不会自动高亮对应的缩略图/树节点）——PLANNING.md §16第5项本身也把这类交互细节列为"待验证"。
- `FileOperationLogger.LogPlaybackPropertyChanged` 仍然没有调用方——单个 `MediaFile` 播放属性
  （停留时长、淡入淡出等）在活动面板里还没有UI能编辑（见下一条），自然也没有变更可记；"文件
  导入/移除"和"方案/活动创建/修改/删除"这两类已经都接上了（见"已知风险"第47条、`ActivitiesPanel`）。
- `Activity.DefaultPlayMode`(顺序自动/手动点选) 和 `MediaFile` 的播放属性（停留时长、淡入淡出、
  完成动作等）在活动面板里完全没有编辑入口——目前"添加文件到活动"用的都是 `MediaFile` 的默认值。
