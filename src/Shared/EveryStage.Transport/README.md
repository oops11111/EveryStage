# EveryStage.Transport

RTP packet framing and H.264-over-RTP packetization for PLANNING.md §4.2's "传输：UDP + RTP" —
shared between `src/Caster/EveryStage.Caster/`(sender, via `RtpSession`, now driven for real by
`Casting/LiveCastSession.cs`) and `src/Terminal/EveryStage.Terminal/`(receiver, via `RtpReceiver`,
now driven for real by `Receiving/CastReceiver.cs`), the same way `EveryStage.Discovery` is shared
for the pairing protocol.

## 现状：协议+收发都写了，现在两端都真的在用了

- `RtpPacket`：RFC 3550 的RTP包头编解码（固定12字节头，不支持CSRC列表之外的扩展头）
- `AnnexBNalSplitter` / `H264RtpPacketizer` / `H264RtpDepacketizer`：RFC 6184 的H.264 NAL单元与
  RTP载荷之间的转换（Single NAL Unit包 + FU-A分片/重组）
- `RtpSession` / `RtpReceiver`：分别是发送端和接收端的会话封装——`RtpSession`管理SSRC、递增的
  序列号，把NAL单元打包成RTP包通过 `UdpClient` 发出去；`RtpReceiver`监听UDP端口、解出RTP包、喂给
  `H264RtpDepacketizer`，重组出完整NAL单元时触发 `NalUnitReceived`。这个事件现在还带上了完成该
  NAL单元的那个RTP包的Marker位（RFC 6184 §5.3"是不是这个访问单元/编码帧的最后一个NAL单元"）——
  `Caster`的`LiveCastSession`发送时设置它，`Terminal`的`CastReceiver`接收时用它判断"这一帧的所有
  NAL单元到齐了，可以拼成一个Annex-B访问单元喂给解码器了"，不用另外发明一套"帧边界"信令。
- `TransportSelfTest`：这个仓库第一个真正跑得起来的端到端验证——用一批人造的、形状像NAL单元
  的随机字节数据（含一个刻意超过MTU、会触发FU-A分片的），通过本机回环UDP走一遍
  `RtpSession → UDP → RtpReceiver → H264RtpDepacketizer`，逐字节比对收到的和发出的是否一致。接入
  `src/Caster/EveryStage.Caster` 的UI（"运行传输自检"按钮），跟真实投屏管线互不影响。

`RtpReceiver`/`RtpSession`现在都有了真实的生产调用方（`CastReceiver`/`LiveCastSession`），不再只是
`TransportSelfTest`自己跟自己对话——这是这两个类第一次真正参与端到端投屏，而不仅仅是被自检验证过
协议逻辑本身正确。

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
4. **RTP的 PayloadType 数值**：`TransportSelfTest` 和 `LiveCastSession` 都硬编码了同一个占位值
   (96，动态负载类型范围内的常见选择)——`DiscoveryProtocol.CastStartMessage` 现在确实携带了一个
   `PayloadType` 字段，但 `RtpReceiver`/`RtpPacket.TryDecode` 完全不检查收到的包的PayloadType是否
   跟预期一致（目前也没有多路复用的需要——`EveryStage.Caster`的README记录了"同一时间只支持一路
   投屏"这个限制），所以这个字段目前只是传过去但没有被真正校验或使用——类似SDP协商的内容，本项目
   仍然没有做任何真正的协商/校验机制。
5. **`TransportSelfTest` 用一次性 `UdpClient(0)` 探测空闲端口再关闭、`RtpReceiver` 再重新绑定
   同一个端口号**：两次绑定之间存在（概率很低的）端口被别的进程抢先占用的竞态，对本机自检这个用途
   可以接受，不是生产级的端口分配方式。
6. **没有正式的自动化测试项目**：`TransportSelfTest` 是手写的自检类，通过UI按钮触发，不是
   `dotnet test` 能跑的单元测试（本仓库目前没有任何测试项目，沙箱没有dotnet无法搭建）。第一次在
   Windows上编译成功后，把 `TransportSelfTest` 的逻辑改造成真正的自动化测试，应该优先于继续加新
   功能。

## 尚未开始

- 丢包恢复、拥塞控制、抖动缓冲（乱序重排）——`CastReceiver`（Terminal）现在是这个限制第一次在真实
  场景下有实际后果的地方：局域网上偶发丢包会让某个访问单元被丢弃/解码出瑕疵帧，而不是本机回环自检
  那种几乎不丢包的环境
- RTP参数（PayloadType数值本身的校验、时钟基准以外的更多元数据）的协商/校验机制——目前完全靠硬
  编码假设双方一致，`CastStartMessage.PayloadType` 传了但没被消费端真正拿来做任何检查
