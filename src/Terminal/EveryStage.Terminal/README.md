# EveryStage.Terminal — 终端机主程序框架 + 本地内容引擎 + 设备发现配对 + 部分主界面

对应 `docs/PLANNING.md` 第15章阶段1的前两项："终端机主程序框架（覆盖式窗口、投屏开关/断状态机、
数据持久化）"+"本地内容引擎（图片/视频/PDF）"，阶段3的"设备发现/配对"部分（§7），以及阶段4正式
四大面板主界面（§8.2）里的**文件**和**设备**两个面板。**不包含**WPS正式集成（有独立验证脚本，
见下）、传输接收端（阶段2，实际推流数据的接收解码）、活动/设置两个面板、正式视觉设计（阶段5）。

现在有一个真正意义上的操作界面（`UI/MainWindow.cs`）：左侧固定导航（投屏开关、断、文件/活动/设备/
设置四个入口、状态指示）+ 右侧内容区。文件面板能导入/拖拽文件、按类型筛选、双击播放——这是
`PlaybackEngine.RequestPlay(MediaFile)` 第一次有真实调用方。设备面板能看已配对设备列表并移除配对。
活动和设置两个面板还是占位（见下）。覆盖窗口 + 悬浮预览窗 + 配对确认弹窗这些阶段1/3的产出维持不变。

## 已实现

| 模块 | 对应 PLANNING.md | 说明 |
|---|---|---|
| `Data/` | §6 数据结构 | Scenario/Activity/MediaFile 模型 + JSON持久化（原子写，损坏时降级为空白方案而不是崩溃循环）；`FileLibraryStore` 是这次新加的——独立于任何活动之外的"文件库"，§6原模型没有覆盖，文件面板需要它 |
| `Display/MonitorService.cs` | §5, §7 | 用 WinForms `Screen` 枚举显示器，选取非主屏作为"绑定扩展屏" |
| `Display/OverlayWindow.cs` | §5, §9.2 | 无边框置顶覆盖窗口，常驻创建、仅隐藏/显示，定期重申TOPMOST z-order |
| `StateMachine/OutputStateMachine.cs` | §9, §10 | 投屏开关与待机/输出状态两个独立维度；设备请求不受开关约束；"断"不改变开关状态 |
| `Audio/AudioTakeoverService.cs` | §9.3 | 媒体键暂停 + 静音兜底两段式，断开时对称恢复 |
| `Tray/TrayIconController.cs` | §10 | 托盘图标 + 开关菜单 + 退出（"关闭程序"的唯一入口） |
| `ContentEngine/` | §3 | 图片(GDI+，WIC编解码器) + PDF(PdfiumViewer) 渲染器、画面呈现控件（等比缩放、黑边）、视频播放控制器(`VideoContentController`，复用 `EveryStage.Rendering` 的D3D11零拷贝管线，直接对接 `OverlayWindow.VideoHost` 的独立SwapChain) |
| `Playback/PlaybackEngine.cs` | §6, §9 | 把上面三种渲染器接到 Scenario/Activity/MediaFile 数据模型和投屏开关/断状态机上："点文件"→(开关判断)→选渲染器播放→按停留时长/完成动作(NextItem/Loop/HoldOnLastFrame)推进；提供悬浮预览窗按钮要用的手动上一项/下一项 |
| `Logging/` | §14.4 | 三类物理独立的按天滚动日志：`FileOperationLogger`(文件操作)、`PlaybackLogger`(播放/投屏记录，已接入`PlaybackEngine`)、`DeviceConnectionLogger`(设备连接，已接入`DiscoveryService`)；JSON-lines格式 + 自动清理过期文件 |
| `Devices/` | §7 | 设备发现(UDP广播 `DiscoveryService`)、配对(信任/手动确认、被投放/被监看权限分离)、配对设备列表持久化(`PairedDeviceStore`)。设备指纹(`DeviceIdentity`)与协议格式(`DiscoveryProtocol`)现在都在 `src/Shared/EveryStage.Discovery/`，因为 `src/Caster/EveryStage.Caster/` 也要用同一套 |
| `UI/FloatingPreviewWindow.cs` | §8.3 | 悬浮预览窗：LIVE标识、缩略图(仅图片/PDF，视频暂无)、文件名、上一项/暂停/下一项/断 四个按钮、置顶开关；拖动位置靠"常驻同一个Form实例、只隐藏不销毁"天然记住 |
| `UI/PairingConfirmationDialog.cs` | §7 | 配对请求的弹窗确认（接受/拒绝 + 被投放/被监看/信任三个独立勾选项）；不含PIN码交换，`DiscoveryProtocol`目前没有PIN字段 |
| `UI/MainWindow.cs` | §8.1 | 主界面外壳：左侧导航(投屏开关/断/四个面板入口/状态) + 右侧内容区；关闭窗口只隐藏不退出进程（终端机要常驻），托盘菜单"打开主界面"或双击托盘图标可以召回 |
| `UI/Panels/FilesPanel.cs` | §8.2 | 文件面板：`ListView`缩略图网格 + 类型筛选(全部/图片/视频/文档/音频) + 导入对话框 + 从资源管理器拖拽导入 + 双击播放(`PlaybackEngine.RequestPlay`) |
| `UI/Panels/DevicesPanel.cs` | §8.2 | 设备面板：已配对设备列表(信任状态/被投放/被监看/配对时间) + 移除配对 |
| `UI/Panels/NotImplementedPanel.cs` | — | 活动、设置两个面板的占位符，写明缺什么而不是空白一片 |

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

## 尚未开始（阶段1剩余 + 后续阶段）

- `PlaybackEngine.RequestPlay(Activity, int)` 仍然没有真实调用方——`RequestPlay(MediaFile)`
  已经被文件面板用上了，但"活动"面板（选一个活动、按顺序播放里面的文件）还是占位符。
- WPS COM互操作：验证脚本见 `src/Poc/WpsComInteropSpike/`（PLANNING.md 标记为"风险仅次于阶段0"，
  这里只验证了"能否静默打开+翻页"，真正的编辑/保存集成到 Content Engine 仍未开始）
- 传输接收端（阶段2：真正的RTP/H.264接收解码，`Devices/`目前只做发现和配对握手，不涉及媒体流）
- 活动面板、设置面板（§8.2 剩余两个面板）——数据源(`Scenario`/`Activity`)和播放能力都已就绪，
  缺的是界面：活动的创建/编辑/拖拽排序，以及通用/显示/播放行为/网络与设备四个设置分类
- 悬浮预览窗与主界面文件面板之间没有联动（比如从悬浮预览窗"下一项"切换后，文件面板不会自动高亮
  对应的缩略图）——PLANNING.md §16第5项本身也把这类交互细节列为"待验证"
- `FileOperationLogger` 的方法（方案/活动创建/修改/删除、文件导入/删除、播放属性变更）仍然没有
  调用方——现在有了"添加一个活动"这类操作的天然位置（就是即将要做的活动面板），只是还没接上；
  文件面板的导入/移除倒是有了地方接，但目前还没接（见风险清单）
