# 全项目审查整改与验收

目标：落实设计、架构与代码审查建议。编译通过不等于运行验收通过。

## 已实现且有针对性测试

- 实时视频待显示队列限制为 3 帧（另有 1 帧正在显示）；积压丢弃旧帧并释放纹理，显示线程失败后拒绝新帧。队列容量、顺序、关闭与晚到帧恰好释放一次的回归测试通过；真实 GPU 压力测试待验收。
- 文件库、方案读取失败后禁止覆盖保存：核心自测真实独占文件锁。
- 设置与已配对设备存储也已禁止读取失败后用默认值覆盖原文件，并在保存失败时回滚内存变更；独占文件锁回归测试通过。
- 投屏机已配对终端存储 `PairedTerminalStore` 补齐与终端 `PairedDeviceStore` 一致的三重保护：读取失败（瞬时文件锁）后置 `_loadFailed` 禁止写回覆盖、保存失败回滚内存快照、临时文件 `Flush(flushToDisk)` 落盘后再原子替换（对应审计 2026-09-27 追加的 A-23 数据丢失、B-18 落盘、C-26 自检缺口）。`PairedTerminalStoreSelfTest` 新增「用降级加载后的同一实例 Upsert 必须被拒绝、磁盘原数据不丢」断言——实测：修复后自检 PASS；临时移除 `_loadFailed` 复现 A-23 后该断言 FAIL（证明非重言式）；恢复后 PASS。Caster 项目 `dotnet build` 0 警告 0 错误。
- 设备身份 `DeviceIdentity.WriteAtomic`（B-18 另一半）：弃用无法落盘的 `File.WriteAllText`，改经 `FileStream` 写入后 `Flush(flushToDisk: true)` 再原子替换，并补临时文件失败清理——补齐它此前只对齐「原子重命名」、漏掉「落盘」的那一半。`DeviceIdentitySelfTest`（创建/重载/改名保存重载/损坏回退/锁定回退全链）实测 PASS，确认 FileStream 写 → ReadAllText 读往返正常；Discovery/Terminal/Caster 三项 `dotnet build` 均 0 警告 0 错误。仍待真机的仅断电落盘窗口本身（机制与四个 Data store 同法）。
- 设备身份降级写保护（A-23 附带）：`DeviceIdentity` 读失败（瞬时锁）返回的临时身份新增 `_loadDegraded` 标记，`Save()` 遇标记即抛 `IOException` 拒绝覆盖——堵住「瞬时锁降级后、操作员同会话改名 `Save()` 用随机 DeviceId 覆盖真身份→所有对端信任失效」。`DeviceIdentitySelfTest` 新增对称断言（对降级实例改名 `Save()` 须被拒绝、原文件不丢）；实测修复后 PASS，临时移除标记复现后该断言 FAIL，恢复后 PASS。
- RTP 缺包后无后续流量时释放缓存：真实 UDP 回环测试。
- 媒体包认证与会话隔离：每次投屏独立会话，音视频分离派生密钥；认证、重放及 UDP 收发测试。真实双机投屏待验收。
- 图片后台解码：真实图片解码、快速切换、坏文件、文件释放、加载中关闭测试。
- PDF 首屏后台渲染：真实 PDFium 加载、双页导航、加载中关闭、文件释放测试。复杂文档及翻页性能待验证。
- 本地媒体解码与投屏音频整改（审计第三十～三十二节，2026-10-03）。WindowsSelfTests 新增第 2b/2c、7～9 节与 `CastAudioTests`，CoreSelfTests 新增下混/格式转换单测，全部实测通过：
  - 音频输出端点消失（A-24）：`AudioPlaybackClock` 识别「非我方请求的停止」为故障并冻结最后位置；视频改墙钟无声续播并记日志、Preview 提示；独立音频出错误卡；投屏降级为纯视频（不再 1 帧/秒）。测试：中途断开后 1.5 s 内 45 帧、位置 +1.51 s。
  - 无输出设备 / 多声道（B-20）、寻址时设备消失（B-21）、音频错误不再拆除整场投屏（A-16）：视频无声播放并给原因；5.1/7.1 统一下混为立体声（本地、终端接收、投屏端采集三处）；寻址失败转错误卡，不再逃逸到 UI。
  - 解码中途媒体类型变化（A-25）：处理 `CURRENTMEDIATYPECHANGED`，音频转换回时钟格式，视频按显示孔径取尺寸。真实 AC-3 文件（声明 2ch、实际 6ch）实测：3000 块时间戳连续、0 重叠（此前每块重叠）。
  - EXIF 方向（B-23）、解码跟不上时画面冻结（B-24）、渲染静音计入位置与音频缺口/重叠（B-22）：照片按方向显示；画面至少每 250 ms 刷新并记日志；按时间戳补静音/截重叠，播放位置只计内容。
  - 投屏音频此前在本机从未工作（新增 A-26）：AAC 负载类型 GUID 写错、解码器缺 `MF_MT_USER_DATA`、编码器取属性 `E_NOTIMPL` 与输出/输入类型设置顺序错误，加上解码器首个输出类型为 32 位浮点；以及 A-6（两个 AAC MFT 均不自带输出样本，实测 flags=0x1）。修复后编码→解码往返：ADTS、440 Hz 正弦完整（881 过零/秒）。
  - 其余：A-17 拆除顺序、C-16 呈现失败归属、C-27 `ResizeBuffers` HRESULT、C-28 解码样本保留到呈现后、C-29 大图缩放缓存（48 MP 重绘 1.4 ms）、C-30 编码名与多音轨回退（REMUX 文件改用 AC-3 音轨出声）、C-31/C-32 软件解码与 10 位素材的明确提示及 BT.709 色彩矩阵、C-33 音频尾部不再截断、C-34 投屏不再显示对齐填充行。
  - 仍待真机：真实拔插 HDMI/USB 音频设备、双机投屏音画（含 C-34 观感、BT.601/709 色彩）、软件解码格式与 10 位 HDR 文件、C-28 驱动池复用顺序。
- 早期审计剩余条目整改（审计第三十三节，2026-10-04）及 B-15 补充整改（审计第三十四节，2026-10-05）：
  - 投屏丢包恢复（A-18/A-19/A-20/C-17）：重排缓冲一看到缺口就发 NACK，并在等待期内保持缺口，重传包可按序补上；每个访问单元结束时发部分块 FEC 校验。新增真实节奏（15 ms/包）经丢包中继的回环测试：无 FEC 靠 NACK、有 FEC 靠校验，均 7/7 补齐、0 丢失；关掉提前上报的变异版本会使测试失败。
  - 投屏音画同步（A-2/A-3/B-8/B-9/B-12）：视频时间戳取采集时刻（穿过编码器）；音频帧按首样本的采集时刻打戳（`PcmTimeline`，跨编码器缓存与静音间隙准确）；接收端在 PCM 入缓冲时采样偏移，并在开局和音频间隙后快速收敛。
  - 投屏端健壮性（A-1/C-1/B-1/B-2/B-3/A-21/C-19/C-20/A-13/A-14/B-6/C-4）：空访问单元不再泄漏背压计数；NACK 绑定会话；启动失败自行清理并补发 cast_stop；关窗也通知终端；编码循环死亡后停止采集；设备丢失检测；热插拔显示器时按设备名恢复选择；超时提示点出时钟偏差。
  - 终端（A-15/C-11/A-22/B-14/C-21/C-22/B-16/A-10/C-7/C-5/A-12/B-11）：投屏结束都记原因；签名消息被拒时记录两端时钟；配对请求限流、截断、一次一个对话框；「自动接受」如实标注为未实现；状态机订阅者异常进崩溃日志；VideoSurface 释放取锁；开关检查原子化；断开路径清理补齐；计时器不再孤立；越界枚举有兜底；坏背景音频不再紧循环。
  - 日志与保存（A-4/A-5/B-4/C-2/C-3/A-7/B-7）：跨日清理、写入失败计数并在诊断页显示、序列化异常不外抛、导出原子化；活动面板保存失败时提示并从磁盘恢复。
  - B-15（配对密钥明文、无 PIN）已改为配对协议 v2：Caster/Terminal 用 P-256 ECDH、nonce commitment/reveal、HKDF 派生 32 字节配对密钥和 6 位核对码；终端 `pair_response` 只回 `KeyConfirmation`，不再下发明文密钥，v2 成功应答的 JSON 也不会携带空的 `PairingKey` 字段。旧 v1 key 已作废，升级后需重新配对一次。

## 已实现但尚未完成验收

- 本地文件解码 `ReadSample` 空样本不再当流结束（审计 A-9）：`VideoDecodeSource`（视频/音频两处）与 `AudioDecodeSource` 改为遇 `sample==null` 且未置 `EndOfStream` 时有界重试到下一个真实样本（`MaxEmptyReadRetries=128` 防病态源死循环），仅真正 `EndOfStream` 才返回 null——修掉「开头一次 STREAMTICK 就把整段判为播完、且日志记为正常完成」。Rendering/Terminal/Caster 编译 0 错误、CoreSelfTests 全通过；真机需一个含流内不连续（VBR MP3/转封装 MP4）的文件验证不再截断。
- 音频拖动进度配速用错时间基（审计 B-17）：`AudioContentController` 配速等待改为 `PositionTicks < chunk.TimestampTicks - _seekBaseTicks - AheadBudgetTicks`，与 `CurrentPosition`/淡变换算一致——修掉 seek 到 T 后播放挂起/长静音+读数错位。编译通过；真机需拖动音频进度条验证即时续播、读数正确。
- 音频接管结算窗口竞态（审计 B-19）：`AudioTakeoverService` 引入 `_takeoverGeneration`，`TakeoverAsync` 结算延迟后校验代次、`Restore` 递增使在途接管失效——修掉「起投 300ms 内断开→静音在 Restore 之后发生、会话卡静音至下一周期」。编译通过；真机需起投后立即断开验证不留卡静音。
- UI 保存失败一致性（审计 C-25）：`SettingsPanel`（保存设置、保存设备名）、`MainWindow`（Toast 两条移除路径）、`DevicesPanel`（移除配对、编辑权限）六处保存/Upsert/Remove 全部对齐 `FilesPanel` 写法——包 `try/catch(IOException/UnauthorizedAccessException)` + 贴切 MessageBox + 失败回滚内存改动（设备名/权限/活动移除）+ 成功后再记日志。修掉「终端上保存失败只落 CrashLogger、无弹框（部分假成功）」。Terminal 编译 0 错误、CoreSelfTests 全通过；真机需构造保存失败（只读/占满 ProgramData）验证提示与回滚。
- 采集纹理错误路径泄漏（审计 C-24）：`ScreenCaptureSource.AcquireNextFrame` 的 `CopyResource` 包 try/catch，抛出即 `copy.Dispose()` 后重抛——修掉「逐帧路径上 CreateTexture2D 成功但 CopyResource 抛出时全屏纹理泄漏」。Caster 编译 0 错误；错误路径本身待真机诱发验证。
- 审计 C-23（`VideoProcessorBlt` 吞 HRESULT）**经编译验证不成立、已撤销**：用 R4 同法加 `.CheckError()` 编译报 `CS0023`（该调用返回 `void`，无可丢弃的 Result），无需修复；审计文档 C-23 条已标注作废，C 级计数由 26 回退为 25。
- 音频回调与音频资源释放互斥，停止后的晚到回调直接返回，避免接收线程停止超时后访问已释放资源。严格构建通过；真实音频设备阻塞及快速重连测试待验收。
- Terminal 默认 960×720、最小 800×600；普通窗口拖拽和工作区适配保持 4:3，小屏优先降低最小尺寸以完整显示。最大化保持铺满屏幕。100%/125%/150%/200% DPI、小屏与双方向尺寸计算回归通过；实际拖拽、跨屏与恢复窗口仍待实机验收。
- Terminal 已按当前页面设计统一为 168px 玻璃侧栏、品牌区、图标+文字横向导航、24px 外边距与 14px 内容间距；活动/设备/设置页面同步采用深色卡片、表格和标签页样式，代码和 Windows 内容自测通过，Windows 10 实机视觉仍待验收。
- 投屏开关关闭时，文件点击/Preview 不再静默丢弃；开关不管理网络内投屏机请求，关闭期间投屏机仍可进入设备源窗口；但若设备画面已经上 Program，关闭开关会把它退回静音预览，并恢复设备投屏前的大屏内容或空闲状态。开启时本地内容仍可走 EveryStage 扩展屏输出链路。
- 导航滚动、音频条紧凑布局、播放与进度点击、活动音频源匹配。音频条离屏布局、空格播放事件、组合键隔离和 L 键循环设置持久化已通过测试；真实键盘焦点遍历及实际播放仍待验证。
- 缩略图后台解码、取消及 128 项缓存。需要大文件库压力与资源占用测试。
- 预览独立持有等比例缩略图；玻璃面板父背景合成。需要视觉对照验收。
- Windows 内容回归测试已接入 CI 配置；本地完整构建、核心/Windows 自测和发布包校验通过，推送后仍需观察远端 CI。
- Terminal/Caster Release 已完成启动冒烟：窗口创建成功且进程响应正常；原生窗口像素级视觉仍需人工对照。

## 尚未完成

- 设计图逐项对照；所有自绘控件 DPI、一致的布局断点、键盘可访问性。
- 桌面模糊玻璃样式及 Windows 10 的实机视觉确认。Caster/Terminal 已接入 Windows 合成 Acrylic，失败自动降级为 Blur-behind，绘制层保留渐变边框；不同 Windows 10 版本和显卡驱动仍待实机确认。
- 视频真实缩略图及无内嵌封面的 Office 文档预览。视频已接入 Windows Shell 缩略图提供器并安全回退；PDF 首屏缩略图和现代 Office 内嵌封面已实现并有测试；旧格式和无封面文档仍使用类型占位图。
- PDF 复杂文档翻页性能、长时间运行及 UI 实机验收（后台翻页、切换文件后拒绝旧结果、关闭中回收和原始页面比例已实现并通过原生 PDFium 回归测试）。
- 完整码率控制仍未完成（硬件编码器运行中动态改写码率尚未实现）；已实现认证 NACK 闭环：重排器报告缺失序号，终端通过认证控制通道请求最多 64 个视频包，Caster 从 512 包短期缓存重新认证封装并重发；同时根据丢包率、RTT、视频错误、发送队列积压和编码器丢帧动态降低采样频率（1/2/3 帧步进），连续健康报告后逐级恢复。已加入 8 包 XOR FEC 单包恢复，核心算法和真实认证 UDP 丢一包回环测试通过；NACK/FEC 的真实双机丢包恢复率仍待网络测试。
- 会话启动确认的真实双机丢包/超时/关闭回归、认证接收资源生命周期复审。已接入签名就绪确认、5 次有界重试、同一请求重复确认而不重启接收器、失败原因显示；仍需模拟双端集成验证。协议版本能力字段与 Caster 不兼容提示已实现，真实新旧版本互操作仍待验证。
- 提取共享 UI/媒体服务，降低 MainForm 与 PlaybackEngine 的职责耦合。
- Windows 10、100%/125%/150%/200% DPI、多屏热插拔与实际音视频/WPS 回归。
- 1080p/4K 延迟、音画同步与连续 1 小时资源稳定性验收。
- 最终严格全项目构建、全部测试、打包、提交推送、远端 CI 和交付版本对齐（本地构建/测试/打包已通过；`aed2d23` 已推送；GitHub Actions Run `35688171417` 已成功）。

## 协议升级说明

认证媒体协议版本为 1。新版终端拒绝未提供该版本和会话标识的投屏请求。配对协议版本为 2：Beacon 广播 `PairingProtocolVersion=2`，`pair_request` 携带 Caster 公钥与 nonce commitment，终端返回 `pair_challenge`，Caster 再发 `pair_reveal`。两端各自派生相同 K 与 6 位码；终端接受后只回 HMAC key confirmation。旧版投屏机收到「投屏机程序版本过旧，请更新后重新配对」，新版投屏机看到旧终端会提示「需要更新程序」。两端需同步升级；所有旧配对密钥格式版本 1 均不再接受，需重新配对。

## 最新本地验证补充

- 通用 ACK 重试不再将失败或取消的确认任务当作成功；提前结束与发送中结束场景均有回归测试。终端接收器 Start 失败路径补充释放已创建接收器。投屏启动就绪确认已接入，双机丢包/关闭场景仍待集成验证。
- Windows 窗口消息测试：四边、四角共 8 个拖拽方向保持 4:3，并保持对边锚点。测试直接调用窗口消息处理，不等同于用户鼠标拖拽、跨屏或实机视觉验收。
- 认证 NACK/FEC 回环集成测试：真实 UDP 中继分别验证首包丢失后的认证重发，以及 8 包块中丢失 1 包后的 FEC 恢复；恢复结果未计入丢包；RTP 与发现服务停止时均改为接收循环结束后再释放 socket。
- 设备投屏窗口截图：`ui_snapshot` 已按 waiting / preview / program 三种状态、960×720 与 1256×942 两个尺寸生成，并执行 SMOKE；SMOKE 会断言 waiting 小卡片文案且无操作按钮、preview 显示「投屏」、program 显示「回退」。正确产物位于 `C:\Users\admin\everystage\verify-all\ui_snapshot\device-cast-shots-fixed\`；总览联系表位于 `C:\Users\admin\everystage\verify-all\ui_snapshot\device-cast-contact-sheet.png`，并已通过当前会话图片发出给用户确认。2026-10-06 又用当前源码复跑 960×720 waiting / preview / program 三种 `SMOKE=1`，输出位于 `C:\Users\admin\everystage\verify-b15-switch-retract\ui_snapshot\`，三种状态均 `SMOKE OK`；随后新增 `scripts/Run-B15LocalVerification.ps1` 串起 CoreSelfTests、WindowsSelfTests、9 项严格构建和三种 ui_snapshot smoke。脚本的 UI-only、CoreSelfTests-only、WindowsSelfTests-only、StrictBuild-only 路径均已验证通过，输出分别位于 `C:\Users\admin\everystage\verify-b15-script-ui\`、`C:\Users\admin\everystage\verify-b15-script-core\`、`C:\Users\admin\everystage\verify-b15-script-windows\`、`C:\Users\admin\everystage\verify-b15-script-build\`；完整本机验证产物已汇总到 `C:\Users\admin\everystage\verify-b15-script-full\`，包含 Core、Windows、严格构建和 UI smoke 四类输出。脚本默认在输出根目录写 `verification.log`，也支持 `-TranscriptPath` 指定日志位置；自管日志写入已用 `C:\Users\admin\everystage\verify-b15-log-capture-smoke\verification.log` 冒烟验证，确认包含子进程 PASS 行。旧目录 `device-cast-shots` 是一次参数传错的中间产物，不作为验收截图。
- 最新完整 B-15 本机门禁已用 `scripts/Run-B15LocalVerification.ps1 -OutputRoot C:\Users\admin\everystage\verify-b15-operator-full -NoRestore` 复跑通过：CoreSelfTests、WindowsSelfTests、9 项 Release `-warnaserror` 严格构建、waiting / preview / program 三种 UI smoke 均通过；`verification.log` 已改为脚本自管日志，包含子进程 PASS / 构建 / SMOKE 明细，位于 `C:\Users\admin\everystage\verify-b15-operator-full\verification.log`。上一轮完整门禁 `C:\Users\admin\everystage\verify-b15-final-local-logged\` 保留为历史记录。
- 新增 `scripts/Prepare-B15AcceptancePackage.ps1` / `scripts\Verify-B15AcceptancePackage.ps1` / `scripts\Test-B15AcceptanceArchive.ps1` / `scripts\Collect-B15AcceptanceEvidence.ps1` 辅助双机验收交付：从最新门禁目录复制 Terminal / Caster 构建、验收文档、验收结果表、现场交接单、清配对脚本、memory 草稿和 `verification.log`，生成 `manifest.json`、`SHA256SUMS.txt`，并默认压缩为 zip，避免现场验收混用不同构建。已用当前最新门禁产物生成并校验，输出位于 `C:\Users\admin\everystage\b15-acceptance-package\`，zip 位于 `C:\Users\admin\everystage\b15-acceptance-package.zip`，外层 zip SHA-256 位于 `C:\Users\admin\everystage\b15-acceptance-package.zip.sha256`，解压前 sidecar 校验脚本位于 `C:\Users\admin\everystage\b15-acceptance-package.verify.ps1`，外层快速说明位于 `C:\Users\admin\everystage\b15-acceptance-package.README.txt`；zip 内 81 项，包含 `Terminal\EveryStage.Terminal.exe`、`Caster\EveryStage.Caster.exe`、`docs\B15-FIELD-HANDOFF.md`、`docs\WINDOWS_ACCEPTANCE.md`、`docs\B15-ACCEPTANCE-RESULTS.md`、`docs\EVERYSTAGE-AUDIT-STATE-MEMORY-DRAFT.md`、六个验收脚本、`manual-evidence\README.txt` 和 `SHA256SUMS.txt`；sidecar `b15-acceptance-package.verify.ps1` 已验证外层 zip SHA 和包内 81 个文件全部 OK。`Verify-B15AcceptancePackage.ps1` 还会断言 `manifest.packageFileCount`、实际文件数和 `SHA256SUMS.txt` 行数一致；反向烟测向临时包加入未列入清单的 `UNLISTED-FILE.txt` 后，脚本以退出码 1 拒绝并报告 actual=82 / SHA256SUMS=81；把 `manifest.packageFileCount` 改成 999 并同步 manifest 哈希后，脚本仍以退出码 1 拒绝并报告 manifest=999 / SHA256SUMS=81。证据采集脚本已用当前包冒烟通过，且包校验失败时会保留证据 zip 并以非 0 退出码提醒现场人员；正常路径输出为 `C:\Users\admin\everystage\b15-acceptance-evidence-smoke-handoff\20261006-180830-terminal-DESKTOP-K93N3P8.zip`，包含 package 记录、现场执行的 `package\package-verify.txt`（81 files OK，含 `docs\B15-FIELD-HANDOFF.md`）、ProgramData 日志、系统信息占位和 `extra\manual-evidence\README.txt`；反向烟测篡改临时包 `README.md` 后脚本退出码为 1，同时生成 `C:\Users\admin\everystage\b15-acceptance-evidence-corrupt-smoke\20261006-131150-terminal-DESKTOP-K93N3P8.zip`，其中 `package\package-verify.txt` 明确记录 `README.md` hash mismatch。
- 新增 `scripts/Reset-B15PairingState.ps1` 辅助双机验收前清旧配对：默认 dry-run，只在加 `-Apply` 时备份并删除 `%ProgramData%\EveryStage\paired-devices.json` 与 `caster-paired-terminals.json`。已用临时数据目录验证 dry-run 不删除、Apply 后源目录清空且备份 2 个文件，输出位于 `C:\Users\admin\everystage\verify-b15-reset-smoke\`。
- 设备投屏编排已从 `Program.cs` 拆入 `src/Terminal/EveryStage.Terminal/Receiving/DeviceCastController.cs`：连接、预览 surface、上 Program、回退、断开、状态上报、liveness/decode health 和质量日志都由 UI 线程控制器维护；`Program.cs` 只保留事件 marshal 与生命周期装配。控制器依赖 `IDeviceCastStream` / `IDeviceCastTransport`，生产路径由 `CastReceiverStream` / `DiscoveryService` 适配，WindowsSelfTests 用 fake stream/transport 覆盖「连接先进设备窗口、按投屏才上 Program、投屏开关不管理网络投屏机请求但关闭时撤回已上大屏设备流并恢复原 Program 状态、CastStatus 用终端 DeviceId 且 OnProgram 正确区分预览/大屏、回退/断开/10 秒无包/解码或呈现坏死恢复、同设备重连直接接回 Program 且保留回退点、本地 Take 让设备退回预览且不断开、第二台设备先 Pending，拒绝不影响旧设备，接受会恢复旧设备投屏前内容并让新设备只进预览、空闲回退仍空闲」；另有真实 `CastReceiver` 双 shared-GPU `VideoSurface` Attach/Detach 无帧冒烟测试，视频解码器延迟到 `Start()` 初始化后已在当前机器实际通过。CoreSelfTests 还断言 v2 成功 `pair_response` JSON 不携带 `PairingKey` 字段，只携带 `KeyConfirmation`；配对码必须是 6 位数字；`RequestId`、Caster DeviceId、Terminal DeviceId 任一不一致都会让派生密钥、数字码和 KeyConfirmation 验证失败。WindowsSelfTests 新增真实 Caster `TerminalDiscoveryClient` ↔ Terminal `DiscoveryService` UDP 配对 v2 端到端测试：旧请求拒绝、新 request/challenge/reveal、两端验证码一致、接受后无明文 key、Terminal 持久化 v2 key、Caster 得到同一派生 key、重复 reveal 缓存应答；同一测试还覆盖新 Caster 看到旧 Terminal 能力时直接返回「需要更新程序」且不显示验证码，以及 Caster 收到 Terminal DeviceId 不一致的 `pair_challenge` 时拒绝且不显示验证码。测试用临时 UDP 端口，不受正在运行的 EveryStage 占用 47990 影响。Caster/Terminal 的发现端口覆盖只用于测试注入，生产默认仍是 `DiscoveryProtocol.Port`。最新完整本机验证输出位于 `C:\Users\admin\everystage\verify-b15-operator-full\`，其中包含 9 项 Release `-warnaserror`、CoreSelfTests、WindowsSelfTests 和 ui_snapshot SMOKE，详细 PASS/build/SMOKE 记录见该目录的 `verification.log`。
- 针对投屏开关语义又追加一次补充验证：`RetractForCastSwitchOff()` 现在走同一回退恢复路径，而不是丢弃回退点后让 Program 变空闲；`OutputStateMachine.SetCastSwitch(false)` 会在通知前记录原始输出源，避免设备回退恢复出的本地 Program 被同一次关开关误断。CoreSelfTests、WindowsSelfTests、Terminal `Release -warnaserror`、WindowsSelfTests `Release -warnaserror` 均通过，输出位于 `C:\Users\admin\everystage\verify-b15-switch-restore\`。
- WindowsSelfTests 继续补充 Caster 收到未签名 `cast_start_rejected` 的回归：假终端收到 Caster 用旧 v2 key 签名的 `cast_start` 后回未签名拒绝；`not_paired` 必须删除本地 stale pairing 并向 UI 抛出「重新配对」错误，从而触发自动转入配对；`not_allowed` 只作为普通拒绝显示，不能删除本地密钥。该补充测试已通过，输出位于 `C:\Users\admin\everystage\verify-b15-rejections\WindowsSelfTests\`；同一测试项目 Release `--no-restore -warnaserror` 严格构建也已通过，输出位于 `C:\Users\admin\everystage\verify-b15-rejections-build\WindowsSelfTests\`。
- 配对确认框与第二台设备确认框均由 `OperatorScreen.PlaceOnOperatorScreen` 在操作屏定位；`DeviceCastRequestDialog` 已显式设为 `FormStartPosition.Manual`，避免未来绕过定位辅助函数时退回系统默认屏幕。配对框注释同步改为配对 v2 的 6 位数字比对语义。针对这次 UI 安全修补，`EveryStage.Terminal` Release `--no-restore -warnaserror` 已通过，输出位于 `C:\Users\admin\everystage\verify-b15-dialog-safety\EveryStage.Terminal\`。
