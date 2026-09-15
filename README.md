# EveryStage

Windows 内网投屏 / 扩展显示程序。支持图片、视频、音频、可编辑文档在扩展屏（LED/投影）上展示，并支持接收内网其他 Windows 设备投放的内容。

## 角色

- **终端机（Terminal）**：常驻服务，绑定扩展屏，接收本地文件与内网投屏机内容，统一决策输出。
- **投屏机（Caster）**：临时轻量工具，选择目标终端机后一键投屏（全屏捕获推流）。

## 目录结构

```
EveryStage/
├── docs/            产品规划与设计文档
├── src/
│   ├── Terminal/    终端机主程序（C#，覆盖式全屏窗口 + Content Engine + 传输接收端，含投屏接收/解码）
│   ├── Caster/      投屏机轻量工具（C#，屏幕捕获 + 音频采集 + 编码 + 推流，端到端投屏已接通）
│   ├── Shared/
│   │   ├── EveryStage.Rendering/  D3D11/Media Foundation零拷贝解码渲染管线，Terminal与阶段0 Demo共用
│   │   ├── EveryStage.Discovery/  局域网发现/配对协议 + 设备身份持久化，Terminal与Caster共用
│   │   └── EveryStage.Transport/  RTP包帧 + H.264 NAL单元打包/拆包(RFC 3550/6184) + 收发会话，Caster/Terminal两端现在都是真实调用方
│   └── Poc/         阶段前置技术验证Demo（不属于正式产品代码）
│       ├── ZeroCopyRenderDemo/    阶段0：D3D11零拷贝渲染管线验证（详见其 README.md）
│       └── WpsComInteropSpike/    阶段1：WPS COM互操作静默/翻页验证脚本（详见其 README.md）
└── .github/         CI/CD 与 issue 模板（待补充）
```

## 核心技术方案（详见 docs/PLANNING.md）

- 屏幕捕获：Desktop Duplication API (DDA)
- 视频编码：H.264 硬件编码，零延迟预设，CBR
- 传输：UDP + RTP（WebRTC 媒体传输能力，不含 NAT 穿透）
- 渲染：Media Foundation 硬件解码 → D3D11 纹理直出 → DXGI SwapChain
- 本地文档：WPS COM 互操作（支持编辑/翻页）

## 开发状态

详见 `docs/PLANNING.md` 中的开发排期建议（第15章）。当前代码均在 Linux 沙箱中编写，**尚未在真实
Windows 环境编译验证**，下一步都需要先在 Windows 开发机上完成构建。

- **阶段0**（D3D11零拷贝渲染管线技术验证Demo）：初版代码已在 `src/Poc/ZeroCopyRenderDemo/` 完成，
  待 Windows/GPU 环境编译并跑通第4.4节验收标准。
- **阶段1**（终端机主程序框架 + 本地内容引擎）：初版已在 `src/Terminal/EveryStage.Terminal/` 完成
  ——覆盖式窗口、投屏开关/断状态机、数据持久化、音频接管、图片/PDF/视频渲染（视频复用
  `src/Shared/EveryStage.Rendering/` 的D3D11零拷贝管线）、把渲染器接到 Scenario/Activity数据模型和
  状态机上的 `PlaybackEngine`，§14.4要求的三类按天滚动日志。还缺WPS COM互操作的正式集成，详见该
  项目的 README.md；WPS互操作已有一个独立验证脚本 `src/Poc/WpsComInteropSpike/`，用于尽早摸清
  静默模式与翻页是否可行。
- **阶段4正式主界面**（§8）四个面板现在全部做完了：`UI/MainWindow.cs` 搭了左侧导航+右侧内容区的
  外壳，**文件**、**活动**、**设备**三个面板真的能用——文件面板导入/拖拽/筛选/双击播放；活动面板
  能管理方案与活动（新建/另存为/删除、增删排序活动里的文件）并按活动播放；设备面板能看已配对设备
  列表并移除配对。外加悬浮预览窗(§8.3)和配对确认弹窗(§7)。`PlaybackEngine` 的两个 `RequestPlay`
  重载和 `PairedDeviceStore` 现在都有了真实的UI入口。**设置**面板这一轮补上了（新增
  `Data/AppSettings.cs`+`Data/SettingsStore.cs`数据模型和`UI/Panels/SettingsPanel.cs`）——
  PLANNING.md §8.2只规定了"通用/显示/播放行为/网络与设备/关于"五个分类的名字，具体字段由本仓库
  自己从其他地方"随手定的、没有依据"的真实配置项里挑选（投屏开关默认状态、扩展屏选择、默认停留
  时长、设备名称），而不是凭空发明；哪些设置需要重启才生效、哪些立即生效都在面板上如实标注，详见
  `src/Terminal/EveryStage.Terminal/README.md`。
- **阶段3的设备发现/配对**部分（§7）也已提前实现，且两端都有了：终端机侧
  `Terminal/.../Devices/DiscoveryService` 广播、响应配对、分离"允许被投放/被监看"权限，配一个配对
  确认弹窗；投屏机侧 `src/Caster/EveryStage.Caster/` 监听终端机列表、发起配对请求。两边共用的协议
  定义搬到了 `src/Shared/EveryStage.Discovery/`——**协议格式仍是本仓库自定义的草案**，现在有了
  两个独立实现，但从未在真实网络上互相验证过。
- **阶段2的捕获/编码/传输现在端到端接通了，视频+音频都有**：Caster侧新增的
  `Casting/LiveCastSession.cs` 把屏幕捕获(Desktop Duplication API，`Caster/Capture/`)→ NV12转换 →
  **H.264硬件编码**(`Caster/Encode/`：`BgraToNv12Converter` 用GPU视频处理器转NV12，
  `H264HardwareEncoder` 驱动一个异步Media Foundation编码器MFT，这是PLANNING.md §15"第二大技术
  风险区"里风险最高的一块，详细风险清单见 `src/Caster/EveryStage.Caster/README.md`)→ RTP打包发送
  (`src/Shared/EveryStage.Transport/`)串成一条真正的链路：配对成功后立即真的开始采集/编码/通过
  RTP发往终端机，不再是占位的"尚未实现"提示。这一轮又加上了**系统音频采集**
  (`Caster/Capture/AudioCaptureSource.cs`：WASAPI loopback采集系统正在播放的声音，转成16-bit PCM，
  走另一个独立的RTP流发送——刻意不经过任何音频编码器，绕开了跟视频编码器同一类的MFT风险，代价是
  未压缩PCM带宽更高)，是video之上的锦上添花而非硬性要求：这台机器没有声音在播、或者WASAPI初始化
  失败，只会让这次投屏退化成纯视频，不影响画面。Terminal侧新增 `Terminal/.../Receiving/`
  （`H264HardwareDecoder` 驱动一个假设为同步的H.264解码器MFT + `CastReceiver` 接收RTP、用marker
  位重组Annex-B访问单元、解码后通过 `EveryStage.Rendering` 的 `SwapChainPresenter` 显示到覆盖
  窗口，现在还有对称的音频侧：收到PCM直接喂给`AudioPlaybackClock`播放，同样独立于视频、失败时独立
  降级)，配合 `DiscoveryService` 新增的 `CastStartMessage`/`CastStopMessage`处理（只信任已配对且
  `AllowCast`的设备）驱动 `OutputStateMachine`/`OverlayWindow`。本地播放与设备投屏共享覆盖窗口
  `VideoHost`的问题（新增 `Terminal/.../Display/VideoSurface.cs`，两者现在用同一个D3D11设备/
  交换链而不是各自建一个绑到同一HWND，并靠`PlaybackEngine.StopForDeviceCast`/
  `LocalPlaybackStarting`互相抢占）已经在后续一轮修复，详见
  `src/Terminal/EveryStage.Terminal/README.md`。**这条链路现在有了一个轻量的应答机制**：
  `DiscoveryProtocol`新增`CastStatusMessage`，Terminal每秒把已解码帧数/收到的音视频字节数/两侧
  各自的出错信息报回给正在投屏的Caster（`Program.cs`的`SendCastStatus()`发送，
  `LiveCastSession`接收并暴露`IsTerminalAlive`），Caster的UI上第一次出现了"终端机确认"这行不是
  纯本地自说自话的状态。这只是一个尽力而为、约5秒容忍窗口的心跳，不是逐包确认或流控，也**没有
  反方向的检测**（Terminal目前无法判断Caster是否还在线）；**仍然没有音视频同步**——视频和音频走
  完全独立的时钟（RTP时间戳分别来自墙钟和采样计数），长时间投屏可能明显不同步。这些都是明确记录、
  留到之后解决的空白，不是被忽略的问题（见两个项目各自的README"已知风险"）。屏幕捕获、H.264编码、
  RTP传输三块各自的独立自检（Caster侧的"屏幕捕获自检"/"编码自检"/"传输自检"三个按钮，
  `EveryStage.Transport`的`TransportSelfTest`是这个仓库第一个不需要Windows/GPU就能跑通的端到端
  自检）仍然保留，作为跟真实投屏管线互不干扰的独立诊断工具——音频和应答机制都还没有对应的自检，
  是明确记录的缺口。

## License

TBD
