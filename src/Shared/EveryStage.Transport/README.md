# EveryStage.Transport

RTP packet framing and H.264-over-RTP packetization for PLANNING.md §4.2's "传输：UDP + RTP" —
meant to eventually be shared between `src/Caster/EveryStage.Caster/`(sender) and
`src/Terminal/EveryStage.Terminal/`(receiver), the same way `EveryStage.Discovery` is shared for
the pairing protocol. **Nothing references this library yet** — see "现状" below.

## 现状：这是纯协议/编解码逻辑，还没有接入任何真实数据

这个库只做两件事，都是标准协议规定的字节层面的打包/拆包，不涉及任何 Windows API、DirectX 或网络
socket 本身：

- `RtpPacket`：RFC 3550 的RTP包头编解码（固定12字节头，不支持CSRC列表之外的扩展头）
- `AnnexBNalSplitter` / `H264RtpPacketizer` / `H264RtpDepacketizer`：RFC 6184 的H.264 NAL单元与
  RTP载荷之间的转换（Single NAL Unit包 + FU-A分片/重组）

之所以先做这一块而不是先做H.264硬件编码：这是纯字节操作+标准协议实现，不依赖任何还不存在的其他
组件就能把逻辑写对、靠人工逐字节推演验证正确性（本次实现时就是这样验证FU-A分片头的往返编解码是否
一致的）；而H.264编码需要驱动 Media Foundation 的硬件编码器（异步MFT，事件驱动状态机），是这个仓库
目前风险最高、最容易出错又最难在没有编译环境时人工验证正确性的一块，值得等这块简单的先立住。

## 已知风险 / 待验证事项

同样：本项目在 Linux 沙箱中编写，从未编译过。但这里的代码没有DirectX/COM那一档的风险——纯托管
字节数组操作 + `System.Buffers.Binary`，风险主要在于**协议实现是否完全正确**，而不是"API名字猜对
没有"：

1. **`RtpPacket` 不解析RTP扩展头**：如果收到的包设置了extension bit，`TryDecode` 会把扩展头错当
   成payload的一部分——这个仓库自己发送的包永远不会设置这个bit，所以自己和自己通信没问题，但不是
   通用RTP解析器，跟第三方RTP实现互通前需要补上。
2. **`AnnexBNalSplitter` 依赖"emulation prevention"规则成立**：H.264编码器有义务保证NAL单元内部
   不会自然出现连续3个零字节后跟0x01（编码时会插入0x03字节打断），这个扫描器假设这个规则成立，
   没有对边界情况（比如非法/损坏的比特流）做额外防御。
3. **`H264RtpDepacketizer` 没有处理包乱序/丢包重传**：假设RTP包按序到达；FU-A分片中间丢包会被
   检测到（整个NAL被丢弃，不会拼出损坏的帧）但不会尝试恢复。真正的丢包恢复(NACK/FEC，PLANNING.md
   §4.2提到的"WebRTC媒体传输能力"部分)完全没有实现。
4. **RTP的 PayloadType 数值、SSRC生成、序列号/时间戳的时钟基准**都还没有定义——这些是发送端和
   接收端要事先约定好的参数（类似SDP协商的内容，但本项目没有做任何协商机制），等真正把发送端/接收端
   接起来时需要选定并双方硬编码或通过配对握手协商。
5. **没有任何自动化测试**：本仓库到目前为止都没有测试项目（沙箱没有dotnet，无法运行）。这个库的
   往返编解码逻辑是靠人工逐字节推演验证的，不代表等同于跑过真实测试——第一次在Windows上编译成功后，
   写一个简单的往返测试（打包再拆包，比较字节是否一致）应该是验证这个库最优先的事。

## 尚未开始

- 真正驱动 H.264 硬件编码器产出 Annex-B 比特流（`AnnexBNalSplitter` 的输入源）
- 用 `UdpClient` 把 `RtpPacket.Encode()` 的字节发送出去、在接收端 `RtpPacket.TryDecode()`——目前
  两端都还没有对接这个库
- 丢包恢复、拥塞控制、抖动缓冲（乱序重排）
- SSRC/序列号/时间戳的实际生成与管理（需要一个"RTP发送会话"状态类，目前完全没有）
