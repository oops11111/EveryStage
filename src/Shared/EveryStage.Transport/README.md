# EveryStage.Transport

RTP packet framing and H.264-over-RTP packetization for PLANNING.md §4.2's "传输：UDP + RTP" —
shared between `src/Caster/EveryStage.Caster/`(sender, via `RtpSession`) and eventually
`src/Terminal/EveryStage.Terminal/`(receiver, via `RtpReceiver`), the same way `EveryStage.Discovery`
is shared for the pairing protocol.

## 现状：协议+收发都写了，但还没有接上真实的H.264数据

- `RtpPacket`：RFC 3550 的RTP包头编解码（固定12字节头，不支持CSRC列表之外的扩展头）
- `AnnexBNalSplitter` / `H264RtpPacketizer` / `H264RtpDepacketizer`：RFC 6184 的H.264 NAL单元与
  RTP载荷之间的转换（Single NAL Unit包 + FU-A分片/重组）
- `RtpSession` / `RtpReceiver`：分别是发送端和接收端的会话封装——`RtpSession`管理SSRC、递增的
  序列号，把NAL单元打包成RTP包通过 `UdpClient` 发出去；`RtpReceiver`监听UDP端口、解出RTP包、喂给
  `H264RtpDepacketizer`，重组出完整NAL单元时触发事件。
- `TransportSelfTest`：**这个仓库第一个真正跑得起来的端到端验证**——用一批人造的、形状像NAL单元
  的随机字节数据（含一个刻意超过MTU、会触发FU-A分片的），通过本机回环UDP走一遍
  `RtpSession → UDP → RtpReceiver → H264RtpDepacketizer`，逐字节比对收到的和发出的是否一致。已经
  接入 `src/Caster/EveryStage.Caster` 的UI（配对成功页面的"运行传输自检"按钮）。

之所以先做传输层而不是先做H.264硬件编码：这是纯字节操作+标准协议实现+`UdpClient`标准API，不依赖
任何还不存在的其他组件就能把逻辑写对、而且**真的能跑起来验证**（`TransportSelfTest`是这个仓库第一个
不需要Windows/GPU、只需要一个能跑.NET的环境就能验证正确性的自检）；H.264编码需要驱动 Media
Foundation 的硬件编码器（异步MFT，事件驱动状态机），是这个仓库目前风险最高、最容易出错又最难在
没有编译环境时哪怕靠自检验证正确性的一块，值得放到最后。

## 已知风险 / 待验证事项

同样：本项目在 Linux 沙箱中编写，从未编译过。但这里的代码没有DirectX/COM那一档的风险——纯托管
字节数组操作 + `System.Buffers.Binary` + 标准 `UdpClient`，风险主要在于**协议/逻辑实现是否完全
正确**，而不是"API名字猜对没有"：

1. **`RtpPacket` 不解析RTP扩展头**：如果收到的包设置了extension bit，`TryDecode` 会把扩展头错当
   成payload的一部分——这个仓库自己发送的包永远不会设置这个bit，所以自己和自己通信没问题，但不是
   通用RTP解析器，跟第三方RTP实现互通前需要补上。
2. **`AnnexBNalSplitter` 依赖"emulation prevention"规则成立**：H.264编码器有义务保证NAL单元内部
   不会自然出现连续3个零字节后跟0x01（编码时会插入0x03字节打断），这个扫描器假设这个规则成立，
   没有对边界情况（比如非法/损坏的比特流）做额外防御。
3. **`H264RtpDepacketizer` 没有处理包乱序/丢包重传**：假设RTP包按序到达；FU-A分片中间丢包会被
   检测到（整个NAL被丢弃，不会拼出损坏的帧）但不会尝试恢复。真正的丢包恢复(NACK/FEC，PLANNING.md
   §4.2提到的"WebRTC媒体传输能力"部分)完全没有实现——`TransportSelfTest`走的是本机回环，不会真的
   丢包，所以这条完全没有被自检覆盖到。
4. **RTP的 PayloadType 数值**目前只在 `TransportSelfTest` 里硬编码了一个占位值(96，动态负载类型
   范围内的常见选择)——发送端和接收端要事先约定好这个数值（类似SDP协商的内容，但本项目没有做任何
   协商机制），等真正把 Caster 发送端和 Terminal 接收端接起来时需要选定并双方硬编码或通过配对
   握手协商。
5. **`TransportSelfTest` 用一次性 `UdpClient(0)` 探测空闲端口再关闭、`RtpReceiver` 再重新绑定
   同一个端口号**：两次绑定之间存在（概率很低的）端口被别的进程抢先占用的竞态，对本机自检这个用途
   可以接受，不是生产级的端口分配方式。
6. **没有正式的自动化测试项目**：`TransportSelfTest` 是手写的自检类，通过UI按钮触发，不是
   `dotnet test` 能跑的单元测试（本仓库目前没有任何测试项目，沙箱没有dotnet无法搭建）。第一次在
   Windows上编译成功后，把 `TransportSelfTest` 的逻辑改造成真正的自动化测试，应该优先于继续加新
   功能。

## 尚未开始

- 真正驱动 H.264 硬件编码器产出 Annex-B 比特流，接到 `RtpSession.SendNalUnitAsync` 上
- Terminal 接收端：`RtpReceiver` 目前只在 Caster 的自检里被使用，Terminal 侧还没有任何代码调用它
- 丢包恢复、拥塞控制、抖动缓冲（乱序重排）
- RTP参数（PayloadType数值、时钟基准以外的更多元数据）的协商机制——目前完全靠硬编码假设双方一致
