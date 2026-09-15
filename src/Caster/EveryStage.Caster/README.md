# EveryStage.Caster — 投屏机（阶段2：发现/配对 + 屏幕捕获 + H.264硬件编码 + 传输层）

对应 `docs/PLANNING.md` §12 的UI描述、§7 的设备发现/配对流程的**投屏机一侧**，以及阶段2"屏幕捕获：
Desktop Duplication API (DDA)"、"视频编码：H.264 硬件编码"和"传输：UDP + RTP"三项的独立实现/自检。
PLANNING.md §15 把整个捕获/编码/传输称为"第二大技术风险区"——现在三块都各自写完并有自检了，但**互相
之间还没有接起来**：捕获自检不喂给编码，编码自检不喂给传输，见下方"尚未开始"。其中编码
（`Encode/H264HardwareEncoder.cs`）是这三块里风险最高、最难验证的一块，原因见该文件自己的doc
comment和下面"已知风险"的专门小节。

## 现在能做什么

1. 启动后监听终端机的UDP广播beacon，标准的"待机态：目标终端机列表"（§12）
2. 选中一个终端机，点"开始投屏"——真的会发送配对请求（`DiscoveryProtocol.PairRequestMessage`）
   并等待终端机的响应
3. 配对成功后切到第二个面板，**如实告知"H.264编码与RTP推流尚未实现"**，而不是假装开始投屏
4. 面板上有一个"屏幕捕获自检"按钮——这个是真的：点击后用 Desktop Duplication API (`Capture/`)
   实际捕获屏幕，实时显示分辨率/已捕获帧数/帧率。**捕获到的画面不会编码、不会发送到任何地方**，
   纯粹是为了在真正接上编码/传输之前，先单独确认捕获这一步本身能不能跑通。
5. 面板上还有一个"开始编码自检 (捕获→NV12→H.264)"按钮——`Encode/EncodeSelfTestRunner.cs` 把
   `ScreenCaptureSource`（BGRA纹理）→ `BgraToNv12Converter`（GPU视频处理器转NV12）→
   `H264HardwareEncoder`（异步MFT，硬件H.264编码）串起来，实时显示已编码访问单元数/编码总字节数。
   这是这个仓库里捕获和编码第一次真正接在一起跑；**编码出来的H.264数据目前不会发送到任何地方**，
   自检只关心这条链路能不能建立、跑不跑得动。
6. 面板上还有一个"运行传输自检"按钮——用一批人造的、形状像H.264 NAL单元的随机数据，走一遍
   `RtpSession → 本机回环UDP → RtpReceiver → H264RtpDepacketizer`（`EveryStage.Transport`），
   逐字节核对收发是否一致，包括FU-A分片重组是否正确。同样**不涉及真实视频数据，不发送到任何其他
   设备**，只是本机内部的协议正确性自检。

"如实告知"是有意的设计选择：PLANNING.md §12 描述的"投屏中态"包含时长、隐私提醒条、停止投屏按钮，
这些都是围绕一个真实存在的视频流设计的UI。在完整管线接通之前，搭建这些UI元素只是在为不存在的功能
画界面，所以这里的"投屏中"面板只做了协议握手结果的诚实反馈 + 三个独立的自检工具（捕获、编码、
传输）。这三个自检目前互不相通——编码自检自己内部有捕获→转换→编码这条链路，但它编码出来的数据不会
喂给传输自检；把编码自检的输出接到传输自检的输入，才是真正端到端投屏的最后一段。

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

## 尚未开始

- 把编码自检的输出接到传输自检的输入（当前两者各自独立跑，见"现在能做什么"最后一段）
- 把整条 `捕获 → NV12转换 → H.264编码 → RTP打包 → UDP发送` 链路和终端机侧的
  `UDP接收 → RTP解包 → 解码 → 显示` 接起来，实现真正的端到端投屏
- 音频采集与同步
- 已配对设备的持久化列表
- 真正的"投屏中"状态（时长显示、隐私提醒条、停止投屏——这些要等真实推流存在才有意义去做）
- 选择捕获哪个显示器（`ScreenCaptureSource` 目前固定捕获 `outputIndex=0`，多显示器场景没有UI选择）
- `H264HardwareEncoder` 里"编码器不提供自己的输出sample"这条分支（见上方风险16）
- 编码器的丢帧/背压策略（见上方风险17）
