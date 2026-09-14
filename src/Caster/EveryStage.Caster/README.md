# EveryStage.Caster — 投屏机（阶段2一部分：发现与配对握手）

对应 `docs/PLANNING.md` §12 的UI描述与 §7 的设备发现/配对流程的**投屏机一侧**。**不包含**实际的屏幕
捕获、H.264编码、RTP推流——PLANNING.md §15 把这部分列为"第二大技术风险区"，依赖阶段0验证结论，
本项目目前完全没有涉及。当前只做了一件事：证明 `src/Shared/EveryStage.Discovery/` 里那套发现/配对
协议，两端各自独立实现后真的能对上。

## 现在能做什么

1. 启动后监听终端机的UDP广播beacon，标准的"待机态：目标终端机列表"（§12）
2. 选中一个终端机，点"开始投屏"——真的会发送配对请求（`DiscoveryProtocol.PairRequestMessage`）
   并等待终端机的响应
3. 配对成功后切到第二个面板，**如实告知"屏幕捕获与推流尚未实现"**，而不是假装开始投屏

这个"如实告知"是有意的设计选择：PLANNING.md §12 描述的"投屏中态"包含时长、隐私提醒条、停止投屏
按钮，这些都是围绕一个真实存在的视频流设计的UI。在真正的捕获/编码/传输管线存在之前，搭建这些UI
元素只是在为不存在的功能画界面，所以这里只做了协议握手成功与否的诚实反馈。

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

## 尚未开始

- 屏幕捕获 (Desktop Duplication API)
- H.264 硬件编码
- RTP 传输（丢包恢复、拥塞控制）
- 音频采集与同步
- 已配对设备的持久化列表
- 真正的"投屏中"状态（时长显示、隐私提醒条、停止投屏——这些要等真实推流存在才有意义去做）
