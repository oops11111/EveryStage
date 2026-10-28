# EveryStage 剩余任务执行顺序

## 1. 自动化测试与 CI 门禁

- [x] Windows Release 全项目严格构建
- [x] 核心协议、传输和持久化自检接入 CI
- [x] 为状态机、媒体队列和配置迁移补充无硬件测试

## 2. 协议可靠性与版本兼容

- [x] 为发现与投屏控制消息增加协议版本
- [x] 状态回报增加消息编号、ACK、超时和有界重试
- [x] 覆盖重复包、乱序、旧版本字段和版本不兼容测试

## 3. 局域网安全

- [x] 配对后建立设备密钥
- [x] 对投屏控制和状态消息做认证并防重放
- [x] 增加密钥轮换、撤销配对和升级兼容策略

## 4. 播放与传输可靠性

- [x] RTP 丢包、抖动和时间戳回绕处理
- [x] 音视频采集延迟差与长期时钟漂移补偿
- [x] 取消 H.264 解码严格一进一出的 FIFO 时间戳假设
- [x] 解决编码器事件循环无法可靠取消的原生资源释放风险

## 5. Windows 真机验收

- [ ] 1080p/4K 零拷贝渲染验收
- [ ] 60 分钟 GPU、内存和音画同步浸泡测试
- [ ] 不同 GPU、驱动、显示器热插拔与全屏应用测试
- [ ] 双机弱网、断网、重连和长时间投屏测试
- [ ] B-15 双机验收：先看 `docs/B15-FIELD-HANDOFF.md`，再按 `docs/WINDOWS_ACCEPTANCE.md` 的 B-15 表执行首次配对、抓包、已配对直连、设备窗口、投屏/回退、第二台设备、操作屏位置、开关边界和旧版兼容
- [x] B-15 设备源窗口截图交付：当前本机已生成 contact sheet，并已通过会话图片发出给用户确认；`lody_upload_images` 工具本环境不可用
- [x] B-15 本机自动化补充：`Run-B15LocalVerification.ps1` 可复跑 Core/Windows/严格构建/UI smoke；补充 WindowsSelfTests 覆盖未签名 `cast_start_rejected:not_paired` 删除 stale pairing 并转入重新配对、`not_allowed` 不删除密钥；`Reset-B15PairingState.ps1` 可在真机验收前备份并清旧配对；`Prepare-B15AcceptancePackage.ps1` 可生成双机验收包、SHA256 清单、现场交接单和带入口/文件数字段的 `manifest.json`；`Verify-B15AcceptancePackage.ps1` / `Test-B15AcceptanceArchive.ps1` 可在现场校验包完整性，并断言 manifest 计数、实际文件数和清单行数一致；`Collect-B15AcceptanceEvidence.ps1` 可采集现场证据 zip，且包校验失败时保留证据并非 0 退出；最新输出见 `docs/B15-FIELD-HANDOFF.md` 与 `docs/WINDOWS_ACCEPTANCE.md`
- [ ] B-15 状态记忆更新：待可写 memory 工具可用后更新 `everystage-audit-state`；当前写入草稿见 `docs/EVERYSTAGE-AUDIT-STATE-MEMORY-DRAFT.md`
- [x] WPS Writer、Spreadsheet、Presentation 编辑保存及进程退出测试

## 6. 产品与发布收尾

- [x] 悬浮预览窗与文件面板联动
- [x] 投屏开关关闭时的本机预览/播放路径
- [x] 监看功能和多终端策略产品决策
- [x] 三套主题、日志导出和诊断面板
- [x] 安装包、Pdfium 原生依赖、WPS License 与批量部署
- [ ] 试点部署和最终验收
