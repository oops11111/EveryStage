# 实施进度（2026-11-03）

清单基线：e7e7e916ba2d05f9a08a315b7f892dadb1bb616d；本地已快进对齐。审议清单为用户提供的总实施清单，引用的八轮原文未随附件提供，不推测其额外要求。

## 版本保护

旧版演讲者窗口等本地修改保存在 Git stash：`archive local presenter implementation before e7e7e91 alignment 2026-11-03`。旧审核文档和旧构建保留为历史资料，不代表当前实现。不要直接 stash pop：远端已改变原生 PPT 的通道归属。

## 本批实现

- R1-A：以远端 Program 原生放映、Preview 静态浏览、监看栏读取 Program 为当前模型。归档旧窗口并纠正文档；真实双屏验证仍待执行。
- R1-B：两类 SelfTests 增加独立线程池看门狗与十秒心跳（存活不等于进度），默认 Core 180 秒、Windows 600 秒；可用 `ES_SELFTEST_TIMEOUT_SECONDS` 覆盖。超时 FailFast 非零退出；CI 启用 .NET minidump，上传日志和可获得的转储。job 30 分钟，Core/Windows step 分别 5/12 分钟。进程完全失去调度、操作系统终止或转储失败时仍由 CI step 外层截止兜底，不能保证任何环境都生成转储。
- R1-D：README 和部署说明明确媒体认证不等于保密加密。
- R3-P0-1：MainWindow 的 Shift+Esc；TextBox 焦点亦处理。第二批新增独立消息窗口的 RegisterHotKey（默认关闭、设置中即时开关、冲突提示、关闭/退出注销），以及应用级消息过滤支持本应用模态窗口的本地快捷键。全局消息窗口不依赖主窗口是否启用或可见。物理按键、失焦和双屏 T1 仍待真机。
- R3-P0-2：标题栏常驻“■ 停止输出”，DangerFill #B42830/白字，禁用不隐藏；后续角色化主题与混合 DPI 验收仍待执行。
- R3-P0-3（部分）：标题栏下 ON AIR 通栏。卡片红描边闪烁和成功 toast 尚未完成。
- R6-P0（首批）：停止入口 Disconnect 并停止 Program、Preview 和独立音乐，清理引擎已有自动推进计时器/加载版本；不提供急停撤销。网络设备、加载竞态、PPT/PDF 自动翻页和对话框路径仍需专项验证。

## 验证纪律

不把历史文档的“通过”当作本批结果，不把构建成功当成运行验收。

## 本轮实际结果

- Terminal、WindowsSelfTests、CoreSelfTests Release 严格构建通过，0 警告、0 错误。
- 核心自检通过，日志 `artifacts/test-diagnostics/core-final.log`。
- 原有停滞复现：旧的样本写法在外部打开用例之后不再推进，本轮由看门狗在约 181 秒终止，非零退出；日志 `windows-local.log`。
- 修复测试样本生成：`MediaFixtures.WriteVideo` 原先写完所有视频才写音频，长素材可能被封装器背压阻塞；现改成逐帧交错写入音频，按采样索引保留时间戳。
- 修复后的 Windows 完整自检在本机约 110 秒通过，覆盖真实音视频、模拟音频输出丢失、配对 v2、PDF、WPS 编辑、PowerPoint 原生放映/静态浏览/降级、界面启动与 AAC 往返；日志 `artifacts/test-diagnostics/windows-retest.log`。其后安全颜色、专项选择输出文案、外部转储调用的增补完成了最终严格构建，急停专项另行复跑；外部转储收集工具尚未在 CI 实际执行，不能宣称已取得 CI 转储。
- 一秒 deadline 反向验证通过：输出 TIMEOUT 并以 -2146232797 退出，日志 `watchdog-probe.log`。
- 急停专项验证 TextBox 焦点以及带计时器的循环图片停止后不再自动启动，最终运行日志 `operator-final.log`。专项只运行操作界面相关检查，未将其误写成完整自检结论。
- 截图产物 `artifacts/screenshots/`，已检查小窗口舞台配色设置页；禁用急停按钮保持可见。
- 本批仍为本地修改，未推送；当前提交对应的 CI 未运行。单机窗口测试不替代 T1 双屏/失焦/模态/设备投屏真机验收，MKV/AVI 真实素材矩阵仍待执行。

CI 的超时诊断另配置 .NET 8 系列 dotnet-dump：在 FailFast 前尝试 Mini dump，最多等待 20 秒，失败仍非零结束；runner 无法调度时由 step 超时兜底。转储可能含素材/路径等进程内数据，诊断产物应仅用于授权排障。

## 后续顺序

1. 在 CI 复核本批日志/超时诊断，并补充硬件环境验证；本机样本生成停滞已修复。
2. 补全上屏反馈并做急停 T1；全局配置/失败提示与模态消息路径的代码和专项已补齐。
3. 按清单 Sprint B 统一角色 Token、主题名称和状态色。
4. 按后续 Sprint 推进出帧、音频设备、版本源与 NSIS、自签名外部条件、布局和素材管理。

OV 代码签名需要用户持有的有效证书/签名服务；目前不具备该外部条件，不能承诺 SmartScreen 声誉或冒称已签名。

## 第二批：全局急停

- 新增 `UI/EmergencyStopHotkey.cs`：独立消息窗口接收 WM_HOTKEY，Shift+Esc + MOD_NOREPEAT；不开启键盘钩子、不记录其他按键。应用级 IMessageFilter 处理本应用消息循环（包括模态）的 Shift+Esc。
- 配置 `GlobalEmergencyStopEnabled` 默认 false，旧配置自然保持关闭，Clone 保留字段。设置页新增“急停快捷键”区块，切换即时保存、生效；保存失败恢复勾选且避免回滚递归。
- 注册失败显示错误码及关闭占用后重试的提示；用户关闭设置或主窗 Dispose 时释放注册和消息过滤。
- Terminal/WindowsSelfTests Release 严格构建通过，0 警告、0 错误。
- 专项日志 `artifacts/test-diagnostics/operator-hotkey.log`：验证默认不注册、冲突提示、注销释放、重试，以及模拟已投递 WM_HOTKEY 在禁用 owner 的模态循环中停止输出；不是物理按键注入测试。其余整套媒体用例本批未重跑。

## 第三批：上屏反馈与主题第一步

- 本地 Program 进入 Playing/Paused 且输出源有效时才提示“已上屏”，按文件对象去重；Loading/Failed 不误报。设备预览切到正式上屏时也反馈，重复状态更新不重复提示。
- 文件卡片和来源卡片约 150ms 的红色描边；后者沿圆角绘制。UI 定时器需消息循环调度，不能当作严格实时指标。新增 Toast Info，避免把成功上屏显示成警告。
- 专项发现状态回调可能来自后台线程，已在 MainWindow 状态监听中切回 UI 线程再启动反馈定时器。
- Palette 新增 AccentFill/DangerFill/ProgramBorder/Preview/PreviewForeground；深色填充 #285ABE、危险填充 #B42830、Program 描边 #C6425E、危险文字 #E8687F、Preview #22C55E。舞台强调填充 #0B5E8E、危险填充 #B42838。浅色预览采用较深绿以维持标签白字可读性。
- 经 ModernUi.StyleButton 建立样式的按钮记录显式 primary/danger 角色，主题切换按角色恢复填充与文字色；**其余旧控件仍有旧色反查，ThemeManager 全量迁移尚未完成**。
- 文件卡片 Preview 绿、Program 红、选中蓝；监看预览边框/徽标改绿、正式输出来源改红；标题栏输出状态文字改红。预览/正式输出的媒体内容不随 UI 色板改变。
- 可见主题卡片顺序为“跟随系统 / 浅色 / 深色 / 舞台”，保留 Light/Dark/Tech/System 原枚举数值和配置兼容；System 不跟随强调色。
- 严格构建通过。日志 `artifacts/test-diagnostics/operator-feedback-theme.log`：主题反复切换后 primary 角色不漂移、主按钮/危险按钮白字对比度 ≥4.5:1、急停、失败加载不误报、成功输出触发反馈、描边终止、界面截图、歌单持久化和过场专项通过。末尾圆角描边/急停按钮角色及文案小改另做最终构建，未重复整套媒体测试。
- 截图在 `artifacts/screenshots/`，检查了 `1024x640-Tech-settings.png` 的 ON AIR 通栏和主题名称。本轮截图是测试设置的状态，不作为系统外观/混合 DPI 真机验收。
- 待继续：Windows 高对比度强制接管、全部控件角色化迁移、固定深色监看、其余 Focus/OnAir 等 Token、Mica 边界及 T1 真机验证。尚未推送/运行本批 CI。
