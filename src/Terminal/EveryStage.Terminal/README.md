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
| `Receiving/` | 阶段2"传输接收端" | `H264HardwareDecoder` 直接驱动一个（假设是同步的）H.264解码器MFT，把推入的Annex-B访问单元解码成D3D11 NV12纹理；`CastReceiver` 把 `RtpReceiver`(EveryStage.Transport)接收到的NAL单元用RTP marker位重新拼回Annex-B访问单元喂给解码器，再通过共享的 `Display/VideoSurface` 呈现到 `OverlayWindow.VideoHost`（不再自建独立的D3D11设备/交换链，见该类README条目）——这是这个仓库第一次让 Caster 和 Terminal 真的通过网络传视频（而不是各自的自检）。`CastReceiver`现在还有音频侧：`RawRtpReceiver`收到的payload按`AudioIsAac`分两条路径——PCM直接喂给`EveryStage.Rendering.Audio.AudioPlaybackClock`播放，AAC先经过`EveryStage.Rendering.Decode.AacAudioDecoder`解码回PCM再喂给它（见"已知风险"第64条），构造失败会独立降级成纯视频（不影响视频侧）；新增`LastPacketReceivedAt`，配合`Program.cs`的`CheckCastLiveness()`在Caster连续10秒无数据包时自动断开；构造函数现在还会把`CastStartMessage`携带的PayloadType传给`RtpReceiver`/`RawRtpReceiver`做真正的校验（见"已知风险"第75条）；呈现调用改成经过`VideoSurface.PresentFrame`而不是直接碰`VideoSurface.Presenter`，修复了一个`Resize`跟`PresentFrame`之间原本完全没有互斥保护的并发bug（见"已知风险"第76条） |
| `UI/FloatingPreviewWindow.cs` | §8.3 | 悬浮预览窗：LIVE标识、缩略图(仅图片/PDF，视频暂无)、文件名、上一项/暂停/下一项/断 四个按钮、置顶开关；拖动位置靠"常驻同一个Form实例、只隐藏不销毁"天然记住 |
| `UI/PairingConfirmationDialog.cs` | §7 | 配对请求的弹窗确认（接受/拒绝 + 被投放/被监看/信任三个独立勾选项，"被监看"旁边现在有一行提示：这个功能本身还没实现，见"已知风险"第70条）；不含PIN码交换，`DiscoveryProtocol`目前没有PIN字段 |
| `UI/EditPairedDevicePermissionsDialog.cs` | §7 | 配对之后修改已配对设备的信任/被投放/被监看这三个字段（见"已知风险"第70条）——之前只有首次配对时的 `PairingConfirmationDialog` 能设置它们 |
| `UI/MainWindow.cs` | §8.1 | 主界面外壳：左侧导航(投屏开关/断/四个面板入口/状态) + 右侧内容区；关闭窗口只隐藏不退出进程（终端机要常驻），托盘菜单"打开主界面"或双击托盘图标可以召回；右下角托管`UI/ToastStack.cs`(§11"异常提示"，见"已知风险"第77条) |
| `UI/ToastNotification.cs`、`UI/ToastStack.cs` | §11 | "右下角Toast通知栈"：按严重程度分色(红=严重/黄=提示)，可选的可执行按钮(重试/移除)——见"已知风险"第77条 |
| `UI/Panels/FilesPanel.cs` | §8.2 | 文件面板：`ListView`缩略图网格 + 类型筛选(全部/图片/视频/文档/音频) + 导入对话框 + 从资源管理器拖拽导入 + 批量选择+移除(二次确认，见"已知风险"第79条) + 双击播放(`PlaybackEngine.RequestPlay`)，导入/移除都接入`FileOperationLogger` |
| `UI/Panels/DevicesPanel.cs` | §8.2 | 设备面板：已配对设备列表(信任状态/被投放/被监看/配对时间) + 移除配对(接入`DeviceConnectionLogger.LogUnpaired`，见"已知风险"第74条) + 编辑权限(见"已知风险"第70条) |
| `UI/Panels/ActivitiesPanel.cs` | §8.2 | 活动面板：方案选择器(切换/新建/另存为/删除) + `TreeView`活动/文件层级(真正可折叠、且折叠状态会持久化，见"已知风险"第72条) + 新建/重命名/删除活动 + 从文件库添加/移除文件 + 上移/下移排序 + 播放方式/音频属性/停留时长/完成后动作(见"已知风险"第55-57、61-62、71、73条) + 输出状态条；双击播放，接入`FileOperationLogger`记录方案/活动的增删改及播放属性变更 |
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
   自己新增的部分——独立播放线程的启动/取消/Join——逻辑上是新代码，同样没有在真实环境跑过。
   **更新（见第76条）**：这条原本还提到"`_presenterLock` 保护并发 Present/Resize"，但第76条发现
   这把锁从来没有真正生效过（真正调用`Resize`的地方根本不经过这个类），已经在那一轮的修复里整个
   移除，互斥逻辑改成在真正被共享的`Display/VideoSurface`里实现——这里不再有自己的锁。
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
    **再次更新**：上一句"确实还是要重启才生效"说得比实际情况绝对了一点，这次核对
    `AppSettings.PreferredMonitorDeviceName`的doc comment时顺手发现——`HandleDisplaySettingsChanged`
    每次被触发时读的是`_settingsStore.Current.PreferredMonitorDeviceName`这个实时值，不是启动时
    缓存的快照，所以严格来说：保存设置这个动作本身确实不会直接触发`Rebind`（这句话没错），但如果
    保存之后、下次重启之前，*任何原因*触发了一次`SystemEvents.DisplaySettingsChanged`（哪怕跟这次
    改动完全无关，比如中途又插拔了别的显示器），`HandleDisplaySettingsChanged`会用新保存的偏好
    重新算一遍绑定，真有可能在运行中途悄悄切换扩展屏——不是"保证重启前不生效"，而是"不保证重启前
    会生效，但也不保证不会"，这个不确定性本身也是操作者未必预期到的行为，值得记录成风险而不是
    简单地说"要重启"。
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
    比例关系）。`CheckCastLiveness`和`SendCastStatus`共用同一个`_castStatusTimer`（1秒一次）—这个
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
    场景在这个沙箱里完全没办法验证，属于有意暂缓而不是遗漏；(b) 已绑定的显示器运行中途被完全拔掉
    ——**更新（见第67条）**：这个场景后来被解决了，`HandleDisplaySettingsChanged`在
    `GetBoundExtendedDisplay`返回`null`时不再直接返回，而是调用`OutputStateMachine.Disconnect()`。
    另外`SystemEvents`
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
    `VolumeFollowsFade`——写这一条时音频播放本身还没有任何音量渐变逻辑，`AudioPlaybackClock.
    Enqueue`原样把解码出来的PCM送进WASAPI缓冲区；**这一半后来在第109条里实现了**（只有
    淡入，淡出仍然缺失，见第109条的完整说明）。(c) `MediaFile.BackgroundAudioVisual`的三个选项里只有
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
67. **【部分实现，原为已知缺口】第59条(b)"已绑定的显示器运行中途被完全拔掉"这个场景现在会
    干净地"断"，不再是静默什么都不做**：`HandleDisplaySettingsChanged`里
    `MonitorService.GetBoundExtendedDisplay`返回`null`（找不到那块已绑定的扩展屏了）时，这次改成
    调用`OutputStateMachine.Disconnect()`——跟用户手动点"断"完全同一条路径：恢复音频、隐藏（反正
    也没地方看得见的）overlay窗口、停掉任何正在进行的设备投屏；如果当时本来就是Idle状态，
    `Disconnect()`本身是no-op，不会有任何多余动作。选择调用`Disconnect()`而不是把
    `_overlay`/`_videoSurface`/`_playback`整个销毁重建：这套对象图仍然保留着，为的是显示器之后
    被重新插回时，下一次`OnDisplaySettingsChanged`触发能直接落进已有的两个分支之一处理——识别成
    同一块显示器（`MonitorInfo`结构相等）就什么都不用做，识别成位置/分辨率变了就走已有的
    `Rebind`/`Resize`路径，不需要为"重新插回"单独写一条新逻辑。**仍然没有解决的部分**：`Disconnect()`
    不会主动通知正在投屏的Caster——这跟`OnOutputStateChanged`里其他"断"路径的既有约定一致
    （Caster靠`CastStatusMessage`报告的消失自己发现），不是这次遗漏；第59条(a)"启动时没绑定、
    运行中途插入新显示器"仍然完全没有处理，原因不变（需要的对象生命周期改动大得多，见第59条）。
    这个改动本身完全没有真实Windows机器/真实显示器拔插可以验证，只是让"逻辑上应该发生什么"这件事
    从"完全没写"变成"照抄一条已经存在、已经被其他调用方用过很多次的`Disconnect()`路径"，属于
    降低风险而不是消除风险。
68. **【部分实现，原为已知缺口】`SendCastStatus`现在真的会重传**（对应`EveryStage.Caster`README
    第34条"状态回报本身也是尽力而为的UDP、没有重传"）：新增`RetransmitCastStatusAsync`，在
    主发送之后约400ms（`StatusRetransmitDelay`）把**同一个**`CastStatusMessage`实例（同一个
    `SentAtUtc`，不是重新构造一份新报告）再发一次——字面意义上的"重传"，不是"发得更频繁"。选
    400ms而不是0ms（紧接着连发两次）：如果丢包是短时突发（比如一次Wi-Fi重传风暴），间隔0ms的
    两次发送很可能被同一次突发一起吞掉，隔开一点时间理论上更可能躲开同一次突发，虽然这个仓库完全
    没有真实网络环境可以验证这个直觉对不对。发送前重新检查`_castReceiver`/`_castingCasterEndPoint`
    是否还是延迟开始时捕获的那一份（跟`LogConnectionQualityAsync`同一个"discovery异步操作完成时
    重新核实状态没变"的写法）——投屏可能在这400ms期间已经停止或换了另一个Caster，这时候补发一份
    针对旧状态的报告没有意义。**仍未解决的部分**：这不是ACK/重试协议，`CastStatusMessage`整体依旧
    完全"尽力而为"；`EveryStage.Caster`README第35条描述的"discovery socket整体变差导致状态通道
    系统性不健康"这种失败模式（不是偶发单包丢失）两次发送很可能同时受影响，这次改动对此基本没有
    缓解；副作用是如果真的是重传副本才让Caster收到，`LastStatusLatencyEstimate`算出来的延迟会比
    真实值多约400ms（`SentAtUtc`沿用的是原始发送时刻），这个偏差只在"第一次发送真的丢了"这个本来
    就不常见的情况下才会出现，被认为可以接受。
69. **【已实现，原为已知缺口】`IContentRenderer.NextPage`/`PreviousPage`/`PageCount`/
    `CurrentPageIndex`终于有了第一个真正的调用方——PDF"翻页"（PLANNING.md §3）**：这四个成员从
    `PdfContentRenderer`/`ImageContentRenderer`写出来那一轮起就存在（`NextPage`/`PreviousPage`自己
    的doc comment甚至早就写好了"调用方应该把返回false当成'翻不动了'而不是错误"这种前瞻性说明），
    但`PlaybackEngine`一直只调用`LoadAsync`/`SetTargetSize`/`CurrentFrame`/`Dispose`，从未真正翻过
    页——一整块渲染层已经写好的能力，上面完全没有接线。`ContentSurface`自己的doc comment甚至已经
    写着"after each page turn or file change"，这次才第一次真的对应上"page turn"这一半。这次把
    悬浮预览窗现有的"上一项/下一项"两个按钮改成了双重用途：`PlaybackEngine.NextManual`/
    `PreviousManual`现在会先尝试`_pdfRenderer.NextPage()`/`PreviousPage()`（仅当当前文件是
    `MediaKind.Document`），翻页成功就更新`ContentSurface`的画面并返回，只有翻到最后/第一页
    （`NextPage`/`PreviousPage`返回`false`）才落回原来的"移动到播放列表里的下一/上一个文件"
    行为——不是新加两个按钮，而是复用现有两个按钮，跟按钮上"上一项/下一项"这几个字面意思略有出入，
    所以新增了`PlaybackEngine.DocumentPageInfo`（`(CurrentPage, PageCount)?`，只在真正的多页
    Document上非空）和悬浮预览窗新增的`_pageLabel`（显示"第X页/共Y页"），让这个行为差异对用户
    可见，而不是让同一对按钮悄悄换了含义却没有任何提示。**故意做出的范围限定**：(1)
    `MediaFile.AllowManualSkip`不影响翻页——这个字段的doc comment说的是"是否允许跳过这个文件本身"，
    不是"是否允许在文件内部翻页"，两者是不同的产品概念，`TryAdvance`里原有的`AllowManualSkip`检查
    只在翻页耗尽、真的要移动到另一个文件时才会生效；(2) 翻页不重置`ArmStayDurationTimer`(停留时长
    计时器)——PLANNING.md §6把"停留时长"列为"图片/文档"整个条目的属性，不是按页计时，这次没有改变
    这个前提，翻页多久都不会延长或缩短整份文档的停留时长；(3) 翻页不经过`PlaybackLogger`，跟
    `Pause`/`Resume`是同一个先例（这两个方法本来就不记日志）；(4) 悬浮预览窗的缩略图/画面刷新
    完全依赖既有的500ms轮询（`RefreshFromEngine`），翻页没有额外触发`FileStarted`或任何新事件——
    因为这不是"开始播放一个新文件"，是同一个文件内部的状态变化，勉强触发`FileStarted`会是语义上
    的误用。窗体`ClientSize`从(220,214)涨到(220,232)以容纳新增的`_pageLabel`。
70. **【已实现，原为已知缺口】`DevicesPanel`新增"编辑权限"按钮——配对之后终于能改
    信任状态/允许被投放/允许被监看了，不用整个移除重新配对**：`PairedDevice.TrustMode`/
    `AllowCast`/`AllowMonitor`这三个字段之前只有`PairingConfirmationDialog`（首次配对确认弹窗）
    一个地方能设置，`DevicesPanel`只是只读展示（"是/否"两列文字），一旦配对完成，唯一能改变这些
    字段的办法就是"移除配对"（整个忘记这个设备）再等它重新发起配对请求——这不是产品决策，只是
    这三个字段从写出来那一轮起就没人给它们补上编辑入口。新增`UI/EditPairedDevicePermissionsDialog.cs`
    （跟`PairingConfirmationDialog`外观相似但语义不同：没有"待处理的配对请求"这回事，按钮是
    "保存/取消"不是"接受/拒绝"），`DevicesPanel`新增"编辑权限"按钮（选中一项才启用，跟"移除配对"
    同一个交互模式），保存时直接修改`PairedDeviceStore.All`里那个`PairedDevice`实例的三个字段
    再调用`Upsert`——复用`DiscoveryService.RespondToPairing`已经在用的"这就是这个DeviceId当前的
    真相，写回去"这套`Upsert`语义，没有另外造一个`Update`方法。**顺带补上的诚实提示**：这次发现
    "允许被监看"这个权限位从这个仓库最早的配对功能那一轮起就只是被采集/持久化/展示，PLANNING.md
    §7提到的"监看"这个能力本身（投屏机远程查看终端机当前状态）在这个仓库里完全没有任何实现——
    不是被这次改动砍掉的功能，是从来就没有过。之前两个勾选框（`PairingConfirmationDialog`和这次
    新增的`EditPairedDevicePermissionsDialog`）上都没有任何提示，勾上"允许被监看"看起来像是在
    启用一个真实功能，实际上什么都不会发生——这次给两个弹窗都加了一行"（监看功能本身尚未实现，
    此开关暂无实际效果）"的灰色小字，跟`AudioPropertiesDialog`给`AudioVisual.Waveform`标"当前是
    占位"是同一个"不要让还没做的功能看起来像做完了"的做法。**这次没有做的部分**：真正的"监看"
    功能本身（协议消息、终端机侧采集什么状态给投屏机看、投屏机侧UI）完全没有开始，需要的是全新的
    协议设计而不是"接线"，属于跟WPS COM互操作、背景音轨叠加同一档的、需要真正产品决策的大改动，
    这次故意没有尝试；权限变更本身也没有接入任何日志（`DeviceConnectionLogger`目前只有配对/断开/
    连接质量几类，没有"权限变更"这一类，PLANNING.md §14.4也没有明确要求记录这个），如果以后需要
    审计权限变更历史，需要单独设计。
71. **【已实现，原为已知缺口】`MediaFile.StayDuration`（单文件停留时长覆盖）终于有了编辑入口——
    这次不只是"缺了UI"，是`SettingsPanel`自己的说明文字之前一直在描述一个不存在的功能**：
    `StayDuration`从这个字段加进数据模型那一轮起，`PlaybackEngine.ArmStayDurationTimer`就在读它、
    `ActivitiesPanel.CloneFile`深拷贝活动文件时也在正确地复制它，行为这一半完全没问题——但从来没有
    任何UI能够设置它，跟`PlayMode`/`AllowManualSkip`/`IsBackgroundAudio`这几个字段当初"先有行为，
    UI跟进"的缺口是同一类。比那几个更严重的是：`SettingsPanel`"默认停留时长"那段说明文字（"单个
    文件自己设置的停留时长（活动面板里配置）始终优先于这里的默认值"）在这次改动之前一直在向用户
    描述一个从来没做出来的功能——不是文档滞后于代码，是文档提前"承诺"了代码还没兑现的东西。这次
    新增`UI/StayDurationDialog.cs`（跟`SettingsPanel`"启用默认停留时长"同一套"勾选框启用/禁用一个
    `NumericUpDown`"写法，只是这次作用在单个文件的覆盖值上），`ActivitiesPanel`新增"停留时长..."
    按钮，只在选中一个`Kind`是`Image`或`Document`的文件节点时启用（PLANNING.md §6"停留时长（图片/
    文档）"），保存时通过`FileOperationLogger.LogPlaybackPropertyChanged`记录变更，取消覆盖（值
    变回`null`）时`oldValue`/`newValue`跟`OnEditPlayMode`一样让`null`原样记成JSON null而不是字符串
    "null"，同一套已经确立的约定。这样一来，`SettingsPanel`那段说明文字现在终于是真的了。
72. **【已实现，原为已知缺口】`Activity.IsCollapsed`（活动是否折叠）以前只是被采集、从来没有真正
    驱动过`TreeView`，用户手动折叠的活动会在几乎每次操作后被静默重新展开**：`Activity`类doc
    comment从写出来那一轮起就说这个字段对应PLANNING.md §6/§11的"可折叠"，`OnSaveAsScenario`克隆
    活动时也在正确地复制它，但`RefreshTree`一直是`activityNode.Expand()`——不看这个字段，无条件
    展开每一个活动节点；同时没有任何代码在用户手动折叠/展开某个活动节点时把结果写回`IsCollapsed`。
    比"这个功能完全没做"更容易被忽略的是它造成的实际体验：`RefreshTree`在这个面板里几乎每个操作
    之后都会被调用（加文件、删文件、上移/下移、编辑播放方式/音频属性/停留时长……），也就是说用户
    刚手动折叠某个活动，紧接着做任何一个其他操作，那个活动就会被无声地重新展开——这不是"功能缺失"
    的沉默失败，是一个看得见、会让人以为"点了没反应"的真实体验问题。这次的修复是双向的：(1)
    `RefreshTree`改成按`activity.IsCollapsed`决定调用`Collapse()`还是`Expand()`，不再无条件展开；
    (2) 新增`_tree.AfterCollapse`/`AfterExpand`订阅，用户真的手动切换某个活动节点的折叠状态时
    把结果写回`IsCollapsed`并`_repository.Save(_store)`持久化。这两者之间刻意没有互相触发：
    `RefreshTree`自己调用`Collapse()`/`Expand()`的时机是在一个刚`new`出来、还没加进`_tree.Nodes`
    的`TreeNode`上，这时候调用不会经过`TreeView`的事件管线，不会触发`AfterCollapse`/`AfterExpand`，
    也就不会又反过来触发一次没必要的`Save`——这是WinForms `TreeNode`本身的行为，不是这次改动特意
    加的防护逻辑，只是恰好利用了这个既有事实。**没有额外做的部分**：文件叶子节点没有自己的子节点，
    物理上不可能触发折叠/展开事件，`OnActivityCollapseStateChanged`里的`node?.Tag is not Activity`
    判断只是防御性的，不是这次发现的真实场景。
73. **【已实现，原为已知缺口】`MediaFile.OnCompletion`（PLANNING.md §6"播放完成后动作：自动下一项/
    循环/停留等待"）终于有了编辑入口——跟上一条`StayDuration`同一类"行为早就对了、UI从来没补上"
    的缺口**：`PlaybackEngine.HandleCompletion`从这个仓库第一次真正实现播放行为那一轮起就在正确
    读取并执行`OnCompletion`（`NextItem`/`Loop`/`HoldOnLastFrame`三选一），`ActivitiesPanel.CloneFile`
    也一直在正确复制它，但从来没有任何UI能把它从默认值`NextItem`改成别的。新增
    `UI/CompletionActionDialog.cs`——跟`PlayModeDialog`处理`Activity.DefaultPlayMode`时用的
    `allowInherit: false`分支是同一种布局（一个`ComboBox`+确定/取消，没有"覆盖/继承"勾选框），
    因为`OnCompletion`在数据模型里根本没有"继承活动默认值"这个概念（不存在
    `Activity.DefaultCompletionAction`）。`ActivitiesPanel`新增"完成后动作..."按钮，**故意不像
    `_stayDurationButton`/`_audioPropertiesButton`那样按`MediaKind`限制启用条件**——只要选中了
    文件就启用，因为`HandleCompletion`本身对视频、独立播放的音频、图片/文档的停留计时器这几条路径
    一视同仁，没有理由让某个`Kind`的文件不能设置这个属性。保存时同样接入
    `FileOperationLogger.LogPlaybackPropertyChanged`，用`ToString()`记录枚举值变化。
74. **【已实现，原为已知缺口】`DeviceConnectionLogger.LogUnpaired`终于有了第一个真正的调用方**：
    PLANNING.md §14.4"设备连接记录"明确要求记录"配对/取消配对"——`LogPaired`/`LogConnected`/
    `LogDisconnected`三个从`DiscoveryService`那一侧早就有真正的调用方了，唯独`LogUnpaired`从这个
    类第一轮写出来起就没人调用过：`DevicesPanel`"移除配对"按钮的`OnRemoveClick`只调用了
    `_pairedDevices.Remove(device.DeviceId)`，没有记任何日志——用户主动取消配对这件事，PLANNING.md
    明确点名要记录，之前完全没有落地。这次把`DeviceConnectionLogger`（`Program.cs`里
    `TerminalApplicationContext`早就持有的那个共享实例，之前只传给了`DiscoveryService`）一路传进
    `MainWindow`构造函数、再传进`DevicesPanel`构造函数，`OnRemoveClick`确认移除后调用
    `_connectionLog.LogUnpaired(device.DeviceId.ToString())`。跟这个仓库其他几次"补上一个从早期
    日志轮次起就没有调用方的方法"（`LogPlaybackPropertyChanged`、`LogQualityMetric`）是同一个模式：
    不是接线漏掉了，是当初这个UI动作（"移除配对"按钮）本身还没做出来，方法先写好等着。
75. **【部分实现，原为已知缺口】`CastReceiver`现在会校验收到的RTP包PayloadType**（对应
    `EveryStage.Transport`README风险第4条）：`DiscoveryProtocol.CastStartMessage.PayloadType`/
    `AudioPayloadType`早就存在，`DiscoveryService.CastStartInfo`也早就接住了它们，但一直没有传
    到`CastReceiver`更深处——`RtpReceiver`/`RawRtpReceiver`完全不检查收到的包是否跟预期一致。这次
    `CastReceiver`构造函数新增`payloadType`/`audioPayloadType`两个可选参数，`Program.cs`的
    `OnCastStartRequested`把`info.PayloadType`/`info.AudioPayloadType`传进去，`RtpReceiver`/
    `RawRtpReceiver`收到PayloadType不匹配的包时当成"不是我们的包"直接丢弃，计入两个类各自新增的
    `PayloadTypeMismatches`计数器。**这不是真正的SDP式协商**，只是"跟本项目自己硬编码的常量比对"；
    在这个仓库自己的Caster↔Terminal流量里预期这个计数器永远是0，这个校验存在的意义是防御同一
    端口上的陌生/无关RTP包或未来的协议版本不一致，不是当前会真的触发的场景。**这次没有做的部分**：
    `PayloadTypeMismatches`目前只是计数器，没有接入`EstimatedPacketLossPercent`或任何UI/日志展示；
    这个改动本身也没有在沙箱里跑过（没有dotnet），完全依赖代码审阅。
76. **【发现并修复】`VideoSurface.Resize`跟`SwapChainPresenter.PresentFrame`之间原来完全没有互斥
    保护——一个真实的并发bug，不是假设性的**：这次在给`VideoContentController.Resize`（一直没有
    调用方，见下文）找调用方之前，先去追踪它为什么会存在，结果发现`VideoContentController`自己
    有一把`_presenterLock`，但只在自己内部的`PresentFrame`调用和自己的`Resize`方法之间生效——而
    真正调用`Resize`的地方（`Program.cs`的`HandleDisplaySettingsChanged`）根本没有经过
    `VideoContentController`，是直接调用`_videoSurface.Resize(...)`，所以这把锁从来没有真正保护过
    任何东西；`Receiving.CastReceiver`自己的呈现调用（`_surface.Presenter.PresentFrame`）更是完全
    不经过这把锁——它甚至看不到`VideoContentController`的私有字段。也就是说，`VideoContentController`
    的解码/呈现线程、`CastReceiver`的解码/呈现线程、以及UI线程上因为显示器配置变化触发的`Resize`
    调用，三者之间对同一个`SwapChainPresenter`实例完全没有互斥——而`SwapChainPresenter.Resize`
    会`Dispose()`并重建`PresentFrame`同时在读的`_backBuffer`/`_outputView`/`_processor`/
    `_enumerator`这些字段，两者真的并发执行是典型的D3D11"呈现的同时重建交换链"竞态。**修复**：把
    锁从`VideoContentController`移到真正被共享的`VideoSurface`本身——新增`VideoSurface.PresentFrame`
    包装方法和内部的`_lock`，`Resize`和`PresentFrame`现在互斥；`VideoContentController`/
    `CastReceiver`都改成调用`_surface.PresentFrame(...)`而不是直接碰`_surface.Presenter`。
    `VideoContentController.Resize`本身**没有被保留、也没有被给一个调用方**——它存在的唯一理由
    （包一层锁）现在完全由`VideoSurface`自己做到了，继续保留这个转发方法只是没有意义的多一层
    间接，删掉比"接线"更符合这次发现的教训。**这次没有解决/无法验证的部分**：修复本身完全没有
    在真实Windows/GPU环境跑过（没有dotnet），锁是否真的按预期覆盖所有三个访问路径完全依赖代码
    审阅；`Resize`发生频率低、`PresentFrame`发生频率高，两者互斥意味着`Resize`偶尔要等一次
    `PresentFrame`完成（反过来也一样），这个延迟量级在真机上是否可接受没有测过。
77. **【新增】PLANNING.md §11"异常提示：右下角Toast通知栈"——这个仓库里第一个完全没有任何代码、
    也从未被任何README提及过的PLANNING.md具体UI元素，直到这一轮才发现这个缺口**：§11原文"按严重
    程度分色（红=严重/黄=提示），涉及播放的异常需带可执行按钮（重试/移除），非关键提示不强加按钮"，
    在这次发现之前完全没有任何实现，也没有作为已知缺口出现在任何一个README里——最接近的东西是
    `MainWindow._statusLabel`那一行常驻文字（用于`PlaybackDeclinedByCastSwitch`，第9条），跟"通知栈"
    完全不是一回事：没有堆叠、没有颜色、没有按钮。新增`UI/ToastNotification.cs`（一条Toast：消息+
    严重程度色条+关闭按钮+可选的动作按钮）和`UI/ToastStack.cs`（管理堆叠、定位到`MainWindow`右下角，
    新Toast出现在栈底、旧的被推高——同时也刻意不使用WinForms的`Anchor`机制来处理"自身`Height`变化时
    重新定位"这件事：这个沙箱没有dotnet无法验证`Anchor`在这种场景下的确切行为，改成每次内容变化都
    根据`Parent.ClientSize`当前值重新计算`Location`，逻辑上更容易脱离编译器独立确认对不对）。
    这次给它接上的第一个（也是目前唯一一个）真正的生产者：`PlaybackEngine`新增
    `PlaybackAbnormallyInterrupted`事件，在`OnVideoFailed`/`OnAudioFailed`里紧跟着已有的
    `LogAbnormalInterruption`日志调用之后触发——**这本身也是这次意外发现的第二个缺口**：一次解码/
    渲染异常之前只会被记进日志，画面就那样冻结在最后一帧上，没有任何面向操作者的提示，`OnVideoFailed`
    自己的旧注释还写着"PLANNING.md doesn't say"（哪知道其实§11写得很清楚，只是当初没找到）。
    "重试"按钮调用新增的`PlaybackEngine.RetryCurrentFile()`（重新播放`CurrentFile`，不改动
    `_currentActivity`/`_currentFileIndex`，就是"再试一次这个文件"而不是"换一个"）。"移除"按钮
    根据新增的`PlaybackEngine.CurrentActivity`（`null`还是非`null`）在两个已有的移除操作之间二选一：
    有活动上下文就复用`ActivitiesPanel.OnRemoveFile`的逻辑（从活动文件列表移除+
    `LogActivityModified`+保存+刷新树），没有（比如从文件面板直接双击播放的）就复用
    `FilesPanel.OnRemoveClick`的逻辑（从文件库移除+`LogFileRemoved`+刷新网格）——刻意跳过这两个
    面板按钮各自原有的二次确认弹窗，因为点一个命名明确的Toast动作按钮本身已经是深思熟虑的操作，
    不需要再确认一次。**这次没有解决的残留风险**：(1) 从活动里"移除"之后，没有同步修正
    `PlaybackEngine`内部的`_currentFileIndex`（现在可能指向列表里一个不同的文件，因为列表变短了）
    ——下一次自动前进可能会跳到意料之外的文件，需要操作者手动通过悬浮预览窗导航或者重新连接来
    恢复一致状态，这次没有尝试同步修正这个索引；(2) `ToastStack`固定宽度320px，如果`MainWindow`
    被用户缩小到比这更窄，Toast可能部分或全部超出窗口左边界，没有设置`MinimumSize`防御这种情况；
    (3) "非关键提示不强加按钮"这一半——非关键、不需要动作按钮的Toast（比如可以把现有的
    `PlaybackDeclinedByCastSwitch`迁移过来）——这次完全没有触碰，`_statusLabel`那条既有机制原封
    不动地保留，`ToastStack`目前只有"播放异常"这一种红色/严重级别的生产者；(4) 整个功能从来没有
    在真实Windows机器上跑过、也没有dotnet编译验证过，`ToastNotification`的固定布局尺寸是否真的
    在真机的默认字体/DPI下不裁剪文字完全没有验证。
78. **【文档修正，非功能变更】`FloatingPreviewWindow`里解释"为什么不做召回按钮"的注释已经过时**：
    这条注释原本说"Phase 4主面板还不存在，所以做不了召回"，但`MainWindow`从更早的一轮起就已经
    存在了，这个理由早就不成立——这次同一轮排查PLANNING.md跟README差异时顺带发现并改正了这句话。
    改正之后的真正理由：PLANNING.md §16第5项自己就把"悬浮预览窗与主面板预览缩略图的召回交互细节"
    列为"待确认/待验证事项"，并且点名了一个这个仓库完全没有的东西——"主面板预览缩略图"（文件面板
    目前是缩略图网格，不是"预览"缩略图，两者语义未必相同）。既然PLANNING.md原作者自己都还没确定
    这个交互该怎么设计，这个仓库现在去猜一个具体实现出来，风险跟"监看"功能（第70条）是同一类——
    不是接线缺口，是需要真正产品决策的开放问题。**已经覆盖的部分**：悬浮预览窗的"意外关闭"这个
    真正的痛点，早就被"X按钮只隐藏不真正关闭"这个既有行为实质性解决了（没有东西会真的丢失），
    缺的只是"从主面板显式召回"这个附加交互，不是恢复丢失状态那么紧急。
79. **【部分实现，原为已知缺口】PLANNING.md §11"批量选择"三个动作（加入活动/统一设置属性/删除）
    里，"删除"这一半现在真的能批量操作了**：`FilesPanel._listView`原来固定`MultiSelect = false`，
    构造处注释早就点明"batch selection (§11) isn't implemented yet"，但这句话从来没有进入过任何
    README。这次改成`MultiSelect = true`，`OnRemoveClick`从"只读`SelectedItems[0]`"改成遍历
    `SelectedItems`里所有能转成`MediaFile`的项，确认弹窗按数量显示单数/复数不同的文案，逐个调用
    `_library.Remove`/`_fileOpLog.LogFileRemoved`。**刻意的简化**：没有像PLANNING.md原文"选中后
    悬浮工具栏出现"那样另建一个悬浮工具栏，而是直接复用现有那个常驻工具栏上的"移除"按钮——它本来
    就已经是"只在选中时启用"，跟"选中后出现"是同一种精神、只是视觉呈现不同，为了这点差异去单独建
    一套悬浮UI不值得。**没有做的部分**：批量选择支持的三个动作里"加入活动"/"统一设置属性"两个
    仍然完全没有实现，见"尚未开始"——它们各自需要新的跨面板管线或者PLANNING.md没有展开的多选编辑
    交互细节，不是这次这种纯"打开一个已有开关"级别的小改动。
80. **【已实现，原为已知缺口】`CastReceiver.PayloadTypeMismatches`终于有了消费方，一路报回给
    Caster**：`EveryStage.Transport`的`RtpReceiver`/`RawRtpReceiver`早就在追踪这个计数器（见
    `EveryStage.Transport`README风险第4条），但它只是个纯本地计数器，Terminal自己都没在任何地方
    展示过，更别说告诉Caster。这次给`CastReceiver`加了合并视频+音频两路的`PayloadTypeMismatches`
    属性，`SendCastStatus()`把它塞进`DiscoveryProtocol.CastStatusMessage`新增的同名字段一起发出去
    ——跟`FramesDecoded`/`VideoBytesReceived`等其他几个"Terminal自己知道、Caster需要被告知"的字段
    走的是完全一样的路径。**为什么现在才做**：这个字段本身是这次改动里比较边缘的一环——它统计的是
    "同一端口收到了陌生/无关的RTP包"这种预期中几乎永远不会发生的防御性场景，不像`FramesDecoded`
    那样是投屏这个核心功能本身就需要的可观测性，所以直到`EveryStage.Transport`那条风险自己写明
    "没有接入任何UI/日志展示"之前，一直没有单独优先级去补这条管线。**已知限制**：`CastStatusMessage`
    这个协议本身没有版本协商（见`EveryStage.Discovery`README对应新增条目）——一个跑旧代码的
    Terminal发的报告没有这个字段，Caster这边反序列化会得到默认值`0`，跟"确实没有发生不匹配"在协议
    层面完全无法区分。
81. **【已实现，原为已知缺口】独立音频播放现在有真正的暂停/恢复了**：见"尚未开始"里"音频以横向
    播放条展示"那条列的四项缺失能力，"暂停/恢复"是其中风险最小的一项——不像"进度拖动"需要
    `AudioDecodeSource`支持seek、"音量"需要给`AudioPlaybackClock`加音量控制、"独立投屏按钮"连
    PLANNING.md本身都没说清楚具体含义，暂停/恢复只需要`AudioPlaybackClock`底下的`WasapiOut`本来
    就有的`Pause()`/`Play()`两个原语——跟`VideoContentController`不同（它的`Stop()`把解码源整个
    拆掉，真正的暂停/恢复需要支持"原地挂起再恢复"，这个仓库没有做），`AudioPlaybackClock`不需要
    拆解码源：`Pause()`只是让WASAPI输出暂停，`PositionTicks`停在原地不再前进，后台解码循环
    （`AudioContentController.RunPlaybackLoopCore`）本来就是"不能超前`PositionTicks`太多就
    停下来等"的节奏，`PositionTicks`一停，解码循环自己就会自然阻塞在已有的等待逻辑里，不需要另外
    教它"暂停"这个概念。链路：`AudioPlaybackClock`新增`Pause`/`Resume`（包`WasapiOut.Pause`/
    `Play`）→ `AudioContentController`新增同名方法转发 → `PlaybackEngine.Pause`/`Resume`原来
    "`_stayDurationTimer == null`就直接返回"的判断改成先分支处理"当前文件是非背景音频"这一种情况
    → `FloatingPreviewWindow.RefreshFromEngine`原来"暂停按钮只对图片/PDF启用"的判断加上音频这一种
    情况（不加这一步的话新增的能力从UI根本按不到，是真正让这条链路有第一个调用方的最后一步，不是
    可有可无的收尾）。**仍然没有做的部分**："横向播放条"四项缺失能力里，进度拖动/音量/独立投屏
    按钮这三项依然完全没有实现，见"尚未开始"对应条目更新；这次改动本身没有在这个沙箱里跑过（没有
    dotnet），`WasapiOut.Pause()`之后再调用`Play()`是否真的从暂停处继续而不是从头开始，完全依赖
    NAudio文档描述的`IWavePlayer`语义，没有真机验证过。**更新（见第83条）**：这条原本还说"视频的
    暂停/恢复仍然是文档化的no-op"——重新读`VideoContentController.RunPlaybackLoopCore`之后发现
    这个判断错了，视频的节奏本来就完全由同一个`AudioPlaybackClock`驱动，第83条已经把这里的暂停/
    恢复也接上了。
82. **【已实现，原为已知缺口】独立音频播放现在也有音量调节了**：紧接着第81条暂停/恢复用的同一个
    "先补底层能力再决定要不要接UI"顺序——`WasapiOut`本来就暴露的`Volume`属性（0.0-1.0）之前完全
    没有被这个仓库的任何代码读写过。链路：`AudioPlaybackClock`新增`Volume`属性（直接包
    `WasapiOut.Volume`）→ `AudioContentController`新增同名属性，值跨越`Play()`调用持久化（不像
    `_audioClock`本身每次`Play()`都会被销毁重建，音量设置一次要在换下一个文件时继续生效，不能
    每次都被静默重置成满音量）→ `PlaybackEngine`新增`AudioVolume`，即使还没有任何音频播放过也能
    先设置（存在`_pendingAudioVolume`里，真正的`AudioContentController`第一次被懒加载出来时会
    把这个值带过去，不会丢）→ `FloatingPreviewWindow`新增"－/音量文本/＋"一行，10%固定步进，只在
    当前文件是非背景音频时启用。**为什么用两个按钮而不是滑块**：PLANNING.md §8.2描述的"横向播放条"
    本身（含真正的进度条/音量滑块）在这个仓库里完全不存在，为悬浮预览窗单独造一个还没有先例的
    `TrackBar`控件，会在"这个控件到底怎么布局/怎么渲染，没有编译器验证不了"的风险之上再加一层——
    复用这个窗口其他控件已经在用、已经验证过能正常工作的Button交互语言，风险不会叠加。**仍然没有
    做的部分**：真正的"横向播放条"UI（含进度条/音量滑块等控件布局）依然完全没有开始；"横向播放条"
    剩下两项缺失能力（进度拖动/独立投屏按钮）依然完全没有实现，见"尚未开始"对应条目更新；这次改动
    本身没有在这个沙箱里跑过（没有dotnet），`WasapiOut.Volume`的取值范围/超出`[0,1]`时的行为完全
    依赖NAudio文档描述，没有真机验证过。
83. **【已实现，原为已知缺口】视频播放现在也有真正的暂停/恢复了——之前"这个仓库没有做"的判断
    是错的，只是没人重新验证过**：`PlaybackEngine.Pause`自己的doc comment以及第81条最初都说过
    "视频暂停需要`VideoContentController`支持原地挂起再恢复，这个仓库没有做"，这个说法在写下来的
    时候依据的是`Stop()`会把解码源整个拆掉——但`Stop()`和"能不能暂停"其实是两个独立的问题，重新读
    一遍`RunPlaybackLoopCore`才发现：
    视频这条循环的节奏（要不要呈现下一帧、要不要继续读下一个音频块）完全由`_audioClock.PositionTicks`
    驱动，跟独立音频播放用的是同一个`AudioPlaybackClock`类。也就是说第81条给`AudioPlaybackClock`
    加的`Pause()`/`Resume()`（包`WasapiOut.Pause()`/`Play()`）对视频这条循环同样有效：暂停时
    `PositionTicks`停止前进，`RunPlaybackLoopCore`里那个`while (... audioClock.PositionTicks <
    frame.Value.TimestampTicks - FrameBudgetTicks) ...`自旋等待就永远不会退出——不会呈现下一帧，
    不会读下一个音频块，SwapChain本来就会一直显示上一次`Present`呈现的画面（没有新的`Present`调用
    之前它不会自己变化），已经解码出来但还没呈现的那一帧（`frame`这个局部变量持有的纹理）也还
    没被释放，恢复时从它当时卡住的地方继续，不需要重新解码，也不会跳帧。**没有触碰的部分**：
    `VideoContentController.Stop()`本身完全没有变——真正的"停止"仍然会把解码源整个拆掉，这次新增
    的`Pause()`/`Resume()`是`Stop()`之外的第三、第四个方法，不是给`Stop()`本身加了保留状态的能力。
    链路：`VideoContentController`新增`Pause`/`Resume`（转发到`_audioClock`，跟
    `AudioContentController`的同名方法几乎一样）→ `PlaybackEngine.Pause`/`Resume`加上
    `MediaKind.Video`分支 → `FloatingPreviewWindow.RefreshFromEngine`"暂停按钮启用条件"加上
    `MediaKind.Video`。**没有音频轨道的视频这种情况没有专门处理**：`VideoContentController.Play`
    本来就无条件构造`AudioPlaybackClock`（不管视频有没有音频轨道），这是这次改动之前就有的既有
    行为，这次的暂停/恢复只是复用这个早就存在的假设，没有让它变得更好或更差。**仍然没有做的部分**：
    这次改动本身没有在这个沙箱里跑过（没有dotnet），依据完全是重新阅读现有代码逻辑推出来的，没有
    真机验证过恢复后是否真的严丝合缝（比如`WasapiOut`暂停期间`BufferedWaveProvider`是否有边界
    情况没考虑到）。
84. **【新发现的真实bug，已修复】活动的顺序自动播放队列，如果排到一个"背景音频"文件，之前会
    静默卡死在那里，永远不会往下走**：修上面第83条给`PlaybackEngine.Pause`加视频分支时，顺带
    重新核对了`PlayFile`里`MediaKind.Audio`分支对`IsBackgroundAudio == true`那个早就存在的
    `return;`——这条分支本身该不该做背景音频叠加播放（需要真正的多轨并发模型）不是这次要解决的，
    但这个`return;`意味着这个文件的完成事件永远不会触发：既没有启动`VideoController`/
    `AudioController`（不会有`PlaybackCompleted`事件），也没有武装停留时长定时器（不会有Tick），
    `HandleCompletion`因此永远不会被调用，`PlayMode.SequentialAuto`的活动队列排到这一项就会
    永远停在那里——不是"没做完这个功能"这么简单，是一个真正的功能性bug：只要活动里混了一个
    背景音频文件（`AudioPropertiesDialog`的"背景音频"复选框早就能勾选，见风险第62条的音频属性
    编辑UI），整个活动从这里开始的所有后续内容都放不出来了。**修复方式**：不是把这条
    分支接进`HandleCompletion`的`OnCompletion`大switch里——`Loop`对一个从来没真正播放过任何东西
    的文件没有意义，而且会立刻在同一个分支里重新进入自己，变成一个不停自旋的死循环，比原来的
    "静默卡死"更糟；只处理`NextItem`+`PlayMode.SequentialAuto`这一种真正需要修的组合，通过
    `BeginInvoke`延后调用`TryAdvance(1, ...)`直接跳到下一项（跟`OnVideoCompleted`/
    `OnAudioCompleted`一样的"延后到下一次UI线程消息循环再处理，不要在`PlayFile`自己还没退出的
    时候重入"套路）；`ManualSelect`/`Loop`/`HoldOnLastFrame`这三种组合保持原样"卡在这一项不动"——
    这不是bug，是`PlayMode.ManualSelect`一直以来的正常语义（操作员手动决定下一项播什么），只有
    `SequentialAuto`+`NextItem`这一种组合下"卡住不动"才是真正的bug（活动本该自动往下走，而不是
    等操作员发现playback已经停了）。**没有解决的部分**：背景音频叠加播放本身依然完全没有实现，
    这次只是让"没实现"从"整个活动卡死"降级成"跳过它，继续放下一项"；这次改动本身没有在这个沙箱
    里跑过（没有dotnet），没有真机验证过。
85. **【新增】活动面板的树状列表现在会直接标出背景音频文件，不用再逐个打开"音频属性..."才知道**：
    紧接着第84条那个bug——修完之后"背景音频文件会被自动跳过"这件事本身没有任何可见提示，操作员
    在树里看到的还是跟普通音频文件一模一样的文件名，容易观察到"这个活动播着播着就跳过了一项"却
    不知道为什么。`RefreshTree`新增`BuildFileNodeText`辅助方法，`IsBackgroundAudio == true`的
    文件节点文字后面加上"`[背景音频-尚未实现，会被跳过]`"。**顺带修的一个必然后果**：
    `OnEditAudioProperties`（"音频属性..."对话框的确定按钮）之前只调用`_repository.Save`，不调用
    `RefreshTree`——因为在这次改动之前，`IsBackgroundAudio`/`BackgroundAudioVisual`这两个属性
    压根不影响树里显示的任何文字，跟`OnEditPlayMode`/`OnEditStayDuration`/`OnEditCompletionAction`
    这三个"编辑属性但不用刷新树"的方法是同一个道理。现在`IsBackgroundAudio`会影响节点文字了，
    这个方法必须补上`RefreshTree()`调用，否则勾选/取消勾选"背景音频"复选框之后，树上的标签要等到
    下一次无关操作（比如切换方案）触发全量刷新才会更新，变成一个新的、这次改动自己引入的显示滞后
    问题。`RefreshTree()`本身会清空重建整个`TreeView`，选中状态会丢失——这跟`OnRenameActivity`/
    `OnDeleteActivity`已经在用的同一个`RefreshTree()`调用是同一个已经接受的代价，不是这次新引入的
    行为，没有为了保留选中状态去单独实现"只更新这一个节点的文字"这种这个类里从未用过的模式。
86. **【新发现的真实bug，已修复】图片/文档解码失败会被完全吞掉，比视频/音频的失败路径更糟——
    既不弹Toast也不写日志，还会让顺序自动播放静默卡死**：委托一个子agent专门去找"跟第84条背景
    音频那个bug同一种形状"的问题，这条是它真正找到的一个——`PlayFile`对`MediaKind.Image`/
    `MediaKind.Document`调用`PlayImageAsync`/`PlayDocumentAsync`是纯粹的fire-and-forget
    （`_ = PlayImageAsync(file);`），这两个方法内部原来完全没有`try/catch`。`ImageContentRenderer.
    LoadAsync`/`PdfContentRenderer.LoadAsync`在文件被外部删除/移动/损坏时会真的抛异常——这是
    现实场景，不是假设：活动/文件面板都没有在"点击播放的那一刻"重新验证过文件还存不存在、还能不能
    解码，只有主动删除路径本身会被UI拦下来，活动列表里引用的文件被外部删除是完全没有防御的。
    异常抛出后落在一个从未被观察过的`Task`上直接消失——这个仓库里没有任何
    `AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException`兜底，连"进程有没有
    因此崩溃"这种最基本的信号都没有。更糟的是`ArmStayDurationTimer`是图片/文档唯一会武装
    `HandleCompletion`触发机制的地方，异常一发生这一步永远不会跑到，`PlayMode.SequentialAuto`
    的自动播放从这里开始就永久卡死——跟第84条一样的"队列静默不再前进"，但比它还差：第84条那个
    背景音频文件至少从来没有假装自己在播放，这里`FileStarted`已经在`PlayFile`最后触发过了（文件
    "看起来"已经开始播放），然后就突然停在那儿，没有任何信号。视频/音频的解码失败路径
    （`OnVideoFailed`/`OnAudioFailed`）早就有真正的处理：记日志+触发`PlaybackAbnormallyInterrupted`
    弹出带"重试/移除"按钮的Toast（PLANNING.md §11的要求），图片/文档这条路径完全没有对应实现，
    是这次才发现的缺口，不是文档里承认过的已知限制。**修复方式**：给`PlayImageAsync`/
    `PlayDocumentAsync`各包一层`try/catch`，异常统一交给新增的`OnImageOrDocumentFailed`——跟
    `OnVideoFailed`/`OnAudioFailed`一样"记日志+触发`PlaybackAbnormallyInterrupted`"，同样的
    `ReferenceEquals(file, _currentFile)`防陈旧判断。**跟视频/音频那两个失败处理方法的一个真实
    区别**：`OnVideoFailed`/`OnAudioFailed`是从真正独立的后台播放线程触发的，所以套了一层
    `_overlay.BeginInvoke`把处理逻辑送回UI线程；`PlayImageAsync`/`PlayDocumentAsync`的
    `await`之后的延续默认恢复在调用时捕获的`SynchronizationContext`上，这是一个WinForms消息循环
    应用，也就是UI线程本身——改动前那行不带`BeginInvoke`直接调用`_overlay.ContentSurface.SetFrame`
    的代码本来就已经依赖这个事实，这次的异常处理沿用同样的假设，不需要额外套一层`BeginInvoke`。
    **没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过
    `LoadAsync`抛出的具体异常类型是否真的都能被这个`catch (Exception ex)`接住（理论上应该都能，
    但没有验证过是否有需要特殊处理的异步取消类异常混在里面）。
87. **【新发现的真实bug，已修复】`DiscoveryService`的UDP接收循环会被一个陌生数据包永久杀死，
    修复在`EveryStage.Discovery`共享库里**：委托另一个子agent专门排查发现/配对握手这条链路后
    找到的——`DiscoveryProtocol.Decode`原来只在`JsonDocument.Parse`本身失败时才会抛
    `JsonException`（`HandleDatagram`确实catch了这个类型），但如果收到的数据包本身是合法JSON、
    只是形状不对（比如裸数字`42`、或者`{"type":123}`这种"type"字段不是字符串的对象），
    `JsonElement.TryGetProperty`/`GetString`会抛`InvalidOperationException`——这个类型完全没被
    catch住，会直接穿透`HandleDatagram`、砸穿`ReceiveLoopAsync`所在的裸`Task.Run`，而这个仓库
    里没有任何`AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException`兜底，
    异常就此彻底消失。`BeaconLoopAsync`是独立的`Task`，所以Terminal表面上看起来还在正常广播
    beacon，实际上从这一刻起再也无法处理任何配对请求/`cast_start`/`cast_stop`/ping——所有后续
    Caster交互都会变成超时，跟普通丢包表现完全一样，但这次是永久性的，不会恢复。详细分析、修复
    方式（`Decode`内部加`ValueKind`检查，不是给`HandleDatagram`加宽catch类型）、新增的
    `DiscoveryProtocolSelfTest.CheckMalformedInputsDontThrow`自检见`EveryStage.Discovery`README
    ——这次改动完全在共享库里，Terminal这边的`DiscoveryService.HandleDatagram`一行代码都没有改，
    单纯是这次影响面覆盖了这一侧，值得在这里也记一笔。
88. **【新增】这个进程第一次有了顶层的"UI线程异常/后台Task未观察异常/其他线程未捕获异常"兜底，
    以及配套的第四类日志（`CrashLogger`）**：第86、87条这一轮连续找到3个"某个后台线程/
    fire-and-forget Task抛出的异常，因为没有兜底而彻底消失，对应的后台循环/回调链从此永久失效"
    形状的真实bug，但那两条各自的修复只堵住了已经发现的两个具体位置——`grep -rn
    "UnobservedTaskException|UnhandledException" src/`确认这个仓库此前任何地方都没有注册过
    `Application.ThreadException`/`AppDomain.UnhandledException`/
    `TaskScheduler.UnobservedTaskException`中的任何一个，意味着还有可能存在没被这轮排查找到的
    第三个、第四个实例，或者以后新增代码引入新的一个。这次在`Program.Main()`里补上这三个顶层
    处理器，不是针对某个具体bug的修复，是PLANNING.md自己"无人值守"这个要求本来就该有、但从一开始
    就没有的安全网：`Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)`+
    `Application.ThreadException`——把UI线程（比如Toast某个action按钮的回调）抛出的异常从.NET
    默认的"整个进程崩溃"行为改成"记录下来，消息循环继续跑"，这对一台没人值守、崩溃后没人会去手动
    重启的设备来说是本质区别；`AppDomain.CurrentDomain.UnhandledException`——虽然无法阻止进程
    真正终止，但至少在"整个Terminal彻底黑屏消失"之前把原因写进日志，而不是留下一个完全没有线索
    的空白；`TaskScheduler.UnobservedTaskException`——专门对应第86条那种bug的形状本身，任何
    这轮排查没找到的、未来新引入的fire-and-forget Task异常，至少会被记下来而不是彻底消失，同时
    `SetObserved()`避免触发二次异常报告。**为什么新增一个第四类日志而不是塞进现成的三类里**：
    PLANNING.md §14.4明确点名的三类（文件操作/播放投屏/设备连接）"分三类物理独立存储，便于按
    问题类型定位"——一个通用的未处理异常可能是任何一类问题、也可能都不是，硬塞进某一类反而会
    误导"按问题类型定位"这个初衷；`CrashLogger`独立于这三类之外存在，专门只服务这次新增的三个
    顶层处理器，不冒充PLANNING.md命名过的类别之一。**没有做的部分**：这次改动本身没有在这个
    沙箱里跑过（没有dotnet），`UnhandledExceptionMode.CatchException`之后WinForms消息循环
    是否真的能在UI线程异常之后干净地继续运行、不留下部分初始化到一半的控件状态，完全依赖.NET
    文档描述的行为，没有真机验证过。
89. **【新发现的真实bug，已修复】`OverlayWindow.ReassertTopMost`每2秒重新维护z-order的同时，
    一直在悄悄把窗口挪回原处**：`SWP_NOMOVE`这个Win32常量早就被声明了
    （`private const uint SWP_NOMOVE = 0x0002;`），但从来没有真正出现在`SetWindowPos`调用自己
    的flags参数里——原来那行是`SWP_NOSIZE | SWP_NOACTIVATE`，唯独漏了`SWP_NOMOVE`。这个方法自己
    的doc comment说得很清楚，它的职责只是"keep nagging the z-order"，不是重新定位——`SWP_NOSIZE`
    已经在正确地屏蔽宽高参数（调用时传的`0, 0`本来就是占位符），但X/Y坐标那两个参数因为没有
    `SWP_NOMOVE`屏蔽，每次调用都会真的执行一次"移动到(X,Y)"。**实际影响很小，但不是零**：因为
    这个坐标本来就是`Monitor.Bounds.X/Y`，跟窗口构造时`Bounds = monitor.Bounds`设置的位置完全
    一样（这个无边框覆盖窗口本来也没有任何途径会被移动——不接受用户拖动，唯一的"真的换地方"入口
    是`Rebind(MonitorInfo)`，走的是完全独立的`Bounds`属性赋值，不经过这个方法），所以正常情况下
    这是一次移动到自己当前位置的空操作，视觉上不会有可观察到的效果；但这终究是一次多余的
    `SetWindowPos`移动调用，每2秒触发一次`WM_WINDOWPOSCHANGING`/`WM_WINDOWPOSCHANGED`，跟这个
    方法自己声明的意图（"只管z-order，别的都不动"）不符，那个从声明起就没被用过的`SWP_NOMOVE`
    常量就是最直接的线索。**修复方式**：把`SWP_NOMOVE`加进flags里，让这次调用变成真正纯粹的
    z-order重申，跟旁边`SWP_NOSIZE`"宽高也不要动"的待遇一致。**没有做的部分**：这次改动本身
    没有在这个沙箱里跑过（没有dotnet），没有真机验证过——包括这条风险本身描述的"正常情况下是
    空操作"这个判断，也完全是代码审阅推出来的，没有在真实Windows多显示器环境下观察过
    `SetWindowPos`带`SWP_NOMOVE`前后的实际行为差异。
90. **【新发现的真实bug，修复在Caster那边】`PendingRequestTimeout`特意留出的2分钟人工确认窗口，
    曾经被Caster自己15秒就放弃的默认超时架空**：`PendingRequestTimeout = TimeSpan.FromMinutes(2)`
    这个常量、以及它注释里"给一个人去点弹窗留出时间"这句话，从写下来那一刻起就隐含一个前提——
    发起配对请求的那一端也愿意等这么久。审计的时候发现Caster端`TerminalDiscoveryClient.
    RequestPairingAsync`原来的默认超时只有15秒，只要操作员点`PairingConfirmationDialog`花的
    时间超过15秒（对一个需要真人反应的交互来说完全正常），Caster就会先一步弹出"终端机未响应
    （超时）"，即使这个请求在Terminal这边`_pendingRequests`里根本还没死——接下来即使操作员真的
    点了"接受"，Caster那时候早就把这个`requestId`忘了，`PairResponseMessage`送到时只会被当成
    "未知/已处理过的RequestId"安静丢弃，配对请求人间蒸发。详细分析、修复方式（新增
    `DefaultPairingRequestTimeout`常量，`PendingRequestTimeout`加15秒余量，两边各自独立声明、
    互相加了交叉引用注释提醒手动保持同步）见`EveryStage.Caster`README——这次改动完全在Caster
    那一侧，Terminal这边`PendingRequestTimeout`本身没有改，单纯是这个bug的根源就是这个常量
    这一轮才第一次被真正对照检查过，值得在这里也记一笔。
91. **【新发现的真实bug，已修复】`H264HardwareDecoder`调用了`MediaFactory.MFStartup()`却从来
    没有调用配对的`MFShutdown()`**：`MFStartup`/`MFShutdown`是进程级引用计数的一对——每次
    `MFStartup`调用计数加一，`MFShutdown`减一，底层Media Foundation子系统真正释放的时机是这个
    计数真正归零的时候，不是某一个具体对象被`Dispose`的时候。审计的时候顺手核对了这个仓库里所有
    调用过`MediaFactory.MFStartup()`的地方（`AudioDecodeSource`/`VideoDecodeSource`/
    `AacAudioDecoder`/`Caster.Encode.AacAudioEncoder`都在各自的`Dispose()`里正确配对了
    `MFShutdown()`），唯独这个类的`Dispose()`原来只有`_decoder.Dispose();`一行，从来没有调用
    `MFShutdown()`——`CastReceiver`每接受一次设备投屏就会构造一个新的`H264HardwareDecoder`，
    这个计数只增不减，永远不会真正把这次投屏占用的Media Foundation资源还给系统，直到整个
    Terminal进程退出为止。**修复方式**：`Dispose()`里补上`MediaFactory.MFShutdown();`，跟
    其他几个类的既有模式完全一致。Caster端`H264HardwareEncoder`有完全同一个bug、同一次改动
    一起修了，见`EveryStage.Caster`README对应条目。**没有做的部分**：这次改动本身没有在这个
    沙箱里跑过（没有dotnet），没有真机验证过——包括这个引用计数泄漏在实际运行中到底会不会造成
    可观察的问题（比如某个内部资源池耗尽），本身也只是基于MF官方文档描述的引用计数语义推断出来
    的，没有实测验证过多次投屏循环之后是否真的有异常表现。
92. **【新发现的真实bug，已修复】`AudioTakeoverService`的COM调用完全没有做异常防护，而它恰好被
    两种最怕异常的方式调用着**：`MuteStillActiveSessions()`/`UnmuteSessionsWeMuted()`里
    `enumerator.GetDefaultAudioEndpoint(...)`以及对每个`session`的`GetProcessID`/
    `SimpleAudioVolume`访问，原来一个try/catch都没有——`MuteStillActiveSessions`自己的注释就
    点名了"GetProcessID的具体签名（方法还是属性、uint还是int）需要对照实际安装的NAudio版本核实"
    这种尚未验证过的interop细节，而`GetDefaultAudioEndpoint`本身在"没有配置/插入任何音频输出
    设备"时会抛`COMException`——这对Terminal这种扩展屏一体机而言不是假设性的边界情况，它完全
    可能就是真实的目标硬件形态。这个未加防护的方法被两个调用点用两种都会出问题的方式在用：
    (a) `Program.OnOutputStateChanged`里`TakeoverAsync()`是"fire-and-forget"（`_ =
    _audioTakeover.TakeoverAsync();`），异常只会在GC某次终结这个被丢弃的Task时才通过
    `TaskScheduler.UnobservedTaskException`（见风险#88那次加的顶层安全网）冒出来，延迟不可预期；
    (b) 更严重的是`Restore()`在同一个方法里被同步调用，紧跟着后面完全没有try/catch保护的
    `StopCasting()`——如果`Restore()`抛出，`StopCasting()`根本不会执行，"断"这个操作会把
    `CastReceiver`晾在那里继续对着已经隐藏的覆盖窗口收流解码，跟`StopCasting()`调用上方那条
    注释明确警告要避免的情况一模一样，只是原来的代码从未真正防住它。**修复方式**：给
    `MuteStillActiveSessions`/`UnmuteSessionsWeMuted`各自加了两层try/catch——外层包住
    `enumerator`/`device`获取（专门应对"没有默认音频输出设备"），内层包住`for`循环体内单个
    session的处理（专门应对"某一个session的COM调用失败不该连累其它session都没处理到"，比如
    枚举之后、真正操作之前进程碰巧退出了）。**没有做的部分**：这次改动本身没有在这个沙箱里跑过
    （没有dotnet），没有真机验证过——包括"这台机器到底有没有默认音频输出设备"以及
    `GetProcessID`真实签名这两件事本身，都还是要在真机第一次编译/运行时才能确认。
93. **【新发现的真实bug，已修复】`FileLibraryStore`/`ScenarioRepository`/`SettingsStore`/
    `PairedDeviceStore`四个持久化存储的`Load()`都只防了"文件内容损坏"，没防"文件暂时读不出来"**：
    四个类的`Load()`原来都是同一个形状——`File.Exists(path)`确认文件存在之后，`try`块里
    `File.OpenRead`+`JsonSerializer.Deserialize`，`catch`只接`JsonException`，注释都明确写着
    "an unattended device must not crash-loop on a corrupt file"。但`File.Exists`返回`true`到
    `File.OpenRead`真正执行之间存在一个经典的TOCTOU缝隙：文件完全可能在这两步之间被杀毒软件/
    备份工具短暂独占锁住，这时`File.OpenRead`抛的是`IOException`，不是`JsonException`——这四个
    `Load()`都是从各自类的构造函数里同步调用的，而这四个类又都是`Program.Main`启动阶段最早期
    构造的对象之一，这个未捕获的异常会让Terminal在真正开始跑之前就直接崩溃退出，完全违背这四处
    注释自己写明的"不能因为文件问题crash-loop"这个目标——只是这些注释把"文件问题"窄化成了"内容
    损坏"一种情况，遗漏了"读不出来"这另一种更常见的情况（尤其是杀毒软件扫描，在无人值守设备上
    是持续发生的常态，不是小概率事件）。**修复方式**：四个`Load()`都新增一个单独的
    `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`分支，直接
    返回一份内存里的默认值（跟`JsonException`分支返回值完全一样），但**不做**`JsonException`
    分支那个"把原文件改名成`.corrupt-时间戳`备份"的动作——这是这次修复里刻意区分的关键点：内容
    确实损坏时改名备份是对的（原文件反正也用不了了，留着给人排查）；但文件只是暂时被锁住、内容
    其实完全正常的情况下，如果沿用同一套"改名"处理，反而会把一份好端端的数据永久藏到`Load()`
    自己以后再也不会去找的文件名下面——比崩溃更隐蔽，因为进程还能正常跑起来，只是无声无息用回了
    默认值，而原本的数据被自己的容错逻辑误伤了。所以这次新加的分支只返回默认值、完全不碰原文件，
    让下次重启（届时锁大概率已经释放）还有机会正常读到它。Caster端`PairedTerminalStore`、
    `EveryStage.Discovery`共享的`DeviceIdentity.LoadOrCreate`是同一次改动一起修的同一个bug，
    分别见`EveryStage.Caster`/`EveryStage.Discovery`各自README对应条目——后者比这四个还严重一层，
    见该README条目说明。**没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），
    没有真机验证过；`File.Exists`本身在极端情况下（比如整个存储卷刚好离线）也可能有自己的
    TOCTOU缝隙，这次没有进一步深挖到这个层级。
94. **【新发现的真实bug，已修复】`OutputStateMachine.StateChanged`原来是一次性`?.Invoke(...)`
    多播调用，一个订阅者抛异常会让排在它后面的其它订阅者这次完全收不到通知**：这是审查上一条
    （第92条，`AudioTakeoverService`）过程中顺手发现的更上一层问题——即使把`AudioTakeoverService`
    自己的COM调用全部加上了防护，`StateChanged`这个事件本身的订阅者列表还有好几个：按
    `TerminalApplicationContext`构造函数里的实际注册顺序，依次是`PlaybackEngine`（在
    `_playback = new PlaybackEngine(...)`那一行内部完成订阅）、`Program`自己的
    `OnOutputStateChanged`（也就是调用`AudioTakeoverService.Restore()`紧接着调
    `StopCasting()`的那一个）、`MainWindow`/`ActivitiesPanel`，最后是`TrayIconController`。
    .NET多播委托的`Invoke`不是"每个订阅者互相隔离、一个抛异常不影响其它"——它就是一次连续调用，
    只要其中任何一个抛出，异常直接从`Invoke`穿出去，排在它后面、原本也该被通知到的订阅者这一次
    根本不会被调用。`PlaybackEngine.OnOutputStateChanged`（`_videoController?.Stop()`/
    `_audioController?.Stop()`，两者内部都有`_audioClock?.Dispose()`/`_source?.Dispose()`这类
    对Media Foundation/WASAPI资源的COM释放调用）排在最前面——如果这里面任何一次`Dispose()`因为
    设备丢失之类原因抛出异常（这个仓库其它地方已经不止一次记录过GPU/设备丢失是真实会发生的
    场景），`Program`自己那个真正负责"断"时`StopCasting()`的处理器就会被跳过，跟第92条描述的
    "`StopCasting()`不执行、`CastReceiver`继续对着隐藏窗口收流"是同一个后果，只是触发路径从
    `AudioTakeoverService`内部换成了"排在它前面的另一个完全不相关的订阅者"——即使`Program`自己
    的处理器本身写得再仔细也没用，因为它根本没有机会被调用。**修复方式**：`OutputStateMachine`
    新增`RaiseStateChanged`/`RaiseCastSwitchChanged`两个私有方法，通过
    `StateChanged?.GetInvocationList()`拿到订阅者列表后逐个手动调用、每个订阅者单独包一层
    try/catch，取代原来的`StateChanged?.Invoke(...)`一次性多播调用——这样任何一个订阅者抛异常
    都只影响它自己，不会连累列表里排在它后面的其它订阅者。`CastSwitchChanged`目前只有
    `TrayIconController`一个订阅者，但同一次改动一起做了同样的隔离，理由见方法自己的doc
    comment。**没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过；
    这个类本身没有logger，所以每个订阅者失败时具体是谁失败、失败原因是什么，这次没有记录下来，
    只是保证了"不连累别人"这一点。
95. **【新发现的真实bug，已修复】`FilesPanel.Refresh_()`每次重建缩略图列表都会泄漏GDI+ Bitmap
    句柄**：`BuildThumbnail(file)`每次调用都会`new`一个`Bitmap`（图片文件解码出的缩略图，或者
    `SystemIcons.Warning`/`SystemIcons.Application`转出来的占位图——`Icon.ToBitmap()`每次调用
    都返回一个新实例，不是共享的缓存对象），原来直接传给`_thumbnails.Images.Add(key,
    BuildThumbnail(file))`，从未`Dispose`过。`ImageList.Images.Add`只是把传入`Image`的像素数据
    拷贝进它自己内部的原生image list句柄里，不会持有、也不会帮调用方释放传入的这个`Image`对象——
    调用方自己需要负责释放，这是.NET WinForms一个相当常见的坑。这次泄漏不是一次性的：
    `Refresh_()`在每次点击分类筛选按钮（全部/图片/视频/文档/音频）、每次导入文件、每次移除文件
    时都会重新执行一遍，`MainWindow`里每次用户切换到"文件"这个标签页也会调一次——在PLANNING.md
    §14.4本身就框定为"无人值守、长期不重启"的设备上，这种反复触发的小额泄漏正是最终会把进程的
    GDI对象配额耗尽（Windows对每个进程的GDI句柄数有上限，用尽后会出现界面绘制失败/崩溃）这类
    问题的典型成因，不是理论上的边界情况。**修复方式**：`BuildThumbnail(file)`的返回值改用
    `using`接住，在`_thumbnails.Images.Add(key, thumbnail)`调用完之后立刻释放——`ImageList`
    自己内部的原生句柄由`ImageList`自己管理、`Clear()`/`Dispose()`会正确处理，这次泄漏的是
    `BuildThumbnail`返回的那个临时`Image`对象本身，不涉及`ImageList`内部状态。全仓库搜索确认
    这是唯一一处使用`ImageList`的地方，不是需要在多处重复修的模式。**没有做的部分**：这次改动
    本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过——包括这个泄漏在真实长期运行下到底
    需要多久才会真的把GDI配额耗尽到出现可观察问题，本身也只是基于.NET/Win32文档描述的
    `ImageList`/`Image`所有权语义推断出来的，没有实测验证过。
96. **【新发现的真实bug，已修复，比之前"Decode杀死接收循环"那个bug更深一层】
    `DiscoveryService.HandleDatagram`的`switch`分发本身完全没有异常防护，只有`Decode`这一步
    被保护了**：第87条记录的`DiscoveryProtocol.Decode`那个bug（一个陌生数据包会让
    `TryGetProperty`/`GetString`抛`InvalidOperationException`、直接杀死整个UDP接收循环）已经
    在共享库里修过了，但审计这次没有到此为止——`HandleDatagram`原来的结构是`try { message =
    Decode(data); } catch (JsonException) { return; }`，然后紧接着一个完全不在任何`try`保护范围
    内的`switch (message) { ... }`，分发给`HandlePairRequest`/`HandleCastStart`/
    `HandleCastStop`/`HandlePing`/`HandlePong`五个处理方法。这个`switch`本身是从
    `ReceiveLoopAsync`的`while`循环体里直接调用`HandleDatagram(...)`，循环体自己也没有额外包一层
    try/catch——也就是说，只要这五个处理方法中任何一个抛出异常（比如`PairingRequested`/
    `CastStartRequested`/`CastStopRequested`这几个事件的订阅方`Program`里的处理器本身抛出，或者
    这几个`Handle*`方法自己内部逻辑抛出），异常会直接穿透`HandleDatagram`、穿透`while`循环体，
    永久结束这个Terminal进程剩余生命周期里的整个发现协议接收循环——跟第87条修的`Decode`bug
    是完全同一种"一个坏数据包/一次处理失败，永久杀死整个后台循环，零可见症状"的形状，只是触发
    点从"解码阶段"往后挪到了"解码成功之后的分发阶段"，第87条那次修复没有覆盖到这一层。
    **修复方式**：给这个`switch`语句本身也包一层`try/catch (Exception)`，跟保护`Decode()`的
    那层`try/catch`相互独立、职责分开——`Decode`失败直接`return`（不认识这个数据包，正常现象），
    `switch`内部失败则记录"这一个数据包的处理失败了"但让循环继续处理下一个数据包，不再让单次
    处理失败连累后续所有数据包都收不到。Caster端`TerminalDiscoveryClient.HandleDatagram`是
    完全同一个bug、同一次审计一起修的，见`EveryStage.Caster`README对应条目——那一侧甚至更关键，
    因为`CastStatusMessage`分支直接决定了`LiveCastSession`能不能收到Terminal的投屏状态回报。
    **没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
97. **【新发现的真实bug，已修复，全仓库唯一会真正让整个进程崩溃（而不只是某个子系统悄悄死掉）
    的一处】`CastReceiver.RunPresentLoop`原来完全没有异常防护，而它是全仓库三个用裸`Thread`
    （不是`Task`）跑后台循环的地方里，唯一没有包`try/catch`的那一个**：`AudioContentController`/
    `VideoContentController`各自的`RunPlaybackLoop`都已经把`RunPlaybackLoopCore`包了一层
    `try/catch`（异常时触发`PlaybackFailed`事件），唯独`CastReceiver`的`RunPresentLoop`（有音频
    时才会启动的、按`AudioPlaybackClock.PositionTicks`节拍呈现解码帧的后台线程）原来是直接裸跑
    循环体，一次`Present(frame)`失败（比如`VideoSurface.PresentFrame`内部D3D11视频处理器调用
    因为GPU设备丢失/重置而抛异常——这个仓库其它地方已经不止一次记录过这是真实会发生的场景）就会
    直接从这个`Thread`的入口方法穿出去。裸`Thread`（不同于`Task`）如果异常穿透入口方法，.NET
    会让整个进程崩溃退出——不是"这个子系统安静死掉、其它功能还能用"，是`AppDomain.
    UnhandledException`兜底（第88条）虽然能把这次崩溃记进`crash`日志分类，但完全没办法阻止
    进程真的终止。这是全仓库审计过的三处裸`Thread`后台循环里，唯一还留着这个"能让整个无人值守
    终端机进程直接崩掉"级别缺口的一处。**修复方式**：把原来的循环体拆成`RunPresentLoopCore`，
    `RunPresentLoop`改成只做`try { RunPresentLoopCore(token); } catch (Exception ex) { LastError
    = ex.Message; PresentLoopFailed = true; } finally { 清空并释放_pendingFrames里剩下的帧 }`
    这个包装——异常不再穿透线程入口，只会让这一次投屏的呈现管线报告"坏了"。新增
    `PresentLoopFailed`属性并接入`TerminalApplicationContext.CheckDecodeHealth`的判定条件：
    没有直接复用已有的`ConsecutiveVideoDecodeErrors`/`LastError`，是因为这两个值会被
    `OnNalUnitReceived`的下一次成功解码重置为0/null——而这个present线程死掉之后，RTP接收路径
    完全可能继续独立地成功解码（只是解码出来的帧再也没有线程去消费/呈现了），会在
    `CheckDecodeHealth`真正读到这个信号之前就被后续的解码成功"洗白"，让这个健康检查形同虚设。
    `PresentLoopFailed`是一个单向锁存（这个`CastReceiver`实例一旦present线程死了就永久是
    `true`，不会被任何后续成功解码重置），从根上避免了这个竞态。**顺手修复的第二个问题**：原来
    循环体末尾"清空`_pendingFrames`剩余帧、释放GPU纹理引用"这行代码，在循环体因为等待中途检测到
    取消而走`return`（`if (token.IsCancellationRequested) { frame.Texture.Dispose(); return; }`）
    这条路径时会被完全跳过——而这条路径很可能是"断"/`StopCasting()`时最常见的退出路径，不是
    冷门边界情况，意味着每次这样正常停止投屏都可能悄悄泄漏队列里剩下的GPU纹理引用。这次把清空
    步骤挪进新`RunPresentLoop`包装方法的`finally`块，不管`RunPresentLoopCore`是正常跑完循环、
    提前`return`、还是抛异常，都保证会执行到。**没有做的部分**：这次改动本身没有在这个沙箱里
    跑过（没有dotnet），没有真机验证过——包括GPU设备丢失/重置在真机上到底以什么具体异常类型/
    时机出现，本身也只是基于这个仓库其它地方已经记录过的"GPU设备丢失是真实场景"这个共识推断
    出来的，没有实测触发过。
98. **【新发现的真实bug，已修复】`H264HardwareDecoder`构造函数一旦`MediaFactory.MFStartup()`
    成功之后剩余部分抛出异常，这次`MFStartup`引用计数永远不会被平衡回来**：跟
    `EveryStage.Rendering`那次审计（见该项目README第7条）是同一次系统性排查一起找到的同一类
    bug——这个类的构造函数形状是`_decoder = ActivateFirstHardwareDecoder();`（内部先调用
    `MFStartup()`再枚举/激活硬件解码器MFT，枚举不到硬件解码器时会抛异常）之后紧跟着
    `ConfigureInputType`/`ConfigureNv12OutputType`/`BindDeviceManager`等一长串同样可能失败的
    配置步骤，构造函数才真正返回。原来只有`Dispose()`里调了`MFShutdown()`——如果
    `ActivateFirstHardwareDecoder`内部枚举不到硬件解码器（这是真实场景：不是所有机器都有兼容的
    硬件H.264解码器），或者后续任何一步配置失败，这个`H264HardwareDecoder`实例就永远不会真正
    构造完成，`CastReceiver`也就永远不会有一个实例去调`Dispose()`，`MFStartup()`增加的引用
    计数就此永久泄漏——而且这不是一次性的：如果某台Terminal机器确实缺少兼容的硬件解码器，
    它每次接受一次设备投屏都会重新尝试构造一次`H264HardwareDecoder`，也就重新泄漏一次，直到
    整个Terminal进程退出为止。**修复方式**：`ActivateFirstHardwareDecoder`自己内部（枚举/激活
    失败的路径）和构造函数剩余部分（配置失败的路径）各自新增一层`try/catch`，失败时都调用
    `MediaFactory.MFShutdown()`再重新抛出异常，保证不管构造函数是正常完成还是在任何一个阶段
    中途失败，`MFStartup`/`MFShutdown`的配对关系都不会被打破。**没有做的部分**：这次改动本身
    没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
99. **【新发现的真实bug，已修复，比第97条更早发生、影响更彻底】`TerminalApplicationContext`
    构造函数里绑定扩展屏的那一段（`OverlayWindow`/`VideoSurface`/`PlaybackEngine`/
    `FloatingPreviewWindow`的构造）原来完全没有异常防护，而`Program.Main`里构造这个类本身
    也同样没有——如果这台机器绑定了扩展屏、但`VideoSurface`内部的D3D11设备初始化失败（这台
    机器没有兼容的GPU/驱动，这个仓库其它地方已经反复记录过是真实场景），异常会直接从
    `Main()`穿出去。`Application.ThreadException`在这里完全帮不上忙——这段代码在
    `Application.Run()`真正启动消息循环*之前*就执行了，根本没有机会经过那个兜底；
    `AppDomain.CurrentDomain.UnhandledException`（在`Main()`里更早注册）倒是能接住并且
    通过`crashLogger`记下这次崩溃，但这个事件本身无法阻止进程终止（`isTerminating`对它
    永远是`true`）——结果是：整个无人值守的Terminal进程会在连托盘图标都还没显示出来之前就
    直接崩溃退出，仅仅因为这台机器的GPU不兼容。这比第97条`RunPresentLoop`那个"能让整个进程
    崩溃"的bug还要更早发生、影响更彻底——第97条至少要先成功接受一次设备投屏才会触发，这一条
    在Terminal刚启动、只是"这台机器恰好绑定了一块扩展屏"这个最普通的场景下就可能触发。而
    PLANNING.md本身早就为"没有绑定扩展屏"这种情况设计了一套完整的优雅降级路径——托盘图标、
    主窗口、设备发现、配对这些功能在`_overlay`为`null`时全部正常工作，这条降级路径这次修复
    之前只覆盖了"用户确实没插第二块显示器"这一种触发条件，从未覆盖"插了，但GPU初始化失败了"
    这另一种同样会导致"没有可用的扩展屏"这个最终状态的路径。**修复方式**：给这一段包一层
    `try/catch`，失败时通过新增的`CrashLogger`字段（`Program.Main`把它已经在用的那个
    `crashLogger`局部变量传进`TerminalApplicationContext`的构造函数，而不是另开一个新实例
    ——避免`FileOperationLogger`那条已知风险提醒过的"两个独立实例写同一个日志文件有极小概率
    冲突"这种情况）记下这次失败，然后把`_overlay`/`_videoSurface`/`_playback`/
    `_previewWindow`（连带清理已经成功构造的那些）全部清回`null`，让构造函数剩余部分把这次
    GPU失败当成跟"压根没绑定扩展屏"完全一样的状态继续走下去，而不是让整个进程直接死掉。
    **没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过——包括
    GPU初始化失败在真机上具体是通过什么异常类型/时机表现出来的，本身也只是推断，没有实测过。
    **同一次审计发现但这次故意没有修的一个近亲问题**：`_discovery = new DiscoveryService(...)`
    （这个构造函数同样会`Bind`一个固定UDP端口，跟`EveryStage.Caster`README第79条修的
    `TerminalDiscoveryClient`绑定失败是完全同一类风险）同样完全没有异常防护，且不在这次
    加的`try/catch`保护范围内。Caster那一侧修了，这一侧刻意没跟着修：`_discovery`在这个类
    里被大量下游代码直接使用（订阅它的三个事件、调用`Start()`、`OnExitRequested`里调用
    `Dispose()`），要让它变成可选、失败后优雅降级（比如"发现服务失败，但本地播放仍然正常
    工作"）需要把这个字段整个改成可空类型并挨个审查每一处使用点加空值保护，这是一次范围
    大得多、出错风险也更高的重构，跟这次"针对具体触发点做最小、对称修复"的一贯做法不是同
    一个量级。而且端口冲突这种触发条件本身在Terminal这一侧发生的概率也比Caster低得多——
    Caster被设计成会被随手启动/重启、甚至在同一台开发机上跑多个实例（`EveryStage.Discovery`
    README自己就提到这个场景），Terminal则是专机专用的常驻服务，不太会出现"同一台机器跑两个
    Terminal实例"这种情况。这次选择的折中是：至少`AppDomain.CurrentDomain.UnhandledException`
    （已经在`Main()`里注册）能保证这个失败被记进crash日志，不会像Caster修复之前那样连日志
    都没有——但进程本身仍然会崩溃，没有做到跟GPU失败那条一样的优雅降级。这个不对称是这次
    审计权衡之后的有意选择，不是遗漏。**补充说明，避免这条读起来像"`_discovery`是这段
    构造函数里唯一没保护的部分"**：往下`var library = new FileLibraryStore(); _mainWindow
    = new MainWindow(...); _mainWindow.Show(); _tray = new TrayIconController(...)`这几行
    同样完全在这个`try/catch`保护范围之外、同样在`Application.Run()`之前执行——这条笔记
    只单独点名`_discovery`，是因为它是这几个里唯一有一个具体、真实可触发的失败场景（固定
    UDP端口冲突）的一个，不是因为它是唯一没受保护的一个。`FileLibraryStore`自己的`Load()`
    早就用这个仓库统一的JSON持久化容错模式处理了`JsonException`/`IOException`/
    `UnauthorizedAccessException`（见第73条附近），残余风险很小；`MainWindow`/
    `TrayIconController`是纯UI控件构造，没有已知的具体触发条件。三者都被判断为风险明显
    低于`_discovery`，所以没有单独展开分析，但"完全没有异常防护"这个事实对它们同样成立，
    这里补充说明是为了不让这条笔记的措辞显得比实际情况更精确。
100. **【修复】`DailyRollingLogWriter`的构造函数（`Directory.CreateDirectory`+`CleanupOldFiles`）
    之前完全没有异常防护，而这正是`CrashLogger`——上面第99条那整套"至少能记进crash日志"
    安全网本身——所依赖的底层类**：这个类自己的`Write()`方法早就把"写入失败（磁盘满、文件被
    外部工具短暂锁住）绝不能拖垮这台无人值守设备"当成明确的设计原则并且用`try/catch`落实了，
    但同一个类的构造函数里做的完全同一类文件系统I/O（`Directory.CreateDirectory`、
    `CleanupOldFiles`内部的`Directory.EnumerateFiles`）却没有同样的保护——这是同一个类内部
    两处行为不一致，不是跨类的问题。而这个构造函数偏偏又在最危险的调用路径上：`CrashLogger`
    （这个共享写入类支持的四个日志分类之一）是`Program.Main()`里的第一条有意义的语句，
    在`Application.ThreadException`/`AppDomain.CurrentDomain.UnhandledException`/
    `TaskScheduler.UnobservedTaskException`这三个安全网注册*之前*就执行——如果这里抛出异常
    （权限问题或者磁盘满了，对一台全新部署、第一次启动的无人值守设备完全谈不上假设性），
    整个Terminal会崩溃得连"至少记进crash日志"这个第99条都提到的最低保障都没有，比这次审计
    发现的所有其它问题都更彻底：连负责记录"进程为什么死了"的基础设施本身都没能活下来。
    **修复方式**：给构造函数里`Directory.CreateDirectory`/`CleanupOldFiles`这两行也包一层
    跟`Write()`完全一致的`try/catch (IOException) {} catch (UnauthorizedAccessException) {}`
    ——失败时这个写入器对象仍然能构造成功，只是往后永远写不进任何东西，而这个"永远写失败"的
    结果早就被`Write()`自己的`try/catch`无限期兼容了，对一台无人值守设备来说，这比"因为
    日志目录建不出来就直接不启动"要好得多。这个修复同时覆盖了`CrashLogger`和另外三个共享
    同一个`DailyRollingLogWriter`的日志分类（`FileOperationLogger`/`PlaybackLogger`/
    `DeviceConnectionLogger`），因为它们的构造函数问题完全出在这一个共享的底层类里。
    **没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
101. **【新发现的真实bug，已修复】`CastReceiver`构造函数里`_decoder = new H264HardwareDecoder(...)`
    成功之后，紧接着的`_rtpReceiver = new RtpReceiver(listenPort, ...)`没有异常防护**：跟
    `EveryStage.Rendering`README记录的`D3D11Device`/`SwapChainPresenter`/`AudioPlaybackClock`
    和`EveryStage.Caster`README记录的`BgraToNv12Converter`构造函数修复是完全同一种形状，这次
    出现在Terminal自己的接收端。`new RtpReceiver(listenPort, ...)`会把一个UDP socket绑定到
    一个固定、众所周知的端口（`DiscoveryProtocol.VideoRtpPort`），这真的可能抛出
    `SocketException`——上一个`CastReceiver`的socket还没来得及释放完（快速停止/重新开始投屏，
    或者这台Terminal刚从一次崩溃里恢复过来），是真实可触发的场景，不是假设。一旦这里抛出异常，
    这个构造函数永远不会正常完成，`Program.OnCastStartRequested`永远拿不到`CastReceiver`实例
    去调用`Dispose()`，前面已经成功构造的`H264HardwareDecoder`就会泄漏——包括它自己内部持有的
    `MFStartup()`引用计数和真实的GPU硬件解码器MFT资源。**这次泄漏尤其值得关注的地方**：这个
    构造函数每次一个设备投屏被接受时就会执行一次，比这一轮修的其它几个构造函数（每次播放本地
    文件、每次投屏开始时才各构造一次）触发频率更高——一台Terminal只要接受过一次投屏、又恰好
    赶上端口还没释放干净这种时机，就会永久泄漏一份真实GPU解码器资源，这一轮之前保护
    `H264HardwareDecoder`自己构造函数的那次修复对这里完全无能为力，因为这里的解码器早就已经
    构造成功了，问题出在它构造成功*之后*、`CastReceiver`自己构造函数剩余部分抛出异常。
    **修复方式**：把`_rtpReceiver = new RtpReceiver(...)`包进`try/catch`，失败时先取消订阅
    `_decoder.FrameDecoded`（跟这个类自己`Dispose()`的清理顺序保持一致）、调用`_decoder.
    Dispose()`，再重新抛出异常。**没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有
    dotnet），没有真机验证过。
102. **【新发现的真实bug，已修复】`PdfContentRenderer`/`ImageContentRenderer`的`LoadAsync`
    只`Dispose()`了旧的`_document`/`CurrentFrame`，没有把它们清成`null`——新的加载失败时，
    这两个字段会永久卡在"指向一个已经被释放的对象"这个状态，而不是`null`**：`PlaybackEngine`
    对这两个渲染器都只构造一个实例、终生复用（`private readonly PdfContentRenderer _pdfRenderer
    = new();`/`ImageContentRenderer`同理），而且`OnImageOrDocumentFailed`（`PlayImageAsync`/
    `PlayDocumentAsync`失败时的统一处理，本README上一轮记录过）刻意不清空`_currentFile`——
    这两点结合起来意味着：一次加载失败（这个类自己的doc comment和`PlayImageAsync`/
    `PlayDocumentAsync`早就承认是真实场景，不是假设：文件被加入活动之后又被删除/移动/损坏）
    之后，`PlaybackEngine.CurrentThumbnail`（`FloatingPreviewWindow`读取的"缩略画面预览"，
    PLANNING.md §8.3）会继续返回这个已经被`Dispose()`过的`Bitmap`，直到下一次加载成功为止；
    `PdfContentRenderer`这边还多一层：`NextPage()`/`PreviousPage()`的`_document == null`
    保护形同虚设（`_document`不是`null`，是一个已释放的对象），调用会直接在已释放对象上抛
    `ObjectDisposedException`。这些异常本身会被`Application.ThreadException`接住、不会崩溃
    整个进程，但`FloatingPreviewWindow`每次重绘这个预览缩略图都会重新命中同一个异常，直到
    有人再成功加载一次新文件为止。**修复方式**：两个类的`LoadAsync`都改成先把`_document`/
    `CurrentFrame`/`PageCount`清成`null`/`0`，再尝试新的加载——这样加载失败时这些字段正确
    停留在"什么都没有"这个已经被`NextPage`/`PreviousPage`/`CurrentThumbnail`自己的判空逻辑
    正确处理的状态，而不是一个看起来非空、实际已经失效的引用。`PdfContentRenderer.
    RenderCurrentPage()`（`LoadAsync`内部调用，也被`NextPage`/`PreviousPage`直接调用，用于
    翻到一个已经成功打开的文档的某一页）单独有同一个问题——它自己那次`_document.Render(...)`
    调用同样在`CurrentFrame?.Dispose()`之后没有把`CurrentFrame`清空，一并按同样方式修了。
    **没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
103. **【新发现的真实bug，已修复】`PlaybackEngine.RedrawWaveformFrame()`同一次审计找到的第三个
    实例——`_audioVisualFrame?.Dispose()`之后紧接着重新赋值，中间没有清成`null`**：跟上面
    第102条`PdfContentRenderer`/`ImageContentRenderer`的`LoadAsync`是完全同一个形状，这次
    出现在音频可视化这一侧。`ApplyAudioVisual`的`DefaultBackgroundImage`分支本身没有这个问题
    （它上面已经统一做过一次`_audioVisualFrame = null`），但`RedrawWaveformFrame()`自己
    独立又做了一次"`Dispose()`旧的、立刻赋值新的"，没有经过那次统一清空——如果
    `AudioVisualRenderer.CreateWaveformFrame`（纯GDI+绘制，真实内存压力下的`Bitmap`/
    `Graphics`分配失败虽然罕见但并非不可能）抛出异常，`_audioVisualFrame`就会卡在"已经
    `Dispose()`过的旧对象"这个状态。**这次触发频率的特殊之处**：这个方法不是像`LoadAsync`
    那样一次性调用，而是`_waveformTimer`每次tick（约15fps）都会重新执行一次，只要一个
    `AudioVisual.Waveform`的音频文件还在播放就会持续触发，比`LoadAsync`每次播放新文件才
    触发一次的频率高得多——失败一次之后下一次tick会在同一个已释放对象上再调用一次
    `Dispose()`（无害，`Bitmap.Dispose()`是幂等的）然后重试，但`ContentSurface`会一直
    停留在展示/重绘它最后一次成功收到的那个（其实已经被这次失败的调用释放掉的）帧。
    **修复方式**：跟第102条完全一致——在`CreateWaveformFrame`调用之前先把`_audioVisualFrame`
    清成`null`。**没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机
    验证过。
104. **【新发现的真实bug，已修复，包含对第102/103条自己遗留缺口的补修】`ContentSurface`独立持有
    一份它被`SetFrame`过的`Bitmap`引用，第102/103条修复"渲染器自己的`CurrentFrame`不再悬空"
    并不能让`ContentSurface`同步知道这件事——这次连着修了三处**：`ContentSurface.SetFrame`
    只是把传入的引用原样存进`_frame`字段（见该类自己的doc comment"Owns none of the renderers
    or their bitmaps"），跟`PdfContentRenderer`/`ImageContentRenderer`/`_audioVisualFrame`各自
    是否已经清空/置空完全独立——第102、103条修的是"渲染器自己的字段不要悬空"，但从来没有告诉
    `ContentSurface`"你手上那份引用指向的对象已经被释放了"，而`ContentSurface.OnPaint`每次
    重绘（窗口移动、最小化恢复、任何触发`Invalidate`的事件——不是罕见场景）都会用`_frame`
    调用`Graphics.DrawImage`，在一个已释放对象上必然抛异常。具体修了三处：（一）
    `PlaybackEngine.OnImageOrDocumentFailed`（`PlayImageAsync`/`PlayDocumentAsync`整个文档/
    图片加载失败时的统一入口）新增`_overlay.ContentSurface.SetFrame(null)`——`LoadAsync`
    自己`CurrentFrame?.Dispose()`那一步释放的正是`ContentSurface`上一次成功`SetFrame`时
    拿到的那个`Bitmap`，之前完全没有人告诉`ContentSurface`清空它。（二）新增私有方法
    `PlaybackEngine.TryTurnDocumentPage`，让`NextManual`/`PreviousManual`不再直接裸调用
    `_pdfRenderer.NextPage()`/`PreviousPage()`——这两个方法内部会调用
    `PdfContentRenderer.RenderCurrentPage`，同样可能因为"文档内某一页本身损坏"而抛出异常
    （见第102条`RenderCurrentPage`那次修复的场景描述），之前这条路径完全没有任何`try/catch`，
    异常会直接从悬浮预览窗的"上一页/下一页"按钮点击处理器里裸抛出去——现在统一走
    `OnImageOrDocumentFailed`上报并清空`ContentSurface`，不再静默吞掉或者裸抛。（三）
    `RedrawWaveformFrame()`自己也补上了同样的`try/catch` + `SetFrame(null)`——第103条那次
    修复的commit说明里其实已经写清楚"`ContentSurface`会一直停留在...已经被释放掉的帧"这个
    残留问题，但当时只修了`_audioVisualFrame`字段本身、没有连着修`ContentSurface`，这次一并
    补上，属于对自己此前那次修复遗漏的追加修正。**为什么第102条最初没有一次性发现这一层**：
    第102条的自检推理集中在`PlaybackEngine.CurrentThumbnail`/`NextPage`/`PreviousPage`的
    `_document == null`判空逻辑这一条具体路径上，`ContentSurface`是另一个独立类、通过完全不同
    的方式（`SetFrame`传引用）持有同一个`Bitmap`，属于同一个根因（"disposed但被下游继续持有"）
    的第二种表现形式，第一轮审计时没有顺着这条线索往下追。**没有做的部分**：这次改动本身没有
    在这个沙箱里跑过（没有dotnet），没有真机验证过。
105. **【新发现的真实bug，已修复，第104条同一根因的第三个受害者】`FloatingPreviewWindow`的
    `_thumbnail.Image`也独立持有一份`PlaybackEngine.CurrentThumbnail`读到的`Bitmap`引用，
    加载失败后同样有一段时间窗口会指向已释放对象**：`RefreshFromEngine()`目前只在按钮点击和
    `_refreshTimer`（每500毫秒一次）触发时才会重新赋值`_thumbnail.Image`；跟`ContentSurface`
    "只在显式`SetFrame`调用时才更新、之前从来没有失败时的`SetFrame(null)`调用"不同，这里的
    暴露窗口本来就会靠这个轮询定时器在500毫秒内自愈（`CurrentThumbnail`本身经第102条修复后
    已经能在失败后正确返回`null`），但500毫秒之内如果这个窗口发生任何重绘（窗口拖动、从最小化
    恢复——不是罕见触发条件），仍然会在一个已经被释放的`Bitmap`上抛异常，属于第104条同一个
    根因（"渲染器自己的状态修好了，但持有同一个引用的下游消费者不知道"）的第三处，只是暴露
    窗口比`ContentSurface`那处（一直持续到下一次成功加载为止）短得多。**修复方式**：这个类
    此前完全没有订阅过`PlaybackEngine`的任何事件（只靠按钮点击和轮询读取状态），这次新增订阅
    `_playback.PlaybackAbnormallyInterrupted`，失败时立即调用`RefreshFromEngine()`重新读取
    （此时已经是正确的`null`），把暴露窗口从"最多500毫秒"直接收紧到"立即"，不再依赖轮询定时器
    凑巧赶上。这个订阅没有对应的取消订阅——这个窗口和它持有的`PlaybackEngine`引用共享整个
    进程生命周期（跟这个应用里其它长期配对的单例订阅是同一个约定，例如`Program.cs`自己的
    `_stateMachine.StateChanged`订阅也从未取消过）。**没有做的部分**：这次改动本身没有在这个
    沙箱里跑过（没有dotnet），没有真机验证过。
106. **【新发现的真实bug，已修复；"唯一"这个说法后来被第107条纠正】`PlaybackEngine.
    PlayStandaloneAudio`是`PlayFile`switch语句里同步调用、完全没有`try/catch`保护的一个
    分支**：`PlayImageAsync`/`PlayDocumentAsync`
    各自都有专门的`try/catch`（见第102条`OnImageOrDocumentFailed`那条doc comment的完整推理：
    "文件在被加入活动之后又被删除/移动/损坏"是真实场景，不是假设），但`PlayStandaloneAudio`
    调用的`AudioContentController.Play`内部同样会`new AudioDecodeSource(path)`——完全同一类
    "打开任意用户提供的文件"风险——却从来没有任何东西接住它。`AudioController.PlaybackFailed`
    （`OnAudioFailed`，这个方法自己已经在订阅）只覆盖`Play()`已经成功启动*之后*、后台播放线程
    上发生的失败，从来没有覆盖过`Play()`自己同步构造阶段的失败——这个同步失败一旦发生，会
    直接从`PlayFile`本身裸抛出去，不会命中`OnAudioFailed`，也完全不会经过`OnImageOrDocumentFailed`
    /`PlaybackAbnormallyInterrupted`这一整套已经给图片/文档/视频/异步音频失败建立好的统一
    上报机制。**修复方式**：给`ApplyAudioVisual(file); AudioController.Play(file.SourcePath);`
    这两行包一层`try/catch`，失败时复用`OnImageOrDocumentFailed`——虽然名字里写的是"图片或
    文档"，但它实际做的三件事（清空`ContentSurface`、记录日志、触发`PlaybackAbnormallyInterrupted`）
    对独立音频加载失败同样完全适用，这次没有为了这一个新增调用方去重命名——跟
    `EveryStage.Transport.RtpVideoClock`自己的doc comment"Not worth a rename or a new file
    for one extra parameter on an existing method"是同一个判断。**没有做的部分**：这次改动
    本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
107. **【新发现的真实bug，已修复，纠正第106条"唯一"的说法】`PlayFile`switch语句里
    `MediaKind.Video`分支同样有完全一样的问题——`VideoController.Play(file.SourcePath)`
    这一行也是同步调用、也完全没有`try/catch`保护**：`VideoController.Play`内部会
    `new VideoDecodeSource(path, ...)`，跟第106条`AudioContentController.Play`内部
    `new AudioDecodeSource(path)`是完全同一类风险，同一个原因——`OnVideoFailed`（这个分支
    自己已经在订阅`VideoController.PlaybackFailed`）只覆盖`Play()`已经成功启动之后、后台
    播放线程上发生的失败，从未覆盖过`Play()`自己同步构造阶段的失败。第106条当时的审计只
    检查了独立音频这一个分支就断言它是"唯一"，没有把`switch`里其它分支也过一遍——这次
    补上video分支之后，图片/文档（`async`方法+各自`try/catch`）、独立音频（第106条）、
    视频（本条）这三类会加载用户文件的内容类型才算真正全部覆盖到。**修复方式**：给
    `VideoController.Play(file.SourcePath)`包一层`try/catch`，失败时同样复用
    `OnImageOrDocumentFailed`——它的`ContentSurface.SetFrame(null)`调用在这里是无害的：
    视频分支根本不用`ContentSurface`做呈现（呈现走的是`VideoSurface`/`SwapChainPresenter`，
    `ShowVideoSurface()`已经在这一行之前调用过），清空一个当前没有显示的控件没有任何可见
    影响。**没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
108. **【新发现的真实bug，已修复，是这次连续修复本身间接制造出来的一个新风险】
    `PlaybackAbnormallyInterrupted?.Invoke(file, ex.Message)`直接调用现在不安全了，
    因为第105条给这个事件新增了第二个订阅者**：这个事件原来只有`MainWindow.
    OnPlaybackAbnormallyInterrupted`一个订阅者（弹Toast），第105条给`FloatingPreviewWindow`
    也订阅了同一个事件（失败时立即刷新缩略图，见第105条）——这意味着如果`MainWindow`的
    Toast处理器本身抛出异常（比如Toast UI自己的构造逻辑有bug），`FloatingPreviewWindow`
    的订阅永远不会跑到，而且这个异常会直接从`PlaybackAbnormallyInterrupted?.Invoke(...)`
    传回调用方——这次连续几条修复（第102、106、107条）新增的所有`try/catch`最终都会走到
    这一次`Invoke`调用，如果这里不做隔离，这些`try/catch`辛辛苦苦接住的异常又会在这里
    重新抛出去，把之前几条修复的保护效果整个抵消掉。**修复方式**：跟`StateMachine.
    OutputStateMachine.RaiseStateChanged`/`RaiseCastSwitchChanged`完全同一个已经在这个
    仓库里用过的写法——新增私有方法`RaisePlaybackAbnormallyInterrupted`，用
    `GetInvocationList()`遍历每一个订阅者单独调用、单独`try/catch`、单独吞掉异常，
    保证一个订阅者的bug不会连累另一个订阅者拿不到通知，也不会把异常传回给这次刚刚新增
    的那几处`try/catch`。三处直接`Invoke`调用（`OnImageOrDocumentFailed`/`OnAudioFailed`/
    `OnVideoFailed`）全部改成调用这个新方法。**这条本身没有修其它三个事件
    （`FileStarted`/`PlaybackDeclinedByCastSwitch`/`LocalPlaybackStarting`）**：这三个
    目前都只有一个订阅者，"单个订阅者抛异常会不会影响其它订阅者"这个问题在只有一个订阅者
    时不存在实际差异——是否要顺手统一改写成同一个模式只是风格一致性问题，不是这次审计
    发现的真实bug，留给之后如果这些事件也长出第二个订阅者时再处理。**没有做的部分**：
    这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
109. **【已实现一半，原为已知缺口】`MediaFile.FadeDuration`/`VolumeFollowsFade`现在有真正的
    行为了，但只有淡入（fade-IN）这一半**：`PlaybackEngine`新增私有静态方法`FadeInDurationFor`
    （`file.VolumeFollowsFade ? file.FadeDuration : null`——`VolumeFollowsFade`是总开关，字段名
    本身就是"音量是否随渐变"，`false`时`FadeDuration`不管是什么值都不生效），在`PlayFile`的
    `Video`分支和`PlayStandaloneAudio`里分别传给`VideoContentController.Play`/
    `AudioContentController.Play`新增的可选参数`fadeInDuration`。两个`Play`方法都在
    `fadeInDuration`非空时把`AudioPlaybackClock.Volume`先设成0（而不是`AudioContentController`
    原来的`_volume`/`VideoContentController`原来隐含的1.0满音量），再由各自
    `RunPlaybackLoopCore`每次循环用`AudioPlaybackClock.PositionTicks / fadeInDuration.Ticks`
    算出0~1的线性进度、`Math.Clamp`夹住，重新设置`Volume`——用`PositionTicks`（而不是
    `Stopwatch`之类的独立墙钟）做时间基准是刻意的：这样淡入进度跟`Pause`/`Resume`天然保持一致
    （暂停时`PositionTicks`本身就停止前进，淡入进度也跟着冻结，不会在暂停期间继续跑）。
    **明确没有做的部分**：(a) 淡出（fade-OUT）——需要提前知道文件总时长才能算出"还剩多少时间
    进入淡出窗口"，但`AudioDecodeSource`/`VideoDecodeSource`都完全没有暴露时长（`IMFSourceReader::
    GetPresentationAttribute(MF_PD_DURATION, ...)`这个API这个仓库从来没用过），在这个连
    dotnet都没有的沙箱里盲目新增一个从未验证过的Media Foundation调用风险太高，这次刻意只做
    了不需要总时长、只需要`PositionTicks`就能算的淡入那一半。(b)
    `VideoContentController`本身没有任何持久化的`Volume`属性（不像`AudioContentController`的
    `_volume`字段跨`Play`调用保留用户设置）——目前没有任何UI暴露"单个视频文件音量"这个概念，
    所以`VideoContentController`的淡入目标音量就是`AudioPlaybackClock`本身的默认满音量1.0，
    不是某个记住的用户设置；这不是这次改动引入的缺口，只是顺手说明为什么两个类的淡入实现
    形状略有不同。(c) 没有编辑UI——`ActivitiesPanel`/`FilesPanel`目前都没有任何地方能设置
    `FadeDuration`/`VolumeFollowsFade`，跟这个仓库一贯"先做行为、再做UI"的顺序一致（见第
    61-62、71条），下一步如果要做的话应该跟第62条的`AudioPropertiesDialog`一样新增一个对话框。
    **没有验证过的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），淡入的音量渐变听感
    是否平滑、`AudioPlaybackClock.Volume`是否支持这么高频率（每个PCM块一次，通常几十毫秒一次）
    的设置调用都没有真机验证过。
110. **【新发现的真实bug，已修复】`DiscoveryService`构造函数：`new UdpClient()`成功后，
    紧跟着的`Bind()`如果抛异常，刚创建的socket就泄漏了**：跟`CastReceiver`构造函数里
    `RtpReceiver`那个已经修过的bug（见该类doc comment）完全同一个形状——`_socket = new
    UdpClient()`这一步先成功，分配了一个真实的原生socket句柄，紧接着`SetSocketOption`/
    `Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port))`这两步中`Bind`是真正可能
    抛出`SocketException`的一步——`DiscoveryProtocol.Port`是一个固定端口，"端口已被占用"是
    真实可达的失败场景（同一台机器上跑了第二个Terminal实例、上一次崩溃遗留的进程还占着这个
    端口、`ReuseAddress`本身并不保证一定能绑定成功），不是假设性的。这个异常一抛出，构造函数
    永远不会正常返回，调用方（`Program.cs`里的`new DiscoveryService(...)`）根本拿不到实例，
    也就永远没有机会调用`Dispose()`——刚创建的那个`UdpClient`会一直泄漏到进程结束。**这跟
    本README之前已经记录的另一个问题不是同一件事**：之前的记录说的是"`TerminalApplicationContext`
    没有给`new DiscoveryService(...)`这个调用包`try/catch`，失败时会让整个进程崩溃"（见该处
    记录，"这次刻意没有修，超出这轮重构范围"）——即使外层调用被包上了`try/catch`，调用方依然
    拿不到`DiscoveryService`实例本身，依然没有办法调用它的`Dispose()`；泄漏必须在这里、在
    局部变量还能被够到的时候关掉，光修外层调用点没用。**修复方式**：跟`CastReceiver`同一个
    写法——先把`new UdpClient()`的结果存进一个局部变量，`SetSocketOption`/`Bind`/
    `EnableBroadcast`都包进`try/catch`，失败时`Dispose()`这个局部变量再重新抛出，只有全部
    成功之后才赋给`_socket`字段。**顺手在同一次审计里发现Caster这边的镜像类
    `TerminalDiscoveryClient`也有完全一样的问题，同一次一起修了**（见Caster README对应条目）。
    **没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过
    `Bind`失败时的具体`SocketException`信息。
111. **【已实现，原为已知缺口】`MediaFile.FadeDuration`/`VolumeFollowsFade`现在有编辑UI了**：
    新增`UI/FadeDialog.cs`，跟`StayDurationDialog`同一个"复选框控制`NumericUpDown`是否可用"的
    写法——`VolumeFollowsFade`复选框（"为此文件启用淡入"）是总开关，勾选后下面的秒数
    `NumericUpDown`（1~60秒，默认3秒）才可编辑。`ActivitiesPanel`新增"淡入淡出..."按钮，
    只在选中一个`Kind`是`Video`或`Audio`的文件时启用（`UpdateButtonStates`），点击后打开
    `OnEditFade`——跟`OnEditAudioProperties`同一个"两个字段分别各自调用一次
    `LogPlaybackPropertyChanged`"写法。**这条本身是对第109条(c)"这个仓库不打算为它们加编辑UI"
    这个决定的自我纠正**：第109条写完之后回头看，`AudioPropertiesDialog`（第62条）早就已经
    证明了这个仓库的实际做法不是"必须100%功能做完才能加UI"——`IsBackgroundAudio`当时对应的
    背景音轨叠加功能完全是no-op，`AudioPropertiesDialog`依然给它做了编辑入口，只是带一条
    红色警示文字说明"勾选后这个文件将不会播放"；`FadeDialog`这次照抄同一个思路——淡入本身
    已经是真正能用的功能（不是no-op），只有淡出这一半还缺，所以对应的警示文字也不是"这个开关
    完全不起作用"，而是如实说明"目前只有淡入生效，淡出还没做"。**没有做的部分**：这次改动
    本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过；淡出的编辑UI本身还是没有——等
    第109条(a)提到的淡出行为真正实现之后，这个对话框大概率需要再加一个字段，不是这次能一起
    做的。
112. **【已实现，原为已知缺口】PLANNING.md §11"批量选择"的"加入活动"半——`FilesPanel`现在能把
    选中的一个或多个文件加入某个方案的某个活动了**：新增`UI/ActivityPickerDialog.cs`（方案
    下拉框+活动下拉框，选方案时联动刷新活动下拉框；活动下拉框为空时"确定"按钮禁用，避免选中
    一个还没有任何活动的空方案），`FilesPanel`新增"加入活动..."按钮，跟"移除"同样的
    enable-on-selection写法。**顺手做的重构**：把原来只存在于`ActivitiesPanel`里的私有
    `CloneFile`方法（`MediaFile`字段逐个复制、`Id`不复制/自动生成新值）提升成`MediaFile.Clone()`
    实例方法——`FilesPanel`这条新路径需要跟`ActivitiesPanel`自己的"添加文件..."完全同一份深拷贝
    逻辑，与其各自维护一份字段列表（将来`MediaFile`每加一个新字段就要同时改两处，忘改一处就是
    一个新bug的来源），不如提到数据类自己身上只维护一份。`ActivitiesPanel.CloneActivity`/
    `OnAddFile`都已经改成调用`source.Clone()`，行为完全不变，只是代码搬了位置——`StayDurationDialog`/
    `CompletionActionDialog`两处doc comment里提到`ActivitiesPanel.CloneFile`的地方也同步改成
    `MediaFile.Clone`。**没有做`ActivitiesPanel`那边的联动刷新**：`FilesPanel`加入活动后不会
    主动刷新`ActivitiesPanel`的树——这个面板原本就不持有`ActivitiesPanel`的引用，而
    `MainWindow.ShowPanel`已经保证每次切换到"活动"标签页都会重新`RefreshTree()`，跟这个应用
    里每个面板"显示时才刷新"的既有约定一致（例如`SettingsPanel.Refresh_`只有显示器列表那部分
    需要这样，见该方法自己的doc comment），不是遗漏。**仍然没有做的部分**：PLANNING.md §11同一句
    点名的"统一设置属性"——这个仓库连单文件属性编辑都是各自独立的对话框，还没有能同时编辑多个
    文件共同属性、处理冲突值的统一入口，PLANNING.md原文"跨类型选中时屏蔽不适用的属性项"具体
    交互细节也没有展开，这次没有尝试。这次改动本身没有在这个沙箱里跑过（没有dotnet），没有
    真机验证过。

## 尚未开始（阶段1剩余 + 后续阶段）

- 音视频同步的残余误差补偿（见"已知风险"第39-40条）——基础的"音频为主时钟+呈现线程等待"已经实现，
  但采集延迟差、RTP时间戳回绕、解码器FIFO假设这几项仍然是接受的已知限制，没有计划中的进一步方案
- WPS COM互操作：验证脚本见 `src/Poc/WpsComInteropSpike/`（PLANNING.md 标记为"风险仅次于阶段0"，
  这里只验证了"能否静默打开+翻页"，真正的编辑/保存集成到 Content Engine 仍未开始）
- 显示器热插拔/运行时重新绑定扩展屏（见"已知风险"第35、59、67条）——"已绑定的扩展屏运行中途改
  分辨率/位置"在第59条实现，"已绑定的显示器运行中途被整个拔掉"在第67条实现（干净地"断"，而不是
  静默什么都不做）；"启动时没绑定、运行中途插入新显示器"这一种场景仍然完全没有处理，见第59条(a)
  列出的具体理由（需要在运行时凭空搭建整套`_overlay`/`_videoSurface`/`_playback`/`_previewWindow`
  对象图，改动规模比这两条大得多）
- 悬浮预览窗、文件面板之间仍然没有联动（见"已知风险"第54条）——活动面板那一半已经在这一轮实现了
- 背景音轨叠加播放（`IsBackgroundAudio == true`，见"已知风险"第61条）——需要`PlaybackEngine`支持
  真正的多轨并发播放，目前完全没有实现；非背景音频（第61条已实现的那一半）不受影响
- `FadeDuration`/`VolumeFollowsFade`——淡入这一半已经在第109条实现了，编辑UI也已经在第111条
  补上了（`FadeDialog`，`ActivitiesPanel`的"淡入淡出..."按钮）。**淡出仍然完全没有做**
  （需要`AudioDecodeSource`/`VideoDecodeSource`暴露文件总时长，这个仓库目前没有任何代码路径
  查询过`IMFSourceReader`的`MF_PD_DURATION`，见第109条(a)的完整理由），对应的编辑UI自然
  也还没有——真正实现淡出行为之后，`FadeDialog`大概率需要再加一个字段，这不是这次能顺手
  做的
- PLANNING.md §8.2"音频以横向播放条展示（含播放/进度/音量/循环/独立投屏按钮）"——`FilesPanel`
  目前把音频文件跟图片/视频/文档放进同一个`ListView`缩略图网格，完全没有单独的横向行样式；这次
  排查PLANNING.md跟README的差异时发现这句话之前只在`FilesPanel`类doc comment里提过一次（且原话
  "audio playback itself isn't implemented anywhere in this repo yet"已经过时并顺手改正），从来
  没有作为已知缺口出现在README里。这句话点名的五项里，"循环"其实已经覆盖——`CompletionAction.Loop`
  是`MediaFile`级别的通用完成后动作，不分媒体类型，音频文件跟图片/视频一样可以设成"循环"（见风险
  第73条完成后动作编辑UI），只是从来没有单独针对"音频循环"这个说法在README里点出来过，这里补上。
  **【更新】"播放"和"暂停/音量"这三项现在也更完整了**：`ContentEngine.AudioContentController`
  不仅能播放，现在也有真正的暂停/恢复（见"已知风险"第81条）和音量调节（见第82条）能力了。
  **仍然没有做的部分**："进度"（拖动跳转到任意位置）需要`AudioDecodeSource`支持seek，这个仓库的
  Media Foundation封装从来没有做过这件事；"独立投屏按钮"具体含义PLANNING.md本身没有展开（跟双击
  播放已有的行为是否是同一件事也不确定）。这两项加起来仍然是真正需要新增播放引擎能力/产品决策的
  工作，风险等级和规模跟WPS COM互操作、背景音轨叠加播放是同一档，不是这次能顺手补上的UI接线，
  故意没有尝试；真正的"横向播放条"UI本身（含进度条等控件布局）也完全没有开始，`FilesPanel`仍然
  把音频文件放进跟图片/视频/文档相同的缩略图网格里，见该类doc comment——第81、82条给
  `FloatingPreviewWindow`加的暂停/音量按钮是这个悬浮小窗自己的临时UI，不是`FilesPanel`里描述的
  那条真正的横向播放条。
- PLANNING.md §11"批量选择"里的"统一设置属性"（见"已知风险"第79、112条——"删除"、"加入活动"
  两半都已经实现了）——需要先决定好清空/合并冲突值这类多选编辑的常见交互细节，PLANNING.md原文
  "跨类型选中时屏蔽不适用的属性项"也只针对这一项，这个仓库连单文件属性编辑（第71、73条）都是
  各自独立的对话框，还没有能同时编辑多个文件共同属性的统一入口，这次没有尝试
- **【文档修正，不是代码改动】`Activity`类自己的doc comment曾经声称"`Scenario.Activities`里的
  顺序是`PlayMode.SequentialAuto`的权威播放顺序"，这句话夸大了实际行为**：追踪
  `PlaybackEngine.TryAdvance`发现它的移动范围严格限定在`_currentActivity.Files`内部——一个活动
  的文件列表播完之后，`SequentialAuto`只会停在最后一项，绝不会自动跨到方案里的下一个活动继续播。
  PLANNING.md §6本身对"顺序自动/手动点选"的描述也只到活动级别（管的是活动自己文件列表内部的
  播放方式），没有提到跨活动自动播放。这次已经把`Activity.cs`那句夸大的doc comment改成准确
  描述现状。**这里单独列一条而不是当作纯粹文档问题略过**：一个方案由多个可拖拽排序的活动组成
  这件事本身（PLANNING.md §6"活动 Activity（可拖拽排序...）"），完整读下来确实可能意味着产品
  真正想要的是"一个方案从头到尾自动跑完所有活动"（类似"周一晨会方案"这个例子暗示的完整日程），
  但PLANNING.md原文没有明确到这个程度，这个仓库目前也完全没有实现——到底该不该做、要做成什么
  交互，仍然是本条要点名的产品决策空白，不是这次能顺手接上的一条UI接线
