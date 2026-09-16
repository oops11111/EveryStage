# EveryStage.Rendering

D3D11/Media Foundation 零拷贝解码渲染管线（对应 `docs/PLANNING.md` §4.2/§4.4），从
`src/Poc/ZeroCopyRenderDemo` 抽取出来的共享库。两个消费方：

- `src/Poc/ZeroCopyRenderDemo`：阶段0技术验收Demo，围绕这个库做调度循环、丢帧/延迟/音画偏差/内存
  指标采集，跑通 PLANNING.md §4.4 的验收标准。
- `src/Terminal/EveryStage.Terminal`：正式产品的视频内容渲染器（`ContentEngine/VideoContentController`）
  直接消费这个库，把解码出的帧显示到终端机绑定的扩展屏覆盖窗口上。

之所以单独拆出来而不是让 Terminal 直接依赖 Demo 项目：Demo 明确定位为"不属于正式产品代码"的一次性
验证脚手架（见其 README），把产品运行时逻辑挂在它身上会让这个边界名不副实；而这条管线本身也确实不是
Demo专属的——两边需要完全一样的解码/渲染行为，所以放进一个两者都能引用的库里最合理。

## 已知风险 / 待验证事项

这段代码**从未在真实 Windows/GPU 环境编译或运行过**（本仓库当前全部在 Linux 沙箱中开发）。以下是
第一次在 Windows 上构建时需要重点核对的地方：

1. **NuGet 包版本**：`EveryStage.Rendering.csproj` 里 `Vortice.Direct3D11` / `Vortice.DXGI` /
   `Vortice.MediaFoundation` / `NAudio` 的版本号未对照真实 NuGet 源核实，如果 `dotnet restore`
   报找不到版本，直接用 `dotnet add package <name>` 取最新版覆盖即可。
2. **Media Foundation GUID 字面量**（`Decode/WellKnownGuids.cs`）：手动从 `mfapi.h` /
   `mfreadwrite.h` 抄录，未经编译器/头文件比对验证。如果 Vortice.MediaFoundation 暴露了对应的
   强类型常量（类似 SharpDX.MediaFoundation 的 `MediaTypeAttributeKeys` /
   `SourceReaderAttributeKeys`），优先切换过去，可读性更好也更不容易抄错。
3. **`IMFAttributes.Set` / `IMFMediaType.Set` / `IMFMediaBuffer.Lock` 的具体重载签名**：代码里
   假设了直接以 native 方法名（`SetUINT32`/`SetUnknown`/`SetGUID`/`Lock`）风格调用，Vortice 实际
   暴露的可能是稍有差异的重载或返回类型（例如 `Lock` 返回 `Span<byte>` 还是裸指针+长度），需要对照
   IntelliSense 修正。
4. **`IMFDXGIBuffer.GetSubresourceIndex()` 的返回形式**：这个值必须等于解码器纹理池里这一帧所在
   的 array slice，`SwapChainPresenter.CreateInputView` 直接拿它当 `ArraySlice` 用——如果实际
   接口是 `out` 参数而非返回值，改一下调用方式即可，语义不变。
5. **`AudioPlaybackClock.PositionTicks`**：依赖 `WasapiOut.GetPosition()` 返回"已经渲染到硬件
   的字节数"（由 `IAudioClock` 支撑），而不是"已经入队等待播放的字节数"——如果 NAudio 某个版本的
   语义不同，音画同步会系统性偏移，值得用已知时长的测试音频单独验证一次。
6. **【新发现的真实bug，已修复】`D3D11Device`构造函数里一条链式调用泄漏了两个中间COM对象**：
   原来是`Device.QueryInterface<IDXGIDevice>().GetParent<IDXGIAdapter>().GetParent<IDXGIFactory2>()`
   这一整行，只有最后的`IDXGIFactory2`被存进字段、在`Dispose()`里释放，链条中间
   `QueryInterface`/`GetParent`各自返回的`IDXGIDevice`/`IDXGIAdapter`实例从来没有被`Dispose`
   过。这个仓库其他地方处理同样形状的调用链时一直很小心——`Caster.Capture.ScreenCaptureSource`
   两处几乎一模一样的`gpu.Device.QueryInterface<IDXGIDevice>().GetParent<IDXGIAdapter>()`
   链条都老老实实用`using`分别接住每一步——唯独这个构造函数图省事写成了一行链式调用，把中间
   对象弄丢了。因为`D3D11Device`不是只构造一次：`Caster.Casting.LiveCastSession.Start()`每次
   开始投屏都会`new`一个新的，这个泄漏是每次投屏循环都会发生一次，不是一次性的。**修复方式**：
   拆成三行，每个中间步骤都用`using var`接住，只把最终需要长期持有的`IDXGIFactory2`赋给字段。
   **没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
7. **【新发现的真实bug，已修复】`AudioDecodeSource`/`VideoDecodeSource`/`AacAudioDecoder`三个类的
   构造函数，一旦`MediaFactory.MFStartup()`成功之后构造函数剩余部分抛出异常，这次
   `MFStartup`引用计数永远不会被对应的`MFShutdown()`平衡回来**：这三个类（连同Caster端的
   `H264HardwareEncoder`/`AacAudioEncoder`，见各自README对应条目）构造函数的通用形状都是
   "先`MFStartup()`，再做一长串可能失败的MFT/SourceReader配置步骤，最后才让构造函数正常返回"。
   问题在于：这些类都只在各自的`Dispose()`里调用`MFShutdown()`，而如果构造函数在`MFStartup()`
   成功**之后**、构造函数真正返回**之前**的任何一步抛出异常——比如`VideoDecodeSource`/
   `AudioDecodeSource`打开一个损坏/不支持编码的媒体文件（这是真实场景，不是假设：这两个类
   存在的意义就是打开任意用户提供的文件），或者`AacAudioDecoder`所在机器没有AAC解码器MFT——
   这个实例就永远不会真正构造完成，也就永远不会有人调用它的`Dispose()`，这次`MFStartup()`
   增加的引用计数就永久泄漏，直到整个进程退出为止。这跟第6条`D3D11Device`的COM泄漏是同一次
   系统性审计一起找到的，都是"构造函数中途失败导致资源清理路径被跳过"这同一个大类下的具体
   实例。**修复方式**：三个类都在`MFStartup()`成功之后新增一层`try/catch`，把构造函数剩余
   部分包起来，失败时手动释放已经拿到的MFT/reader、调用`MFShutdown()`再把异常重新抛出，
   保证不管构造函数是正常完成还是中途失败，`MFStartup`/`MFShutdown`的配对关系都不会被打破。
   `VideoDecodeSource`额外发现了一个独立的小问题一起修了：构造函数里`attributes`/`videoType`/
   `audioType`三个`IMFAttributes`/`IMFMediaType`局部变量原来都没有包`using`（跟这个类自己下面
   `actualVideoType`/`actualAudioType`、以及`AudioDecodeSource`里对应的`audioType`已经在用的
   写法不一致），每次构造`VideoDecodeSource`（也就是每次开始播放本地视频文件）都会各自泄漏
   一个原生COM句柄，这次一并用`using`接住。**没有做的部分**：这次改动本身没有在这个沙箱里
   跑过（没有dotnet），没有真机验证过。
8. **【新发现的真实bug，已修复】`D3D11Device`自己的构造函数、以及`SwapChainPresenter`的构造
   函数，都有跟第7条完全同一种"构造函数中途失败，已经拿到的资源没人释放"的形状，只是触发条件
   不是`MFStartup`而是分别持有Device/ImmediateContext/DxgiFactory/DeviceManager（前者）和
   videoDevice/videoContext/swapChain/backBuffer（后者）这几个真实COM/GPU资源**：这两个类此前
   都恰好没有被第6条/第7条那一轮系统性审计覆盖到——第6条只修了`D3D11Device`构造函数里一条链式
   调用泄漏中间COM对象的问题，从未处理这同一个构造函数自己"`Device`/`ImmediateContext`已经赋值
   给属性之后，`QueryInterface<ID3D11Multithread>`/`DxgiFactory`链/`MFCreateDXGIDeviceManager`/
   `ResetDevice`中任何一步抛出异常，这两个已经成功的属性谁来释放"这个问题——这甚至有点讽刺：
   第7条修的六个MFStartup类全都依赖这一个类给它们的GPU设备，而这一个类自己却从未受到同等的保护。
   `SwapChainPresenter`的构造函数是完全相同形状的独立一份——`_videoDevice`/`_videoContext`两次
   `QueryInterface`成功之后，`CreateSwapChainForHwnd`/`GetBuffer`任何一步失败，这两个已经拿到的
   COM接口同样没人释放。两处失败共同的后果是：一旦`VideoSurface`（`Terminal.Display`）的构造
   在这两步失败，`TerminalApplicationContext`自己的`try/catch`（见Terminal README）虽然已经
   能接住这次异常、不让整个进程崩溃，但接不住的是这里泄漏掉的GPU资源——一台机器只要触发过一次
   这种失败，就会在没有任何提示的情况下永久占用一份真实显卡资源，直到整个Terminal进程退出为止。
   **修复方式**：两个构造函数都改成先用可空局部变量逐步持有每一步的结果，只有全部成功之后才
   赋给字段，用`try/catch`包住中间过程，失败时按照跟各自`Dispose()`相同的顺序把已经成功的那些
   局部变量清理掉再重新抛出异常——`D3D11Device`额外的细节是`Device`/`ImmediateContext`本身在
   `D3D11CreateDevice`成功后就无条件赋值给了属性（这一步本身不会因为后续代码失败而需要回滚），
   所以`catch`块里这两个不需要判空直接释放；`SwapChainPresenter`则不持有它收到的`D3D11Device`
   参数本身的所有权（这是`VideoSurface`自己的资源，见`SwapChainPresenter`/`VideoSurface`各自的
   doc comment），所以它的`catch`块刻意不释放`gpu`。**没有做的部分**：这次改动本身没有在这个
   沙箱里跑过（没有dotnet），没有真机验证过。
9. **【新发现的真实bug，已修复】`AudioPlaybackClock`构造函数里`_output = new WasapiOut(...)`
   成功之后紧接着的`_output.Init(_buffer)`没有异常防护**：同一次审计顺着第8条的模式往音频这边
   也查了一遍，找到的第四个实例——`WasapiOut`的构造本身几乎不会失败，但`Init()`是一次真实的
   WASAPI初始化调用，格式协商失败、构造和`Init`之间默认播放设备被拔掉/切换，都是真实可能触发
   的失败场景，不是假设性的。一旦`Init()`抛出异常，这个构造函数永远不会正常完成，调用方
   （`VideoContentController.Play`/`AudioContentController.Play`，两者都是每次播放新文件就
   `new`一个全新的`AudioPlaybackClock`）永远拿不到实例去调用`Dispose()`，已经构造好的
   `WasapiOut`就永久占用一份WASAPI音频客户端资源。**修复方式**：把`_output.Init(_buffer)`
   包进`try/catch`，失败时只调用`_output.Dispose()`——不调用这个类自己`Dispose()`里同时
   调用的`_output.Stop()`，因为在一个`Init()`从未成功过的`WasapiOut`实例上调用`Stop()`是
   这次修复没有理由去冒险验证的未知行为。**没有做的部分**：这次改动本身没有在这个沙箱里
   跑过（没有dotnet），没有真机验证过。
10. **【同一次审计发现但这次故意没有修的一个近亲问题】`SwapChainPresenter.Resize()`不是
    事务性的——中途失败会让`_backBuffer`处于"已经`Dispose()`掉但没有被重新赋值"的破损状态**：
    跟第8条修的构造函数不一样，`Resize()`是这个类活着之后才会被调用的方法（`Terminal.Program.
    HandleDisplaySettingsChanged`在显示器分辨率/位置变化时调用），它自己的顺序是先
    `_backBuffer.Dispose()`，再`_swapChain.ResizeBuffers(...)`，再`_backBuffer =
    _swapChain.GetBuffer<ID3D11Texture2D>(0)`——如果`ResizeBuffers`或`GetBuffer`任何一步
    抛出异常，`_backBuffer`字段这时候已经被`Dispose()`过、但还没有被重新赋值成新的有效值，
    这个`SwapChainPresenter`实例就会永久卡在这个破损状态：调用方（`Terminal.Program`的
    `HandleDisplaySettingsChanged`跑在`Application.ThreadException`已经覆盖的
    `Application.Run()`消息循环里，异常本身会被接住、不会崩溃整个进程）不会崩溃，但下一次
    `PresentFrame`调用会在这个已经`Dispose()`过的`_backBuffer`上失败，直到整个Terminal进程
    重启为止。**这次为什么没有跟着一起修**：让`Resize()`真正事务性（比如先在局部变量里构建
    好新的`_backBuffer`、全部成功之后才`Dispose()`旧的、失败时保留旧的continue工作）比第8条
    构造函数那种"局部变量+catch清理"模式复杂得多——构造函数失败时"调用方永远拿不到实例"这个
    前提，在`Resize()`这里不成立：实例早就存在、还在被其它方法持续使用，一次`Resize()`失败后
    "回滚到旧状态、假装这次调用没发生"需要对`ResizeBuffers`调用之后交换链本身处于什么状态
    做出没有真机就无法验证的假设，贸然修改风险比现状更高。触发条件本身也相当罕见——显示器
    热插拔时分辨率变化触发`ResizeBuffers`，这个调用本身失败是Direct3D里不常见的失败模式。
    这次选择只记录、不修，等真机验证阶段这条风险要么被排除、要么再决定怎么改。**补充说明，
    避免这条读起来像`Resize()`是这个类里唯一一个非事务性方法**：`SwapChainPresenter.
    EnsureProcessor`（`PresentFrame`每一帧都会调用，不是只在显示器变化时才触发）和
    `EveryStage.Caster.Encode.BgraToNv12Converter.EnsureProcessor`（`Convert`每一帧都会
    调用）是完全同一种形状——都是先`_processor?.Dispose(); _enumerator?.Dispose();`，再
    `_enumerator = _videoDevice.CreateVideoProcessorEnumerator(...)`，再`_processor =
    _videoDevice.CreateVideoProcessor(_enumerator, 0)`——如果`CreateVideoProcessorEnumerator`
    成功但`CreateVideoProcessor`抛出异常，`_processor`字段会卡在"已经`Dispose()`过的旧值"，
    下一帧`PresentFrame`/`Convert`直接用`_processor!`就会在一个已释放对象上失败。这两处
    比`Resize()`触发频率更高（每一帧都可能触发，不是只在分辨率变化时），但没有单独展开
    分析——原因、风险评估和"为什么不修"的理由跟上面`Resize()`完全相同，这里只是澄清"完全
    没有事务性保护"这个事实同样适用于它们，不是`Resize()`独有的问题。

在 Windows 上第一次编译成功、把 `src/Poc/ZeroCopyRenderDemo` 跑通验收标准之后，这份清单里已确认
没问题的条目可以直接删掉，只留下真正还需要注意的坑。

## 目录

```
EveryStage.Rendering/
├── D3D11Device.cs             共享 D3D11 设备 + IMFDXGIDeviceManager
├── SwapChainPresenter.cs      Video Processor 直出到 SwapChain
├── Decode/
│   ├── WellKnownGuids.cs      Media Foundation GUID 常量
│   └── VideoDecodeSource.cs   SourceReader 封装（视频/音频双流拉取）
└── Audio/
    └── AudioPlaybackClock.cs  WASAPI 播放 + 主时钟
```
