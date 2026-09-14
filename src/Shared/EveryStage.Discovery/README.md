# EveryStage.Discovery

LAN发现/配对的共享部分（PLANNING.md §7），两个消费方各自实现自己那一半角色：

- `src/Terminal/EveryStage.Terminal`：接收方——广播beacon、响应配对请求、维护配对/信任列表
- `src/Caster/EveryStage.Caster`：发起方——监听beacon、发起配对请求

这里只有两样东西，且都是**双方共用、不属于任何一方**的：

- `DiscoveryProtocol.cs`：UDP消息格式（beacon / pair_request / pair_response）。**本仓库自己拟的
  草案**，不是PLANNING.md规定的格式——该文档只说了要有发现和配对，没定义具体协议。这个格式随时可能
  因为两端实际联调时发现的问题而改变。
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
- 不含任何安全/认证机制（明文JSON，无签名无加密）——这与PLANNING.md §14.3 "不加密：内网传输明文"
  的产品决策一致，不是遗漏。
