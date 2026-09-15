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
| `Devices/` | §7 | 设备发现(UDP广播 `DiscoveryService`)、配对(信任/手动确认、被投放/被监看权限分离)、配对设备列表持久化(`PairedDeviceStore`)。设备指纹(`DeviceIdentity`)与协议格式(`DiscoveryProtocol`)现在都在 `src/Shared/EveryStage.Discovery/`，因为 `src/Caster/EveryStage.Caster/` 也要用同一套。`DiscoveryService` 现在还处理 `CastStartMessage`/`CastStopMessage`（只信任 `AllowCast` 的已配对设备），驱动下面的 `Receiving/`；新增 `SendCastStatusAsync`，配合 `Program.cs` 里每秒一次的 `SendCastStatus()` 把接收状态报回给正在投屏的Caster（`DiscoveryProtocol.CastStatusMessage`，见该README"已知风险"新增小节）；新增 `HandlePing`，无条件echo任何收到的 `PingMessage`（不检查配对状态，见"已知风险"第65条），供Caster端测量真实RTT |
| `Receiving/` | 阶段2"传输接收端" | `H264HardwareDecoder` 直接驱动一个（假设是同步的）H.264解码器MFT，把推入的Annex-B访问单元解码成D3D11 NV12纹理；`CastReceiver` 把 `RtpReceiver`(EveryStage.Transport)接收到的NAL单元用RTP marker位重新拼回Annex-B访问单元喂给解码器，再通过共享的 `Display/VideoSurface` 呈现到 `OverlayWindow.VideoHost`（不再自建独立的D3D11设备/交换链，见该类README条目）——这是这个仓库第一次让 Caster 和 Terminal 真的通过网络传视频（而不是各自的自检）。`CastReceiver`现在还有音频侧：`RawRtpReceiver`收到的payload按`AudioIsAac`分两条路径——PCM直接喂给`EveryStage.Rendering.Audio.AudioPlaybackClock`播放，AAC先经过`EveryStage.Rendering.Decode.AacAudioDecoder`解码回PCM再喂给它（见"已知风险"第64条），构造失败会独立降级成纯视频（不影响视频侧）；新增`LastPacketReceivedAt`，配合`Program.cs`的`CheckCastLiveness()`在Caster连续10秒无数据包时自动断开 |
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
   返回`false`就不播)。**更新（见风险#63）**：关闭状态下双击"彻底无反馈的静默无操作，用户会以为
   点击没生效"这半句已经解决——不是真正的本地预览（那个渲染目标仍然不存在），而是新增
   `PlaybackEngine.PlaybackDeclinedByCastSwitch`事件，让UI至少能确认"点击收到了，只是投屏开关
   关着"，见第63条。音频类文件(`MediaKind.Audio`)的播放**更新（见风险#60-61）**：非背景音频
   （`IsBackgroundAudio == false`）现在有真正的播放行为了，背景音轨叠加（§6"音频特殊性"那条规则
   本身在文档§16第3项里也还标着"待细化"）仍然完全没接。
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
    **更新（见第64条）**：这里说的"收到PCM chunk后直接喂给`AudioPlaybackClock`"现在只是
    `AudioIsAac`为假时的路径——Caster端换成AAC之后，这里多了一层`AacAudioDecoder`解码，见第64条。

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
    立即生效。**更新（见第59条）**：这里说的"从未被任何调用方实际调用过"现在只在"设置面板改了
    扩展屏选择"这一条路径上仍然成立——`OverlayWindow.Rebind`本身已经有了另一个真正的调用方
    （已绑定的扩展屏运行中途自己改分辨率/位置），但设置面板这条路径确实还是要重启才生效，因为
    这里触发`Rebind`的是`SystemEvents.DisplaySettingsChanged`，跟设置面板改选择完全是两回事。
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

### `CastReceiver`/`Program.cs` 的持续解码/播放出错处理 — 这次新加的部分

48. **【已实现，原为已知缺口】持续解码/播放出错现在会自动断开投屏了**：`CastReceiver`新增
    `ConsecutiveVideoDecodeErrors`/`ConsecutiveAudioPlaybackErrors`两个计数器，分别在
    `OnNalUnitReceived`/`OnAudioPayloadReceived`里失败时`+1`、成功时清零；`Program.cs`新增
    `CheckDecodeHealth()`，跟`CheckCastLiveness()`共用同一个1秒定时器，任一计数器连续达到90次
    （按30fps估算约3秒）就`StopCasting()`+`_stateMachine.Disconnect()`，回到待机态而不是让一个
    卡死的解码器永远显示冻结/花屏的最后一帧——之前这仓库明确说过这是一个还没做出的产品决策，
    现在做出的选择是"自动降级到待机"，理由是Terminal本来就是无人值守设备，没有人在旁边看到出错
    信息去手动点"断"。
49. **顺带修复了一个此前没有被单独列为风险的真实bug：`LastError`/`AudioError`原来是"曾经出过错"
    就永久非空**：只在出错时被赋值，从来没有在成功时清空过，导致哪怕只是最初几帧解码失败、之后
    完全恢复正常，`CastStatusMessage`也会永远把"投屏出错"报给Caster——`LiveCastSession`那边的
    `TerminalVideoError`/`TerminalAudioError`本身是"每次状态包直接覆盖"（不是自己累积的），所以
    这个修复不需要改Caster那一侧任何代码就能生效。现在这两个属性反映的是"最近一次尝试是否成功"，
    真正代表"是否处于持续失败状态"的是新增的两个`Consecutive*Errors`计数器。
50. **`OnAudioPayloadReceived`之前完全没有try/catch**：`AudioPlaybackClock.Enqueue`如果抛出，
    异常会一路冒到`RawRtpReceiver.ReceiveLoopAsync`所在的`Task.Run`里变成一个没人观察的task
    异常——不会让进程崩溃，但会让整个音频接收循环从此静默死掉，且没有任何地方报告过这件事。这次
    顺带补上了这层try/catch，让它跟视频侧的`OnNalUnitReceived`一样，把失败转成`AudioError`+
    `ConsecutiveAudioPlaybackErrors`而不是让接收线程无声消失。
51. **90次这个阈值本身没有真机验证过**：按"典型30fps编码"倒推出"约3秒"，但真实编码帧率、解码器
    真正的故障模式（是会连续每帧都失败，还是间歇性失败）都是这个沙箱没法观察的——如果真机上出现
    "解码器偶尔失败但很快自愈"的模式，90这个阈值可能太宽松或太严格，目前完全是估算值。

### `CastStatusMessage` 新增时间戳字段 — 这次新加的部分

52. **【已实现，原为已知缺口】`SendCastStatus`现在会带上发送时刻**：`DiscoveryProtocol.CastStatusMessage`
    新增`SentAtUtc`字段，`SendCastStatus()`每次构造消息时填成`DateTimeOffset.UtcNow`——这是
    Caster端README里记录的已知缺口（原风险36："`CastStatusMessage`本身不携带发送时的时间戳"）
    在协议层的实现，具体怎么用见`EveryStage.Caster`README对应条目。**这个时间戳只在Terminal和
    Caster两台机器的系统时钟大致同步时才是一个有意义的延迟估算**——这个协议本身不做任何时钟偏移
    协商，如果两台机器时钟差得远，Caster那边算出来的"延迟"其实主要反映的是时钟偏差而不是网络
    延迟，这个仓库没有办法从沙箱里验证真实局域网环境下两台Windows机器的时钟同步情况。

### `ActivitiesPanel` 跟随播放高亮 — 这次新加的部分

53. **【部分实现，原为已知缺口】活动面板现在会跟随播放自动高亮对应的文件节点了**：
    `PlaybackEngine.FileStarted`（所有推进播放的路径——悬浮预览窗上一项/下一项、双击活动面板里的
    文件/活动节点、`CompletionAction.NextItem`自动切换——最终都会触发这个事件）新增了
    `ActivitiesPanel.TryHighlightPlayingFile`订阅：收到事件时在`_tree`里查找`Tag`跟事件传来的
    `MediaFile`是同一个对象引用的节点并选中它；找不到（比如从文件面板直接双击播放、没有活动上下文，
    或者播放的文件属于当前没有显示的另一个方案）就清空选中，而不是留着一个过时的选择。这解决的是
    PLANNING.md §16第5项"悬浮预览窗/文件面板/活动面板联动"里活动面板的那一半。
54. **文件面板那一半故意没有做**：活动里的文件是文件库条目的深拷贝（见风险#22），既不是同一个
    对象、`MediaFile.Id`也不一样，第53条依赖的"引用相等"在这里根本用不上——唯一能用的信号是按
    `SourcePath`匹配，但这个信号本身就模糊：同一个源文件可能被添加到好几个活动里、也可能已经从
    文件库删除了但仍在某个活动里播放着。这一轮判断这个较弱的匹配基础不值得单独建一套逻辑，所以
    第53条只做了活动面板这一半，文件面板完全没有改动，PLANNING.md §16第5项本身也把这类交互细节
    列为"待验证"，不是这个仓库单方面决定简化的。

### `PlayMode`/`AllowManualSkip` 第一次有了真正的运行时效果 — 这次新加的部分

55. **【已实现，原为已知缺口】`PlayMode`不再是一个没有任何行为的枚举**：`Activity.DefaultPlayMode`/
    `MediaFile.PlayModeOverride`这两个字段从数据模型加进来那一轮起就没有任何代码读过它们——
    `CompletionAction.NextItem`一直是无条件自动前进，跟这两个字段的值完全无关。这一轮
    `PlaybackEngine`新增`EffectivePlayMode(file)`（文件级`PlayModeOverride`优先，否则回退到所属
    活动的`DefaultPlayMode`），`HandleCompletion`处理`NextItem`时先查一下有效播放方式：
    `SequentialAuto`才自动前进，`ManualSelect`则停在当前这一项，直到用户通过悬浮预览窗手动点
    "下一项"或直接点选别的文件/活动。这是这个仓库第一次真正区分"顺序自动播放"和"手动点选"这两个
    模式的实际含义。
56. **【已实现，原为已知缺口】`MediaFile.AllowManualSkip`也第一次有了效果**：`PlaybackEngine.TryAdvance`
    在触发来源是`PlaybackTrigger.ManualSkip`（也就是悬浮预览窗的上一项/下一项按钮）时，如果当前
    文件的`AllowManualSkip`是`false`就直接拒绝前进——特意只挡`ManualSkip`这一种触发来源，不影响
    `CompletionAction.NextItem`自身的自动前进（`ActivityAuto`触发），因为这个字段的doc comment
    从一开始描述的就是"是否允许手动跳过"，不是"是否允许自动前进"，两者是不同的产品概念，不应该
    被同一次改动混在一起。
57. **【已实现，原为已知缺口】`ActivitiesPanel`新增"播放方式..."按钮，`Activity.DefaultPlayMode`
    和`MediaFile.PlayModeOverride`现在都有编辑入口了**：是这个仓库第一次给`PlayMode`配上编辑
    UI——上面第55-56条先让这个枚举有了真正的行为，这里才跟进补上编辑入口，避免重蹈"UI能设置一个
    完全不影响任何行为的值"这类反面例子的覆辙（另见风险#9关于本仓库对"没有行为支撑的UI"这类东西
    的一贯态度）。按钮是上下文相关的：选中一个活动节点本身，编辑的是这个活动的`DefaultPlayMode`；
    选中活动内的某个文件节点，编辑的是这个文件自己的`PlayModeOverride`（带一个"覆盖活动默认播放
    方式"复选框，取消勾选就是显式设回`null`即"跟随活动默认"，勾选后才能选具体的播放方式，这个
    复选框+下拉框联动的写法复用了`SettingsPanel`"启用默认停留时长"已经用过的同一个约定）。
    `UI/PlayModeDialog.cs`一开始只支持编辑活动级别（非空）的`DefaultPlayMode`，后来同一轮里扩展
    成同时支持这个可空的文件级别场景，而不是另开一个几乎重复的对话框类。
58. **【已实现，原为已知缺口】`FileOperationLogger.LogPlaybackPropertyChanged`终于有了第一个
    真正的调用方**：编辑`MediaFile.PlayModeOverride`时用的是这个方法，而不是文件列表增删/移动
    操作一直在用的`LogActivityModified`——这次改动前这个方法从这个仓库存在以来就没人调用过，
    是专门为"记录单个文件的某个播放属性从什么值变成了什么值"设计的，这次是它第一次真正被用在
    它本来的用途上（`propertyName`传的是`nameof(MediaFile.PlayModeOverride)`，`oldValue`/
    `newValue`是`PlayMode?.ToString()`，null会原样记成`null`而不是字符串"null"）。
59. **【部分实现，原为已知缺口】`Program.cs`现在订阅`Microsoft.Win32.SystemEvents.DisplaySettingsChanged`，
    给`OverlayWindow.Rebind`/`VideoSurface.Resize`补上了第一个真正的调用方**：上面第35条提到这两个
    方法写好之后从未被调用过——这次只解决其中最窄的一种场景："终端机启动时已经绑定了某个扩展屏，
    这个屏幕运行途中改了分辨率/位置"（`HandleDisplaySettingsChanged`重新调用
    `MonitorService.GetBoundExtendedDisplay`，跟`OverlayWindow.Monitor`做`record`结构相等比较，
    不同才真正`Rebind`+`Resize`，避免无意义的重建）。**明确没有解决的两种情况**：(a)
    启动时完全没有扩展屏绑定的，`_overlay`/`_videoSurface`永远是`null`，运行中途插入新显示器
    也不会凭空生出一整套`_overlay`/`_videoSurface`/`_playback`/`_previewWindow`——这需要的对象
    生命周期改动比这次大得多，尤其是"正在播放本地视频或正在接收设备投屏时显示器被拔掉"这类并发
    场景在这个沙箱里完全没办法验证，属于有意暂缓而不是遗漏；(b) 已绑定的显示器运行中途被完全拔掉，
    `HandleDisplaySettingsChanged`在`GetBoundExtendedDisplay`返回`null`时直接返回，`OverlayWindow`
    停留在最后已知的位置/尺寸——Windows会把这块离屏的无边框窗口简单裁剪掉而不会报错，所以这不是
    崩溃风险，只是"重新插回或者重启进程之前画面不对"这个已知的、可接受的缺口。另外`SystemEvents`
    在真实Windows机器上到底从哪个线程触发这个事件，这个沙箱没有dotnet/Windows SDK，完全没办法
    验证——`OnDisplaySettingsChanged`因此防御性地用构造函数里捕获的`_uiContext.Post`把
    `HandleDisplaySettingsChanged`转回UI线程执行，而不是假设它已经在UI线程上。
60. **新增`EveryStage.Rendering.Decode.AudioDecodeSource`——这个仓库第二个`IMFSourceReader`
    封装**：跟`VideoDecodeSource`同样的"从未编译/从未在真机跑过"风险等级，但比它简单——不需要
    `D3D11Device`/`MF_SOURCE_READER_D3D_MANAGER`/`MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS`（音频
    没有DXVA硬件解码这回事），构造函数只协商音频媒体类型、只选中`MF_SOURCE_READER_FIRST_AUDIO_STREAM`
    这一路流。具体未核实点跟`VideoDecodeSource`是同一类：`MFCreateSourceReaderFromURL`接受的
    `IMFAttributes`参数这里传了一个空的（而不是猜测Vortice绑定是否接受`null`），`IMFMediaBuffer.Lock`
    的签名沿用`VideoDecodeSource.ReadNextAudioChunk`已经标注过的同一个不确定点。有一点在真机上
    需要重点验证但这个沙箱完全没办法验证：某些容器格式的音频文件（比如内嵌封面图的mp3）可能会被
    Media Foundation识别出一个"视频"流（封面图本身），这里的实现是"完全不对视频流调用
    `SetStreamSelection`"，指望`IMFSourceReader`对没有显式选中的流不产生任何行为——如果这个假设
    不成立，构造函数或`ReadNextChunk`的行为需要重新核实。
61. **【已实现，原为已知缺口】`PlaybackEngine`现在真正播放非背景音频文件了**（对应第9条"音频类
    文件的播放目前完全没接"）：`MediaFile.Kind == Audio`且`IsBackgroundAudio == false`时，新增的
    `ContentEngine.AudioContentController`（结构上是`VideoContentController`的音频版——同样是
    独立的解码/播放后台线程、`Play`/`Stop`/`PlaybackCompleted`/`PlaybackFailed`这套接口）通过
    `AudioDecodeSource`解码、`AudioPlaybackClock`（跟视频共享的同一个WASAPI输出类）播放，完成时
    走跟视频完全一样的`HandleCompletion`（`OnCompletion`的`NextItem`/`Loop`/`HoldOnLastFrame`对
    音频和视频现在是同一套代码路径）。**明确没有实现的部分**：(a) `IsBackgroundAudio == true`
    的背景音轨叠加——PLANNING.md §6要求"叠加在其他视觉内容之上播放，不占用主队列顺序位"，这需要
    一个真正支持多轨同时播放的模型，`PlaybackEngine`目前是单一"当前文件"的顺序播放模型，完全没有
    并发轨道的概念，这次刻意没有尝试；这个分支目前维持原样直接`return`。(b) `FadeDuration`/
    `VolumeFollowsFade`——音频播放本身没有任何音量渐变逻辑，`AudioPlaybackClock.Enqueue`原样
    把解码出来的PCM送进WASAPI缓冲区。(c) `MediaFile.BackgroundAudioVisual`的三个选项里只有
    `Black`是"真的什么都不用做"（`ContentSurface`本来就在没有帧时画黑屏）；`DefaultBackgroundImage`
    和`Waveform`都是新增的`ContentEngine.AudioVisualRenderer`生成的占位画面，不是真正的产品视觉
    ——`DefaultBackgroundImage`只是纯色背景+文件名文字（这个仓库完全没有任何图片资源文件，见该类
    doc comment），`Waveform`是单柱状峰值电平表（每个解码出来的PCM块算一次峰值绝对值，归一化到
    [0,1]），不是真正的滚动波形动画。电平表通过`AudioContentController.LevelChanged`从后台解码
    线程把最新数值写进一个`volatile float`字段，再由UI线程上一个约15fps（66ms间隔）的
    `System.Windows.Forms.Timer`读取并重新生成`Bitmap`——刻意不在`LevelChanged`每次触发时都在
    后台线程直接生成/`BeginInvoke`一个新`Bitmap`，因为PCM块到达的频率完全由Media Foundation
    决定、可能远高于任何显示器需要的帧率，真这样做等于把大量GDI+ `Bitmap`分配和UI线程封送堆在
    解码热路径上。
62. **【已实现，原为已知缺口】`ActivitiesPanel`新增"音频属性..."按钮，`MediaFile.IsBackgroundAudio`/
    `BackgroundAudioVisual`现在都有编辑入口了**：跟上面第57条`PlayModeDialog`一样的"先做行为、
    再做UI"顺序——上面第61条先让这两个字段有了真正的行为，这里跟进补上编辑入口。新增
    `UI/AudioPropertiesDialog.cs`，只在选中一个`MediaFile.Kind == MediaKind.Audio`的文件节点时才
    启用（`UpdateButtonStates`），跟活动级别没有对应关系——不像`PlayModeOverride`，
    `IsBackgroundAudio`/`BackgroundAudioVisual`只在文件级别有意义，没有活动级别的默认值可以退化
    到。对话框里"作为背景音频叠加播放"这个复选框刻意带了一条红色警示文字（勾选后才显示）：
    第61条已经说明背景音轨叠加目前是文档化的no-op，勾选这个复选框现在的真实效果是"这个文件完全
    不会播放"而不是"作为背景叠加播放"，UI在这里选择诚实告知而不是让复选框看起来像是已经实现的
    功能——同样的"复选框做的事不能超过实际实现"的原则，参见Caster项目"确认≠健康"那条提醒。
63. **【已实现，原为已知缺口】投屏开关关闭时点文件不再是彻底静默的无反馈操作**（对应第9条）：
    新增`PlaybackEngine.PlaybackDeclinedByCastSwitch`事件，在`PlayFile`发现
    `RequestLocalFilePlayback`返回`false`时（原来这里只是直接`return`）额外触发一次，携带被
    拒绝播放的那个`MediaFile`。`MainWindow`订阅它，把导航侧边栏本来就常驻可见的`_statusLabel`
    （之所以选它而不是某个面板自己的状态条：它在四个面板之间切换时始终可见，`ActivitiesPanel`
    自己的`_statusBar`只在活动面板打开时才看得到）临时改写成"⚠ 投屏开关已关闭\n未投放：文件名"，
    3秒后（一次性`System.Windows.Forms.Timer`，每次新的拒绝事件都会重新启动同一个计时器而不是
    叠加多个）自动恢复成原来的"●输出中"/"○待机中"状态文字。**明确没有解决的部分**：这仍然不是
    第9条本来想要的"仅本地预览"——没有任何地方真的把这个文件渲染出来，用户看到的只是"知道点击
    被收到了、也知道原因"，而不是"能在没投屏的情况下就地预览内容"；真正的本地预览渲染目标本身
    依然完全不存在（同第9条原文）。
64. **【已实现，原为已知缺口】设备投屏收到的音频现在真的能是AAC了**（对应第33条，见
    `EveryStage.Caster` README第53-55条）：Caster端`LiveCastSession`这一轮从发送未压缩PCM改成
    发送AAC，Terminal这一侧对称地补上了解码。`CastReceiver`构造函数新增`audioIsAac`参数
    （来自`DiscoveryProtocol.CastStartMessage.AudioIsAac`，经`DiscoveryService.CastStartInfo`/
    `Program.cs`一路传进来），为真时构造一个`EveryStage.Rendering.Decode.AacAudioDecoder`——这个
    类本身继承`AacAudioEncoder`同样的最大风险点（内置AAC解码MFT被假设是同步transform，这个假设
    在这个沙箱里没办法验证，见该类doc comment）。`OnAudioPayloadReceived`收到的payload先经过
    `_audioDecoder.SubmitAccessUnit`解码、通过新增的`OnAacPcmDecoded`回调再送进
    `_audioClock.Enqueue`，而不是像PCM路径那样直接把RTP payload送进`AudioPlaybackClock`——这个
    分支写法上特意不用`try/catch`包`SubmitAccessUnit`（它自己从不抛异常，成功/失败都通过
    `PcmDecoded`/`DecodingFailed`事件同步汇报），避免"调用完之后不管三七二十一先把`AudioError`
    清空"这种会覆盖掉刚刚同步发生的解码失败的错误写法——这是这一轮设计时特意避免的一个坑，不是
    事后发现的bug。**明确没有做的部分**：(a) 没有任何协议版本协商——`AudioIsAac`如果被反序列化成
    默认值`false`（两端代码版本不一致时），Terminal会把AAC字节当PCM直接送进WASAPI，播放出来是
    噪音而不是报错，见`EveryStage.Discovery`README对应的新增风险条目；(b) `OnAacPcmDecoded`失败、
    `OnAacDecodingFailed`都会让`ConsecutiveAudioPlaybackErrors`自增，这个计数器本身已经被
    `Program.cs`的`CheckDecodeHealth`读取（连续90次触发自动断开投屏），这次改动不需要额外接线就
    自动获得了这个既有的恢复策略——但这个既有阈值本身是照PCM路径的失败模式（`AudioPlaybackClock`
    偶发的WASAPI写入失败）估的，从未针对"AAC解码器本身持续失败"这种新失败模式重新核实过是否
    仍然合适。
65. **【新增】`DiscoveryService.HandlePing`无条件echo任何`PingMessage`，不检查配对/信任状态**：
    Caster端新增了真正的RTT测量（不依赖两台机器时钟同步，见`EveryStage.Caster`README第56条），
    这一侧对应的实现就是收到`PingMessage`立刻原样回一个带同样`RequestId`的`PongMessage`——没有
    检查发送方是否已配对、是否被允许投屏，纯粹是网络层面的echo。这跟这个协议本身"不含任何安全/
    认证机制"是同一个已经接受的产品决策（见`EveryStage.Discovery`README），但确实意味着局域网
    里任何人都可以用这两个消息类型探测一个Terminal是否在线、测出到它的往返时延，即使从未跟它
    配对过——这个仓库认为这个额外的探测能力不比协议本身已有的明文/无认证风险更严重，选择不为
    这一个消息类型单独加一层配对检查。
66. **【已实现，原为已知缺口】`DeviceConnectionLogger.LogQualityMetric`终于有了第一个真正的
    调用方**：这个方法从这个仓库最早的日志功能那一轮起就存在，一直没有任何代码调用它（对应
    PLANNING.md §14.4"连接质量指标（丢包率/延迟）"这半句需求）——不是没接线，是当时真的没有任何
    数据可以喂给它。这一轮把两个缺失的数据来源都补上了：(1) **延迟**：`DiscoveryService`新增
    `PingAsync`/`HandlePong`，跟`EveryStage.Caster`的`TerminalDiscoveryClient.PingAsync`完全对称
    （现在`PingMessage`/`PongMessage`双向都能发起，见该协议的doc comment和`EveryStage.Caster`
    README第56条）——Terminal主动ping正在给自己投屏的那个Caster，不依赖两台机器时钟同步；
    (2) **丢包率**：`EveryStage.Transport`的`RtpReceiver`/`RawRtpReceiver`新增序列号跳变检测
    （见该项目README第9条，一个粗略的、不精确的信号，不是真实丢包数），`CastReceiver`新增
    `EstimatedPacketLossPercent`把视频+音频两路合并成一个百分比。`Program.cs`新增
    `LogConnectionQualityAsync`，复用现有的`_castStatusTimer`（每15个tick、约15秒跑一次，而不是
    每秒都写一条日志——诊断日志本来就该是周期性摘要，不是逐tick的流水账），ping和丢包率任何一个
    缺失都用`null`记录而不是编造一个`0`——`LogQualityMetric`的两个参数因此从`double`改成了
    `double?`：一个持久化日志里的`0`会被误读成"测量了、结果是满分"，比诚实的"这一轮没测到"是
    更严重的谎言，这是这次改动过程中特意做的一个小修正，不是最初就设计好的。**残留的不确定性**：
    `EstimatedPacketLossPercent`本身只是"跳变次数/(收到数+跳变次数)"这种粗略估算（见
    `EveryStage.Transport`README第9条），`PingAsync`往返测量的也是discovery socket的RTT，不是
    RTP媒体流本身的延迟——跟`EveryStage.Caster`第35条已经说明的"状态通道健康不代表媒体流健康"
    是同一类需要牢记的区别。

## 尚未开始（阶段1剩余 + 后续阶段）

- 音视频同步的残余误差补偿（见"已知风险"第39-40条）——基础的"音频为主时钟+呈现线程等待"已经实现，
  但采集延迟差、RTP时间戳回绕、解码器FIFO假设这几项仍然是接受的已知限制，没有计划中的进一步方案
- WPS COM互操作：验证脚本见 `src/Poc/WpsComInteropSpike/`（PLANNING.md 标记为"风险仅次于阶段0"，
  这里只验证了"能否静默打开+翻页"，真正的编辑/保存集成到 Content Engine 仍未开始）
- 显示器热插拔/运行时重新绑定扩展屏（见"已知风险"第35、59条）——"已绑定的扩展屏运行中途改分辨率
  /位置"这一种场景已经在第59条实现；"启动时没绑定、运行中途插入新显示器"和"已绑定的显示器运行
  中途被整个拔掉"这两种场景仍然完全没有处理，见第59条列出的具体理由
- 悬浮预览窗、文件面板之间仍然没有联动（见"已知风险"第54条）——活动面板那一半已经在这一轮实现了
- 背景音轨叠加播放（`IsBackgroundAudio == true`，见"已知风险"第61条）——需要`PlaybackEngine`支持
  真正的多轨并发播放，目前完全没有实现；非背景音频（第61条已实现的那一半）不受影响
- `FadeDuration`/`VolumeFollowsFade`这两个字段仍然完全没有任何代码读取过（见`PlaybackEngine`类doc
  comment"deliberately out of scope"那一段；`IsBackgroundAudio`/`BackgroundAudioVisual`这两个
  字段已经在第61条里有真正的行为了，从这条移出）——在`FadeDuration`/`VolumeFollowsFade`本身有真正
  的播放行为之前，这个仓库不打算为它们加编辑UI，同样的"先做行为、再做UI"的顺序，见风险#55-57、
  61-62（`PlayMode`/`AllowManualSkip`/`FileOperationLogger.LogPlaybackPropertyChanged`/音频播放
  行为+编辑UI已经按这个顺序做完了）
