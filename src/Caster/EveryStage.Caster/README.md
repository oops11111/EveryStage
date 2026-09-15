# EveryStage.Caster — 投屏机（阶段2大部分：发现/配对 + 屏幕捕获 + 传输层）

对应 `docs/PLANNING.md` §12 的UI描述、§7 的设备发现/配对流程的**投屏机一侧**，以及阶段2"屏幕捕获：
Desktop Duplication API (DDA)"和"传输：UDP + RTP"两项。**不包含**H.264编码、音频采集——
PLANNING.md §15 把整个捕获/编码/传输称为"第二大技术风险区"，其中风险最高、最难验证的编码这一块
还是空白；捕获和传输的协议/收发逻辑都已经写完并各自有自检。

## 现在能做什么

1. 启动后监听终端机的UDP广播beacon，标准的"待机态：目标终端机列表"（§12）
2. 选中一个终端机，点"开始投屏"——真的会发送配对请求（`DiscoveryProtocol.PairRequestMessage`）
   并等待终端机的响应
3. 配对成功后切到第二个面板，**如实告知"H.264编码与RTP推流尚未实现"**，而不是假装开始投屏
4. 面板上有一个"屏幕捕获自检"按钮——这个是真的：点击后用 Desktop Duplication API (`Capture/`)
   实际捕获屏幕，实时显示分辨率/已捕获帧数/帧率。**捕获到的画面不会编码、不会发送到任何地方**，
   纯粹是为了在真正接上编码/传输之前，先单独确认捕获这一步本身能不能跑通。
5. 面板上还有一个"运行传输自检"按钮——用一批人造的、形状像H.264 NAL单元的随机数据，走一遍
   `RtpSession → 本机回环UDP → RtpReceiver → H264RtpDepacketizer`（`EveryStage.Transport`），
   逐字节核对收发是否一致，包括FU-A分片重组是否正确。同样**不涉及真实视频数据，不发送到任何其他
   设备**，只是本机内部的协议正确性自检。

"如实告知"是有意的设计选择：PLANNING.md §12 描述的"投屏中态"包含时长、隐私提醒条、停止投屏按钮，
这些都是围绕一个真实存在的视频流设计的UI。在编码管线存在之前，搭建这些UI元素只是在为不存在的功能
画界面，所以这里的"投屏中"面板只做了协议握手结果的诚实反馈 + 两个独立的自检工具（捕获、传输），
两者互不相通——捕获到的画面目前不会喂给传输自检，接上这一段需要先有能把捕获画面编码成H.264的东西。

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
6. **`Capture/ScreenCaptureSource.cs` 是这个仓库第二块真正的DirectX interop代码**（第一块是
   `EveryStage.Rendering`），风险级别和它一样高——`IDXGIOutputDuplication`/
   `OutputDuplicateFrameInformation` 相关的成员名是照着Vortice.DXGI大概率的命名方式猜的，没有对照
   实际安装的包核实。具体需要重点检查的点见该文件顶部的doc comment，这里不重复。
7. **`DXGI_ERROR_ACCESS_LOST` 的捕获方式**（`catch (Exception ex) when (ex.HResult == ...)`）：
   假设 `IDXGIOutputDuplication.AcquireNextFrame` 返回的失败 `Result` 经 `.CheckError()` 转换成
   异常后，`Exception.HResult` 能拿到原始HRESULT——这是`.CheckError()`已有用法的合理延伸，但这条
   具体路径（ACCESS_LOST这个特定错误码）没有实测过，真机上第一次触发时（比如切换RDP会话）要重点看。
8. **`ScreenCaptureSource` 每帧都新分配一个 `ID3D11Texture2D` 并 `CopyResource`**：为了在没编译
   验证过的代码里绝对避免"DDA的纹理在ReleaseFrame后还被外部持有"这种悬空引用bug，选择了更保险但
   更费显存/带宽的做法，不是最终产品该有的零拷贝方案——真正做编码管线时，应该考虑让编码器直接消费
   DDA给的纹理（在ReleaseFrame前完成），而不是先拷贝一份。
9. **单帧超时500ms、自检UI每500ms轮询一次统计数据**：这两个数字都是随手定的，没有根据实际DDA刷新
   频率或UI响应性要求推算过。
10. **传输自检是这个仓库第一个真正"跑得起来"的自检**（相对屏幕捕获/DirectX那一类只能等Windows机器
    才能验证的代码）——它只需要.NET运行时和本机回环网络，理论上甚至在这个开发沙箱里如果有dotnet
    也能跑，只是环境里确实没有dotnet。逻辑本身见 `EveryStage.Transport` 的README，这里不重复。

## 尚未开始

- H.264 硬件编码（Media Foundation 编码器会话）——`EveryStage.Transport` 的 `RtpSession` 已经
  能接收NAL单元发送，缺的是编码器产出这些NAL单元
- 把屏幕捕获自检和传输自检接起来（中间还差编码这一步，见上）
- 音频采集与同步
- 已配对设备的持久化列表
- 真正的"投屏中"状态（时长显示、隐私提醒条、停止投屏——这些要等真实推流存在才有意义去做）
- 选择捕获哪个显示器（`ScreenCaptureSource` 目前固定捕获 `outputIndex=0`，多显示器场景没有UI选择）
