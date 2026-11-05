# EveryStage 0.9.1（内测）

对外只发布 `EveryStage-Setup-0.9.1-win-x64.exe`（NSIS，自带 .NET 运行环境）。ZIP 与 PowerShell 部署脚本仅供开发和内部批量部署（DEV-ONLY）。本安装包未经代码签名，Windows SmartScreen 可能提示“未知发布者”。

## 安全说明：认证不等于加密

配对后，投屏的控制消息和媒体数据都用设备密钥认证，可以防止伪造和重放；但**媒体内容本身没有加密**。同一局域网内能抓包的人可以看到投屏画面、听到声音。请只在受信任的局域网中使用 EveryStage，不要把它描述为“加密投屏”。

配对密钥和设备身份保存在 `%ProgramData%\EveryStage\secure`，只有 SYSTEM、管理员和创建它们的账户可以读取。HTTP 外控默认关闭，开启后只监听本机（127.0.0.1:18790），每个请求都需要令牌。

## 本版变化

见 `docs/V1-RELEASE-GATE.md` 的 01–27。
