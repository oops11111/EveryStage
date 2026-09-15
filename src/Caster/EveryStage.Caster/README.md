# EveryStage.Caster — 投屏机（阶段2：发现/配对 + 屏幕捕获 + H.264硬件编码 + 传输层 + 音频采集，端到端已接通）

对应 `docs/PLANNING.md` §12 的UI描述、§7 的设备发现/配对流程的**投屏机一侧**，以及阶段2"屏幕捕获：
Desktop Duplication API (DDA)"、"视频编码：H.264 硬件编码"、"传输：UDP + RTP"三项，加上这一轮新加的
系统音频采集（PLANNING.md没有单独给音频采集一个章节，但"全屏捕获推流"隐含了画面+声音一起投）。
PLANNING.md §15 把整个捕获/编码/传输称为"第二大技术风险区"——这几块不但各自写完并有自检，还被
`Casting/LiveCastSession.cs` 接成了一条真正的端到端投屏管线：配对成功后立即开始真的采集/编码/
发送，Terminal侧（`src/Terminal/EveryStage.Terminal/Receiving/`）也有了对应的接收/解码/播放。
其中视频编码（`Encode/H264HardwareEncoder.cs`）仍然是这几块里风险最高、最难验证的一块，原因见
该文件自己的doc comment和下面"已知风险"的专门小节；音频最初几轮走的是低风险路线（原始PCM，不
经过任何硬件编码器），**这一轮换成了AAC编码**（`Encode/AacAudioEncoder.cs`，见下方"已知风险"
第53、55条）——真正投屏发送的音频不再是未压缩PCM。

## 现在能做什么

1. 启动后监听终端机的UDP广播beacon，标准的"待机态：目标终端机列表"（§12）
2. 选中一个终端机，点"开始投屏"——真的会发送配对请求（`DiscoveryProtocol.PairRequestMessage`）
   并等待终端机的响应
3. **配对成功后立即真的开始投屏，视频+音频**：`Casting/LiveCastSession.cs` 启动完整链路——
   视频：`ScreenCaptureSource`(BGRA) → `BgraToNv12Converter`(NV12) → `H264HardwareEncoder`(H.264
   访问单元) → `AnnexBNalSplitter`(拆NAL单元) → `RtpSession`(RTP/UDP)，发送到终端机固定的
   `DiscoveryProtocol.VideoRtpPort`；音频（**这一轮换成AAC，见下方"已知风险"第55条**）：
   `Capture/AudioCaptureSource.cs`(WASAPI loopback采集系统播放的声音，转成16-bit PCM) →
   `Encode/AacAudioEncoder.cs`(编码成ADTS封装的AAC access unit) → 另一个
   `RtpSession`(`SendRawPayloadAsync`，AAC access unit够小、不需要像H.264那样分片) →
   `DiscoveryProtocol.AudioRtpPort`。同时通过 `DiscoveryProtocol.CastStartMessage`/
   `CastStopMessage`（单播）告诉终端机"我要开始/停止投屏了，视频分辨率、音频采样率/声道数/编码
   方式（PCM还是AAC，新增的`AudioIsAac`字段）分别是多少"，终端机据此启动/停止自己的
   `Receiving/CastReceiver`。**音频是video之上的锦上添花，不是硬性要求**——如果这台机器上没有
   正在播放的声音、WASAPI初始化失败、或者AAC编码器构造失败，都只会让这一次投屏退化成纯视频，
   不会连累视频一起失败。面板上实时显示分辨率/已捕获帧数/已发送访问单元数/已发送字节数/音频
   状态/**终端机确认**（终端机每秒回报一次已解码帧数，见下方"已知风险"里`CastStatusMessage`
   那一节），出错时如实显示错误而不是假装成功。
4. 面板上有一个"屏幕捕获自检"按钮——用 Desktop Duplication API (`Capture/`) 独立验证捕获本身，
   跟正在进行的投屏互不影响，用于定位问题出在哪一步。
5. 面板上还有一个"开始编码自检 (捕获→NV12→H.264)"按钮——`Encode/EncodeSelfTestRunner.cs`
   独立跑一遍捕获→转换→编码，同样不影响正在进行的投屏。
6. 面板上还有一个"运行传输自检"按钮——用一批人造的、形状像H.264 NAL单元的随机数据走一遍
   `RtpSession → 本机回环UDP → RtpReceiver → H264RtpDepacketizer`（`EveryStage.Transport`），
   逐字节核对收发是否一致，包括FU-A分片重组是否正确，同样不涉及真实投屏、不影响正在进行的投屏。
7. 面板上还有一个"开始音频采集自检 (WASAPI loopback)"按钮——
   `Capture/AudioCaptureSelfTestRunner.cs` 独立驱动 `AudioCaptureSource`，显示采样率/声道数/
   已捕获字节数/近1秒吞吐量，不经过RTP发送、不涉及`LiveCastSession`，同样不影响正在进行的投屏。
8. 面板上还有一个"开始AAC编解码自检 (WASAPI loopback→AAC→PCM)"按钮——
   `Encode/AacEncodeSelfTestRunner.cs` 把 `AudioCaptureSource` 接到 `Encode/AacAudioEncoder.cs`
   （这个仓库第一个音频编码MFT，见下方"已知风险"第53条），再把每个编码出来的AAC访问单元原地喂给
   `EveryStage.Rendering.Decode.AacAudioDecoder.cs`（解码方向的镜像，见第54条），显示
   PCM输入字节数/已编码AAC访问单元数/编码总字节数/解码回PCM字节数，同样不经过RTP发送、不涉及
   `LiveCastSession`真正的投屏路径——纯粹是独立诊断工具，不像`LiveCastSession`真正发送的AAC
   那样受网络/丢包影响。**AAC编解码本身已经在第55条接进了真正的投屏发送路径**（`LiveCastSession`
   现在真的发送AAC、`CastReceiver`真的解码它），这个自检只是仍然保留的、跟真实投屏完全隔离的
   独立验证手段，不是这条链路唯一的验证方式了。
9. 面板上新增了一个"运行音频传输自检 (Raw RTP, 本机回环)"按钮——`EveryStage.Transport`新增的
   `RawTransportSelfTest`，跟第6条的视频传输自检是同一种手法（本机回环UDP、逐字节比对），但走的
   是音频真正在用的那条路径：`RtpSession.SendRawPayloadAsync → RawRtpReceiver`，不经过H.264的
   NAL/FU-A分片逻辑。这次改动之前，`RawRtpReceiver`/`SendRawPayloadAsync`完全没有任何自动化验证
   过，只是"逻辑上跟视频那条路径共享同一个`RtpPacket`编解码，应该没问题"这种人工推理（见
   `EveryStage.Transport`README风险第7条）。

这六个自检的角色从"补上还没接通的功能"变成了纯粹的独立诊断工具——真实投屏管线已经接通后，它们的
价值是在投屏出问题时帮助判断问题出在采集、编码、视频传输、音频采集、AAC编解码、还是音频传输哪一
步，而不是必须先跑通它们才能投屏。

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
16. **【已实现，原为已知缺口】`OutputProvidesOwnSamples` 恒为假时的分支完全没实现**（直接抛
    `NotSupportedException`）：这个仓库写这段代码时的假设是"主流硬件编码器都会设置
    `MFT_OUTPUT_STREAM_PROVIDES_SAMPLES`"，但这个假设本身也未经真机验证——如果某个编码器MFT不
    这样，`H264HardwareEncoder` 现在完全用不了，需要补一个"按 `GetOutputStreamInfo` 报告的大小
    自己分配输出sample"的分支。**更新（见第52条）**：这个分支已经实现——不再是硬失败。
17. **【已实现，原为已知缺口】`HandleNeedInput` 里的背压策略是占位的**：队列空了就 `Thread.Sleep(1)`
    再返回，而不是阻塞等待下一帧——这在真实负载下会造成事件循环忙等，且没有实现任何"编码器跟不上
    时该丢帧还是该等"的策略，只是刻意没有在无法测试的前提下假装选了一个"正确"策略。**更新（见
    第51条）**：两半都已经实现——`Thread.Sleep(1)`忙等换成了`SemaphoreSlim.Wait`（带超时+
    CancellationToken）真正阻塞等待，"编码器跟不上"选的是丢弃最旧那一帧（连GPU纹理一起释放），
    详见第51条。
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

34. **【部分实现，仍是已知缺口】状态回报本身也是尽力而为的UDP**：`SendCastStatusAsync`跟这个协议
    里其他所有消息一样，丢了就丢了，靠下一次(约1秒后)重新报告来"自愈"——`IsTerminalAlive`的5秒
    容忍窗口就是为了不让单个丢包被误判成"终端机失联"，但如果连续丢5秒的状态包（网络抖动/拥塞），
    Caster这边会显示"未确认"，即使终端机其实一切正常——UI上没有区分"终端机真的有问题"和"最近几个
    状态包都丢了"。**更新（见Terminal README对应条目）**：Terminal这一侧新增了一个真正的、字面
    意义上的"重传"——`SendCastStatus`现在会把同一份`CastStatusMessage`（同一个`SentAtUtc`，不是
    重新构造一份新报告）在约400ms后再发一次，让单次1秒报告需要两次独立的UDP丢包才会被Caster这边
    看漏，而不是一次。**这仍然没有解决这条风险的核心限制**：这不是ACK/重试协议，`CastStatusMessage`
    依旧完全是尽力而为，只是把"单次丢包=一次报告完全消失"变成"需要连续丢两次独立的包"，对"网络
    抖动/拥塞导致discovery socket整体变差"这种系统性丢包（不是偶发的单包丢失）基本没有帮助——见
    第35条，那才是这条风险真正的另一半。副作用：如果第一次发送丢了、靠400ms后的重传副本才让Caster
    收到，`LastStatusLatencyEstimate`（第36条）算出来的延迟会比真实网络延迟多约400ms（因为
    `SentAtUtc`是重传副本沿用的原始发送时刻，不是重传发生的那一刻）——这个偏差只在"第一次发送真的
    丢了"这个本来就不常见的情况下才会出现，被认为可以接受，没有额外处理。
35. **【部分实现，仍是已知缺口】状态回报走的是discovery socket，和视频/音频RTP流完全独立**：
    如果discovery端口(47990)本身因为某种原因被防火墙/网络设备限速或丢包率更高，会造成"画面音频
    都在正常播放，但Caster却显示未确认"这种误导性的状态，反过来也可能"确认"显示正常但媒体流其实
    已经卡住——状态回报的健康程度不代表媒体流的健康程度。**已经做的**：`MainForm`的"终端机确认"
    那一行现在总是带一句"（仅代表状态通道送达，不代表画面/声音本身一定在正常播放）"，直接回应了
    这条风险原来的最后一句话（"没有特别提醒用户这个区别"）。**没有做、也评估过为什么不做**：曾经
    考虑过用"Terminal报告的`FramesDecoded`最近有没有变化"来检测媒体流是否真的卡住，但
    `ScreenCaptureSource`基于DDA的自适应帧率（`CapturedFrame.HasNewImage`）意味着Caster自己的
    屏幕内容一旦静止不变，本来就会连续好几秒不产生任何新的访问单元——这种完全正常的静止画面场景
    下`FramesDecoded`同样会停止增长，用它做"卡住"检测会在最普通的日常使用场景里持续误报，比什么
    都不做还糟糕；要做对需要同时对比Caster自己`AccessUnitsSent`是否在增长（区分"没有新画面可发"
    和"发了但对方没收到/没解码"两种情况），这个仓库这一轮判断这个额外的状态追踪复杂度不值得，
    选择只做前面那句诚实的免责声明。
36. **【已实现，原为已知缺口】`CastStatusMessage`现在带了发送时刻**：新增`SentAtUtc`字段
    （Terminal端`SendCastStatus()`填成`DateTimeOffset.UtcNow`），`LiveCastSession.OnCastStatusReceived`
    收到后计算`DateTimeOffset.UtcNow - status.SentAtUtc`存进新增的只读属性
    `LastStatusLatencyEstimate`，`MainForm`的"终端机确认"那一行现在会带一个"延迟估算"数字。
    **这个数字只在两台机器时钟大致同步时才有意义**——这个协议本身完全没有时钟偏移协商，如果
    Terminal和Caster的系统时钟本身就差得远，算出来的"延迟"主要反映的是时钟偏差而不是真实网络
    延迟；这个仓库没有办法从沙箱里验证真实局域网环境下两台Windows机器实际的时钟同步情况，所以
    UI上和doc comment里都没有把这个数字包装成"精确延迟"，而是明确标注为"估算"并解释了这个前提。
    `LastStatusReceivedAt`本身（"Caster收到的那一刻"）没有变，两个字段现在并存，各自服务不同的
    问题："新鲜度判断"继续用收到时刻，"延迟估算"才用这个新时间戳。**更新（见第56条）**：这个
    "只在两台机器时钟大致同步时才有意义"的限制现在有了一个不受时钟同步影响的补充指标——见第56条
    新增的`RealRoundTripEstimate`，两个数字并存、互不替代。
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
51. **【已实现，原为已知缺口】`H264HardwareEncoder`的输入队列现在有真正的背压策略，事件循环也
    不再忙等**（见上方第17条）：`SubmitFrame`（捕获线程调用）改成有界丢弃——`_pendingFrames`满了
    （`MaxPendingFrames = 3`）就先丢弃并释放最旧那一帧的GPU纹理，再入队新的一帧，而不是无限增长；
    `HandleNeedInput`（事件循环线程调用）改成`SemaphoreSlim.Wait(200ms超时, CancellationToken)`
    真正阻塞等待下一帧，而不是`Thread.Sleep(1)`忙轮询。`_frameAvailable`这个信号量的计数被刻意
    维持成跟`_pendingFrames.Count`完全一致：`SubmitFrame`只在"净队列深度真的增加了"（没有触发丢弃）
    时才`Release`一次——丢一帧、入队一帧，深度不变，就不`Release`，避免信号量计数和队列实际深度
    脱节导致`HandleNeedInput`要么错误地立刻醒来发现队列其实是空的、要么该醒的时候不醒。跟
    `LiveCastSession`第25条(`_sendQueue`)的策略选了不同的丢弃粒度——那边必须"整个访问单元一起丢"
    （NAL单元之间有依赖，撕开会产生解不出来的残缺访问单元），这里的NV12帧彼此完全独立，直接丢弃
    最旧的单帧就是最自然的低延迟选择：编码器已经跟不上了，再去编码一帧陈旧的画面没有任何意义。
    新增`FramesDroppedForBackpressure`计数器，`LiveCastSession.EncoderFramesDroppedForBackpressure`
    和`EncodeSelfTestRunner.FramesDroppedForBackpressure`分别转发它，跟第48条已经在做的"只在大于0
    时才多显示一行警告"是同一个UI约定（`MainForm`的投屏面板和编码自检面板各自新增了一行）——这是
    一个和第48条(`AccessUnitsDroppedForBackpressure`，网络发送跟不上编码器)完全不同的瓶颈信号
    (`EncoderFramesDroppedForBackpressure`，编码器本身跟不上屏幕采集)，两者理论上可以同时出现，
    UI上是两条独立的行，不会互相覆盖。**仍未验证/仍是限制**：`MaxPendingFrames = 3`和200ms超时
    都是没有真机可测的估算值（同第50条那类数字一样的处境）；`SemaphoreSlim.Wait`带
    `CancellationToken`重载本身是成熟的BCL API、风险很低，但`_events.GetEvent`这个原生阻塞调用
    本身完全不响应这个token——如果事件循环恰好卡在`GetEvent`里而不是`HandleNeedInput`的
    `Wait`里，`Dispose`里的取消依然可能要等到下一次真的收到MFT事件才能真正退出，这是这个文件
    原本就有、这次没有解决的既有限制。
52. **【已实现，原为已知缺口】`H264HardwareEncoder.HandleHaveOutput`补上了`OutputProvidesOwnSamples`
    恒为假时自己分配输出sample的分支**（见上方第16条）：新增`CreateOutputSample`——按
    `GetOutputStreamInfo`报告的`Size`/`Alignment`（原生`cbSize`/`cbAlignment`，具体命名的猜测
    理由见该方法自己的NOTE注释）创建一个内存缓冲区（`Alignment > 0`时用
    `MFCreateAlignedMemoryBuffer`，否则`MFCreateMemoryBuffer`，`cbAlignment`原生语义就是"对齐值
    减一"，跟`MFCreateAlignedMemoryBuffer`的对齐参数是同一套约定，不需要转换），`AddBuffer`进一个
    新建的`IMFSample`，赋给`outputBuffer.Sample`后再调用`ProcessOutput`——这个模式下MFT是往调用方
    提供的sample里原地写数据，不会替换掉它，所以`ProcessOutput`成功之后`buffers[0].Sample`跟这里
    自己创建、赋值的是同一个对象，跟`_outputProvidesOwnSamples`为真时"MFT自己分配、这里只是接手
    所有权"的路径共用同一段`finally { ownedSample?.Dispose(); }`清理逻辑，两条路径都对。
    `ProcessOutput`失败（比如正常的`MF_E_TRANSFORM_NEED_MORE_INPUT`）时，自己分配的sample从未被
    消费，这里额外加了`outputBuffer.Sample?.Dispose()`释放它，避免泄漏。**这个分支本身依然完全
    没有真机验证**（第16条本来就说明这里假设的"主流硬件编码器都会设置
    `MFT_OUTPUT_STREAM_PROVIDES_SAMPLES`"没有核实过）——这次改动只是把"这个假设不成立时直接
    `NotSupportedException`硬失败"换成"这个假设不成立时也能跑"，不代表这条路径本身被验证过。
53. **新增`Encode/AacAudioEncoder.cs`——这个仓库第一个音频编码MFT**：解决"尚未开始"里长期挂着的
    "音频压缩"缺口的第一步。跟`H264HardwareEncoder`同一类"从未编译/从未在真机跑过"的风险等级，
    但结构性风险更低的两个原因：(1) 不需要在多个硬件厂商实现里挑一个——Windows只内置一个AAC编码
    MFT（`MFT_CATEGORY_AUDIO_ENCODER`/AAC子类型），`ActivateFirstAacEncoder`因此比
    `ActivateFirstHardwareEncoder`"在可能有好几个硬件厂商MFT里挑最合适的"简单；(2) 这个内置AAC
    编码MFT文档/社区经验普遍认为是**同步**transform，不像硬件视频编码器那样是异步的——这个类因此
    完全没有`IMFMediaEventGenerator`事件循环、没有async-unlock要求、没有任何后台线程，直接同步调用
    `ProcessInput`/`ProcessOutput`。**这个同步假设是这个文件最大的风险点**：这个沙箱完全没办法
    在真机上验证——如果某台机器激活出来的AAC编码器实际上是异步的，下面每一次`ProcessInput`/
    `ProcessOutput`调用都会直接失败，这个类需要按`H264HardwareEncoder`已经在用的同一套异步事件
    循环模式重写。为防御这个假设不成立，构造函数里仍然照抄了`UnlockAsyncProcessing`（对同步MFT
    设置这个属性无害但没用，跟`H264HardwareEncoder.UnlockAsyncProcessing`自己的doc comment
    同样的理由）。另一个只有这个文件才有的新风险点：内置AAC编码器对输出媒体类型（尤其码率）
    出了名地只接受一小组固定组合，不能像H.264那样直接`SetOutputType`一个任意的
    `MF_MT_AVG_BYTES_PER_SECOND`——这里用`GetOutputAvailableType`枚举、直接取第0个候选，而不是
    搜索一个"最接近目标码率"的——真正做码率搜索需要额外的`IMFAttributes.CopyAllItems`（把
    枚举出来的候选克隆到枚举调用之外还能存活的另一个`IMFMediaType`），这个方法本身也没验证过，
    这一轮选择不在一个已经足够新的路径上再叠加一层不确定性。**明确没有做的部分**：输出的AAC
    码率完全由编码器自己第0个候选决定，不可配置（`AacAudioEncoder`构造函数因此没有`bitrateBps`
    参数——不像`H264HardwareEncoder`那样可以指定，加一个当前不生效的参数会违反这个仓库自己的
    "没有真实行为支撑就不加"的一贯态度）。**更新（见第55条）**：`LiveCastSession`现在真的用它
    发送了，不再只是自检里的孤立组件——不需要RTP打包器就接进去了，因为AAC access unit本身够小、
    不需要像H.264那样分片（见第55条）。**更新（见第54条）**：输出帧格式从最初的原始（无头）AAC
    访问单元改成了ADTS
    （`MF_MT_AAC_PAYLOAD_TYPE = 1`）——这个仓库自己的两端是这个流唯一的消费者，ADTS每帧自带
    采样率/声道配置，解码那一侧因此完全不需要单独传一份`AudioSpecificConfig`，比照抄RFC 3640
    坚持用原始访问单元更简单，是一次有意的修正而不是最初就规划好的。这个编码器现在能通过第54条
    新增的解码器做真正的编码→解码闭环自检了，不再只是"编码之后没有任何办法验证解出来的东西对
    不对"。
54. **新增`EveryStage.Rendering.Decode.AacAudioDecoder.cs`——这个仓库第一个音频解码MFT，
    `AacAudioEncoder`的解码方向镜像**：跟第53条同一类"从未编译/从未在真机跑过"风险，也继承了
    同一个最大风险点——内置AAC解码MFT同样被假设是**同步**transform，如果这个假设不成立，这个类
    需要的重写量跟第53条完全一样。刻意跟`AacAudioEncoder`各自独立实现、不抽共享基类
    （`ProcessInput`/`ProcessOutput`驱动循环本质上是镜像的两份几乎一样的代码）——同样的"两个方向
    差异大到共享抽象反而更容易搞错哪个类型参数对应哪个方向"的理由，这个仓库对
    `VideoDecodeSource`/`AudioDecodeSource`、以及Terminal/Caster两份独立的discovery协议实现，
    都是同一个态度。输入协商顺序（先设输入类型、再枚举输出类型）跟编码器完全对称；`ConfigureOutputType`
    同样直接取`GetOutputAvailableType`第0个候选，不去搜索/构造一个特定的PCM格式。因为
    `AacAudioEncoder`现在输出ADTS（见第53条更新），这个解码器不需要单独的`AudioSpecificConfig`
    带外配置数据——这是选择ADTS而不是原始访问单元的直接收益。**同步测试**：新增的
    `Encode/AacEncodeSelfTestRunner.cs`把每个编码出来的AAC访问单元立刻原地喂给这个解码器（同一个
    调用线程上——WASAPI采集回调→编码→解码全程同步、没有任何后台线程或线程封送），新增
    "解码回PCM字节数"统计行；`MainForm`原来的"开始AAC编码自检"按钮改名"开始AAC编解码自检"，
    准确反映现在验证的是完整的编→解码闭环，而不只是单向编码。**这个自检的局限**：只验证"解码
    没有抛异常、产生了看起来数量合理的PCM字节"，不校验解出来的PCM在感知上跟原始音频一致（没有
    做任何波形/频谱比对），也完全没有播放出来让人耳朵听一下——真正确认"编解码音质没问题"仍然
    需要在真机上跑起来。**更新（见第55条）**：`LiveCastSession`/`CastReceiver`现在真的用它了，
    不再只是自检里的孤立组件。
55. **【已实现，原为已知缺口】`LiveCastSession`真正发送AAC了，`CastReceiver`真正解码它**：
    上面第53、54条实现的编码器/解码器这一轮接进了真正的投屏收发路径，不再只是自检工具。
    Caster侧：`OnPcmCaptured`不再自己按MTU切PCM分片，改成把整段捕获缓冲区喂给
    `AacAudioEncoder.SubmitPcm`，新增`OnAacAccessUnitEncoded`把每个编码出来的access unit（不需要
    任何RTP分片——AAC access unit通常几百字节，远小于视频那种需要`H264RtpPacketizer`
    FU-A分片的量级，见这个类doc comment）直接送进原有的`_audioSendQueue`/`RunAudioSendLoop`/
    背压计数器，机制不变，只是队列里现在装的是AAC access unit而不是PCM分片。每个access unit
    的RTP时间戳按"这批捕获缓冲区起始时刻的墙钟时间戳 + 已经吐出的access unit数 × 1024采样"计算
    （`AacSamplesPerFrame`常量），延续了这个仓库"时间戳必须锚定墙钟时间、不能用一个跟视频流毫无
    关系的采样计数器"的既有教训（本文件`OnPcmCaptured`/`OnAacAccessUnitEncoded`的doc comment里
    专门重复了这条教训，避免以后有人为了"更精确"而改回纯采样计数）。协议侧：
    `DiscoveryProtocol.CastStartMessage`新增`AudioIsAac`字段（`TerminalDiscoveryClient.AudioStreamInfo`
    新增对应的`IsAac`参数），告诉Terminal该把收到的payload当PCM还是AAC解释——这个协议本身完全
    没有版本协商，两端必须跑同一次提交的代码，否则字段被反序列化成默认值`false`会导致Terminal把
    AAC字节当PCM直接播放出噪音（见`EveryStage.Discovery`README新增的对应风险条目）。Terminal侧：
    `CastReceiver`构造函数新增`audioIsAac`参数，为真时构造一个`AacAudioDecoder`，
    `OnAudioPayloadReceived`收到的payload先经过`_audioDecoder.SubmitAccessUnit`解码、通过新增的
    `OnAacPcmDecoded`回调再送进`_audioClock.Enqueue`，而不是像原来那样直接把RTP payload当PCM送进
    `AudioPlaybackClock`——这个分支写法上特意不用`try/catch`包`SubmitAccessUnit`（它自己从不抛
    异常，成功/失败都通过`PcmDecoded`/`DecodingFailed`事件同步汇报），避免"调用完之后不管三七
    二十一先把`AudioError`清空"这种会覆盖掉刚刚同步发生的解码失败的错误写法。**明确没有做的部分**：
    (a) 没有任何针对异常大的AAC access unit的处理——`AacAudioEncoder.ConfigureOutputType`本身不
    控制/不封顶协商出来的码率（见第53条），万一某台机器协商出一个明显偏高的码率导致单帧超过
    正常UDP载荷预算，这里完全依赖普通IP分片兜底，这个仓库没有办法在真机验证这个假设是否成立；
    (b) 没有做A/B测试或任何形式的"先用PCM验证问题不是这次改动引入的"回退开关——AAC现在是唯一的
    音频发送路径，不像H.264视频编码器那样从一开始就没有"发送方式"这个选择，音频这条路径倒退回
    PCM当前唯一的办法是回退这次提交。
56. **【已实现，原为已知缺口】新增真正的、不受时钟同步影响的RTT测量**（对应第36条"这个数字只在
    两台机器时钟大致同步时才有意义"的限制）：`DiscoveryProtocol`新增`PingMessage`/`PongMessage`
    ——Caster单播一个`PingMessage`，Terminal收到后立即原样回一个带同样`RequestId`的`PongMessage`
    （Terminal端`DiscoveryService.HandlePing`，无条件回应，不检查配对/信任状态——这个协议本身
    已经完全没有认证，见`EveryStage.Discovery`README，多一个无害的echo不增加新的攻击面）。
    `TerminalDiscoveryClient.PingAsync`用一个`Stopwatch`：紧贴在实际发送前`Start()`，收到匹配
    `RequestId`的pong那一刻读`Elapsed`——全程只用Caster自己这一台机器的时钟，完全不比较两台机器
    的时钟，因此不会像`CastStatusMessage.SentAtUtc`那个估算值一样被时钟偏差污染。`_pendingPings`
    的匹配机制照抄`RequestPairingAsync`/`_pendingPairRequests`已经在用的"按RequestId关联
    TaskCompletionSource"模式，一字不差地复用同一套写法。`LiveCastSession`新增`RunPingLoop`
    后台循环，投屏运行期间每2秒ping一次，成功时更新`RealRoundTripEstimate`/`LastRttMeasuredAt`
    ——单次ping超时（3秒）不会把这两个值清空，而是保留上一次成功测量的结果，跟`IsTerminalAlive`
    的5秒容忍窗口同一个"别因为丢一个UDP包就翻脸"的态度。这个循环故意不依赖`HasAudio`：RTT是
    通用的网络诊断信息，不是音频链路的一部分。`MainForm`新增一行"真实RTT估算"，跟原有的"延迟
    估算"那行故意保持独立——不挂在`IsTerminalAlive`分支下面，因为这两个信号走的是完全不同的
    通道（ping/pong vs. `CastStatusMessage`discovery socket），理论上可能出现"RTT正常但状态
    通道确认失联"或反过来的情况，这本身就是有价值的诊断信息，不应该被合并掩盖。**仍未解决**：
    第34条"状态回报本身没有重传"这个问题本身完全没有触碰——这次加的是一个独立的、按需的RTT
    测量机制，不是给`CastStatusMessage`加重传；`_liveCastStatsLabel`的Bounds高度本来就是已知
    偏紧（见该控件构造处的注释），这次又加了一行，跟已有的丢包警告行同时出现时是否会被裁剪没有
    验证过，也没有借这次机会去修。
57. **【已实现，原为已知缺口】`MainForm`新增第六个自检按钮，接上`EveryStage.Transport`新增的
    `RawTransportSelfTest`**：见`EveryStage.Transport`README风险第7条——之前音频真正在用的
    `RtpSession.SendRawPayloadAsync`/`RawRtpReceiver`这条RTP路径完全没有自动化验证过。
    `OnRawTransportSelfTestClick`一字不差照抄`OnTransportSelfTestClick`（第6条那个视频传输自检）
    的写法：禁用按钮→跑自检→按`Result.Success`显示灰色/红色文字→无论成败都重新启用按钮，两个
    方法之间的重复没有提炼成共用helper，跟这个仓库"结构相似但服务不同路径的两小段代码，各自独立
    比强行共享更清楚"的一贯做法一致。顺带修正了这个类doc comment和`_aacEncodeSelfTestButton`
    构造处注释里两处已经过时的说法（"AAC还没接进`LiveCastSession`真正的投屏路径"——这句在第55条
    做完之后就已经不对了，这次一并改成准确描述），以及"现在能做什么"那一节里一段自我复制粘贴
    留下的重复语句（跟这个改动本身无关，顺手清理）。窗体`ClientSize`高度从733涨到813以容纳第六个
    按钮+状态标签，是这个仓库这类增长里的第六次。
58. **【部分实现，原为已知缺口】`CastStatusMessage`现在真的有了一次字面意义上的重传**（对应第34条，
    Terminal端`Program.cs`的`SendCastStatus`/`RetransmitCastStatusAsync`，见Terminal README对应
    条目）：每次1秒的定时报告现在会连续发送两份完全相同的`CastStatusMessage`（同一个`SentAtUtc`，
    第二份延迟约400ms），而不是只发一份靠下一次tick自愈。这是这条风险标题字面意义上要求的"重传"，
    但刻意不是ACK/重试协议——`CastStatusMessage`整体的"尽力而为"性质完全没变，效果仅仅是把"一次
    报告=一次UDP发送、丢了就整份没了"变成"一次报告=两次独立发送，需要两次都丢才会被Caster看漏"。
    **仍未解决的部分**：第35条描述的"discovery socket整体变差（网络抖动/拥塞）导致状态通道系统性
    不健康"这种失败模式，两次发送很可能同时受影响，这次改动对此基本没有缓解——这两条风险原本就是
    两类不同的问题（偶发单包丢失 vs. 通道整体降级），这次只解决了前一半。

## 尚未开始

- 状态回报的真正可靠传输（ACK/重试协议）——第34条这次只加了"同一份报告发两次"这种最简单的冗余
  发送，不是真正的确认/重传机制；第35条描述的"discovery socket整体变差导致状态通道系统性不健康"
  这种失败模式完全没有被这次改动触及，真正的往返延迟测量则已经在第56条用独立的ping/pong机制
  解决了，跟这条的关系已经解耦
