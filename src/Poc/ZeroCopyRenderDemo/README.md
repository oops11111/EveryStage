# Zero-Copy Render Demo（阶段0技术验证）

对应 `docs/PLANNING.md` 第4.4节与第15章"开发排期建议"中标记为**最高优先级、不可跳过**的前置任务。

验证链路：本地视频文件 → Media Foundation 硬件解码(DXVA) → D3D11 纹理直出（不落地系统内存）→
GPU Video Processor 转换/缩放 → DXGI SwapChain 呈现。音频经 WASAPI 播放并作为主时钟，用于给视频调度定拍
与计算音画偏差。**不包含**屏幕捕获、H.264编码、网络传输、正式UI —— 这些属于阶段2及之后。

实际的解码/渲染管线代码在 `src/Shared/EveryStage.Rendering/`（这里通过 ProjectReference 引用），
与终端机正式的视频内容渲染器共用——本项目只负责窗口、调度循环、验收指标这些"验证Demo专属"的部分，
详见该库的 README.md。

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

解码/渲染管线本身（NuGet包版本、Media Foundation GUID字面量、COM接口重载签名等）的已知风险事项
已经随代码一起搬到 `src/Shared/EveryStage.Rendering/README.md`，避免同一份清单在两个项目里维护
两份、后续改一边忘了改另一边。这里只列这个Demo项目自己引入的风险点：

1. **消息泵方式**：`Program.cs` 用 `Application.DoEvents()` 循环代替标准 WinForms 消息循环，
   足够跑通这个验证 Demo，但不是阶段4正式UI应该采用的模式（正式产品应该用合适的渲染循环/合成方式，
   避免 `DoEvents()` 已知的重入问题）。
2. **NuGet 包版本**（`ZeroCopyRenderDemo.csproj` 已经不直接引用 Vortice/NAudio，通过
   `EveryStage.Rendering` 间接依赖；这里没有自己的包版本需要单独核实）。

## 目录

```
ZeroCopyRenderDemo/
├── Program.cs                     入口：拼装管线（来自 EveryStage.Rendering）、调度、指标输出
├── RenderWindow.cs                承载 SwapChain 的输出窗口
└── Diagnostics/
    └── MetricsLogger.cs           延迟/丢帧/音画偏差/内存 采集与报告
```

实际的 `D3D11Device` / `SwapChainPresenter` / `VideoDecodeSource` / `AudioPlaybackClock` 在
`src/Shared/EveryStage.Rendering/`。
