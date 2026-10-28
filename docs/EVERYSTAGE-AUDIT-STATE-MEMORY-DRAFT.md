# everystage-audit-state memory draft

更新时间：2026-10-06

建议写入 `everystage-audit-state`：

```text
EveryStage B-15 已进入“本机实现与自动化验证完成，等待双机真机验收”状态。

已实现：
- 配对协议 v2：P-256 ECDH、nonce commitment/reveal、HKDF 派生 32 字节配对密钥和 6 位数字核对码；终端 `pair_response` 只携带 `KeyConfirmation`，不再下发明文 `PairingKey`。
- v1 配对密钥格式已作废；升级后设备需要重新配对一次。
- 旧 Caster / 旧 Terminal 兼容提示已实现。
- 已配对设备不再重复配对，网络投屏机请求不受投屏开关管理。
- 设备先进入设备源窗口静音预览，按“投屏”才上 Program；上屏后取消静音。
- “回退”、设备断开、liveness/decode failure 会按回退点恢复原 Program 内容或空闲状态。
- 本地 Take / 停止输出 / 扩展屏断开期间，设备流退回设备窗口并保持连接。
- 投屏开关关闭时，若设备正在 Program，会把设备流退回静音预览，并恢复设备投屏前的大屏内容或空闲状态；普通本地 Program 输出仍由投屏开关收回。
- 第二台设备请求时先回 signed Pending，终端操作屏确认；拒绝不影响旧设备，接受后按断开流程恢复旧设备并让新设备只进预览。
- 同一设备重连不弹二次确认；若原来在 Program，新会话直接接回 Program 并沿用回退点。
- 配对框与第二台设备确认框通过 `OperatorScreen.PlaceOnOperatorScreen` 固定在操作屏。
- 设备源窗口 UI 已改为预览画面直接替代原设备源提示，preview/program/waiting 三态截图已生成并交付。

主要文件：
- `src/Shared/EveryStage.Discovery/PairingHandshake.cs`
- `src/Shared/EveryStage.Discovery/DiscoveryProtocol.cs`
- `src/Shared/EveryStage.Discovery/PairingSecurity.cs`
- `src/Terminal/EveryStage.Terminal/Devices/DiscoveryService.cs`
- `src/Caster/EveryStage.Caster/Discovery/TerminalDiscoveryClient.cs`
- `src/Terminal/EveryStage.Terminal/Receiving/DeviceCastController.cs`
- `src/Terminal/EveryStage.Terminal/Receiving/CastReceiver.cs`
- `src/Terminal/EveryStage.Terminal/Display/VideoSurface.cs`
- `src/Terminal/EveryStage.Terminal/UI/SharedPreviewDock.cs`
- `src/Terminal/EveryStage.Terminal/UI/OperatorScreen.cs`
- `src/Terminal/EveryStage.Terminal/UI/DeviceCastRequestDialog.cs`
- `src/Caster/EveryStage.Caster/UI/PairingCodeDialog.cs`
- `scripts/Run-B15LocalVerification.ps1`
- `scripts/Reset-B15PairingState.ps1`
- `scripts/Prepare-B15AcceptancePackage.ps1`
- `scripts/Verify-B15AcceptancePackage.ps1`
- `scripts/Collect-B15AcceptanceEvidence.ps1`
- `scripts/Test-B15AcceptanceArchive.ps1`
- `docs/B15-FIELD-HANDOFF.md`

最新本机验证：
- 完整门禁输出：`C:\Users\admin\everystage\verify-b15-operator-full\`
- 日志：`C:\Users\admin\everystage\verify-b15-operator-full\verification.log`
- 覆盖 CoreSelfTests、WindowsSelfTests、9 项 Release `-warnaserror` 严格构建、waiting / preview / program 三态 ui_snapshot SMOKE，全部通过。

双机验收包：
- 目录：`C:\Users\admin\everystage\b15-acceptance-package\`
- Zip：`C:\Users\admin\everystage\b15-acceptance-package.zip`
- Zip SHA-256：以 `C:\Users\admin\everystage\b15-acceptance-package.zip.sha256` 为准
- 解压前校验脚本：`C:\Users\admin\everystage\b15-acceptance-package.verify.ps1`
- 外层快速说明：`C:\Users\admin\everystage\b15-acceptance-package.README.txt`
- 现场入口文档：`docs/B15-FIELD-HANDOFF.md`
- 包内 `manifest.json` 已列出 `packageFileCount`、`fieldHandoffDocument`、`acceptanceDocument`、`acceptanceResultSheet`、`evidenceCollectionScript` 和 `packageVerificationScript`；`Verify-B15AcceptancePackage.ps1` 会断言 manifest 计数、实际文件数和 `SHA256SUMS.txt` 行数一致；向临时包加入未列入清单的 `UNLISTED-FILE.txt` 会被拒绝，错误为 actual=82 / SHA256SUMS=81；把 `manifest.packageFileCount` 改成 999 并同步 manifest 哈希也会被拒绝，错误为 manifest=999 / SHA256SUMS=81。
- `Verify-B15AcceptancePackage.ps1` 校验 81 个文件全部 OK。
- `Collect-B15AcceptanceEvidence.ps1` 已做本机正常路径与反向烟测：正常路径生成 `C:\Users\admin\everystage\b15-acceptance-evidence-smoke-handoff\20261006-180830-terminal-DESKTOP-K93N3P8.zip`，其 `package\package-verify.txt` 记录 81 files OK；篡改临时包 `README.md` 后脚本退出码为 1，同时保留 evidence zip 并记录 hash mismatch。现场把 `.pcapng`、截图和录屏放入 `manual-evidence\` 后再采集。

仍待外部验收：
- 按 `docs/WINDOWS_ACCEPTANCE.md` 的 B-15 表执行双机首次配对数字比对、UDP 抓包确认 `pair_response` 无明文密钥、已配对直连、设备窗口预览静音、投屏/回退恢复文件位置/PDF/PPT 页/暂停状态、设备断开恢复、第二台设备 Pending/确认、同设备重连、投屏开关边界、操作屏位置、旧版兼容。
- 1080p/4K 零拷贝渲染、60 分钟 GPU/内存/音画同步浸泡、不同 GPU/驱动/热插拔、双机弱网/断网/重连/长时间投屏仍按 Windows 真机验收计划执行。

注意：
- 本机自动化不能替代双机、抓包、扩展屏和操作屏验收。
- 当前会话未发现可写 memory 工具，因此该条 memory 尚未真正写入，只生成了本草稿。
```
