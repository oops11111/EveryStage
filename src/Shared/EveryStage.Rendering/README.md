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
