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
│   └── Poc/         阶段前置技术验证Demo（不属于正式产品代码）
│       └── ZeroCopyRenderDemo/  阶段0：D3D11零拷贝渲染管线验证（详见其 README.md）
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
- **阶段1**（终端机主程序框架）：初版框架已在 `src/Terminal/EveryStage.Terminal/` 完成——覆盖式
  窗口、投屏开关/断状态机、数据持久化、音频接管；本地内容引擎（图片/视频/PDF）、WPS COM互操作验证、
  设备发现配对仍未开始，详见该项目的 README.md。
- 其余阶段尚未开始。

## License

TBD
