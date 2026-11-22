# 离线只读校验与验收边界

最终新目录提供VERIFY.ps1（源为scripts/Verify-PresentationDelivery.ps1），不需要先安装Python。Windows PowerShell运行：`powershell.exe -NoProfile -File .\VERIFY.ps1`。脚本只读文件和ZIP；不会启动应用/安装器、注册组件或改系统设置。也可用Get-FileHash -Algorithm SHA256核对manifest中的安装器和nupkg摘要。

目录约定：manifest.json绑定app_version0.9.3、application_commit、component_source_commit、run、产品路径/hash、input_mapping、原input/smoke/control ZIP的archives及primary_post_receipt_sha256；evidence/input保留原props/build-input/component-state/provenance，evidence/smoke保留完整普通安装/正常退出记录，evidence/control保留原213实际控件结果与图片，baseline保留原0.9.1 ZIP。全目录SHA256SUMS包含每个payload，嵌套SHA256SUMS也验证，拒绝额外/丢失文件、路径穿越及任意祖先reparse。

校验包含全部input映射/长度、nupkg精确member集合和摘要、真实mapped managed/native路径与包member相等、同PID/开始时间的held normal Exit0、9次安装/卸载调用0、三组件选择、原baseline ZIP/EXE、213检查与Created=Released、控件图片和主审普通UI图片摘要。只内容通过但ROOT_ACCEPTANCE缺失时默认exit2；-AllowPending只供准备阶段，不能作为最终交付签认。最终签认绑定实际postmerge源、postCI成功、主审接受和产品原字节；不依赖尚未发生的源码自签认。

当前提交只记录实现与验证方法。实际最终产物必须来自S3已合并后的同一成功pipeline；PR临时合并产物不能冒充最终交付。完整CI/主审/post未通过前不封存。旧0.9.2脚本和目录保持只读。测试为hosted Windows runner及隔离合成控件，不是用户电脑/标准用户权限/Office现场/真实显示设备或性能认证。
