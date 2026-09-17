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
    纯粹是巧合地用了同一种持久化模式。**【更新】这条其实把"需要真机"的范围说宽了**：`PairedTerminalStore`
    这一侧自己的保存/重新加载/删除/损坏文件回退，现在有了`PairedTerminalStoreSelfTest`（见第66条）
    ——这几件事本身只需要标准.NET文件IO，不需要Windows/GPU/网络，只是这次仍然没有在这个沙箱里真的
    跑过（没有dotnet）。Terminal端`PairedDeviceStore`没有对称的自检（见第66条"为什么只做了这一半"），
    而且即便两边各自的自检都通过，也仍然不构成"两边互相验证过"——两个独立实例分别读写各自的临时
    文件，从来没有真正操作过同一份数据，这条风险的核心（两份手写实现是否在字节层面完全一致）依然
    没有被回答，只是两边各自的正确性有了更高的把握。
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
59. **【已实现，原为已知缺口】两处"数据早就有了，UI从来没显示过"的缺口**：(1)
    `LiveCastSession.LastRttMeasuredAt`自己的doc comment从写出来那一轮起就说"调用方可以用它判断
    RTT估算是不是过时了"，但从来没有代码真的这样用过——如果终端机停止响应ping，`RealRoundTripEstimate`
    会永远停在最后一次成功测量的数字上，UI上没有任何"这个数字可能已经不新鲜了"的提示，容易被误读成
    "网络现在依然是这个延迟"。新增`IsRttStale`（超过10秒没有成功测量，同样是"连续错过几次而不是
    一次就翻脸"的口径，跟`IsTerminalAlive`/`StatusStaleAfter`是同一套推理，只是这次真的套用到了
    `LastRttMeasuredAt`身上），`MainForm`的"真实RTT估算"那一行在过时时追加一行醒目的提示。(2)
    `LiveCastSession.TerminalHasAudio`/`TerminalVideoBytesReceived`/`TerminalAudioBytesReceived`
    跟`TerminalFramesDecoded`同一批从`CastStatusMessage`解出来，协议/会话层完全接好了，但
    `MainForm`的"终端机确认"那一行以前只显示`TerminalFramesDecoded`和两个Error字段，这三个字段
    从来没有被显示过——这次补上了"视频已接收X字节"和（当`TerminalHasAudio`为真时）"音频已接收Y
    字节"。**残留的、这次没有解决的部分**：`_liveCastStatsLabel`的Bounds高度本来就已知偏紧（见
    第56条），这次给"真实RTT估算"那一行多加了一行过时提示，是继续往这个已知紧张的高度里塞内容，
    没有借这次机会去修；`IsRttStale`的10秒阈值跟这个仓库其他所有计时常量一样，没有真实网络环境
    可以验证是否合适。
60. **【已实现，原为已知缺口】`TerminalDiscoveryClient.TerminalListChanged`事件终于有了订阅方**：
    这个事件从这个类写出来那一轮起就在`HandleBeacon`里正确触发（新终端机出现/信息变化时），但
    `MainForm`一直只靠`_listRefreshTimer`（1秒一次）轮询`GetTerminals()`来刷新列表，从未订阅这个
    事件——不是坏的，只是让"刚开机、第一次广播beacon的终端机出现在列表里"这件事平白多等最多约1秒
    才被看见。这次`MainForm`新增`OnTerminalListChanged`订阅它，收到时通过`BeginInvoke`回到UI线程
    调用`RefreshTerminalList()`——`_listRefreshTimer`本身完全没有移除，`TerminalListChanged`按设计
    就不覆盖"终端机过期消失"这种情况（见该事件旁边`PruneExpired`那段注释：过期检测故意留给轮询
    覆盖），这次订阅只是让"新增/更新"这一半立即生效，不是替换轮询机制。**唯一需要防御的边界情况**：
    `Program.cs`里`discoveryClient.Start()`发生在`new MainForm(...)`之前，理论上一个beacon可能在
    这个窗体还没创建好窗口句柄之前就到达并触发这个事件——`OnTerminalListChanged`因此先检查
    `IsHandleCreated`，为`false`就直接跳过（反正`_listRefreshTimer`马上就会自己刷新一次），而不是
    冒着对着还没句柄的窗体调用`BeginInvoke`抛异常的风险。`Dispose`里对称地取消订阅，避免窗体销毁
    过程中这个回调还在对着已经拆到一半的窗体尝试`BeginInvoke`。
61. **【已实现，原为已知缺口】`PairedTerminal.PairedAt`（配对时间）终于有了读取方**：这个字段从
    `PairedTerminal`这个record写出来那一轮起就在`ShowPaired`成功配对时被正确赋值、持久化进
    `caster-paired-terminals.json`，但从来没有任何地方读取过它——待机列表里离线条目除了"（离线）"
    后缀什么额外信息都不给，用户没法一眼看出"这是刚配对5分钟前的终端机"还是"这是几个月没见过、
    大概可以移除了的终端机"。`TerminalListEntry`新增`PairedAt`字段（在线条目从`_pairedTerminals.
    Find(t.DeviceId)?.PairedAt`查，离线条目直接用`PairedTerminal`自带的值，从未配对过的在线设备
    两者都是`null`），`DisplayText`在有值时追加"· 配对于yyyy-MM-dd"。刻意没有像Terminal端
    `DevicesPanel`那样做成多列`ListView`——待机列表本身就是一个简单`ListBox`，为了一个字段重做
    整个列表控件不值得，追加到现有的单行文字里足够。
62. **【核实为有意设计，非缺口】`CaptureSelfTestRunner`/`EncodeSelfTestRunner`/
    `AudioCaptureSelfTestRunner`/`AacEncodeSelfTestRunner`/`LiveCastSession`这五个类各自的
    `StatsUpdated`事件，从来没有任何订阅方，但不应该被当成待修的缺口去接线**：排查
    `TerminalListChanged`（第60条）的时候顺手查了这个仓库里所有`public event Action? StatsUpdated`
    声明，发现全部五个类都是同一个模式——声明、在多处触发，但`MainForm`统统改用独立的轮询定时器
    （`_captureStatsTimer`/`_encodeStatsTimer`/`_audioCaptureStatsTimer`/`_aacEncodeStatsTimer`/
    `_liveCastStatsTimer`，都是500ms一次）去读取属性，从未订阅对应的事件。**核实后判断这是刻意的
    设计，不是遗漏**：跟`TerminalListChanged`（beacon大约每3秒一次，是低频事件）不同，这五个
    `StatsUpdated`里至少`LiveCastSession`那个是在`OnAccessUnitEncoded`结尾触发的——也就是**每个
    视频访问单元编码完成就触发一次**，实际投屏时这个频率是30-60次/秒量级。如果真的把这个事件订阅
    起来直接触发UI刷新（就像给`TerminalListChanged`做的那样），会在UI线程上产生每秒几十次的
    `BeginInvoke`调用，这跟现有500ms轮询"用一个粗粒度定时器摊平高频事件"的设计意图正好相反，
    是在制造新问题而不是修复缺口。**结论**：这五个`StatsUpdated`事件保留下来更可能是给未来某个
    还不存在的消费方准备的可选基础设施（比如另一套UI、遥测、日志），而不是"忘了接线"，故意不去
    给它们找一个第一个调用方——跟这个仓库这次找到的其他几十个"缺调用方"的例子性质不同，这次是
    "确认过，这个不该被接线"，不是新的已知缺口。
63. **【已实现，原为已知缺口】`LiveCastSession.TerminalPayloadTypeMismatches`——Terminal那边的
    RTP PayloadType防御性校验丢了多少包，这个Caster现在终于能看到了**：`EveryStage.Transport`的
    `RtpReceiver`/`RawRtpReceiver`早就在Terminal那侧追踪这个计数器（见`EveryStage.Transport`
    README风险第4条），但一直只是个纯本地计数器，连Terminal自己的UI都没展示，更没有传回过Caster。
    这次`DiscoveryProtocol.CastStatusMessage`新增同名字段，Terminal的`SendCastStatus()`把它填上，
    `OnCastStatusReceived`接住存成这个新属性，`RefreshLiveCastStats()`最后加一行展示——跟
    `AccessUnitsDroppedForBackpressure`/`EncoderFramesDroppedForBackpressure`那两行一样的
    "只在非零时才显示"处理：这个数字在本项目自己的Caster↔Terminal流量里预期永远是0（两端用的是
    同一套硬编码PayloadType常量），只有局域网上出现陌生/无关的RTP包时才会变成非零，日常投屏时这行
    完全不出现。**没有做的部分**：`_liveCastStatsLabel`的`Bounds`高度（108）本来就是个已知偏紧的
    空间（见该控件构造处、以及RTT那两行旁边的NOTE），这次又叠了第六种可能出现的行，没有借机重新
    评估这个布局——理由跟其他几行一样，为一个预期几乎永远不出现的行去改布局，不值得。
64. **【已实现】`TransportSelfTest`/`RawTransportSelfTest`新增PayloadType不匹配丢包校验后，
    两个自检按钮的成功文案跟着更新**：`EveryStage.Transport`那两个自检各自新增了
    `RunPayloadTypeMismatchCheckAsync`小节（见该库README风险第4条"更新"段落），验证"用错误
    PayloadType构造的包会被丢弃、用正确PayloadType构造的包仍然正常送达"——`OnTransportSelfTestClick`/
    `OnRawTransportSelfTestClick`的成功文案原来只提"NAL单元全部往返一致（含FU-A分片重组）"/
    "payload全部往返一致，GapEvents=0"，没有反映这次新增的校验内容，这次一并在文案里加上
    "含PayloadType不匹配丢包校验"，避免这两个按钮点击后显示的"通过"文案实际上比它真正验证的范围
    要窄。
65. **【新增】第七个自检按钮："运行发现协议自检 (本机回环)"，接入
    `EveryStage.Discovery.DiscoveryProtocolSelfTest`**：`EveryStage.Discovery`README风险第1条
    一直说"全部内容都没有在真实网络环境验证过"，但连这句话里更基础的那一半——协议自己的JSON
    编解码逻辑本身是否往返无损——都从来没有真正跑过。这次新增的自检把协议定义的全部8种消息类型
    （beacon/pair_request/pair_response×2种取值/cast_start/cast_stop/cast_status/ping/pong）各
    构造一份带有非默认值的实例，逐个走真实本机回环UDP（`Encode`→`UdpClient`→`Decode`），逐字段
    比对往返前后是否一致。窗口`ClientSize`从813长到883（+70）以放下第七组"按钮+说明标签"，
    `diagnosticsNoteLabel`"以下六个按钮"改成"以下七个按钮"。**跟其他六个自检的本质区别**：前六个
    都是投屏这条主链路（采集/编码/传输/音频）某一段的自检，这个不是——它跟`LiveCastSession`/
    屏幕捕获完全无关，验证的是配对/发现这条完全独立的协议本身。**没有覆盖的部分**：见
    `EveryStage.Discovery`README风险第1条"更新"段落——这个自检只验证协议线格式无损，不验证
    `DiscoveryService`/`TerminalDiscoveryClient`两个真实服务自己的握手时序/信任列表逻辑。
66. **【新增】第八个自检按钮："运行配对列表持久化自检 (临时目录)"，接入新增的
    `PairedTerminalStoreSelfTest`**：风险第42条说`PairedTerminalStore`和Terminal端
    `PairedDeviceStore`"两份完全独立、从未互相验证过的JSON持久化实现"，"只有两边都在真机上跑过才
    可能发现"字节层面的bug——但这个说法把"需要真机"的范围说宽了：保存→（模拟进程重启）用第二个
    独立实例重新加载→比对、删除后再加载、损坏文件触发`.corrupt-*`重命名回退，这几件事本身只需要
    标准.NET文件IO，不需要Windows、GPU或网络，`PairedTerminalStore`构造函数早就有的
    `storePathOverride`参数（原本是为了避免Caster/Terminal两个进程在同一台开发机上互相覆盖对方的
    身份文件）刚好也让这个自检可以完全指向一个临时目录、不碰真实配对列表。窗口`ClientSize`从883
    长到963（+80），`diagnosticsNoteLabel`"以下七个按钮"改成"以下八个按钮"。**为什么只做了这一半**：
    Terminal端的`PairedDeviceStore`结构上跟这个几乎是双胞胎（同样的原子写入-再替换、同样的损坏
    文件重命名回退），本可以照着同一个模式再写一份对称的自检，但Terminal项目目前完全没有任何
    自检UI（这个仓库所有自检都活在`Caster.UI.MainForm`里）——为了单独放下这一个小检查去专门搭一套
    Terminal自检UI基础设施，规模远超它要解决的风险本身，这次没有做，属于故意的不对称，不是遗漏。
    **仍然没有做的部分**：这个自检本身也没有在这个沙箱里真正跑过——虽然它是这个仓库目前唯一一个
    不需要Windows/GPU/网络就能跑的自检，但沙箱依然没有`dotnet`运行时，"不需要Windows"和"这次真的
    跑过"是两件独立的事，这次仍然只做到了前者。
67. **【新发现的真实bug，已修复】屏幕捕获意外丢失（`ScreenCaptureLostException`）之后，另外三个
    后台循环会继续空转，直到操作员注意到"投屏出错"手动点"停止投屏"**：委托一个子agent专门排查
    Terminal那边"队列静默卡死"同一种形状的bug时，它顺带查了`LiveCastSession.RunLoop`，发现
    这条不完全是同一种形状（这条不是"静默"的——`LastError`会被设置，`MainForm.RefreshLiveCastStats`
    立刻会显示红色"投屏出错"），但确实是个真实的清理不完整问题：`RunLoop`原来在
    `ScreenCaptureLostException`（以及任何其他捕获到的异常）发生时，只设置`LastError`然后
    `break`退出自己的循环，从来不会取消`_cts`——`RunSendLoop`/`RunAudioSendLoop`/`RunPingLoop`
    三个后台循环因此会继续跑下去，各自在空Channel/定时器上空等，直到操作员看到错误提示、手动点击
    "停止投屏"（`StopInternal()`）才会真正被清理。**为什么这次决定只做部分修复，不是自动回到待机**：
    投屏机是操作员正在主动使用的界面，跟PLANNING.md要求"无人值守"的Terminal不是同一类场景——
    错误发生后要求操作员看一眼再手动点"停止投屏"，本身就是合理、预期内的UX，这次没有改成"出错就
    自动回到待机"这种更大的产品行为改动；只是三个后台循环在这段等待期间纯粹空转、没有实际用处，
    这才是这次要修的部分。**修复方式**：`RunLoop`的`finally`块里新增`_cts?.Cancel()`——不能直接在
    `RunLoop`自己的异常处理里调用`Stop()`/`StopInternal()`，因为`StopInternal()`会
    `_loopTask?.Wait(...)`等待`RunLoop`自己这个任务完成，从`RunLoop`自己的线程内部这样调用会
    死锁；只取消`_cts`，让另外三个循环的`token.IsCancellationRequested`检查自然生效退出，GPU/
    编码器等资源的实际释放仍然留给`Stop()`/`StopInternal()`，等操作员点击"停止投屏"时才发生。
    `IsRunning`因此新增了一段doc comment解释一个容易被忽略的细节：它反映的是"`Stop()`有没有被
    调用过"，不是"`RunLoop`是否真的还在跑"——这次修复能安全在`RunLoop`自己的`finally`里调用
    `_cts?.Cancel()`而不用担心撞上一个新`Start()`带来的新`_cts`实例，依据的正是`IsRunning`这个
    "僵尸态"保证了`Start()`自己的`if (IsRunning) return;`门禁在旧会话真正被`Stop()`收尾之前
    不会放行新会话。**没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机
    验证过。
68. **【新发现的真实bug，已修复】`TerminalDiscoveryClient`的UDP接收循环会被一个陌生数据包永久
    杀死，修复在`EveryStage.Discovery`共享库里**：跟Terminal端`DiscoveryService`中的是完全同一个
    bug——两边的`HandleDatagram`都是从`EveryStage.Discovery`的`DiscoveryProtocol.Decode`
    复制的同一套调用+catch模式，`Decode`原来在收到形状不对但语法合法的JSON（比如裸数字、
    `{"type":123}`）时会抛`InvalidOperationException`而不是两边都在catch的`JsonException`，
    直接杀死各自的接收循环、永久且不留任何痕迹。Caster这一侧中招后的表现：`HandleBeacon`不再
    触发，`PruneExpired`（每次`GetTerminals()`轮询都会跑）会把所有之前发现过的终端机悄悄从列表
    里过期清空，即使它们其实还在正常广播；`HandlePairResponse`/`HandlePong`也不再触发，所有
    进行中和未来的`RequestPairingAsync`/`PingAsync`调用都会跑完自己的超时返回null，表现跟普通
    丢包一样，但这次永远不会恢复。详细分析、修复方式、新增自检见`EveryStage.Discovery`README——
    这次改动完全在共享库里，`TerminalDiscoveryClient.HandleDatagram`一行代码都没有改。
69. **【新增】这个进程第一次有了顶层的"UI线程异常/后台Task未观察异常"兜底，但比Terminal那边
    做得更轻**：见Terminal README对应新增条目的完整推理——`grep -rn
    "UnobservedTaskException|UnhandledException" src/`确认这个仓库此前任何地方都没有注册过
    任何一种顶层异常兜底，第67、68条这一轮连续找到的两个真实bug（`ScreenCaptureLostException`
    后台循环空转、`DiscoveryProtocol.Decode`会杀死接收循环）都是这同一个大缺口下的具体症状，
    修复各自都只堵住了已经发现的位置，不能排除还有没找到的第三个实例。`Program.Main()`里补上
    `Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)`+
    `Application.ThreadException`（UI线程异常不再让整个进程崩溃，弹一个"发生了未预期的错误，
    但程序会尝试继续运行"的对话框，消息循环继续跑）和`TaskScheduler.UnobservedTaskException`
    （单纯`SetObserved()`）。**为什么比Terminal那边做得更轻——两个明确的、故意的删减**：
    (1) 没有`AppDomain.UnhandledException`——这个事件本来就无法阻止进程真正终止，它唯一的价值
    是"死之前留一条日志线索"，但这个项目完全没有任何持久化日志基础设施（跟Terminal不同，
    PLANNING.md §14.4的三类日志要求本来就只针对"无人值守"的Terminal，Caster是操作员正在盯着
    屏幕主动操作的界面），为了这一个处理器专门造一套日志系统，规模超过这个风险本身；
    (2) `UnobservedTaskException`没有弹`MessageBox`——这个事件触发在GC终结哪个线程上不确定，
    不一定是UI线程，从这里弹UI存在跨线程访问控件的风险（`MainForm`别处自己一直很小心地用
    `BeginInvoke`做UI线程封送），`SetObserved()`本身也没有真正记录任何东西（这个项目没有日志
    可写），这个处理器诚实地说唯一价值是"面向未来"——不是修一个已知缺口，是防止以后新引入的
    fire-and-forget Task异常变得比现状更糟。**没有做的部分**：这次改动本身没有在这个沙箱里
    跑过（没有dotnet），`UnhandledExceptionMode.CatchException`之后消息循环是否真的能在UI线程
    异常之后干净地继续运行，完全依赖.NET文档描述的行为，没有真机验证过。
70. **【新发现的真实bug，已修复】`RequestPairingAsync`默认15秒就放弃，比Terminal那边真正愿意
    等的时间短了整整8倍**：这两个超时本该服务同一件事——等一个真人在Terminal那边点开
    `PairingConfirmationDialog`、看一眼、决定接受还是拒绝——但`TerminalDiscoveryClient.
    RequestPairingAsync`原来默认只等15秒（`timeout ?? TimeSpan.FromSeconds(15)`），而Terminal
    端`DiscoveryService.PendingRequestTimeout`早就明确设成2分钟，注释原话就是给"一个人去点弹窗"
    留出的时间。这意味着：只要操作员看到弹窗、决定要不要接受花了超过15秒（对一个需要真人反应的
    交互来说完全正常），Caster这边就已经先一步放弃、弹出"终端机未响应（超时）"——即使Terminal
    那边这个请求根本还没死，还在`_pendingRequests`里安静等着，接下来1分45秒里操作员真的点了
    "接受"也没用：那时候Caster早就在`finally`块里把这个`requestId`从自己的`_pendingPairRequests`
    里移除了，`HandlePairResponse`收到这份迟到的`PairResponseMessage`时只会走"未知/已经处理过的
    RequestId，安全地什么都不做"这条路径（见`EveryStage.Discovery`README对这类"两边握手协议"
    风险的一贯态度），配对请求就这样人间蒸发，操作员在Caster这边看到的却是"超时"而不是"正在等你
    确认"。Terminal自己特意设的2分钟耐心，被Caster自己的15秒等不到就先放弃直接架空了。
    **修复方式**：新增`DefaultPairingRequestTimeout`常量，值是`Terminal.Devices.DiscoveryService.
    PendingRequestTimeout`（2分钟）再加15秒余量——余量是因为Terminal那边的过期清理
    （`PruneExpiredPendingRequests`）只在每次`BeaconInterval`（3秒）触发的beacon广播循环里顺带
    检查一次，不是持续监视，实际过期时间点可能比整2分钟晚最多几秒，Caster这边的等待时间必须
    覆盖这整个窗口，不能卡着整2分钟这个点。**故意没有做的部分**：这两个常量分别独立声明在各自
    项目里，没有抽成共享常量——跟这个仓库"两个独立实现"的一贯做法一致，代价是以后如果任何一边
    改了这个数字，需要手动记得去改另一边，这次在两处都补了交叉引用注释提醒这件事。**没有做的
    部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过——包括"一个真人平均
    需要多久点开并回应这个弹窗"这个判断本身，2分钟本来就是Terminal那边凭感觉定的一个数字，这次
    只是让Caster跟它保持一致，不是重新论证这个数字本身对不对。
71. **【新发现的真实bug，已修复】`H264HardwareEncoder`调用了`MediaFactory.MFStartup()`却从来
    没有调用配对的`MFShutdown()`**：`MFStartup`/`MFShutdown`是进程级引用计数的一对——每次
    `MFStartup`调用计数加一，`MFShutdown`减一，底层Media Foundation子系统真正释放的时机是这个
    计数真正归零的时候。审计的时候顺手核对了这个仓库里所有调用过`MediaFactory.MFStartup()`的
    地方（`EveryStage.Rendering`的`AudioDecodeSource`/`VideoDecodeSource`/`AacAudioDecoder`、
    这个项目自己的`AacAudioEncoder`都在各自的`Dispose()`里正确配对了`MFShutdown()`），唯独
    `H264HardwareEncoder.Dispose()`原来漏了这一句——`LiveCastSession.Start()`每次开始投屏就会
    构造一个新的`H264HardwareEncoder`，`EncodeSelfTestRunner`每跑一次自检也会构造一个，这个
    计数只增不减，永远不会真正把这些编码会话占用的Media Foundation资源还给系统，直到整个
    Caster进程退出为止。**修复方式**：`Dispose()`里补上`MediaFactory.MFShutdown();`，跟其他
    几个类的既有模式完全一致。Terminal端`H264HardwareDecoder`有完全同一个bug、同一次改动一起
    修了，见`EveryStage.Terminal`README对应条目。**没有做的部分**：这次改动本身没有在这个
    沙箱里跑过（没有dotnet），没有真机验证过——包括这个引用计数泄漏在实际运行中到底会不会造成
    可观察的问题，本身也只是基于MF官方文档描述的引用计数语义推断出来的，没有实测验证过多次
    开始/停止投屏循环之后是否真的有异常表现。
72. **【新发现的真实bug，已修复】`EveryStage.Rendering.D3D11Device`的构造函数每次投屏都会泄漏
    两个中间COM对象**：跟上一条MFStartup/MFShutdown是同一次审计顺手找到的——`LiveCastSession.
    Start()`每次开始投屏都会`new`一个新的`D3D11Device`，它的构造函数原来有一行链式调用
    `Device.QueryInterface<IDXGIDevice>().GetParent<IDXGIAdapter>().GetParent<IDXGIFactory2>()`，
    只有最后的`IDXGIFactory2`被存下来、在`Dispose()`里释放，中间`QueryInterface`/`GetParent`
    各自返回的`IDXGIDevice`/`IDXGIAdapter`实例从来没有被释放过——跟这个仓库自己的
    `Capture/ScreenCaptureSource.cs`两处几乎一模一样的调用链（都老老实实用`using`分别接住
    每一步）对比就能看出这是个真的遗漏，不是故意的。这个泄漏发生在共享库`EveryStage.Rendering`
    里，但因为只有Caster这边的`LiveCastSession`会重复构造`D3D11Device`（Terminal那边
    `Display/VideoSurface`只构造一次，泄漏影响小得多），修复方式、详细分析见
    `EveryStage.Rendering`README对应条目——这次代码改动完全在共享库里，Caster自己的代码一行
    都没有改，值得在这里也记一笔说明为什么这条对Caster的实际意义更大。
73. **【新发现的真实bug，已修复】`PairedTerminalStore.Load()`只防了"文件内容损坏"，没防"文件
    暂时读不出来"**：跟Terminal那四个持久化存储（`FileLibraryStore`/`ScenarioRepository`/
    `SettingsStore`/`PairedDeviceStore`）是完全同一个bug形状、同一次审计一起找到的——`try`块
    只catch了`JsonException`，但`File.Exists(path)`确认存在之后`File.OpenRead`仍然可能因为
    杀毒软件/备份工具短暂锁住文件而抛`IOException`，这个异常原来会直接从`Load()`穿透出去，而
    `Load()`是`PairedTerminalStore`构造函数里同步调用的，会让Caster在真正开始跑之前就直接
    崩溃退出——完全违背这个类自己注释里"a corrupt file must not crash-loop the app on every
    startup"这句话本来想做到的事，只是原来的实现把"文件问题"窄化成了"内容损坏"一种情况。
    **修复方式**：新增`catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`
    分支，直接返回空列表，但刻意不做`JsonException`分支那个"把原文件改名成`.corrupt-时间戳`
    备份"的动作——文件只是暂时被锁住而非真损坏时，沿用同一套改名逻辑反而会把一份完好的已配对
    终端列表永久藏到`Load()`以后再也不会去找的文件名下面。详细的"为什么不能共用同一个catch"
    的推理见Terminal README对应条目（第93条），这里是完全同一套推理在Caster这一侧的应用。
    **没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
74. **【新发现的真实bug，已修复，比第68条那个bug更深一层】`TerminalDiscoveryClient.HandleDatagram`
    的`switch`分发本身完全没有异常防护，只有`Decode`这一步被保护了**：第68条修的是
    `DiscoveryProtocol.Decode`本身会因为陌生数据包抛`InvalidOperationException`直接杀死接收
    循环的bug（修在共享库`EveryStage.Discovery`里）。这次审计没有止步于此——`HandleDatagram`
    原来的结构是`try { message = Decode(data); } catch (JsonException) { return; }`，紧接着一个
    完全不在任何`try`保护范围内的`switch (message) { ... }`，分发给`HandleBeacon`/
    `HandlePairResponse`/`CastStatusReceived?.Invoke`/`HandlePong`/`HandlePing`五个分支。这个
    `switch`是从`ReceiveLoopAsync`的`while`循环体里直接调用的，循环体自己也没有额外包一层
    try/catch——只要这五个分支中任何一个抛出异常（尤其是`CastStatusReceived`唯一的订阅方
    `LiveCastSession.OnCastStatusReceived`本身抛出），异常会直接穿透`HandleDatagram`、穿透
    `while`循环体，永久结束这个Caster进程剩余生命周期里的整个发现协议接收循环——跟第68条是
    完全同一种"一个坏数据包/一次处理失败，永久杀死整个后台循环，零可见症状"的形状，只是触发点
    从"解码阶段"往后挪到了"解码成功之后的分发阶段"，第68条那次修复没有覆盖到这一层。这一侧比
    Terminal那一侧（见`EveryStage.Terminal`README对应条目）更关键：`CastStatusMessage`分支
    直接决定了`LiveCastSession`能不能收到Terminal的投屏状态回报，这条通道一旦被杀死，正在
    投屏的这一次投屏会永远收不到任何后续状态更新，直到进程重启。**修复方式**：给这个`switch`
    语句本身也包一层`try/catch (Exception)`，跟保护`Decode()`的那层相互独立——`Decode`失败
    直接`return`，`switch`内部失败则让循环继续处理下一个数据包，不再让单次处理失败连累后续
    所有数据包都收不到。**没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），
    没有真机验证过。
75. **【新发现的真实bug，已修复】`H264HardwareEncoder`/`AacAudioEncoder`两个类的构造函数，一旦
    `MediaFactory.MFStartup()`成功之后剩余部分抛出异常，这次`MFStartup`引用计数永远不会被
    平衡回来**：跟`EveryStage.Rendering`（该项目README第7条）、`EveryStage.Terminal`的
    `H264HardwareDecoder`（该项目README第98条）是同一次系统性排查一起找到的同一类bug——这两个
    类的构造函数形状都是`_encoder = ActivateFirstXxxEncoder();`（内部先调用`MFStartup()`再
    枚举/激活MFT，枚举不到时会抛异常）之后紧跟着一长串同样可能失败的配置步骤（`H264HardwareEncoder`
    还多了`_events = _encoder.QueryInterface<IMFMediaEventGenerator>()`这一步），构造函数才
    真正返回。原来只有`Dispose()`里调了`MFShutdown()`——如果枚举不到对应MFT（`H264HardwareEncoder`
    的硬件编码器枚举不到是真实场景：不是所有机器都有兼容的硬件H.264编码器），或者后续任何一步
    配置失败，这个实例就永远不会真正构造完成，`LiveCastSession`也就永远不会有一个实例去调
    `Dispose()`，`MFStartup()`增加的引用计数就此永久泄漏——而且不是一次性的：每次尝试开始投屏
    都会重新构造一次，也就重新泄漏一次，直到Caster进程退出为止。**修复方式**：两个类各自的
    `ActivateFirstXxxEncoder`内部（枚举/激活失败的路径）和构造函数剩余部分（配置失败的路径）
    都新增一层`try/catch`，失败时调用`MediaFactory.MFShutdown()`（`H264HardwareEncoder`还会
    连带释放已经拿到的`_events`）再重新抛出异常，保证不管构造函数是正常完成还是中途失败，
    `MFStartup`/`MFShutdown`的配对关系都不会被打破。**没有做的部分**：这次改动本身没有在这个
    沙箱里跑过（没有dotnet），没有真机验证过。
76. **【新发现的真实bug，已修复】`AacEncodeSelfTestRunner.Start()`原来把三个构造出来的对象先放进
    局部变量，等三个构造函数全部成功之后才一次性赋给`_capture`/`_encoder`/`_decoder`三个字段——
    如果`AudioCaptureSource`和`AacAudioEncoder`都构造成功、但`AacAudioDecoder`构造失败（真实
    场景：这台机器没有注册AAC解码器MFT、或者协商出的格式不受支持），已经成功构造的这两个对象
    就变成了没有任何字段引用它们的局部变量，`catch`块原来只设置`LastError`就直接`return`，
    从来没有人调用过它们的`Dispose()`——每次这条自检的构造失败路径被触发，就泄漏一个WASAPI
    采集句柄和一个自带`MediaFactory.MFStartup()`/`MFShutdown()`配对的AAC编码器MFT。这跟这一轮
    在别处系统性修的"构造函数中途失败导致资源清理路径被跳过"（见第75条、`EveryStage.Rendering`
    README第7条）是完全同一类问题，只是这次出现在自检runner自己组合多个构造函数的地方，不是某个
    类自己的构造函数内部——对比它的姐妹类`EncodeSelfTestRunner.Start()`会发现后者从一开始就是
    每构造一个对象就立刻赋给对应字段，`catch`块调用的`StopInternal()`因此总能找到已经构造成功
    的那些字段去释放，这次的`AacEncodeSelfTestRunner`是这一类自检runner里唯一没有遵循这个既有
    正确写法的一个。**修复方式**：改成每个对象构造成功就立刻赋给`_capture`/`_encoder`/`_decoder`
    对应的字段，`catch`块里调用`Stop()`（本来就已经对每个字段做了`null`检查，可以安全地在只有
    部分对象构造成功时调用）代替原来"什么都不清理"的处理。**没有做的部分**：这次改动本身没有在
    这个沙箱里跑过（没有dotnet），没有真机验证过。
77. **【新发现的真实bug，已修复，跟第76条同一次审计一起找到】`CaptureSelfTestRunner.Start()`的
    `new D3D11Device()`原来完全在`try/catch`保护范围之外**：这个仓库其它每一个自检runner的
    对应第一步都在各自的`try`块内部（比如`EncodeSelfTestRunner`自己的`_gpu = new D3D11Device();`
    就在它的`try`里），唯独这一个把它写在了`try`外面。`D3D11Device`构造函数真实会抛异常的场景
    ——这台机器没有兼容的D3D11硬件/驱动——恰好就是"屏幕捕获自检"这个按钮存在的意义所在：它是
    专门用来帮人诊断这类环境问题的工具，但如果这类问题真的发生，原来的代码会让异常直接从
    `Start()`穿出去，而不是像其它任何构造失败一样体面地显示在`LastError`标签里。Caster的
    `Program.cs`顶层`Application.ThreadException`兜底（这一轮更早的时候加的）能保证这不会
    真的让进程崩溃，但操作者看到的会是一个突兀的未处理异常弹窗，而不是这个自检本身"自检失败：
    <原因>"那种统一的报错体验。**修复方式**：把`new D3D11Device()`挪进`try`块内部，
    `gpu`/`capture`两个局部变量都改成可空并在`catch`里对称地`Dispose()`（`D3D11Device()`
    自己失败时`capture`本来就还是`null`，跟修复前"只释放`gpu`"这一半保持行为一致）。
    **没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
78. **【新增】`PairedTerminalStoreSelfTest`补上了"文件暂时锁住"这条分支的覆盖，之前只测了
    "文件内容损坏"那一半**：`PairedTerminalStore.Load()`本身在更早一轮加了一个单独的
    `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`分支（见本
    README第73条），专门处理"文件暂时被其他进程锁住"跟"文件内容真的损坏"（`JsonException`分支）
    这两种情况需要不同的善后方式——损坏的文件可以放心改名成`.corrupt-*`备份，但被锁住的文件
    内容其实完好，改名反而会把好数据永久藏起来。但这次审计发现，`PairedTerminalStoreSelfTest`
    自己一直只测了`JsonException`那一半（`File.WriteAllText`写入非法JSON那一步），从来没有
    验证过"文件被锁住"这条路径是不是真的按预期表现——这正是这个仓库一贯的做法（每次给某个bug
    加修复，都顺手给对应自检补上覆盖，例如`DiscoveryProtocolSelfTest.CheckMalformedInputsDontThrow`
    之于`DiscoveryProtocol.Decode`的修复），这次是把这个既有习惯回头补到一个当时漏掉的地方。
    **新增的测试步骤**：用`FileStream`以`FileShare.None`独占打开`storePath`模拟"另一个进程正在
    占用这个文件"，在锁定期间构造一个新的`PairedTerminalStore`，验证它不抛异常、安全降级成空
    列表；释放锁之后再验证文件本身的字节长度完全没变、且能被正常读回——后面这条断言才是真正
    区分"锁住"和"损坏"两条分支的关键：两者都会让*这一次*加载得到空列表，但只有"损坏"分支被
    允许真的动这个文件，"锁住"分支必须完全不碰它。**没有做的部分**：这次改动本身没有在这个
    沙箱里跑过（没有dotnet），没有真机验证过——包括`FileShare.None`独占锁在真实Windows文件
    系统上是否真的会让`File.OpenRead`按预期抛`IOException`，也只是基于.NET文档推断，没有
    实测验证过。
79. **【新发现的真实bug，已修复，跟`EveryStage.Terminal`那一条同一次审计一起找到】
    `Program.Main`里`new TerminalDiscoveryClient()`原来完全没有异常防护，而这个构造函数会
    绑定一个固定端口的UDP socket**：`TerminalDiscoveryClient`构造函数里
    `_socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port));`如果这个
    端口（47990）已经被占用会抛`SocketException`——这是真实场景，不是假设：本机同时跑两个
    Caster实例（`EveryStage.Discovery`README自己就提到过"同一台开发机上测试时互相覆盖"这类
    场景）、或者上一次Caster没能正常退出留下的僵尸进程还占着这个端口，都会触发。这段代码在
    `Application.Run()`真正启动消息循环*之前*就执行，`Application.ThreadException`（上面
    刚注册的）完全帮不上忙；而且跟Terminal不一样，这个进程从来没有注册过
    `AppDomain.CurrentDomain.UnhandledException`（这个类自己的注释解释了为什么故意没加
    ——这个应用没有常驻日志基础设施，专门为这个加一个不值得）——原来的代码一旦触发这条失败
    路径，操作者看到的会是一个毫无样式的.NET原生崩溃对话框，没有任何说明，也完全不符合这个
    进程其它所有意外失败早就在用的"弹一个MessageBox、不是崩溃"的统一体验。**修复方式**：把
    `new TerminalDiscoveryClient()`包一层`try/catch`，失败时用跟这个进程其它地方完全一致
    风格的`MessageBox.Show`说明"监听UDP端口失败"、给出两个最可能的原因（另一个投屏机实例在
    跑/端口被别的程序占用），然后正常返回而不是让异常继续往外抛。**没有做的部分**：这次改动
    本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
80. **【新增】新增`DeviceIdentitySelfTest`（`EveryStage.Discovery`共享库）+ Caster这一侧
    第九个自检按钮"运行设备身份持久化自检"**：`DeviceIdentity`这个类这一轮已经修了两个真实bug
    （`LoadOrCreate`的`IOException`/`UnauthorizedAccessException`处理——见本README第79条隔壁、
    `EveryStage.Discovery`README对应条目——以及把两处写入换成跟其它存储一致的原子写入约定），
    但在这之前完全没有任何自检覆盖过它的持久化行为，是这个仓库里唯一一个"两次真实bug修复、
    零测试覆盖"的持久化类（那两处修复本身分别记在`EveryStage.Discovery`README对应条目和
    本README第73条`PairedTerminalStore.Load()`那条附近，`DeviceIdentity`跟`PairedTerminalStore`
    是同一次改动一起修的同一类bug）——跟第78条给`PairedTerminalStoreSelfTest`补"文件暂时锁住"
    分支覆盖是同一次审计一起发现的同一类缺口，只是这次缺口是"完全没有自检"而不是"自检覆盖不全"。
    **新增内容**：`DeviceIdentitySelfTest.Run()`跟`PairedTerminalStoreSelfTest`是完全同一个
    形状——首次创建、重新加载确认拿到同一个`DeviceId`、改名后`Save()`并重新加载确认改名生效、
    损坏文件后`LoadOrCreate`必须重新生成一个新身份而不是抛异常、文件被独占锁住时必须降级成
    一个不落盘的临时身份且完全不碰原文件、解锁后原文件内容必须原封不动还能正常读回——五个
    步骤跟`DeviceIdentity.LoadOrCreate`/`Save()`自己的实际行为逐一对应。UI这一侧`MainForm`
    新增第九个自检按钮，`ClientSize`从963长到1043，类doc comment里"eight independent
    self-tests"相应改成"nine"，说明文字里的"以下八个按钮"也改成"以下九个按钮"。**没有做的
    部分**：这次新增的代码本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
81. **【修复】`Program.Main`里`DeviceIdentity.LoadOrCreate("caster")`/`new PairedTerminalStore()`
    也在`Application.Run()`之前跑，之前完全没包`try/catch`**：跟第79条修的`new
    TerminalDiscoveryClient()`是完全同一种形状的漏洞，只是早了两行——`DeviceIdentity.
    LoadOrCreate`自己"文件不存在"/"文件损坏、重新生成"这两条路径末尾的`Directory.
    CreateDirectory`+原子写入那一对调用，本身没有包`try/catch`（见`EveryStage.Discovery`
    README/这个类自己的doc comment），如果`ProgramData\EveryStage`目录权限有问题或者磁盘
    满了，会直接抛出来。这段代码执行的时候，`Application.ThreadException`（第25-28行刚注册的）
    帮不上忙——它只能接住`Application.Run()`消息循环*内部*抛出的异常，这里还在消息循环启动
    之前的同步代码里；而且这个进程照第79条的说明，从来没注册过`AppDomain.CurrentDomain.
    UnhandledException`。两者叠加的结果是：这里一旦抛出异常，操作者会看到的不是"内部错误"
    MessageBox，也不是第79条那种"启动失败"提示，而是彻彻底底一个原生.NET崩溃对话框、没有
    任何记录——跟第79条修复之前`TerminalDiscoveryClient`失败时一模一样的最坏情况，只是触发
    路径提前到了这一行。**修复方式**：把`identity = DeviceIdentity.LoadOrCreate("caster")`
    和`pairedTerminals = new PairedTerminalStore()`两行一起包进一个`try/catch`，风格跟第79条
    完全一致——失败时`MessageBox.Show`说明"无法读取或创建设备身份/配对记录文件"，建议检查
    ProgramData目录权限和磁盘空间，然后`return`而不是让异常继续往外抛。`PairedTerminalStore`
    自己的`Load()`其实已经对`JsonException`/`IOException`/`UnauthorizedAccessException`做过
    降级处理（见本README第73条附近），单独拎出来看不太可能在这里抛出，跟`identity`放进
    同一个`try`纯粹是因为这是这个进程里仅有的两个"在`Application.Run()`之前构造、背后有真实
    文件I/O"的调用，一次性堵上而不是只堵`DeviceIdentity`这一个已知会抛的。**没有做的部分**：
    这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
82. **【修复】`BgraToNv12Converter`构造函数里`_videoDevice`/`_videoContext`两次`QueryInterface`
    之间没有异常防护**：跟`EveryStage.Rendering`README记录的`D3D11Device`/`SwapChainPresenter`
    构造函数修复是完全同一种形状——`_videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>()`
    成功、赋给只读字段之后，如果紧接着`_videoContext = gpu.ImmediateContext.
    QueryInterface<ID3D11VideoContext>()`抛出异常，这个构造函数永远不会正常完成，调用方
    （`LiveCastSession.Start()`/`EncodeSelfTestRunner.Start()`，两者都是每次开始投屏/自检
    就`new`一个全新实例）永远拿不到实例去调用`Dispose()`，`_videoDevice`就永久泄漏一份COM
    资源。**修复方式**：把第二个`QueryInterface`包进`try/catch`，失败时释放已经成功的
    `_videoDevice`再重新抛出——跟`AacAudioDecoder`构造函数那次修复（单个字段场景）完全
    同一个写法，不释放传入的`gpu`参数本身，因为这个类不拥有它。**没有做的部分**：这次改动
    本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
83. **【新发现的真实bug，已修复】`TerminalDiscoveryClient`构造函数：`new UdpClient()`
    成功后，紧跟着的`Bind()`如果抛异常，刚创建的socket就泄漏了**：跟Terminal那边
    `Devices.DiscoveryService`构造函数同一次审计发现的完全同一个bug（两个类是彼此的镜像——
    一个是Terminal监听/广播，一个是Caster监听/广播，构造函数写法几乎逐字相同），见Terminal
    项目README对应条目的完整理由（`new UdpClient()`分配真实原生socket句柄在先，`Bind`到固定的
    `DiscoveryProtocol.Port`在后，端口已被占用是真实可达的失败场景，异常一抛构造函数就
    永远不会返回，调用方永远拿不到实例去`Dispose()`，socket永久泄漏）。**修复方式**：完全
    同一个写法——局部变量+`try/catch`+失败时`Dispose()`再重新抛出，只有全部成功才赋值给
    `_socket`字段。**没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），
    没有真机验证过。

## 尚未开始

- 状态回报的真正可靠传输（ACK/重试协议）——第34条这次只加了"同一份报告发两次"这种最简单的冗余
  发送，不是真正的确认/重传机制；第35条描述的"discovery socket整体变差导致状态通道系统性不健康"
  这种失败模式完全没有被这次改动触及，真正的往返延迟测量则已经在第56条用独立的ping/pong机制
  解决了，跟这条的关系已经解耦
