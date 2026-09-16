# EveryStage.Discovery

LAN发现/配对的共享部分（PLANNING.md §7），两个消费方各自实现自己那一半角色：

- `src/Terminal/EveryStage.Terminal`：接收方——广播beacon、响应配对请求、维护配对/信任列表
- `src/Caster/EveryStage.Caster`：发起方——监听beacon、发起配对请求

这里只有两样东西，且都是**双方共用、不属于任何一方**的：

- `DiscoveryProtocol.cs`：UDP消息格式（beacon / pair_request / pair_response / cast_start /
  cast_stop / cast_status / ping / pong）。**本仓库自己拟的草案**，不是PLANNING.md规定的格式——
  该文档只说了要有发现和配对，没定义具体协议。这个格式随时可能因为两端实际联调时发现的问题而
  改变。
- `DeviceIdentity.cs`：持久化的自身身份（Guid+设备名）。Terminal和Caster各自调用
  `DeviceIdentity.LoadOrCreate("terminal")` / `LoadOrCreate("caster")`，用不同文件名避免两个程序
  在同一台开发机上测试时互相覆盖对方的身份文件。

## 为什么单独拆出来

一旦两个项目都需要按位一致地理解同一份网络协议，把协议定义放在其中任何一个项目里，另一个项目就得
要么依赖那个项目（角色不对等，也会把不相关的UI/业务代码一起拉进来），要么复制一份代码（两份代码
迟早会不同步）。放进一个两边都能引用、且不属于任何一边业务逻辑的共享库，是自然的第三个选项。

## 已知风险 / 待验证事项

- 全部内容都**没有在真实网络环境验证过**——这是协议第一次有两个独立实现（Terminal的
  `DiscoveryService`、Caster的`TerminalDiscoveryClient`）需要真正对上，此前只有Terminal一侧存在
  的时候，协议对不对根本无从验证。
- `DiscoveryProtocol.Port = 47990` 是随手挑的，没有检查是否和其他常见软件冲突。
- 协议整体缺 PIN 码字段——PLANNING.md §7 "弹窗/PIN码"里"PIN码"这一半完全没实现，如果产品侧决定
  需要，得在这里加字段，两端一起改。
- **【更新】`CastStartMessage.PayloadType`/`AudioPayloadType`现在终于有了真正的消费方**：这两个
  字段早就存在，但接收端（`EveryStage.Transport`的`RtpReceiver`/`RawRtpReceiver`）从来没有检查过
  收到的RTP包是否跟它们一致——见`EveryStage.Transport`README风险第4条。这不是真正的SDP式协商，
  只是"跟本项目自己硬编码的常量比对"，两端预期永远一致，这个校验存在的意义是防御同一端口上的
  陌生/无关RTP包，不是当前会真的触发的场景。
- 不含任何安全/认证机制（明文JSON，无签名无加密）——这与PLANNING.md §14.3 "不加密：内网传输明文"
  的产品决策一致，不是遗漏。
- **【新增】`CastStartMessage.AudioIsAac`没有协议版本协商保护**：这个字段告诉Terminal该把
  `AudioRtpPort`上收到的每个payload当成原始PCM还是ADTS封装的AAC访问单元（见Caster/Terminal各自
  README里AAC编解码那几条风险）。这个协议本身没有版本号、没有"对方不认识这个字段就忽略/协商回退"
  的机制——如果Terminal和Caster两端跑的不是同一次提交的代码（比如Terminal没更新、还是旧版本的
  `CastStartMessage`反序列化），`AudioIsAac`会被反序列化成默认值`false`，导致Terminal把AAC字节
  当PCM直接送进WASAPI，播放出来是噪音而不是报错——这个仓库假设两端总是同一次提交部署，没有为
  跨版本不匹配做任何防御，风险等级跟这个协议整体"从未在真实网络环境验证过"是同一类。
- **【新增】`PingMessage`/`PongMessage`没有任何配对/信任检查**：Terminal端`HandlePing`收到任何
  `PingMessage`都无条件原样回一个`PongMessage`，不检查发送方是否已配对——跟这条列表第一条"不含
  任何安全/认证机制"是同一个已经接受的产品决策，多一个无条件echo不比协议本身已经存在的明文/无
  签名风险更差，但意味着局域网里任何人都可以拿这两个消息类型探测一个Terminal是否在线、测量到它
  的往返时延，即使从未跟它配对过。
