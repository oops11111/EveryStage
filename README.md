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
│   ├── Terminal/    终端机主程序（C#，覆盖式全屏窗口 + Content Engine + 传输接收端）
│   ├── Caster/      投屏机轻量工具（C#，屏幕捕获 + 编码 + 推流）
│   ├── Shared/
│   │   ├── EveryStage.Rendering/  D3D11/Media Foundation零拷贝解码渲染管线，Terminal与阶段0 Demo共用
│   │   └── EveryStage.Discovery/  局域网发现/配对协议 + 设备身份持久化，Terminal与Caster共用
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
- **阶段4正式主界面**（§8）已经开工，不是完全空白：`UI/MainWindow.cs` 搭了左侧导航+右侧内容区的
  外壳，**文件**和**设备**两个面板是真的能用（导入/拖拽/筛选/双击播放，已配对设备列表/移除），
  外加悬浮预览窗(§8.3)和配对确认弹窗(§7)——`PlaybackEngine`和`PairedDeviceStore`终于都有了真实的
  UI入口。**活动**和**设置**两个面板还是占位符，等着接上已经就绪的 `Scenario`/`Activity` 数据和
  `PlaybackEngine.RequestPlay(Activity, int)`。
- **阶段3的设备发现/配对**部分（§7）也已提前实现，且两端都有了：终端机侧
  `Terminal/.../Devices/DiscoveryService` 广播、响应配对、分离"允许被投放/被监看"权限，配一个配对
  确认弹窗；投屏机侧 `src/Caster/EveryStage.Caster/` 监听终端机列表、发起配对请求。两边共用的协议
  定义搬到了 `src/Shared/EveryStage.Discovery/`——**协议格式仍是本仓库自定义的草案**，现在有了
  两个独立实现，但从未在真实网络上互相验证过。
- **阶段2的捕获/编码/传输**（DDA屏幕捕获、H.264硬件编码、RTP推流）完全没有开始——`Caster` 项目目前
  只做了发现与配对握手，点"开始投屏"配对成功后会如实告知"推流尚未实现"，而不是假装已经在投屏。

## License

TBD
