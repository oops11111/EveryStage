# Windows 真机验收记录

## 当前环境

- 日期：2026-09-20
- 显示器：1 台，1280×1024（`DISPLAY6`）
- WPS COM ProgID：`KWPS.Application`、`KET.Application`、`KWPP.Application`
- 仓库内测试素材：没有 1080p/4K 视频样本
- 媒体生成工具：未发现 ffmpeg
- 显卡/驱动信息：当前执行账户读取 `Win32_VideoController` 被拒绝

## Release 启动冒烟

- Terminal Release：进程正常启动，窗口标题为 `EveryStage Terminal`，进程响应正常，随后正常退出。
- Caster Release：进程正常启动，窗口标题为 `EveryStage Caster`，进程响应正常，随后正常退出。
- 该冒烟只证明程序入口、依赖加载和基础窗口创建成功，不替代玻璃材质、DPI、GPU、音视频和跨机器验收。

## WPS COM 回归（2026-09-22）

- Writer：`KWPS.Application` 创建、静默属性、编辑、保存、重开和退出通过。
- Spreadsheet：`KET.Application` 创建、静默属性、编辑、保存、重开和退出通过。
- Presentation：`KWPP.Application` 创建、编辑、保存、重开和退出通过；该版本对 `Visible=false` 与 `ScreenUpdating` 返回 `E_FAIL`/不提供属性，按兼容性差异处理。
- 三项测试结束后未残留 `wps`、`et`、`wpp` 进程。

## WPS 编辑保存与退出验收

使用 `WpsComInteropSpike --self-test-dir` 自动执行创建、编辑、保存、关闭、重新打开、内容校验和退出。

| 应用 | 创建/编辑/保存/重开 | 进程退出 | 备注 |
|---|---|---|---|
| Writer | 通过 | 通过 | `Visible=false`、`DisplayAlerts=0`、`ScreenUpdating=false` 均成功 |
| Spreadsheet | 通过 | 通过 | `Visible=false`、`DisplayAlerts=0`、`ScreenUpdating=false` 均成功 |
| Presentation | 通过 | 通过 | `Visible=false` 返回 `E_FAIL`；无 `ScreenUpdating` 属性；核心自动化仍通过 |

测试前后 `wps`、`et`、`wpp` 进程数均为 0，没有残留进程。Presentation 的静默属性差异需要在产品接入时按此版本单独处理，不能照搬 Writer/Spreadsheet 设置。

## B-15 配对安全与设备投屏验收

待两台 Windows 机器和一台扩展屏就绪后执行。建议 Terminal 与 Caster 均使用同一构建产物，先清空旧配对；如果要验证兼容提示，再额外保留一份升级前旧 Caster/Terminal。

### B-15 验收准备

1. 使用最新严格构建产物：
   - Terminal：`C:\Users\admin\everystage\verify-b15-operator-full\EveryStage.Terminal\EveryStage.Terminal.exe`
   - Caster：`C:\Users\admin\everystage\verify-b15-operator-full\EveryStage.Caster\EveryStage.Caster.exe`
2. 清空旧配对后再做首次配对验证：
   - 先 dry-run：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Reset-B15PairingState.ps1`
   - 确认目标正确后执行：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Reset-B15PairingState.ps1 -Apply`
   - 脚本会备份并删除 `%ProgramData%\EveryStage\paired-devices.json` 与 `%ProgramData%\EveryStage\caster-paired-terminals.json`；如果 Terminal / Caster 分别在不同机器上运行，需要在两台机器上各执行一次。
   - 旧 v1 配对本来就会被拒绝；这里清空只是为了让 UI 流程更干净。
3. 抓包建议：
   - Wireshark 过滤：`udp.port == 47990`
   - 首次配对重点保存 `pair_request` / `pair_challenge` / `pair_reveal` / `pair_response` 四类报文。
   - 通过标准是 `pair_response` 里只有 `KeyConfirmation`，没有 `PairingKey` 字段，也没有可直接作为 32 字节密钥的 base64 明文。
4. 每项验收建议记录：
   - Terminal / Caster 版本或构建目录。
   - 操作视频或截图。
   - Wireshark `.pcapng`（首次配对和旧版兼容至少各一份）。
   - Terminal `ProgramData\EveryStage\logs` 中对应时间段日志。
   - 可直接填写 `docs\B15-ACCEPTANCE-RESULTS.md` 作为最终归档表。
5. 本机 B-15 验证可用脚本复跑（PowerShell）：
   - 完整本机门禁：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Run-B15LocalVerification.ps1 -OutputRoot C:\Users\admin\everystage\verify-b15-local`
   - 最新完整本机门禁通过记录：`C:\Users\admin\everystage\verify-b15-operator-full\`，包含 CoreSelfTests、WindowsSelfTests、9 项 Release `-warnaserror` 严格构建、waiting / preview / program 三种 UI smoke；日志为 `C:\Users\admin\everystage\verify-b15-operator-full\verification.log`，其中包含子进程 PASS / 构建 / SMOKE 明细。
   - 双机验收包生成：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Prepare-B15AcceptancePackage.ps1 -OutputRoot C:\Users\admin\everystage\b15-acceptance-package`
   - 当前验收包：`C:\Users\admin\everystage\b15-acceptance-package\` 与 `C:\Users\admin\everystage\b15-acceptance-package.zip`，外层 zip 校验文件为 `C:\Users\admin\everystage\b15-acceptance-package.zip.sha256`，解压前校验脚本为 `C:\Users\admin\everystage\b15-acceptance-package.verify.ps1`，外层快速说明为 `C:\Users\admin\everystage\b15-acceptance-package.README.txt`；包内含 Terminal / Caster 构建、现场交接单 `docs\B15-FIELD-HANDOFF.md`、验收文档、清配对脚本、完整门禁日志、`manifest.json` 和 `SHA256SUMS.txt`。
   - 验收包外层与内层一键校验：`powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\admin\everystage\b15-acceptance-package.verify.ps1 -ZipPath C:\Users\admin\everystage\b15-acceptance-package.zip`；当前 archive 校验通过。
   - 验收包解压后完整性校验：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Verify-B15AcceptancePackage.ps1 -PackageRoot C:\Users\admin\everystage\b15-acceptance-package`；当前包已校验通过，81 个文件全部 OK。
   - 现场证据采集：把 `.pcapng`、截图和录屏放入验收包 `manual-evidence\` 后，分别在 Terminal / Caster 机器运行 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Collect-B15AcceptanceEvidence.ps1 -PackageRoot C:\Users\admin\everystage\b15-acceptance-package -MachineRole terminal`（Caster 机器把 `terminal` 改为 `caster`）。若系统信息采集被机器策略阻塞，可加 `-SkipSystemInfo`。证据 zip 会包含 `package\package-verify.txt`，记录现场机器实际执行的验收包 SHA 校验输出；如果包校验失败，脚本仍保留证据 zip，但会以非 0 退出码结束，需先处理 `package-verify.txt` 中的失败项。
   - 历史完整本机门禁通过记录：`C:\Users\admin\everystage\verify-b15-final-local-logged\`，首次自管日志完整跑通。
   - 历史完整本机验证产物：`C:\Users\admin\everystage\verify-b15-script-full\`，包含 Core、Windows、严格构建和 UI smoke 四类输出。
   - 脚本会在输出根目录写 `verification.log`；需要指定日志位置时可传 `-TranscriptPath <path>`。
   - 只复跑设备源窗口 UI smoke：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Run-B15LocalVerification.ps1 -OutputRoot C:\Users\admin\everystage\verify-b15-script-ui -SkipCoreSelfTests -SkipWindowsSelfTests -SkipStrictBuild -NoRestore`
   - 当前脚本 UI-only 通过记录：`C:\Users\admin\everystage\verify-b15-script-ui\`，waiting / preview / program 三种状态均 `SMOKE OK`。
   - 当前脚本 CoreSelfTests-only 通过记录：`C:\Users\admin\everystage\verify-b15-script-core\`。
   - 当前脚本 WindowsSelfTests-only 通过记录：`C:\Users\admin\everystage\verify-b15-script-windows\`。
   - 当前补充 WindowsSelfTests 通过记录：`C:\Users\admin\everystage\verify-b15-rejections\WindowsSelfTests\`，覆盖 Caster 收到未签名 `cast_start_rejected:not_paired` 后删除本地 stale pairing 并转入重新配对，以及 `not_allowed` 不删除本地密钥。
   - 当前补充 WindowsSelfTests 严格构建通过记录：`C:\Users\admin\everystage\verify-b15-rejections-build\WindowsSelfTests\`，Release `--no-restore -warnaserror` 为 0 警告 0 错误。
   - 当前脚本 StrictBuild-only 通过记录：`C:\Users\admin\everystage\verify-b15-script-build\`，9 项均 0 警告 0 错误。
6. 设备源窗口 UI smoke 也可单项复跑（PowerShell）：
   - `$env:SMOKE='1'; $env:DEVICE_CAST='preview'; dotnet run --project artifacts\ui_snapshot\ui_snapshot.csproj -c Release -p:OutDir=C:\Users\admin\everystage\verify-b15-switch-retract\ui_snapshot_bin\ -- C:\Users\admin\everystage\verify-b15-switch-retract\ui_snapshot preview 960 720`
   - 把 `preview` 分别替换为 `waiting` / `program` 可复跑三种状态。
   - 当前通过记录：`C:\Users\admin\everystage\verify-b15-switch-retract\ui_snapshot\`，waiting / preview / program 三种状态均 `SMOKE OK`。

| 项目 | 操作 | 通过标准 | 记录 |
|---|---|---|---|
| 首次配对数字比对 | Caster 选择 Terminal 并开始投屏；Terminal 弹出配对确认框 | 两端显示同一组 6 位数字；Terminal 确认后 Caster 保存 v2 密钥 | 待执行 |
| 报文不含明文密钥 | 对首次配对抓 UDP 47990 包，重点看 `pair_response` | `pair_response` 只有 `KeyConfirmation`，没有 `PairingKey` 字段和 32 字节 base64 明文密钥 | 待执行 |
| 已配对直连 | 关闭并重开 Caster，再次开始投屏同一 Terminal | 不再弹配对框；设备先进入设备源窗口预览，不直接顶掉大屏 | 待执行 |
| 设备窗口预览 | 设备连接后不按「投屏」 | 设备源窗口直接显示视频预览；预览静音；大屏保持原本内容 | 待执行 |
| 投屏 / 回退 | 大屏先播放图片、PDF/PPT 指定页或视频指定位置；设备预览中按「投屏」，再按「回退」 | 上大屏后才出声；回退后恢复投屏前文件、页码/位置和暂停状态；设备仍留在设备源窗口 | 待执行 |
| 设备断开恢复 | 设备正在大屏输出时断开 Caster 或停止投屏 | 大屏按回退点恢复；Terminal 不崩溃，设备源窗口回到等待/断开状态 | 待执行 |
| 第二台设备确认 | A 已连接时，B 发起投屏 | Terminal 先向 B 回 Pending；操作屏弹「设备投屏请求」；拒绝不影响 A，接受后 A 按断开流程恢复，B 只进入预览 | 待执行 |
| 同设备重连 | 同一 Caster 切换显示器或重启会话 | 不再弹二次确认；如果原来在大屏上，新会话直接接回大屏，回退点沿用 | 待执行 |
| 投屏开关边界 | 关闭投屏开关后让网络 Caster 请求投屏；设备已上大屏时关闭开关；另测本地内容正在 Program 时关闭开关 | 网络 Caster 仍可进入设备源窗口；设备已上大屏时被退回静音预览且不被断开，大屏恢复设备投屏前内容或空闲状态；本地 Program 输出被收回并恢复扩展屏原状态 | 待执行 |
| 操作屏位置 | 主窗口可见、隐藏、最小化以及扩展屏为主屏时分别触发配对框和第二设备确认框 | 确认框出现在操作屏；不会弹到观众扩展屏 | 待执行 |
| 旧版兼容 | 旧 Caster 请求新 Terminal；新 Caster 发现旧 Terminal | 旧 Caster 显示「投屏机程序版本过旧，请更新后重新配对」；新 Caster 显示「需要更新程序」 | 待执行 |

本机自动化已覆盖协议握手、`pair_response` 不携带 `PairingKey`、设备预览/投屏/回退、第二台设备 Pending/接受/拒绝、投屏开关边界、可见/隐藏 owner 下的操作屏居中，以及 9 项 Release `-warnaserror`。这些自动化结果不替代上表的双机、抓包、扩展屏和多屏操作屏验收。

### B-15 结果记录模板

- Terminal 机器 / GPU / 扩展屏：
- Caster 机器 / GPU / 捕获源：
- 构建目录：
- 首次配对验证码：
- 抓包文件：
- 日志目录或压缩包：
- 未通过项与复现步骤：

## 尚需外部条件

- 1080p 与 4K H.264 测试视频，用于零拷贝渲染和 60 分钟浸泡测试。
- 至少一台 1080p/4K 显示器，以及不同 GPU/驱动机器，用于热插拔与全屏测试。
- 第二台安装 EveryStage 的 Windows 机器和可控弱网环境，用于断网、重连和长时间投屏。
