# Windows 真机验收记录

## 当前环境

- 日期：2026-09-20
- 显示器：1 台，1280×1024（`DISPLAY6`）
- WPS COM ProgID：`KWPS.Application`、`KET.Application`、`KWPP.Application`
- 仓库内测试素材：没有 1080p/4K 视频样本
- 媒体生成工具：未发现 ffmpeg
- 显卡/驱动信息：当前执行账户读取 `Win32_VideoController` 被拒绝

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

