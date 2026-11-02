# EveryStage 总实施清单（八轮合议，2026-11-03）
版本基线：e7e7e91。只列**已拍板**事项；出处见 `PRODUCT-DECISIONS-2026-11-03.md` 与各轮 CONSOLIDATED。未真机项标「待真机」。工作量 S/M/L 为合议估计。

主题显示名：跟随系统 / 浅色 / 深色（默认）/ 舞台（代码 `Tech`）/ 系统高对比度（自动接管）。

---

## 阶段 0 · 发布阻断（首轮 P0，可与阶段 1 并行部分项）

| ID | 事项 | 验收要点 | 量 |
|---|---|---|---|
| R1-A | 演讲者窗 / 原生 PPT 数据源冲突说清并修 | 跨版本行为一致；文档与 UI 一致；不得宣传未验收能力 | L |
| R1-B | SelfTests 超时+心跳；CI `timeout-minutes`；超时转储；**文档以 CI 实际结果为准**，删「全部通过」 | CI 不会挂 6h；文档无虚假验收 | M |
| R1-C | **对外只发 NSIS 自包含**；ZIP/PS1 标开发用；统一 Version 源（Directory.Build.props）+ CI 注入 | 装包唯一；版本可溯 | M |
| R1-D | 发布说明：**媒体认证≠加密**；禁止写「加密投屏」 | 说明可见 | S |
| R5-A | OV 代码签名（signtool） | SmartScreen 可接受 | M |
| R5-B | 卸载可选「同时删除配置与配对」，默认不勾 | 安装器选项存在 | S |

---

## 阶段 1 · 现场安全 P0（最先落地）

| ID | 事项 | 拍板细节 | 量 |
|---|---|---|---|
| R3-P0-1 | 急停热键 | `MainWindow.ProcessCmdKey`：**Shift+Esc** → `StopProgramOutput()`；全局 `RegisterHotKey` **默认关**可配；TextBox 焦点亦生效；注册失败提示 | M |
| R3-P0-2 | 标题栏常驻「■ 停止输出」 | 禁用不隐藏；与 Take ≥16px；**DangerFill `#B42830`** + 白字 | S |
| R3-P0-3 | 上屏反馈 | **ON AIR 通栏在标题栏下** + 卡片红描边 ~150ms + toast | M |
| R6-P0 | 急停取消全部自动推进 | 含自动下一项、PPT/PDF 自动翻页计时器；**无「重新投屏」撤销** | S |
| 真机 T1 | 急停全路径 | 有/无焦点、抽屉开、对话框开、自动推进中；输出必须断 | — |

---

## 阶段 2 · 播放与出帧 P1

| ID | 事项 | 拍板细节 | 量 |
|---|---|---|---|
| R4-D | 出帧节拍 | 去 `Sleep(1)`+`vsync:false`；可等待 swap chain + SyncInterval=1；丢帧/呈现计数入日志；**待真机** | L |
| R6-E | 播完策略 | **默认定格末帧**；新增 **播完黑场**；双击直上屏场景用新默认 | M |
| R6-E2 | 全局「下一项」 | **仅当前活动内**；禁跳过时提示 | S |
| R4-AV | 音画目标（设计） | 声早≤45ms/晚≤125ms；切换 <100ms；出处未核，**以真机为准** | — |
| R6-D | 音频设备 | 设置可选设备，**默认跟随系统**；视频声与 BGM **同设备**；丢失回退默认并提示；不做独占 | M |

---

## 阶段 3 · 主题与对比度 P1

| ID | 事项 | 拍板细节 | 量 |
|---|---|---|---|
| R7/R8 | 扩展 UiPalette | 分离 Fill/描边；补 AccentFill/DangerFill/Preview/Program/OnAir/Focus… | M |
| R7 | Dark 色 | AccentFill `#285ABE`；DangerFill `#B42830`；Program 描边 `#C6425E`；**红字 `#E8687F`**；Preview `#22C55E` | S |
| R8 | 舞台（Tech） | Accent 描边 `#00BEFF`；AccentFill `#0B5E8E`；DangerFill `#B42838`；**关 Mica** | S |
| R8 | 主题名 | 界面：跟随系统/浅色/深色/舞台；顺序同此；枚举 `Tech` 保留 | S |
| R8 | 高对比度 | Windows 开启时**强制**系统色；形状+文字+线型三重编码；无用户关断 | M |
| R8 | System | 跟 AppsUseLightTheme；**不跟系统强调色**；红绿永不跟 | S |
| R8 | ThemeManager | 按**控件角色**换色，禁止旧色反查 | M |
| R3 | 状态色 | Program 红 / Preview 绿 / 选中蓝（改 FP:1176 混用） | S |
| R3-P2 | Mica | 仅顶栏+rail；attr38 + ExtendFrame **同 PR**；Win10/高对比/输出窗关；舞台关 | M |
| R8 | 大屏与舞台监看 | **固定深色**，不受主题影响 | S |

---

## 阶段 4 · 窗口与布局 P1

| ID | 事项 | 拍板细节 | 量 |
|---|---|---|---|
| R4 | 窗口位置 | Get/SetWindowPlacement；屏不在则拉回主屏；含浮动预览窗 | M |
| R4 | 窄窗 | 宽 <1200 自动折坞；折条保留在播红点；**不做浮层小预览** | M |
| R3 | 缩略图 | 可见区加载 + 磁盘缓存；**contain**；红点+进度/页码；缺失红标 | L |
| R3 | 撤回 | toast 触发（**勿绑 Ctrl+Z**）；投屏/原生 PPT **不提供** | M |

---

## 阶段 5 · 库、清单、设置 P1

| ID | 事项 | 拍板细节 | 量 |
|---|---|---|---|
| R5 | 导入 | 去重；文件夹递归；后台一次落盘（禁 UI 线程 O(n²)） | M |
| R5 | 移除在播 | **只提示**可手动停，**不自动急停**；可撤销 | S |
| R5 | 源丢失 | 库内红标 + 批量重定位 | M |
| R5 | 方案导出/导入 | 默认同路径相对；**默认不打包媒体，可勾选打包**；schema 版本；切换确认且勿动大屏 | L |
| R4 | 设置 | **统一即时生效**；危险项提示+可撤销；恢复默认+导入导出 | M |
| R4 | 热键配置页 | 可查改；急停键位与上一致 | M |
| R5 | 配对密钥 | DPAPI + 收紧 ACL；**关闭旧版直下密钥路径** | M |
| R6 | AccessibleName | 急停/上屏/文件网格等关键控件可读 | M |

---

## 阶段 6 · 本期功能（第四轮拍板）

| ID | 事项 | 拍板细节 | 量 |
|---|---|---|---|
| R4 | Stage Display + 倒计时 | 复用 OperatorScreen；≤60s 黄、≤10s 红脉冲；固定深色 | L |
| R4 | HTTP 外控 | **默认仅本机** + ≥128bit 令牌；Host/Origin；指令白名单；限速；审计；局域网手动开 | L |
| — | OSC / NDI | **下期**；NDI 先核许可 | — |

---

## 阶段 7 · P2 与债务（可排期）

Pdfium 升级评估；边框 BorderStrong；ToolTip/MessageBox 跟主题；切换防闪；文案集中管理（不做英文化）；首次引导/空状态；音频分路；滚动备份；防火墙 profile；日志路径脱敏；折叠偏好记忆。

---

## 明确不做 / 以后做

- **不做：** 推流、录制、多机位、SDI、VJ/映射；本期英文化；急停后自动恢复投屏；窄窗浮层预览；Tech 单独全套概念图。
- **以后：** OSC/MIDI、NDI、字幕、音视频分设备。

---

## 建议冲刺顺序（可并行标注 ∥）

```
Sprint A: R1-B SelfTests/CI/文档 ∥ R3-P0 急停三件套+取消自动推进 ∥ R1-D 安全披露文案
Sprint B: R7/R8 token+对比度+主题名+状态色 → 急停按钮用 DangerFill
Sprint C: R4 出帧节拍 ∥ R6 播完定格/黑场 ∥ R6 音频设备
Sprint D: R1-C 只发 NSIS+版本源 ∥ R5 签名/卸载选项
Sprint E: 窗口位置+窄窗折坞 ∥ 缩略图 contain/缓存/缺失标
Sprint F: 导入移除/方案导出 ∥ 设置即时+热键页 ∥ 配对 DPAPI
Sprint G: Stage Display+倒计时 ∥ HTTP 外控
Sprint H: Mica 同 PR ∥ 高对比度 ∥ AccessibleName
Sprint I: 真机矩阵 T1–T19（见 ROUND6 各方附录）→ 修实测问题
```

---

## 真机最小矩阵（执行前准备）

Win10 / Win11 × Intel/AMD/NVIDIA × 混合 DPI 双屏 × ≥2 音频设备 × WPS/Office/皆无。优先跑：急停、出帧、对比度、拔屏、安装升级卸载、配对、导入移除、方案、音频丢失、播完策略。

---

## 索引

| 文件 | 用途 |
|---|---|
| `PRODUCT-DECISIONS-2026-11-03.md` | 全部拍板原文 |
| `EVERYSTAGE-CONSOLIDATED-REVIEW-2026-11-03.md` | 首轮 |
| `EVERYSTAGE-SUPPLEMENT-UI-UX-2026-11-03.md` | UI/UX 补充 |
| `EVERYSTAGE-ROUND3`…`ROUND8-CONSOLIDATED-*.md` | 各轮总审议 |
| `round7-mockups/` `round8-mockups/` | 概念图（非实拍） |
