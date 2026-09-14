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
│   │   └── EveryStage.Rendering/  D3D11/Media Foundation零拷贝解码渲染管线，Terminal与阶段0 Demo共用
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
  状态机上的 `PlaybackEngine`，§14.4要求的三类按天滚动日志，以及悬浮预览窗(§8.3，上一项/暂停/下一项
  /断)——`PlaybackEngine` 终于有了真实调用方，尽管还只是这一个小窗口，不是完整的四大面板主界面。
  还缺WPS COM互操作的正式集成，详见该项目的 README.md；WPS互操作已有一个独立验证脚本
  `src/Poc/WpsComInteropSpike/`，用于尽早摸清静默模式与翻页是否可行。
- **阶段3的设备发现/配对**部分（§7）也已提前实现：`Devices/DiscoveryService` 用UDP广播发现终端机、
  处理配对请求、分离"允许被投放/被监看"权限，配上一个配对确认弹窗(`UI/PairingConfirmationDialog`)。
  **协议格式是本仓库自定义的草案**，因为投屏机(Caster)项目还没有任何代码——两边协议真正对齐要等
  Caster 开发启动之后。
- 其余阶段（阶段2 传输接收端的媒体流部分、阶段4 正式四大面板主界面）尚未开始。

## License

TBD
