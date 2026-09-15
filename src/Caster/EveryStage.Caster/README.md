# EveryStage.Caster — 投屏机（阶段2：发现/配对 + 屏幕捕获 + H.264硬件编码 + 传输层 + 音频采集，端到端已接通）

对应 `docs/PLANNING.md` §12 的UI描述、§7 的设备发现/配对流程的**投屏机一侧**，以及阶段2"屏幕捕获：
Desktop Duplication API (DDA)"、"视频编码：H.264 硬件编码"、"传输：UDP + RTP"三项，加上这一轮新加的
系统音频采集（PLANNING.md没有单独给音频采集一个章节，但"全屏捕获推流"隐含了画面+声音一起投）。
PLANNING.md §15 把整个捕获/编码/传输称为"第二大技术风险区"——这几块不但各自写完并有自检（音频除
外，见下方风险），还被 `Casting/LiveCastSession.cs` 接成了一条真正的端到端投屏管线：配对成功后
立即开始真的采集/编码/发送，Terminal侧（`src/Terminal/EveryStage.Terminal/Receiving/`）也有了
对应的接收/解码/播放。其中视频编码（`Encode/H264HardwareEncoder.cs`）仍然是这几块里风险最高、
最难验证的一块，原因见该文件自己的doc comment和下面"已知风险"的专门小节；音频这一轮走的是低风险
路线（原始PCM，不经过任何硬件编码器），见下方 `Capture/AudioCaptureSource.cs` 的专门小节。

## 现在能做什么

1. 启动后监听终端机的UDP广播beacon，标准的"待机态：目标终端机列表"（§12）
2. 选中一个终端机，点"开始投屏"——真的会发送配对请求（`DiscoveryProtocol.PairRequestMessage`）
   并等待终端机的响应
3. **配对成功后立即真的开始投屏，视频+音频**：`Casting/LiveCastSession.cs` 启动完整链路——
   视频：`ScreenCaptureSource`(BGRA) → `BgraToNv12Converter`(NV12) → `H264HardwareEncoder`(H.264
   访问单元) → `AnnexBNalSplitter`(拆NAL单元) → `RtpSession`(RTP/UDP)，发送到终端机固定的
   `DiscoveryProtocol.VideoRtpPort`；音频：`Capture/AudioCaptureSource.cs`(WASAPI loopback采集
   系统播放的声音，转成16-bit PCM) → 另一个 `RtpSession`(`SendRawPayloadAsync`，不经过H.264那套
   NAL分片) → `DiscoveryProtocol.AudioRtpPort`。同时通过 `DiscoveryProtocol.CastStartMessage`/
   `CastStopMessage`（单播）告诉终端机"我要开始/停止投屏了，视频分辨率、音频采样率/声道数分别是
   多少"，终端机据此启动/停止自己的 `Receiving/CastReceiver`。**音频是video之上的锦上添花，不是
   硬性要求**——如果这台机器上没有正在播放的声音、或者WASAPI初始化失败，`AudioCaptureSource`
   构造失败只会让这一次投屏退化成纯视频，不会连累视频一起失败。面板上实时显示分辨率/已捕获帧数/
   已发送访问单元数/已发送字节数/音频状态/**终端机确认**（终端机每秒回报一次已解码帧数，见下方
   "已知风险"里`CastStatusMessage`那一节），出错时如实显示错误而不是假装成功。
4. 面板上有一个"屏幕捕获自检"按钮——用 Desktop Duplication API (`Capture/`) 独立验证捕获本身，
   跟正在进行的投屏互不影响，用于定位问题出在哪一步。
5. 面板上还有一个"开始编码自检 (捕获→NV12→H.264)"按钮——`Encode/EncodeSelfTestRunner.cs`
   独立跑一遍捕获→转换→编码，同样不影响正在进行的投屏。
6. 面板上还有一个"运行传输自检"按钮——用一批人造的、形状像H.264 NAL单元的随机数据走一遍
   `RtpSession → 本机回环UDP → RtpReceiver → H264RtpDepacketizer`（`EveryStage.Transport`），
   逐字节核对收发是否一致，包括FU-A分片重组是否正确，同样不涉及真实投屏、不影响正在进行的投屏。
7. **【这次新加】面板上还有一个"开始音频采集自检 (WASAPI loopback)"按钮**——
   `Capture/AudioCaptureSelfTestRunner.cs` 独立驱动 `AudioCaptureSource`，显示采样率/声道数/
   已捕获字节数/近1秒吞吐量，不经过RTP发送、不涉及`LiveCastSession`，同样不影响正在进行的投屏。

这四个自检的角色从"补上还没接通的功能"变成了纯粹的独立诊断工具——真实投屏管线已经接通后，它们的
价值是在投屏出问题时帮助判断问题出在采集、编码、传输、还是音频采集哪一步，而不是必须先跑通它们
才能投屏。

## 已知风险 / 待验证事项

同样：本项目在 Linux 沙箱中编写，从未编译过。用到的 API（`UdpClient`、标准 WinForms 控件）都是
成熟、低风险的，风险主要在于：

1. **这是协议第一次有两个独立实现互相对话**：`src/Terminal/EveryStage.Terminal/Devices/DiscoveryService.cs`
   和这里的 `Discovery/TerminalDiscoveryClient.cs` 都是照着同一份 `DiscoveryProtocol.cs` 分别写的，
   从未真的在网络上跑过——序列化细节两边理解是否完全一致，只有在Windows机器上跑起来才能确认。
2. **配对超时与拒绝在UI上不区分**（`MainForm.OnStartButtonClick` 里 `response == null` 才是超时，
   否则是明确拒绝），这个区分逻辑本身没问题，但超时时间(15秒，`RequestPairingAsync` 的默认值)是
   随手定的，没有依据。
3. **【已实现，原为已知缺口】"已配对设备直接显示"**：新增 `Discovery/PairedTerminalStore.cs`
   （JSON持久化，跟Terminal那边`Devices/PairedDeviceStore.cs`是同一套写法：写临时文件再替换、
   反序列化失败时改名保留损坏文件而不是静默丢弃——两边故意各自独立实现，不抽共享类型，见该文件
   doc comment），每次成功配对（`MainForm.ShowPaired`）都会写入一条记录。待机列表现在是
   `TerminalDiscoveryClient.GetTerminals()`（当前收到beacon的、在线）和 `PairedTerminalStore.All`
   里没有对应在线条目的（离线，标"（离线）"后缀）两个来源合并显示的结果（`MainForm.TerminalListEntry`）。
   刻意没有持久化IP地址——配对过的终端机重新联网后的地址完全可能变了（DHCP续租、换网络），缓存一个
   旧地址去尝试比"暂时不知道"更危险，所以离线条目在列表里可见但不可选，必须等它重新广播beacon、
   变回在线条目才能真正发起投屏。
4. **单一网卡/广播地址假设**与 Terminal 那边一样（用 `IPAddress.Broadcast`），多网卡环境未处理。
5. `TerminalDiscoveryClient` 和 Terminal 的 `DiscoveryService` 各自独立维护了"pending request /
   TaskCompletionSource"这类相关性匹配逻辑，没有抽出共享代码——两边角色不对称（一个等确认、一个做
   确认），暂时觉得不值得为此再抽一层公共基础设施，但如果协议以后加更多"发送并等待响应"的消息类型，
   这块可能需要重新考虑。
6. **`Capture/ScreenCaptureSource.cs` 是这个仓库第二块真正的DirectX interop代码**（第一块是
   `EveryStage.Rendering`），风险级别和它一样高——`IDXGIOutputDuplication`/
   `OutputDuplicateFrameInformation` 相关的成员名是照着Vortice.DXGI大概率的命名方式猜的，没有对照
   实际安装的包核实。具体需要重点检查的点见该文件顶部的doc comment，这里不重复。
7. **`DXGI_ERROR_ACCESS_LOST` 的捕获方式**（`catch (Exception ex) when (ex.HResult == ...)`）：
   假设 `IDXGIOutputDuplication.AcquireNextFrame` 返回的失败 `Result` 经 `.CheckError()` 转换成
   异常后，`Exception.HResult` 能拿到原始HRESULT——这是`.CheckError()`已有用法的合理延伸，但这条
   具体路径（ACCESS_LOST这个特定错误码）没有实测过，真机上第一次触发时（比如切换RDP会话）要重点看。
8. **`ScreenCaptureSource` 每帧都新分配一个 `ID3D11Texture2D` 并 `CopyResource`**：为了在没编译
   验证过的代码里绝对避免"DDA的纹理在ReleaseFrame后还被外部持有"这种悬空引用bug，选择了更保险但
   更费显存/带宽的做法，不是最终产品该有的零拷贝方案——真正做编码管线时，应该考虑让编码器直接消费
   DDA给的纹理（在ReleaseFrame前完成），而不是先拷贝一份。
9. **单帧超时500ms、自检UI每500ms轮询一次统计数据**：这两个数字都是随手定的，没有根据实际DDA刷新
   频率或UI响应性要求推算过。
10. **传输自检是这个仓库第一个真正"跑得起来"的自检**（相对屏幕捕获/DirectX那一类只能等Windows机器
    才能验证的代码）——它只需要.NET运行时和本机回环网络，理论上甚至在这个开发沙箱里如果有dotnet
    也能跑，只是环境里确实没有dotnet。逻辑本身见 `EveryStage.Transport` 的README，这里不重复。

### `Encode/` — H.264 硬件编码，这个仓库风险最高的一块

`H264HardwareEncoder.cs` 自己的doc comment已经解释了*为什么*这块比其他DirectX interop代码风险更
高（异步MFT协议本身比阶段0用过的同步 `IMFSourceReader` 更容易出错，而且这个仓库从没写过一个异步
MFT的消费者）。这里列出具体需要重点核实的点，按怀疑程度从高到低：

11. **`ApplyLowLatencySettings` 里的 `ICodecAPI.SetValue` 调用和六个 `CODECAPI_*` GUID**：这是全
    仓库置信度最低的一段。`EncoderGuids.cs` 里这几个GUID是凭记忆重构的，不是照着刚读过的头文件
    抄的；`ICodecAPI.SetValue` 的Vortice签名（接受装箱的 `object`、专门的Variant包装类型、还是别
    的什么来表示原生PROPVARIANT）完全没有核实过。已经用 try/catch 包住且视为尽力而为——即使这整
    个方法在真机上编译失败或直接跑不通，也不应该阻塞构造函数本身，只是拿不到CBR/低延迟/短GOP这些
    调优效果。
12. **异步事件循环的具体API形状**（`RunEventLoop`）：`IMFMediaEventGenerator.GetEvent` 的参数、
    `IMFMediaEvent` 上拿事件类型的成员名（绝不可能字面叫 `GetType()`，这里当前是占位写法
    `mediaEvent.EventType`，真实绑定名字未核实）、`MediaEventTypes.TransformNeedInput` /
    `TransformHaveOutput` 这两个枚举成员名，都是按"Vortice通常怎么给MF的enum命名"猜的。
13. **`MFTEnumEx` 的签名**（`ActivateFirstHardwareEncoder`）：参数顺序、`MFTEnumFlag` 是否真的是
    这个名字、是否真的支持 `|` 组合，都未核实。找不到硬件编码器时会抛异常而不是静默回退到软件编码
    器——这是有意的：在能真正测试软件编码器路径之前，宁可显式失败也不要假装成功。
14. **`IMFActivate.ActivateObject<IMFTransform>()` 的泛型/类型化调用形状**未核实——原生签名是
    `ActivateObject(REFIID riid, void** ppv)`，Vortice的高层封装具体怎么把它变成一个泛型方法是猜的。
15. **`ProcessMessage(MFTMessageType.SetD3DManager, gpu.DeviceManager.NativePointer)`**
    （`BindDeviceManager`）：原生第二参数是 `ULONG_PTR`，指向设备管理器的裸 `IUnknown` 指针；这里
    假设 `gpu.DeviceManager` 上有一个 `.NativePointer` 属性能拿到这个指针，未核实。这一步如果绑定
    失败，编码器仍可能工作，但会退化成走系统内存拷贝而不是直接消费D3D11纹理——零拷贝的意义就没了。
16. **`OutputProvidesOwnSamples` 恒为假时的分支完全没实现**（直接抛 `NotSupportedException`）：
    这个仓库写这段代码时的假设是"主流硬件编码器都会设置 `MFT_OUTPUT_STREAM_PROVIDES_SAMPLES`"，
    但这个假设本身也未经真机验证——如果某个编码器MFT不这样，`H264HardwareEncoder` 现在完全用不了，
    需要补一个"按 `GetOutputStreamInfo` 报告的大小自己分配输出sample"的分支。
17. **`HandleNeedInput` 里的背压策略是占位的**：队列空了就 `Thread.Sleep(1)` 再返回，而不是阻塞
    等待下一帧——这在真实负载下会造成事件循环忙等，且没有实现任何"编码器跟不上时该丢帧还是该等"
    的策略，只是刻意没有在无法测试的前提下假装选了一个"正确"策略。
18. **`MFCreateDXGISurfaceBuffer` / `sample.SetSampleTime` / `SetSampleDuration` 的确切签名**未核
    实，尤其 `SetSampleTime`/`SetSampleDuration` 有意写成方法调用而不是C#属性赋值（本回合的一处自
    我修正，理由见文件内注释），以匹配这个代码库其他地方对不确定COM setter的处理约定。
19. **`BgraToNv12Converter`（反向使用视频处理器：BGRA→NV12）**：和解码方向的 `SwapChainPresenter`
    共享同一套"video processor做像素格式转换"的技术，但从未在编码方向验证过；具体存疑点是给
    `VideoProcessorBlt` 的输出目标用 `BindFlags.RenderTarget` 的NV12纹理是否真的是合法组合——如果
    真机上创建 `outputView` 失败，退路是去掉 `BindFlags`，该文件的doc comment里已经写了这个备选
    方案。另外，该转换器每次调用复用同一张输出纹理（不新分配），调用方（`EncodeSelfTestRunner`）
    必须在下一帧转换前用完/拷贝走——这个约定目前只在自检代码里遵守，真正接入编码管线时也要遵守。
20. **`H264HardwareEncoder.SubmitFrame` 的纹理生命周期约定**：调用者传入的纹理所有权转移给编码
    器，编码器在 `ProcessInput` 接受后（或提前关闭时）负责 `Dispose()`。`EncodeSelfTestRunner` 里
    因此在提交前显式拷贝了一份 `BgraToNv12Converter` 的输出纹理（见上一条），如果以后有新的调用方
    忘记这一步、直接把转换器复用的那张纹理交给编码器，会产生一个转换器下一帧写入时数据被并发修改
    的竞态——这个约定目前只有代码注释在提醒，没有运行时检查。
21. **码率/帧率相关的媒体类型属性打包方式**（`PackUInt64` 把宽高、分子分母打包进一个 `ulong` 给
    `MF_MT_FRAME_SIZE`/`MF_MT_FRAME_RATE`）：这是MF属性系统"用一个UINT64同时装两个UINT32"的标准
    技巧，位序（高32位在前还是分子在前）容易搞反，未核实。
22. **`frameRateNumerator` 假设整数帧率**：不支持 29.97 这类需要真分数表示的帧率，构造函数的doc
    comment里已注明这是简化，不是遗漏。

### `Casting/LiveCastSession.cs` — 端到端投屏，这次新加的部分

23. **【已实现，原为已知缺口】现在有一个轻量的应答/心跳机制了**：`DiscoveryProtocol` 新增
    `CastStatusMessage`（Terminal → Caster，单播，约每秒一次），带上终端机已解码的帧数/收到的视频
    字节数/音频字节数/两边各自的出错信息。`LiveCastSession` 订阅 `TerminalDiscoveryClient` 的
    `CastStatusReceived` 事件，按`DeviceId`过滤出自己正在投的这台终端机，暴露成
    `TerminalFramesDecoded`等只读属性和一个 `IsTerminalAlive`（超过5秒没收到新状态就视为"未确认"）。
    UI上新增了"终端机确认"这一行，第一次让"投屏中"不完全是本地自说自话。**这不是逐包确认或流控**，
    只是一个尽力而为的心跳——如果终端机根本没有接受这次投屏（没有配对权限、没有绑定扩展屏、解码器
    构造失败），Caster这边永远收不到第一条状态回报，`IsTerminalAlive`会一直是false，跟"曾经确认过、
    后来终端机沉默了"是同一个UI表现，这个仓库现在还是分不清这两种情况，也没打算分——多加一个"从未
    确认 vs. 确认后失联"的区分，需要的信息量和UI复杂度目前看不值得。
24. **`CastStartMessage` 用UDP尽力发送，可能先于/后于第一批RTP包到达，也可能直接丢失**：
    `LiveCastSession.Start()` 是"先fire-and-forget发cast_start，再立刻启动采集循环"，两者之间没有
    任何握手等待——正常局域网延迟下控制消息（走discovery socket）应该比D3D设备创建+编码器协商
    快得多，但这只是经验判断，没有验证过竞态情况（比如终端机那一侧`GetOutputAvailableType`循环
    卡住导致解码器创建很慢，而RTP包已经开始到达却无处安放）。
25. **`OnAccessUnitEncoded` 通过一个单读者 `Channel` 把NAL单元交给专门的 `RunSendLoop` 任务顺序
    发送**，而不是直接在编码器事件循环线程上 fire-and-forget：最早的写法是每个NAL单元各自
    `_ = SendNalUnitAsync(...)`，写完之后自我审查发现这样并发的多个"发送任务"之间的实际完成顺序
    不受保证（`RtpSession`内部递增的序列号是同步分配的没问题，但字节真正送上网线的顺序可能跟分配
    顺序不一致）——RTP接收端`RtpReceiver`/`H264RtpDepacketizer`不做乱序重排（见
    `EveryStage.Transport`README），顺序颠倒可能导致FU-A分片重组失败。改成单读者Channel后，
    "按编码顺序发送"是结构上保证的，不再依赖多个并发`Task`凑巧按顺序完成。**【残留风险已实现，
    原为已知缺口】**：Channel本身仍然是无界的（结构没变），但`OnAccessUnitEncoded`/`OnPcmCaptured`
    现在会在入队前检查一个独立维护的队列深度计数器，超过阈值（视频约2秒、音频约1秒的量，估算值）
    就整体丢弃这一个访问单元/这一段PCM，而不是继续无限堆积——具体实现和"为什么不直接给Channel本身
    设容量+DropOldest"的取舍见下方第48-50条。跟`H264HardwareEncoder`风险17"背压策略是占位的"仍然
    是两个独立的问题：17说的是编码器MFT自己的异步事件循环在输入队列满时怎么办，这里解决的是编码
    完成之后、送上网络之前这一段队列的背压，两者互不依赖，17依然完全未解决。
26. **只支持单一目标**：`LiveCastSession` 构造时绑定一个 `DiscoveredTerminal`，`RtpSession` 内部
    只有一个 `IPEndPoint`——不支持同时投屏给多个终端机（PLANNING.md 没有明确要求这个能力，但也
    没有明确排除）。
27. **`Stop()` 依赖 `IsRunning` 判断"是否要发cast_stop"**：如果 `Start()` 因为异常提前失败
    （`LastError`非空但从未真正开始循环），`Stop()`不会发送`cast_stop`——这是对的（cast_start本身
    也从未发出），但如果调用方在`LastError`非空时误以为"投屏已经开始，需要停止"而调用`Stop()`，
    行为上是安全的空操作，只是没有额外提示"其实什么都没开始过"。

### `Capture/AudioCaptureSource.cs` — 系统音频采集，这次新加的部分

跟视频编码那条路径比，这一块选的是刻意更低风险的方案：WASAPI loopback采集用的是这个仓库已经在
`AudioPlaybackClock`/`AudioTakeoverService`上用过的NAudio成熟API，而且音频完全不经过硬件编码器
（直接发送量化后的16-bit PCM），绕开了整个"再写一个异步/同步MFT消费者"的风险类别——代价是带宽
明显更高（未压缩PCM vs. H.264压缩后的视频），但这个仓库现在没有能力验证一个新的音频编码器MFT
（无论是MF的AAC编码器还是别的），所以选择先接通链路、把压缩留到以后。

28. **假设WASAPI loopback的原生混音格式是32位IEEE浮点**（`ConvertFloatToPcm16`）：这是WASAPI共享
    模式几乎普遍的格式，但不是API保证的——如果真机上报告的格式不是32位float，这个方法会抛异常而
    不是把字节误读成别的格式产出噪音，异常会被`LiveCastSession`按"音频尽力而为"的原则捕获，退化
    成纯视频投屏，不会崩溃整个程序。
29. **【已实现，原为已知缺口】新增`AudioCaptureSelfTestRunner`+"开始音频采集自检"按钮**：跟屏幕
    捕获/编码/传输三块已有的自检是同一个模式——只驱动`AudioCaptureSource`本身，不经过RTP发送/
    `LiveCastSession`，纯粹验证WASAPI loopback采集在这台机器上能不能跑起来（采样率/声道数/近1秒
    吞吐量KB/s），出错时`LastError`直接显示`AudioCaptureSource`构造或`CaptureFailed`事件报告的
    原始异常信息。这次特意在`MainForm`类doc comment里记录了一个尚未验证的假设：这个自检允许跟
    一次真实投屏同时运行（互不禁用），前提是WASAPI loopback捕获本身是"只读旁听渲染流"、不同于
    独占模式渲染那种会互相排斥的资源——这个假设本身符合WASAPI的一般设计，但这个仓库从没有在真机
    上验证过两个`WasapiLoopbackCapture`实例真的能同时跑在同一个渲染设备上不互相干扰。
    面板高度（`ClientSize`）从506涨到600以容纳第四个自检区块。
30. **`OnDataAvailable`/`OnRecordingStopped`在NAudio自己的采集回调线程上跑，`ConvertFloatToPcm16`
    抛出的异常会被这个方法自己的try/catch捕获转成`CaptureFailed`事件**——但`WasapiLoopbackCapture`
    本身在其回调线程里如果抛出未被这层try/catch覆盖的异常（比如构造`WasapiLoopbackCapture`本身
    没有异常但内部COM调用运行时失败），NAudio内部怎么处理这类异常没有核实过，可能表现为
    `RecordingStopped`事件带着异常（已处理），也可能是完全没有捕获到的场景。
31. **音频RTP分片大小(`MaxAudioPayloadBytes = 1280`)是估算值，不是实测的**：WASAPI共享模式回调
    间隔通常在10ms左右，但具体缓冲区大小依赖声卡驱动/系统配置，1280字节的选择留了一些余量，但
    没有在真实硬件上验证过是否总能避免IP分片。
32. **没有处理系统默认播放设备切换**：`WasapiLoopbackCapture`绑定的是构造时的默认渲染设备——如果
    投屏过程中用户切换了系统默认输出设备（比如插拔耳机），这个类不会自动跟着切换，捕获到的会是
    旧设备（如果还存在）或者直接停止（如果设备消失，`OnRecordingStopped`应该会带着异常触发
    `CaptureFailed`，但没有实测过具体行为）。
33. **没有回声消除/音量归一化**：纯粹是"把系统正在播放的声音转发出去"，不做任何后处理——如果
    Terminal机器本身也在播放声音又被其他设备投屏监看，这属于产品层面的场景设计问题，不是这个类
    的职责范围。

### `CastStatusMessage` 应答机制 — 这次新加的部分

34. **状态回报本身也是尽力而为的UDP、没有重传**：`SendCastStatusAsync`跟这个协议里其他所有消息
    一样，丢了就丢了，靠下一次(约1秒后)重新报告来"自愈"——`IsTerminalAlive`的5秒容忍窗口就是为了
    不让单个丢包被误判成"终端机失联"，但如果连续丢5秒的状态包（网络抖动/拥塞），Caster这边会显示
    "未确认"，即使终端机其实一切正常——UI上没有区分"终端机真的有问题"和"最近几个状态包都丢了"。
35. **状态回报走的是discovery socket，和视频/音频RTP流完全独立**：如果discovery端口(47990)本身
    因为某种原因被防火墙/网络设备限速或丢包率更高，会造成"画面音频都在正常播放，但Caster却显示
    未确认"这种误导性的状态——状态回报的健康程度不代表媒体流的健康程度，这个仓库现在把两者放在
    UI上却没有特别提醒用户这个区别。
36. **【已实现，原为已知缺口】`CastStatusMessage`现在带了发送时刻**：新增`SentAtUtc`字段
    （Terminal端`SendCastStatus()`填成`DateTimeOffset.UtcNow`），`LiveCastSession.OnCastStatusReceived`
    收到后计算`DateTimeOffset.UtcNow - status.SentAtUtc`存进新增的只读属性
    `LastStatusLatencyEstimate`，`MainForm`的"终端机确认"那一行现在会带一个"延迟估算"数字。
    **这个数字只在两台机器时钟大致同步时才有意义**——这个协议本身完全没有时钟偏移协商，如果
    Terminal和Caster的系统时钟本身就差得远，算出来的"延迟"主要反映的是时钟偏差而不是真实网络
    延迟；这个仓库没有办法从沙箱里验证真实局域网环境下两台Windows机器实际的时钟同步情况，所以
    UI上和doc comment里都没有把这个数字包装成"精确延迟"，而是明确标注为"估算"并解释了这个前提。
    `LastStatusReceivedAt`本身（"Caster收到的那一刻"）没有变，两个字段现在并存，各自服务不同的
    问题："新鲜度判断"继续用收到时刻，"延迟估算"才用这个新时间戳。
37. **【已实现，原为已知缺口】持续解码/播放出错现在会由Terminal自己决定断开**：这个产品决策
    （是否要在解码/播放持续出错时自动断开）已经做出并实现——见
    `src/Terminal/EveryStage.Terminal/README.md`"已知风险"第48-51条：`CastReceiver`新增连续
    失败计数器，`Program.cs`新增`CheckDecodeHealth()`，连续约3秒（90次）解码/播放失败就自动
    `StopCasting()`回到待机态。这一侧（Caster）不需要跟着改任何代码——`LiveCastSession`早就是
    "每次状态包直接覆盖`TerminalVideoError`/`TerminalAudioError`"，Terminal那边一旦停止发送
    状态包（因为它自己已经断开了），Caster这边`IsTerminalAlive`超时机制会自然接管，跟Caster自己
    的Terminal崩溃场景走的是同一条路径。

### 音视频同步 — 这次新加的部分

38. **【已实现，原为已知缺口】音视频同步**：`OnPcmCaptured`不再给`_audioTimestamp`累加发送的
    采样数（该字段已删除），改成跟视频同一个`_clock`（Stopwatch）经过时间推导，只是把
    `RtpVideoClock.FromElapsed`原来固定90kHz的重载拆出一个通用的`clockRate`参数重载，音频这边
    传自己的采样率——这样两条流的RTP时间戳就落在同一条"投屏会话开始后经过多少时间"的时间线上，
    是Terminal端`CastReceiver`能做同步的前提。Caster这一侧本身不做任何等待/配速，纯粹是让时间戳
    可比较，真正的配速逻辑在Terminal端（见`EveryStage.Terminal`README的对应章节）。
39. **两条流从各自的采集设备到真正调用`FromElapsed`取时间戳之间，中间还有一段未测量的延迟**：
    视频侧是Desktop Duplication的`AcquireNextFrame`到编码器`OnAccessUnitEncoded`触发之间的编码
    延迟，音频侧是WASAPI loopback的捕获回调延迟，两者不保证相等，也没有做任何补偿——这意味着即使
    时间戳换算完全正确，Terminal端算出来的同步结果仍然会带一个未知大小的固定残余偏差，这套方案
    从设计上就没打算解决这一层，只解决了"两条时钟压根不在同一条时间线上"这个更基础的问题。
40. **RTP 32位时间戳约13小时后回绕**（`RtpVideoClock.FromElapsed`/`ToElapsedTicks`的doc comment
    里都记了这条），这次的同步方案完全没有处理回绕——投屏会话如果连续开着超过这个时长，换算出来的
    时间戳会变成一个错误的、看起来"更早"的值，这是接受的已知限制，不是这次修改遗漏的。

### 已配对设备直接显示 — 这次新加的部分

41. **【已实现，原为已知缺口】新增"移除配对"按钮**：`PairedTerminalStore.Remove(Guid)`早就存在
    但一直没有调用方，跟`Terminal.DevicesPanel`的"移除配对"是同一个交互模式——选中一项才启用、
    点击后二次确认、确认后从持久化列表里删除并刷新。区别于Terminal那边只对已配对设备生效，这里
    在线/离线条目都能选中并移除（`_pairedTerminals.Find`先判断这个条目是否真的有持久化记录，
    没有的话按钮保持禁用——一个当前在线但从未跟这台Caster配对过的终端机没有什么可移除的）。
    对一个当前在线的条目点"移除配对"不会让它立刻从列表消失（它仍在广播beacon，`RefreshTerminalList`
    的在线来源跟持久化列表完全独立），只是它下次离线后不会再出现，也不会在下次Caster重启时被记住
    ——确认对话框里针对这两种情况分别给出了不同的提示文案，而不是用同一句话含糊带过。
42. **`PairedTerminalStore`和Terminal端`PairedDeviceStore`是两份完全独立、从未互相验证过的JSON
    持久化实现**（故意不共享代码，见该类doc comment），如果两边谁的写文件逻辑有一个字节层面的
    bug（比如`File.Replace`在目标文件不存在时的行为差异），只有两边都在真机上跑过才可能发现——
    这跟仓库里其他"两个独立实现互相对话"的风险（见风险1）是同一类问题，只是这次两边完全不通信，
    纯粹是巧合地用了同一种持久化模式。
43. **合并列表（在线+离线）目前完全依赖`RefreshTerminalList`这个1秒定时器**，配对刚成功的那一刻
    是在`ShowPaired`里手动`Upsert`，但要等到下一次面板切回待机态、下一次定时器触发才会真正反映在
    列表上——因为配对成功后立刻就切到"投屏中"面板了，待机列表在这个时间窗口里本来就不可见，实际
    观察不到这个延迟，只是值得记录这里没有做"写入后立即刷新UI"这一步，纯粹依赖下一次定时刷新。

### "投屏中"面板的时长显示与常驻隐私提醒 — 这次新加的部分

44. **【已实现，原为已知缺口】"投屏中"面板现在有持续的隐私提醒和时长显示了**：之前只有"开始投屏"
    前的一次性`privacyLabel`，真正投屏过程中完全没有任何持续提醒——`LiveCastSession`新增只读的
    `Elapsed`属性（直接读已有的`_clock`，不新起一个计时器），`MainForm`新增`_privacyReminderLabel`
    常驻显示"正在投放整个屏幕"+已投屏时长，跟已有的`_liveCastStatsTimer`（500ms一次）同步刷新。
    时长格式化特意用`(int)elapsed.TotalHours`而不是`TimeSpan`的`hh`自定义格式说明符——后者按24小时
    表盘那样折返，一次连续投屏如果真的跑到24小时会显示错误的、折返过的小时数；不过这本身已经是个
    次要问题，因为RTP 32位时间戳本身在约13.25小时就会先回绕（见风险40），这套时长显示不太可能真的
    撑到自己的24小时上限就先遇到那个更根本的限制。
    为了腾出这条新标签的位置，"投屏中"面板下方所有控件（统计文字、停止按钮、三个自检按钮及其状态
    文字）的Y坐标都整体下移了24px——手工核对过新布局最底部的`_transportStatsLabel`仍然落在当时的
    `ClientSize.Height`(506)以内，不需要放大整个窗口（`ClientSize`后来在新增音频采集自检那一轮
    涨到600——见风险29，这条记录的仍然是它写下时的真实状态，不追溯修改）。

### 选择捕获哪个显示器 — 这次新加的部分

45. **【已实现，原为已知缺口】新增显示器选择**：`ScreenCaptureSource`新增静态方法
    `EnumerateOutputs`，循环`adapter.GetOutput(i)`直到抛出为止列出这个适配器上的所有DXGI输出
    （用的是`H264HardwareDecoder.ConfigureNv12OutputType`已经用过的"循环到API自己报告耗尽为止"
    同一个模式）。`MainForm`待机面板新增一个显示器下拉框（`RefreshMonitorList`），选中的
    `outputIndex`原样传给新增的`LiveCastSession(..., outputIndex)`构造函数参数，再原样传给
    `ScreenCaptureSource`的同名参数——三层之间不存在任何"翻译"步骤，因为它们用的是同一套DXGI
    适配器输出枚举顺序，不是分别独立编号再互相映射。**这是全新的、未经验证的Vortice.DXGI用法**
    （`IDXGIOutput.Description`／`OutputDescription`的`DeviceName`/`DesktopCoordinates`字段名），
    跟这个文件里`IDXGIOutputDuplication`那部分是同一类风险，从未针对真实安装的Vortice.DXGI包
    核实过。
    刻意没有走"复用Terminal那边`MonitorService`（WinForms `Screen.AllScreens`）"这条更省事的
    路——那样得到的下标是WinForms自己的显示器枚举顺序，跟DXGI的`adapter.GetOutput(i)`下标是否
    真的一一对应完全是另一个未经验证的假设；用同一套DXGI API既枚举又消费，从设计上就不存在这层
    映射风险，即使这意味着要多写一段新的、同样未验证的DXGI代码。
46. **下拉框的枚举跟`RefreshMonitorList`同一个"每次回到待机态都重新枚举"约定**（构造函数里调用
    一次，`ShowStandby()`里再调用一次）——这次特意没有重蹈上一轮才刚修过的
    `SettingsPanel.PopulateMonitorComboBox`"只在构造时枚举一次"覆辙，选择保留（而不是像
    `SettingsPanel.Refresh_()`那样只保留"当前选中项"）当前选中的`outputIndex`：如果对应显示器
    被拔掉了会回退到列表第一项，而不是留着一个已经不存在的选择。
47. **枚举显示器需要临时构造一个`D3D11Device`，用完立即释放**：这是跟`CaptureSelfTestRunner`/
    `EncodeSelfTestRunner`已有的"自检需要自己一整套GPU资源"同一个模式，但这里纯粹是为了填一个
    下拉框而创建/销毁一整个D3D11设备，比起真正开始投屏时才创建的那个更浪费——如果这个下拉框将来
    需要更频繁地刷新（比如响应`WM_DISPLAYCHANGE`而不是只在回到待机态时），这个"每次都新建一个
    设备"的开销可能需要重新评估。

### `LiveCastSession` 两个发送队列的背压策略 — 这次新加的部分

48. **【已实现，原为已知缺口】视频/音频两个发送队列现在都有背压+整体丢弃策略了**：`Channel`本身
    仍然是无界的（结构没变，见风险25），但`OnAccessUnitEncoded`/`OnPcmCaptured`现在会在真正入队
    前，用一个`Interlocked`维护的深度计数器检查"这个队列现在积压了多少"——视频超过60个访问单元
    （约2秒，按30fps估算）、音频超过100个分片（约1秒，按WASAPI约10ms一个缓冲区估算）就整体丢弃
    这一次的访问单元/PCM缓冲区，一个NAL/字节都不放进`Channel`，而不是继续无限堆积。丢弃次数分别
    计入新增的`AccessUnitsDroppedForBackpressure`/`AudioChunksDroppedForBackpressure`，`MainForm`
    的"投屏中"面板只在丢弃次数大于0时才多显示一行警告，避免健康状态下的界面被一条永远是"0"的
    统计行占用空间。这行新警告文字追加到`_liveCastStatsLabel`固定高度(90px)的末尾，没有为它调整
    过标签高度/整体布局——这个标签本来就已经装了5行文字，真出现丢包警告需要额外一行时，视觉上
    有没有被裁掉的风险，这次没有验证过、也没有修。
49. **刻意没有直接给`Channel`本身设置容量+`BoundedChannelFullMode.DropOldest`**，尽管那样代码量
    更少：`_sendQueue`里的一个访问单元由多个NAL单元组成（`isLastNalOfAccessUnit`标记最后一个），
    如果让`Channel`自己的内置策略去丢弃队列最前面的单个NAL单元，完全可能只丢掉一个还没发完的
    访问单元中间的几个NAL单元，把它撕成一个残缺的访问单元交给`RunSendLoop`发出去——终端机那边
    `CastReceiver`重组出来的会是一个再也无法正确解码的Annex-B流。改成"入队前一次性判断这一整个
    访问单元还要不要入队"，保证了`Channel`里任何时候要么是完整的访问单元、要么完全不存在，代价
    是多维护一个独立的深度计数器（`_queuedAccessUnitCount`/`_queuedAudioChunkCount`，写入方
    `Interlocked.Increment`/`Decrement`，读取方`Interlocked.CompareExchange(_, 0, 0)`）而不是
    直接读`Channel.Reader.Count`。音频分片之间没有这种互相依赖，理论上可以直接用`Channel`自带的
    `DropOldest`，这里为了两条队列写法保持一致、少一种模式要记，也用了同一套计数器方案。
50. **60/100这两个阈值都是估算值，没有真机验证过**：跟这个仓库里几乎所有"没有真实网络/硬件可以
    测"的时间常数一样，只是"生成速率×大约2秒/1秒"倒推出来的数字——`H264HardwareEncoder`是否真的
    稳定在30fps、WASAPI回调是否真的稳定在约10ms一个缓冲区，这两个假设本身都没有核实过，真机上如果
    编码帧率明显不同，这两个阈值背后代表的真实缓冲时长也会相应偏离"2秒/1秒"这个估算。

## 尚未开始

- 音频压缩（当前是未压缩16-bit PCM，带宽明显高于H.264视频——真要做流畅的低延迟音频编码需要走
  Media Foundation的AAC编码器MFT，跟视频编码器同一类风险，这一轮为了先接通链路特意绕开了）
- 状态回报的可靠性/时间戳（见风险34-36）——目前是最简单的"定时报告+新鲜度窗口"，没有重传、没有
  真正的往返延迟测量
- `H264HardwareEncoder` 里"编码器不提供自己的输出sample"这条分支（见上方风险16）
- 编码器`HandleNeedInput`自己的背压策略仍然是占位的（见上方风险17）——这次解决的是编码完成之后
  发送队列这一段，MFT异步事件循环内部输入队列满时该怎么办仍然完全没有实现
