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

## 尚需外部条件

- 1080p 与 4K H.264 测试视频，用于零拷贝渲染和 60 分钟浸泡测试。
- 至少一台 1080p/4K 显示器，以及不同 GPU/驱动机器，用于热插拔与全屏测试。
- 第二台安装 EveryStage 的 Windows 机器和可控弱网环境，用于断网、重连和长时间投屏。
