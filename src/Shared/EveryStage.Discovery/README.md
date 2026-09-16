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
  的时候，协议对不对根本无从验证。**【更新】这句话现在只对"两个真实服务的握手时序"还成立**：新增
  的`DiscoveryProtocolSelfTest`（见`Caster.UI.MainForm`"运行发现协议自检"按钮）第一次真正跑了一遍
  `DiscoveryProtocol.Encode` → 真实本机回环UDP → `DiscoveryProtocol.Decode`，覆盖协议定义的全部
  8种消息类型各自一份、逐字段比对往返前后是否一致——之前这个协议的JSON编解码逻辑（尤其是
  `Encode`里"用`JsonNode`拆开再手动塞进小写`type`字段"这个手写trick）连这么基础的问题都从来没有
  真正执行验证过，只靠代码审阅推理过"看起来应该没问题"。**这次没有覆盖的部分**：`DiscoveryService`/
  `TerminalDiscoveryClient`两个真实服务自己的握手逻辑（beacon广播→配对请求→配对响应这一整套时序、
  信任列表持久化、`DeviceIdentity`加载）完全没有被这个自检触及——它只验证协议的线格式本身无损，
  不验证两个真实服务会不会真的按预期时序把消息发出/处理对，这仍然要等到真实Windows双机联调才能
  验证，风险等级不变。
- **【新发现的真实bug，已修复】`DiscoveryProtocol.Decode`会因为一个陌生数据包而永久杀死整个接收
  循环，两端都会中招**：委托一个子agent专门排查发现/配对握手这条链路里"跟`PlaybackEngine`那几个
  静默卡死bug同一种形状"的问题，找到的这一个影响面最大——`Decode`原来是`using var doc =
  JsonDocument.Parse(data); if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return
  null;`。`JsonDocument.Parse`能接受任何合法JSON作为根元素，不只是对象——一个裸数字、裸字符串、
  裸布尔值、`null`字面量、或者数组都是合法JSON——但`JsonElement.TryGetProperty`/`GetString`在
  根元素不是对象、或者"type"字段不是字符串的时候，抛的是`InvalidOperationException`，不是
  `JsonException`。Terminal的`DiscoveryService.HandleDatagram`和Caster的
  `TerminalDiscoveryClient.HandleDatagram`两边原来都只catch了`JsonException`（注释原话都是"not
  one of ours — ignore, don't crash the loop"）——这个端口（见下一条，`47990`从来没检查过跟其他
  软件是否冲突）上随便一个形如`42`或`{"type":123}`的陌生数据包，就会让异常直接穿透两层catch，
  砸穿各自接收循环所在的裸`Task.Run`，而这个仓库里任何地方都没有
  `AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException`兜底，所以这个异常
  会彻底消失、不留任何日志或可见症状——中招的那一侧从此再也无法处理任何后续发现/配对/投屏状态
  流量（Terminal一侧的beacon广播循环是独立的`Task`，所以Terminal表面上看起来还活着，实际上再也
  answer不了配对请求/cast_start/cast_stop/ping）。比这份清单第一条本来就接受的"消息可能在传输
  中丢失"这个best-effort限制严重得多——丢包会在下次重试时大概率恢复，这个bug是永久性的，一次
  触发就彻底失效。**修复方式**：直接在`Decode`这个共享方法内部加两处`ValueKind`检查（根元素必须
  是`Object`、"type"字段必须是`String`，否则提前返回`null`，走跟"不认识的type值"完全一样的处理
  路径），而不是分别在两个调用方各自加宽catch类型——修一处保护两个现有调用方和未来任何新调用方。
  `DiscoveryProtocolSelfTest`新增`CheckMalformedInputsDontThrow`，直接调用`Decode`喂8种畸形
  输入（裸数字/裸字符串/裸数组/裸布尔/`null`/`type`是数字/`type`是对象/完全没有`type`字段），
  确认全部返回`null`而不抛异常——这是纯函数调用，不需要走网络。**没有做的部分**：这次改动本身
  没有在这个沙箱里跑过（没有dotnet），没有真机验证过；这次审计没有再往`Decode`每个`case`分支里
  的`Deserialize<T>()`调用深挖——那些走的是`JsonSerializer`的完整反序列化管线，按.NET文档的
  一般契约应该统一抛`JsonException`，跟这次修的`TryGetProperty`/`GetString`裸DOM API不是同一
  类调用，但这个假设本身也没有专门测试过。
- `DiscoveryProtocol.Port = 47990` 是随手挑的，没有检查是否和其他常见软件冲突。
- 协议整体缺 PIN 码字段——PLANNING.md §7 "弹窗/PIN码"里"PIN码"这一半完全没实现，如果产品侧决定
  需要，得在这里加字段，两端一起改。
- **【更新】`CastStartMessage.PayloadType`/`AudioPayloadType`现在终于有了真正的消费方**：这两个
  字段早就存在，但接收端（`EveryStage.Transport`的`RtpReceiver`/`RawRtpReceiver`）从来没有检查过
  收到的RTP包是否跟它们一致——见`EveryStage.Transport`README风险第4条。这不是真正的SDP式协商，
  只是"跟本项目自己硬编码的常量比对"，两端预期永远一致，这个校验存在的意义是防御同一端口上的
  陌生/无关RTP包，不是当前会真的触发的场景。
- **【新增】`CastStatusMessage.PayloadTypeMismatches`**：跟上面那条是同一次改动的下半段——上面
  校验的结果（丢弃了多少个PayloadType不匹配的包）现在会被Terminal通过这个新字段一路报回Caster，
  见`EveryStage.Transport`README风险第4条"这次没有做的部分"更新、以及Terminal/Caster各自README
  里这一轮的新增条目。这个协议本身没有为"以后又加一个字段"做任何版本协商（跟`AudioIsAac`是同一类
  风险，见下面那条）——一个跑旧代码的Terminal发出的`CastStatusMessage`不会有这个字段，反序列化到
  跑新代码的Caster这边会得到默认值`0`，看起来像是"从未发生过不匹配"而不是"这个字段这个版本还不
  存在"，这两种情况在协议层面完全无法区分，跟这份清单一直以来的"两端假设总是同一次提交部署"的
  态度一致。
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
- **【新发现的真实bug，已修复】`DeviceIdentity.LoadOrCreate`原来只防了"身份文件内容损坏"，没防
  "身份文件暂时读不出来"，而后者反而更容易把前者的补救手段变成新的破坏**：原来的`try`只catch了
  `JsonException`（对应"文件内容不是合法JSON"这种真损坏），`File.Exists(path)`返回`true`之后
  `File.ReadAllText(path)`完全可能因为文件被其他进程（杀毒软件扫描、备份工具）短暂独占锁住、或者
  权限问题而抛`IOException`/`UnauthorizedAccessException`——这两种异常原来的代码完全没catch，
  会直接从`LoadOrCreate`里穿透出去；Terminal/Caster两边`Program.Main`里构造`_identity`都是
  启动阶段最早期的一步，这个异常会让整个进程在真正开始跑之前就直接崩溃退出，比这份清单里其它任何
  "某个子系统悄悄死掉"的bug都更严重——那些好歹进程还活着，这个是根本起不来。更麻烦的是就算加了
  catch，如果照搬"文件损坏就下面`Directory.CreateDirectory`+`File.WriteAllText`兜底重新生成一份
  新身份"这条已有的处理路径，会把一个只是暂时被锁住、内容其实完全正常的身份文件直接覆盖成一个
  全新的`Guid`——这台设备此前跟别的设备建立的所有配对/信任关系，会因为一次纯粹偶发的文件锁竞争
  就永久失效，比进程崩溃启动失败更隐蔽也更难排查（表现为"配对好的设备突然不认识了"，而不是一次
  明显的启动崩溃）。**修复方式**：新增一个单独的`catch (Exception ex) when (ex is IOException or
  UnauthorizedAccessException)`分支，只返回一个不落盘的临时内存身份供这一次运行使用，不执行
  `File.WriteAllText`——真正的身份文件保持原样不动，等下次重启时（届时占用锁大概率已经释放）
  还有机会被正常读到。`JsonException`分支的"确实损坏就重新生成并覆盖"这个既有行为本身没有改动，
  只是不再跟"暂时读不出来"共用同一条处理路径。**没有做的部分**：这次改动本身没有在这个沙箱里
  跑过（没有dotnet），没有真机验证过；`Directory.CreateDirectory`/`File.WriteAllText`这两行本身
  在"文件从未存在过"（第一次运行）这条路径上如果失败，这次没有额外处理，这次改动范围只到"文件
  已存在但读不出来"这一种场景。
- **【新发现的真实bug，已修复】`DeviceIdentity`原来的两处写入（`LoadOrCreate`里首次生成身份、
  `Save()`里保存改名）都直接用`File.WriteAllText`，是这个仓库里唯一没有跟其它JSON存储用同一套
  原子写入约定的一处**：`FileLibraryStore`/`ScenarioRepository`/`SettingsStore`/
  `PairedDeviceStore`/`PairedTerminalStore`这五个存储全部都是"先写到`.tmp`临时文件，再用
  `File.Replace`（首次保存时用`File.Move`）原子性地换到正式文件名"这套写法，唯独
  `DeviceIdentity`这两处直接`File.WriteAllText`到正式文件名——`WriteAllText`语义上等价于先
  截断文件再写入，如果写入过程中进程崩溃/断电，正式文件会被留在"已经截断、内容还没写完"的
  中间状态。这一点对`DeviceIdentity`而言比对其它五个存储更值得在意：这份文件就是上面那条
  `LoadOrCreate`修复专门想保护的"这台设备此前跟别的设备建立的所有配对/信任关系"所系的那份身份
  数据，如果真的被写坏，下次启动时`LoadOrCreate`要么撞上`JsonException`分支、要么解析出一个
  字段不全的对象，两种情况都会重新生成一个全新`Guid`、永久丢失所有已配对设备对这台机器的
  信任——跟上面那条修复要防的是同一种后果，只是触发原因从"读的时候被锁住"换成了"写的时候没写完"。
  **修复方式**：新增私有的`WriteAtomic`方法，跟其它五个存储用完全一样的"先写`.tmp`、再
  `File.Replace`/`File.Move`"套路，`LoadOrCreate`和`Save()`两处都改成调用它。**没有做的部分**：
  这次改动本身没有在这个沙箱里跑过（没有dotnet），没有真机验证过。
