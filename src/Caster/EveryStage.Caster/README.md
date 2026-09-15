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
   已发送访问单元数/已发送字节数/音频状态，出错时如实显示错误而不是假装成功。**没有任何应答
   机制**——Caster完全不知道终端机是否真的收到并显示/播放了画面/声音，见下方"已知风险"。
4. 面板上有一个"屏幕捕获自检"按钮——用 Desktop Duplication API (`Capture/`) 独立验证捕获本身，
   跟正在进行的投屏互不影响，用于定位问题出在哪一步。
5. 面板上还有一个"开始编码自检 (捕获→NV12→H.264)"按钮——`Encode/EncodeSelfTestRunner.cs`
   独立跑一遍捕获→转换→编码，同样不影响正在进行的投屏。
6. 面板上还有一个"运行传输自检"按钮——用一批人造的、形状像H.264 NAL单元的随机数据走一遍
   `RtpSession → 本机回环UDP → RtpReceiver → H264RtpDepacketizer`（`EveryStage.Transport`），
   逐字节核对收发是否一致，包括FU-A分片重组是否正确，同样不涉及真实投屏、不影响正在进行的投屏。

这三个自检的角色从"补上还没接通的功能"变成了纯粹的独立诊断工具——真实投屏管线已经接通后，它们的
价值是在投屏出问题时帮助判断问题出在采集、编码、还是传输哪一步，而不是必须先跑通它们才能投屏。

## 已知风险 / 待验证事项

同样：本项目在 Linux 沙箱中编写，从未编译过。用到的 API（`UdpClient`、标准 WinForms 控件）都是
成熟、低风险的，风险主要在于：

1. **这是协议第一次有两个独立实现互相对话**：`src/Terminal/EveryStage.Terminal/Devices/DiscoveryService.cs`
   和这里的 `Discovery/TerminalDiscoveryClient.cs` 都是照着同一份 `DiscoveryProtocol.cs` 分别写的，
   从未真的在网络上跑过——序列化细节两边理解是否完全一致，只有在Windows机器上跑起来才能确认。
2. **配对超时与拒绝在UI上不区分**（`MainForm.OnStartButtonClick` 里 `response == null` 才是超时，
   否则是明确拒绝），这个区分逻辑本身没问题，但超时时间(15秒，`RequestPairingAsync` 的默认值)是
   随手定的，没有依据。
3. **没有"已配对设备直接显示"**：PLANNING.md §12 原话"已配对直显"暗示 Caster 也应该记住之前配对过
   的终端机，即使对方暂时没有广播也能显示（可能标记为离线）。这里完全没做持久化，每次都是纯粹基于
   当前收到的beacon构建列表，重启就清空。
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

23. **完全没有应答/心跳机制**：`LiveCastSession` 发出RTP包和 `CastStartMessage`/
    `CastStopMessage` 之后就不再关心终端机的反应——UI上的"投屏中"只代表"本地采集/编码/发送没有
    报错"，不代表终端机真的收到、解码、显示了画面。如果终端机没有配对权限（`AllowCast=false`）、
    没有绑定扩展屏、或者解码器构造失败，Caster这边会显示得一切正常，用户不会得到任何反馈。这是
    有意先接通主链路、把应答机制列为明确的后续工作，不是遗漏。
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
    "按编码顺序发送"是结构上保证的，不再依赖多个并发`Task`凑巧按顺序完成。**残留风险**：Channel是
    无界的——如果网络发送速度长期跟不上编码速度（不太可能发生在真实局域网上，但没有验证过），
    队列会无限增长，没有背压或丢弃策略；这属于跟`H264HardwareEncoder`风险17"背压策略是占位的"
    同一类、有意先不解决的问题。
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
29. **完全没有自检**：跟屏幕捕获/编码/传输三块都有的自检按钮不同，`AudioCaptureSource`没有独立的
    "音频采集自检"——现在唯一的验证方式就是真机跑一次完整的`LiveCastSession`，如果音频路径本身
    (格式转换、分片、发送)有问题，不容易跟视频问题区分开。
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

## 尚未开始

- 应答/心跳机制：让Caster真正知道终端机是否收到、显示/播放了画面/声音（见风险23）
- 音视频同步：视频侧的RTP时间戳来自墙钟(`RtpVideoClock.FromElapsed`)，音频侧来自采样计数
  (`_audioTimestamp`累加发送的采样数)，两条时钟完全独立，Terminal那边`CastReceiver`也没有做任何
  基于时间戳的对齐——音频播放纯粹是"收到就播"，跟画面之间没有同步保证，长时间投屏后可能出现明显的
  音画不同步
- 音频采集自检（见风险29）
- 音频压缩（当前是未压缩16-bit PCM，带宽明显高于H.264视频——真要做流畅的低延迟音频编码需要走
  Media Foundation的AAC编码器MFT，跟视频编码器同一类风险，这一轮为了先接通链路特意绕开了）
- 已配对设备的持久化列表
- 真正的"投屏中"状态里的时长显示、隐私提醒条（PLANNING.md §12 提到的UI细节，目前只有停止按钮和
  统计数字）
- 选择捕获哪个显示器（`ScreenCaptureSource` 目前固定捕获 `outputIndex=0`，多显示器场景没有UI选择）
- `H264HardwareEncoder` 里"编码器不提供自己的输出sample"这条分支（见上方风险16）
- 编码器的丢帧/背压策略、以及`LiveCastSession`两个发送队列（视频/音频）的背压策略（同一类问题，
  见上方风险17、25）
