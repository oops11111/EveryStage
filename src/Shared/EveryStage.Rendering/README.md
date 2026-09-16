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
