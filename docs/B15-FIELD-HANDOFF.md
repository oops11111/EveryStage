# B-15 现场验收交接单

## 当前交付物

- 验收包：`C:\Users\admin\everystage\b15-acceptance-package.zip`
- SHA-256：以同目录 `b15-acceptance-package.zip.sha256` 为准
- 解压前校验：`C:\Users\admin\everystage\b15-acceptance-package.verify.ps1`
- 外层快速说明：`C:\Users\admin\everystage\b15-acceptance-package.README.txt`
- 最新完整本机门禁：`C:\Users\admin\everystage\verify-b15-operator-full\verification.log`

## 现场先做

1. 复制 `b15-acceptance-package.zip`、`.zip.sha256`、`.verify.ps1` 和 `.README.txt` 到现场机器。
2. 解压前运行：
   `powershell -NoProfile -ExecutionPolicy Bypass -File .\b15-acceptance-package.verify.ps1 -ZipPath .\b15-acceptance-package.zip`
3. 解压后阅读 `README.md` 和 `docs\WINDOWS_ACCEPTANCE.md`。
4. 在 Terminal / Caster 机器分别 dry-run 清旧配对：
   `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Reset-B15PairingState.ps1`
5. 确认目标正确后再加 `-Apply`。

## 必收证据

- 首次配对 UDP 47990 抓包，至少包含 `pair_request`、`pair_challenge`、`pair_reveal`、`pair_response`。
- 两端 6 位数字一致的照片或录屏。
- `pair_response` 无 `PairingKey` 字段、只有 `KeyConfirmation` 的抓包截图或导出。
- 设备源窗口预览、按「投屏」上大屏、按「回退」恢复原内容的录屏。
- 投屏开关关闭期间新 Caster 仍可进入预览、已上大屏设备会退回预览并恢复原大屏内容的录屏。
- 第二台设备 Pending / 拒绝 / 接受的录屏。
- 操作屏定位场景截图：主窗口可见、隐藏/最小化、扩展屏为主屏。
- 旧版兼容提示截图。
- Terminal、Caster A、Caster B 各自的 evidence zip。

## 现场执行表

填写 `docs\B15-ACCEPTANCE-RESULTS.md`，至少覆盖 B15-01 到 B15-18。

## 证据采集

把 `.pcapng`、截图和录屏放入验收包的 `manual-evidence\` 后运行：

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Collect-B15AcceptanceEvidence.ps1 -PackageRoot C:\Users\admin\everystage\b15-acceptance-package -MachineRole terminal`

Caster 机器把 `terminal` 改成 `caster`。如果系统信息采集被策略拦截，加 `-SkipSystemInfo`。

如果包校验失败，脚本仍会保留 evidence zip，但退出码为非 0；先查看 evidence 中的 `package\package-verify.txt`。

## 本机已验证

- CoreSelfTests 通过。
- WindowsSelfTests 通过。
- 9 个项目 Release `-warnaserror` 通过。
- 设备源窗口 waiting / preview / program 三态 UI smoke 通过。
- 验收包外层 SHA 与包内 81 个文件校验通过。
- 证据采集正常路径通过。
- 篡改临时包后的反向烟测通过：脚本退出码为 1，同时保留 evidence zip 并记录 hash mismatch。

## 仍需真机确认

- 双机首次配对数字一致。
- 抓包证明配对密钥 K 未上网。
- 已配对再次投屏不弹配对框。
- 预览静音，上大屏后出声。
- 回退恢复图片、视频、PDF/PPT 页码和暂停状态。
- 设备断开恢复。
- 第二台设备确认。
- 操作屏定位。
- 旧版兼容提示。
