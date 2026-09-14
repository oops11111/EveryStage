# Zero-Copy Render Demo（阶段0技术验证）

对应 `docs/PLANNING.md` 第4.4节与第15章"开发排期建议"中标记为**最高优先级、不可跳过**的前置任务。

验证链路：本地视频文件 → Media Foundation 硬件解码(DXVA) → D3D11 纹理直出（不落地系统内存）→
GPU Video Processor 转换/缩放 → DXGI SwapChain 呈现。音频经 WASAPI 播放并作为主时钟，用于给视频调度定拍
与计算音画偏差。**不包含**屏幕捕获、H.264编码、网络传输、正式UI —— 这些属于阶段2及之后。

## 为什么这样设计

- **共享一个 `ID3D11Device`**：解码器（通过 `IMFDXGIDeviceManager`）与渲染器用同一张显卡设备，
  这是"零拷贝"成立的前提；否则 Media Foundation 会在内部悄悄退化成 CPU 拷贝路径。
- **强制 NV12 输出**：显式把 SourceReader 的视频流媒体类型设为 `MFVideoFormat_NV12`，避免解码器
  前面插入一个软件颜色转换 Transform，破坏零拷贝链路。
- **GPU Video Processor 做 NV12→RGBA 转换与缩放**：而不是 `CopyResource`（格式不同没法直接拷贝）
  或 CPU 端转换（那就不是零拷贝了）。这是 mpv/madVR/ffmpeg d3d11va 等播放器的标准做法。
- **音频为主时钟**：解码出的视频帧按其时间戳与音频播放位置对拍，落后超过一帧预算直接丢帧而不是
  阻塞等待，对应 PLANNING.md"帧率无掉帧"验收项——这里刻意让丢帧可见（计数+日志），而不是掩盖它。

## 构建与运行（仅限 Windows）

这段代码**从未在本沙箱环境中编译过**——当前会话运行在 Linux 容器里，没有 Windows SDK、没有 GPU、
没有 `dotnet`，而 D3D11 / DXGI / Media Foundation 是纯 Windows API。所有代码是照着这些 API 的
官方文档形状手写的，但请在真正的 Windows 开发机上完成第一次编译验证，而不是直接信任它能跑。

```powershell
cd src/Poc/ZeroCopyRenderDemo
dotnet restore
dotnet build -c Release
dotnet run -c Release -- --input C:\path\to\1080p_test_clip.mp4 --metrics-dir .\out
```

参数：
- `--input`：本地测试视频（建议准备 1080p 与 4K 两个样本，对应验收标准里的两档分辨率）
- `--metrics-dir`：`frames.csv` / `memory.csv` 与汇总报告的输出目录
- `--minutes N`：循环播放输入文件 N 分钟，用于第4.4节"连续播放1小时无内存泄漏"的浸泡测试
  （`--minutes 60`）

## 验收标准核对（PLANNING.md §4.4）

| 标准 | 本Demo如何验证 |
|---|---|
| 端到端延迟 ≤30-50ms | `frames.csv` 的 `latency_ms` 列 + 运行结束时打印的 p50/p95/max |
| 帧率无掉帧（CBR测试流下） | 运行结束汇总里的 `frames dropped` 计数，目标为 0 |
| 1080p/4K均可跑通 | 分别用 1080p、4K 测试文件跑一遍 `--input` |
| 音画偏差 ≤1帧 | `frames.csv` 的 `av_skew_ms` 列 + 汇总里的 `av skew max` |
| 连续播放1小时无内存泄漏 | `--minutes 60`，看 `memory.csv` 的 `working_set_bytes` 趋势线是否平稳 |

## 已知风险 / 待验证事项

这些是本沙箱环境无法验证、需要在 Windows 上第一次编译/运行时重点检查的地方：

1. **NuGet 包版本**：`ZeroCopyRenderDemo.csproj` 里 `Vortice.Direct3D11` / `Vortice.DXGI` /
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
6. **消息泵方式**：`Program.cs` 用 `Application.DoEvents()` 循环代替标准 WinForms 消息循环，
   足够跑通这个验证 Demo，但不是阶段4正式UI应该采用的模式（正式产品应该用合适的渲染循环/合成方式，
   避免 `DoEvents()` 已知的重入问题）。

## 目录

```
ZeroCopyRenderDemo/
├── Program.cs                     入口：拼装管线、调度、指标输出
├── RenderWindow.cs                承载 SwapChain 的输出窗口
├── Rendering/
│   ├── D3D11Device.cs             共享 D3D11 设备 + IMFDXGIDeviceManager
│   └── SwapChainPresenter.cs      Video Processor 直出到 SwapChain
├── Decode/
│   ├── WellKnownGuids.cs          Media Foundation GUID 常量
│   └── VideoDecodeSource.cs       SourceReader 封装（视频/音频双流拉取）
├── Audio/
│   └── AudioPlaybackClock.cs      WASAPI 播放 + 主时钟
└── Diagnostics/
    └── MetricsLogger.cs           延迟/丢帧/音画偏差/内存 采集与报告
```
