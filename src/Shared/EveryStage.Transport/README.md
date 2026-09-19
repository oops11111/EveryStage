# EveryStage.Transport

RTP packet framing for PLANNING.md §4.2's "传输：UDP + RTP" — shared between
`src/Caster/EveryStage.Caster/`(sender, via `RtpSession`, driven for real by
`Casting/LiveCastSession.cs`) and `src/Terminal/EveryStage.Terminal/`(receiver, via `RtpReceiver`/
`RawRtpReceiver`, driven for real by `Receiving/CastReceiver.cs`), the same way
`EveryStage.Discovery` is shared for the pairing protocol. Now carries both of the cast's media
streams: H.264 video (with its own NAL-specific framing) and raw PCM audio (with none — see below).

## 现状：协议+收发都写了，现在两端都真的在用了，视频和音频都是

- `RtpPacket`：RFC 3550 的RTP包头编解码（固定12字节头，不支持CSRC列表之外的扩展头）
- `AnnexBNalSplitter` / `H264RtpPacketizer` / `H264RtpDepacketizer`：RFC 6184 的H.264 NAL单元与
  RTP载荷之间的转换（Single NAL Unit包 + FU-A分片/重组）——只有视频用得到，音频的载荷本身就是连续
  字节流，没有"NAL单元"这种需要拆装的边界概念
- `RtpSession`：发送端会话封装，管理SSRC、递增的序列号。`SendNalUnitAsync`（视频用，调用
  `H264RtpPacketizer`分片）和这次新加的 `SendRawPayloadAsync`（音频用，一个payload=一个RTP包，
  不做任何NAL/FU-A分片）现在共享同一套SSRC/序列号状态，`SendNalUnitAsync`内部也改成调用
  `SendRawPayloadAsync`而不是各自重复构造`RtpPacket`
- `RtpReceiver`：视频接收端，解出RTP包、喂给`H264RtpDepacketizer`，重组出完整NAL单元时触发
  `NalUnitReceived`，带上完成该NAL单元的那个RTP包的Marker位（RFC 6184 §5.3"是不是这个访问单元/
  编码帧的最后一个NAL单元"）——`Caster`的`LiveCastSession`发送时设置它，`Terminal`的
  `CastReceiver`接收时用它判断"这一帧的所有NAL单元到齐了，可以拼成一个Annex-B访问单元喂给解码器
  了"，不用另外发明一套"帧边界"信令
- `RawRtpReceiver`（这次新加）：音频接收端，解出RTP包后payload直接原样交出，不经过
  `H264RtpDepacketizer`——刻意没有跟`RtpReceiver`合并成一个通用类，理由见该文件自己的doc comment
- `TransportSelfTest`：这个仓库第一个真正跑得起来的端到端验证，覆盖视频这条路径（人造NAL形状
  数据 + FU-A分片）——用一批人造的、形状像NAL单元的随机字节数据（含一个刻意
  超过MTU、会触发FU-A分片的），通过本机回环UDP走一遍
  `RtpSession → UDP → RtpReceiver → H264RtpDepacketizer`，逐字节比对收到的和发出的是否一致。接入
  `src/Caster/EveryStage.Caster` 的UI（"运行传输自检"按钮），跟真实投屏管线互不影响。
- `RawTransportSelfTest`（这次新加）：`TransportSelfTest`的音频对应版本，覆盖`RtpSession.
  SendRawPayloadAsync → UDP → RawRtpReceiver`这条之前完全没有自动化验证过的路径（见下面"已知
  风险"第7条）——同样是本机回环、固定随机种子、逐字节比对，额外多验证一件事：在这种"根本不会真的
  丢包"的本机回环环境下，`RawRtpReceiver.GapEvents`必须恰好是0，如果不是0说明序列号跟踪或者
  `RtpSession`的序列号递增本身有真正的bug，不是网络运气不好，所以这里把它当成自检失败而不是
  警告。同样接入`src/Caster/EveryStage.Caster`的UI（"运行音频传输自检"按钮）。

`RtpReceiver`/`RawRtpReceiver`/`RtpSession`现在都有了真实的生产调用方（`CastReceiver`/
`LiveCastSession`），不只是被`TransportSelfTest`/`RawTransportSelfTest`自己跟自己对话验证过协议
逻辑。

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
4. **【部分实现，原为已知缺口】RTP的 PayloadType 数值现在真的会被校验了**：`TransportSelfTest` 和
   `LiveCastSession` 都硬编码了同一个占位值 (96/97，动态负载类型范围内的常见选择)——
   `DiscoveryProtocol.CastStartMessage` 早就携带了`PayloadType`/`AudioPayloadType`字段，但之前
   `RtpReceiver`/`RawRtpReceiver`完全不检查收到的包的PayloadType是否跟预期一致，这两个字段被
   `DiscoveryService.CastStartInfo`接住之后就再没传下去过。这次`RtpReceiver`/`RawRtpReceiver`
   （各自独立实现，同一套逻辑）新增可选的`expectedPayloadType`构造参数，收到的包如果PayloadType
   不匹配就当成"不是我们的包"直接丢弃（计入新增的`PayloadTypeMismatches`计数器），处理方式
   跟`RtpPacket.TryDecode`本身失败时完全一样——不传这个参数（`null`，默认值）保留原来的宽松行为，
   向后兼容`TransportSelfTest`/`RawTransportSelfTest`自己主体验证流程用的那个接收端（它没有改，
   仍然不传这个参数）——两个自检各自新增的独立校验小节见下面"更新"段落，用的是另一个专门为此新建
   的接收端，不影响主体流程原有的行为。
   `Terminal.Receiving.CastReceiver`是这个参数第一个真正的生产调用方：从`DiscoveryService.
   CastStartInfo.PayloadType`/`AudioPayloadType`一路传进来。**仍然不是真正的SDP式协商**——这里
   校验的是"跟本项目自己硬编码的常量是否一致"，不是"跟对方声明的值协商出一个双方都接受的值"，本项目
   仍然没有任何真正的协商机制；在这个仓库自己的Caster↔Terminal流量里`PayloadTypeMismatches`预期
   永远是0（两边用的是同一套硬编码常量），这个校验存在的意义是防御同一端口上出现的陌生/无关RTP包，
   或者以后协议版本不一致的情况，不是当前就会触发的场景。**【更新】`PayloadTypeMismatches`现在有了
   真正的展示路径**：跟`GapEvents`当初加进来但过了一轮才被真正用在诊断日志里是同一个"先加计数器、
   再决定怎么用"的顺序，这次终于走完第二步——`Terminal.Receiving.CastReceiver`新增合并视频+音频
   两路的`PayloadTypeMismatches`属性，`Program.cs`的`SendCastStatus()`把它塞进
   `DiscoveryProtocol.CastStatusMessage`新增的同名字段一起发给Caster，`Caster.Casting.
   LiveCastSession`接住存成`TerminalPayloadTypeMismatches`，`MainForm.RefreshLiveCastStats()`
   最后展示出来——跟`AccessUnitsDroppedForBackpressure`那几行一样，只在非零时才显示一行警告，日常
   情况下（预期永远是0）这行完全不出现，不会污染UI。**【更新】`TransportSelfTest`/
   `RawTransportSelfTest`现在各自新增了一段专门验证"PayloadType不匹配的包真的会被丢弃"的小节**
   （`RunPayloadTypeMismatchCheckAsync`，各自独立实现，同一套逻辑，跟这两个自检整体的"两个独立
   实现"惯例一致）：各自另开一个全新的接收端（带上`expectedPayloadType`），故意先发一个用错误
   PayloadType构造的包、再发一个用正确PayloadType构造的包，断言只有正确的那个真正送达
   （`NalUnitReceived`/`PayloadReceived`只触发一次、内容匹配），且`PayloadTypeMismatches`恰好为1、
   `PacketsReceived`恰好为1、`GapEvents`恰好为0（错的包不该被算进序列号跟踪，也不该被算成一次跳变
   ——这正是第9条`GapEvents`那个"跳过的序号不计入丢包统计"设计决定要防的另一种误判）。主体验证流程
   本身用的那个接收端没有改，仍然不传`expectedPayloadType`（同上一段）——这个新校验完全是加在旁边
   的独立小节，不影响主体流程原有的行为/覆盖范围。**这次仍然没有做的部分**：这次改动本身也没有在
   这个沙箱里跑过（没有dotnet），新增的这段校验逻辑、以及`CastStatusMessage`新字段的序列化/
   反序列化，是否真的按预期工作，完全依赖代码审阅而非实际执行验证过——包括一个容易被忽略的细节：
   这段新校验依赖"UDP在本机回环上按发送顺序到达"这个假设（先发的错包应该先被处理并丢弃，后发的
   对包再到达），这本身不是UDP协议保证的行为，只是本机回环环境下几乎总是成立的经验事实，跟这两个
   自检主体验证流程"逐字节按顺序比对"从一开始就依赖的假设是同一类、没有比它更弱也没有更强。
5. **`TransportSelfTest` 用一次性 `UdpClient(0)` 探测空闲端口再关闭、`RtpReceiver` 再重新绑定
   同一个端口号**：两次绑定之间存在（概率很低的）端口被别的进程抢先占用的竞态，对本机自检这个用途
   可以接受，不是生产级的端口分配方式。
6. **没有正式的自动化测试项目**：`TransportSelfTest` 是手写的自检类，通过UI按钮触发，不是
   `dotnet test` 能跑的单元测试（本仓库目前没有任何测试项目，沙箱没有dotnet无法搭建）。第一次在
   Windows上编译成功后，把 `TransportSelfTest` 的逻辑改造成真正的自动化测试，应该优先于继续加新
   功能。
7. **【已实现，原为已知缺口】`RawRtpReceiver` 现在有了自己的自检**：新增`RawTransportSelfTest`，
   跟`TransportSelfTest`同样的本机回环+固定随机种子+逐字节比对手法，覆盖`RtpSession.
   SendRawPayloadAsync → UDP → RawRtpReceiver`这条路径（音频用）——之前这条路径完全靠人工推理
   "逻辑上和视频那条路径共享同一个`RtpPacket`编解码，应该没问题"，从未真正跑一遍验证过。这次的
   自检额外验证了一件事`TransportSelfTest`自己没验证的：在这种本机回环、根本不该真的丢包的环境下，
   `RawRtpReceiver.GapEvents`必须恰好是0；如果不是，说明第9条新增的序列号跟踪本身有bug，不是网络
   运气不好，所以自检把它当成失败条件而不是仅仅记录下来。接入`src/Caster/EveryStage.Caster`的UI
   （"运行音频传输自检"按钮），跟真实投屏管线互不影响。**跟`TransportSelfTest`一样，这个自检本身
   也从未在真实Windows机器上跑过**——沙箱里的本机回环UDP行为预期和Windows上一致，但这终究是个
   预期，不是验证过的事实。
8. **音频完全没有丢包/乱序处理，比视频更脆弱**：视频丢一个NAL单元至少会被`H264RtpDepacketizer`
   检测到并丢弃整个访问单元（不会拼出损坏的帧喂给解码器）；音频这边`RawRtpReceiver`把每个payload
   都直接交给调用方，`CastReceiver`收到就直接`AudioPlaybackClock.Enqueue`——一个包丢失或乱序到达，
   听到的就是原始PCM顺序被打乱/出现空隙的效果（可能是可闻的爆音/跳跃），没有任何检测或缓解。
   **更新（见第9条）**：现在至少能"看见"这件事发生了（`RtpReceiver`/`RawRtpReceiver`新增的
   `GapEvents`计数器），但这条风险原本说的"没有任何检测或缓解"里"缓解"那一半依然完全成立——
   检测到之后什么都不做，跟第3条"没有尝试恢复"是同一个未解决的限制。
9. **【新增】`RtpReceiver`/`RawRtpReceiver`新增`PacketsReceived`/`GapEvents`，但只是粗略的丢包
   信号、不是精确计数**：两个类现在都会跟踪RTP序列号，每收到一个包就检查它是不是恰好比上一个包
   大1，不是就把`GapEvents`加1——刻意不去计算"预期序号"和"实际序号"之间的数值差（RFC 3550序列号
   在65536处回绕），因为一个乱序但没有丢的包（比如序号提前1个到达）如果直接算数值差会通过`ushort`
   回绕被误判成"丢了65535个包"这种荒谬的数字，比"一次跳变只算1个事件"这种保守的低估更糟——这两个
   类本来就完全没有乱序重排支持（见第3条），一个乱序包本来就已经会打乱认知，这里选择不在这个基础
   上再犯一个更大的错误。这意味着一次跳过5个序号的丢包，在这里只算1次`GapEvents`，不是5——
   `Terminal.Receiving.CastReceiver.EstimatedPacketLossPercent`把`GapEvents`/`PacketsReceived`
   合并成一个粗略的百分比，喂给`DeviceConnectionLogger.LogQualityMetric`（PLANNING.md §14.4"连接
   质量指标"，见Terminal README），这个数字应该被当成"大致的健康趋势"而不是精确的丢包率。
10. **【新增，防御性加固，不是修复已确认的bug】`RtpReceiver`/`RawRtpReceiver`的`NalUnitReceived`/
    `PayloadReceived`事件分发现在包了一层`try/catch`，新增`DispatchExceptions`计数器**：委托一个
    子agent专门排查这个仓库里"未处理异常杀死某个后台接收循环，从此永久失效"这一形状的bug（这一轮
    已经在`PlaybackEngine`和`EveryStage.Discovery.DiscoveryProtocol.Decode`里各自真的找到过、
    修过一次），它审计到编码/解码/采集这条链路本身是干净的（`H264HardwareEncoder.RunEventLoop`
    整个循环体本来就包了一层`try/catch`并通过`EncodingFailed`上报，`H264HardwareDecoder`/
    `AacAudioDecoder`的调用方`CastReceiver`也早就各自有`try/catch`），但顺带指出这两个类的
    `ReceiveLoopAsync`比看起来薄一层——`_socket.ReceiveAsync`本身有`catch (SocketException)`，
    但往后`_depacketizer.Process`/事件分发这一段完全没有保护，如果`NalUnitReceived`/
    `PayloadReceived`的订阅方（生产环境里是`Terminal.Receiving.CastReceiver`的
    `OnNalUnitReceived`/`OnAudioPayloadReceived`）在它们自己那层`try/catch`之外的代码抛出异常
    （子agent没有找到具体能触发这一点的输入，`H264RtpDepacketizer.Process`本身也已经是防御性
    边界检查过的），这个异常会直接杀穿这整个接收循环，永久失效，不留任何痕迹——跟这一轮已经真的
    修过的那几个bug是完全同一种形状。**这次不是在修一个已确认的bug**，是在一个已知会造成这种
    后果的位置提前加固：给`_depacketizer.Process`/事件分发这一段包一层`try/catch`，异常发生时
    只丢弃这一个包、计入新增的`DispatchExceptions`计数器，继续处理下一个包——跟这个循环本来就有的
    "TryDecode失败/PayloadType不匹配就跳过，不杀循环"是同一个哲学，只是这次覆盖到事件分发这一步。
    **为什么只加计数器、不加日志**：这是Terminal和Caster共用的库，没有自己的日志基础设施，
    跟`GapEvents`/`PayloadTypeMismatches`当初"先加计数器、后面再决定怎么用"是同一个顺序，这次
    只做到第一步，预期这个计数器永远是0。**没有做的部分**：这次改动本身没有在这个沙箱里跑过
    （没有dotnet），没有真机验证过。
11. **【新增，补上第10条的自检覆盖】`TransportSelfTest`/`RawTransportSelfTest`现在真的验证了
    第10条那层`try/catch`确实生效**：第10条落地之后，一直没有任何自检去实际验证它——只是文档里
    说"新增了`DispatchExceptions`计数器"，从没有一个测试真的让一个订阅方抛出异常，确认这个循环
    真的活下来了。一个只统计"发生过几次"但从来没被验证过的计数器，比"循环干脆没有存活下来但没人
    发现"这种更严重的失败模式要小得多——所以这次新增的`RunDispatchExceptionResilienceCheckAsync`
    （两个类里各自独立实现了一份，跟这两个类一贯的"两份小的独立实现，不共享抽象"是同一个做法）
    刻意验证的不是"计数器变成1"这一件事，而是：订阅方在收到第一个NAL单元/音频包时故意抛出异常，
    第二个包紧接着发送，断言第二个包确实通过`NalUnitReceived`/`PayloadReceived`正常送达（证明
    接收循环在第一次异常之后还在跑下一轮），送达的字节内容正确，且`DispatchExceptions`恰好等于1。
    **没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
12. **【新增】`RtpVideoClock.FromElapsed`/`ToElapsedTicks`补上了纯数学的往返/回绕自检**：这两个
    方法自己的doc comment一直在用文字描述"往返应该精确"、"超过约13.26小时会按2^32回绕，这是
    RFC 3550规定的正常行为，接收端应该用模运算处理，不是bug"、"回绕之后的时间戳会跟一个早期的
    时间戳没法区分"——但直到现在没有任何代码真的验证过这些描述本身是不是对的。这次新增的
    `RunRtpVideoClockRoundTripCheck`（同步方法，不需要`async`，是整个自检里唯一连socket都不用
    的检查）做两件事：（一）用`clockRate`的整数倍时长（1.5秒@90000Hz=135000，不会触发
    `FromElapsed`内部`(ulong)ticks`那次截断的任何模糊性）验证往返精确无误差；（二）构造一个
    刻意超过回绕边界（约47721.86秒≈13.26小时）的时长，用`%`独立算出预期回绕后的值（不是照抄
    `FromElapsed`自己的强制转换写法，避免"用同一段逻辑验证自己"），确认`FromElapsed`真的按2^32
    回绕，并且确认回绕后的时间戳转换回去确实会得到一个远小于实际经过时间的值——把doc comment里
    "回绕后跟一个早期时间戳没法区分"这句描述从文字断言变成一次真正复现出来的行为。**没有做的
    部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过——虽然这是纯数学、
    理论上这个仓库唯一有可能在没有dotnet的环境里手动验证正确性的检查，这次仍然只做了人工推导
    （见本条附带的Python验算），没有真的编译执行过C#代码本身。
13. **【新发现的真实bug，已修复，用Python手动验算确认过】`AnnexBNalSplitter.Split`在两个NAL单元
    之间存在"多余的零字节填充"时，会把这些多余字节错误地泄漏进前一个NAL单元的输出末尾**：
    Annex B规范允许一个start code前面出现任意数量的`leading_zero_8bits`，不限于常见的2-3个；
    但这个扫描器每次遇到不构成start code的零字节时只会逐字节前移（`i++`）重新尝试，最终只记录
    "实际匹配上的那个start code"从哪里开始，而不是"这一串零字节真正从哪里开始"——中间被跳过、
    但从未被识别为某个start code一部分的零字节，就会被当成上一个NAL单元的真实内容，原封不动地
    包含在返回的切片末尾。具体例子（已用Python手动逐字节模拟验证，见对应的commit）：字节序列
    `00 00 01 67 41 42 00 00 00 00 01 68 43 44`（两个NAL单元之间比正常情况多了1个零字节）
    修复前会把第一个NAL单元的内容错误地返回成`67 41 42 00`（多了一个不属于它的尾随零字节），
    而不是正确的`67 41 42`。**修复方式**：在计算出每个NAL切片的`[nalStart, nalEnd)`范围之后，
    从`nalEnd`往回收缩，把切片末尾所有连续的`0x00`字节都去掉。这不是启发式猜测，而是H.264规范
    本身保证的安全操作——`rbsp_trailing_bits`（ITU-T H.264 §7.3.2.11）要求一个NAL单元的RBSP
    必须以`rbsp_stop_one_bit`结尾，这意味着一个格式正确的NAL单元最后一个真实字节永远不可能是
    `0x00`，所以切片末尾任何连续的全零字节——不管是编码器自己的`trailing_zero_8bits`填充，还是
    这次发现的"扫描过程中被跳过、未被识别为start code一部分的零字节"——都可以安全去掉，不会
    误删真实内容。**新增自检**：`TransportSelfTest`新增`RunAnnexBNalSplitterExtraPaddingCheck`，
    直接用上面那个具体字节序列断言修复后的输出完全正确——纯字节数组输入，跟第12条的
    `RunRtpVideoClockRoundTripCheck`一样不需要socket/GPU/音频硬件。**没有做的部分**：这次改动
    本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过——修复的正确性推导本身用Python脚本
    手动模拟了逐字节扫描过程（修复前/修复后两个版本都跑了一遍确认行为符合预期），但没有真的
    编译执行过C#代码本身；这个具体的"两个NAL单元之间有多余填充字节"场景在真实Media Foundation
    编码器输出里是否真的会出现，也还没有真机数据可以确认，只能确定Annex B规范本身允许这种情况
    存在。
14. **【新发现的真实bug，已修复】`RtpReceiver`/`RawRtpReceiver`的`ReceiveLoopAsync`没有捕获
    `ObjectDisposedException`，理论上能让接收循环变成没人观察的未处理Task异常**：这次是在
    Caster那边`LiveCastSession.RunLoop`发现并修复过同一形状的`ObjectDisposedException`竞争
    之后（见`EveryStage.Caster`README对应条目），回头检查这个仓库里所有"取消令牌+后台循环"
    结构是否有同款问题的一次系统性复查，不是响应某个具体报告。**具体场景**：这两个类各自的
    `Dispose()`都是同一个写法——`_cts.Cancel()`之后`_receiveLoop?.Wait(TimeSpan.FromSeconds(2))`
    等待后台循环退出，但完全没有检查这次等待到底是等到了循环真正退出、还是单纯超时；不管等到
    没等到，紧接着都会照样执行`_socket.Dispose()`。正常情况下`_socket.ReceiveAsync(token)`
    会在取消令牌被触发后几乎立刻抛出`OperationCanceledException`（已经被捕获），但如果这次
    等待恰好超时（网络栈异常缓慢、循环恰好卡在两次迭代之间等等），`_socket.Dispose()`就可能在
    这次`ReceiveAsync`还没返回的时候执行——对一个正在等待接收数据的`UdpClient`调用`Dispose()`
    会让那次`ReceiveAsync`抛出`ObjectDisposedException`，而不是`OperationCanceledException`。
    这个异常此前完全没有被捕获，会从`ReceiveLoopAsync`一路抛出`Task.Run`那个后台任务，变成一个
    没人`await`/观察的未处理异常。**修复方式**：给这两个类的`ReceiveLoopAsync`各自补上一个
    `catch (ObjectDisposedException) { return; }`，跟已有的`OperationCanceledException`分支
    同样处理——都是"没什么可再接收的了，正常退出"，不是需要报告的错误。**范围核实**：搜索了
    这个仓库里所有共享同一种"`Task.Run`跑接收循环+`Dispose()`里`Cancel`后`Wait`超时再
    `Dispose`socket"结构的类，一共只有四个——这两个之外，`EveryStage.Terminal.Devices.DiscoveryService`
    和`EveryStage.Caster.Discovery.TerminalDiscoveryClient`是另外两个（各自项目自己的README
    有对应的简短条目指回这里），已经用完全同样的修复方式一并处理，不是这次特意去两个不同项目
    分别发现的两次独立巧合。**没有做的部分**：这次改动本身没有在这个沙箱里跑过（没有dotnet），
    这条竞争条件本身的窗口极窄（需要2秒等待真的超时），没有办法在没有真实网络/真实卡顿场景的
    情况下构造出一次真正触发它的复现，跟`LiveCastSession`那条的"没有做的部分"是同一种没法验证
    的理由。

## 尚未开始

- 丢包恢复、拥塞控制、抖动缓冲（乱序重排）——`CastReceiver`（Terminal）现在是这个限制第一次在真实
  场景下有实际后果的地方：局域网上偶发丢包会让某个访问单元被丢弃/解码出瑕疵帧，音频则直接是可闻
  的爆音/跳跃（见风险第8条），而不是本机回环自检那种几乎不丢包的环境
- RTP参数真正的SDP式协商机制——PayloadType数值本身现在会被`RtpReceiver`/`RawRtpReceiver`校验了
  （见风险第4条，`TransportSelfTest`/`RawTransportSelfTest`现在也都验证了这个丢弃行为本身），但
  校验的只是"是否等于本项目自己硬编码的常量"，不是"双方协商出一个都接受的值"；时钟基准以外的更多
  元数据（真正的SDP能表达的那些）仍然完全没有协商机制
