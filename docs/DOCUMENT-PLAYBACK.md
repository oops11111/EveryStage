# PDF / Word / PPT 预览、编辑与投屏

接入 [Preview / Program 双通道](PREVIEW-PROGRAM.md)：文档与图片、视频一样，先在 Preview（主窗口"本地预览 / 播控窗口"）中呈现，
「投到屏幕」后由 Program（扩展屏 OverlayWindow）输出。替换了旧的 `WpsDocumentController`（WPS 窗口直接盖在扩展屏上、
Preview 只有封面卡片）。

## 1. 统一文档模型

每个 `PlaybackEngine`（Preview、Program 各一个）各自保存文档状态（`DocumentStatus`）：文件、模式、页 / 幻灯片、
页内偏移、缩放、提示、错误。两个通道共享的只有**底层资源**：

| 资源 | 共享方式 |
|---|---|
| PDF 文档 | 不共享：两个通道各自打开、各自渲染（各自的视口与缓存） |
| Word 编辑器（WPS / Word 进程 + 窗口捕获） | `OfficeSessionManager` 按路径引用计数，Preview 与 Program 显示同一捕获画面 |
| 原生放映（PowerPoint / WPS 演示） | 同上；放映窗口放在 Program 屏幕的最底层（无 Program 时放在主屏最底层） |
| 静态幻灯片（PDF 转换结果） | `DocumentConversionCache`，按 SHA1(路径\|长度\|修改时间) 缓存，源文件变化即失效，最多保留 40 份 |

`DocumentProgramSync`（主窗口持有）连接两个通道：

- **实时同步**（默认）：Preview 中翻页 / 滚动 / 缩放 / 编辑，Program 立即跟随。
- **冻结大屏**：Program 保持当前画面；Preview 可继续翻页、编辑；「更新大屏」把 Preview 当前位置 / 最新画面送上 Program。
- 关闭实时同步后同样需要「更新大屏」。同步按钮只有在 Program 正在显示同一文档时可用。
- 「投到屏幕」（Take）带上 Preview 的精确视口（`PdfViewportState`：页 + 页内偏移 + 缩放模式）或幻灯片序号。

## 2. 按类型的策略

### PDF（`Documents/PdfDocumentView.cs`、`PdfViewport.cs`）

- 连续滚动 / 单页两种模式；只渲染可见页（+ 预读 1 页），后台渲染线程 + LRU 页缓存（按字节上限）；滚动中先出半分辨率，
  停止 180 ms 后补全分辨率；渲染完成前画白页，不出现黑闪。
- 视口以「页 + 页内偏移 + 缩放」保存，窗口尺寸变化、Preview→Program 尺寸不同时位置不变。
- 缩放：适合宽度 / 适合页面 / 实际大小 / 自定义（25%–400%，Ctrl+滚轮）。
- Preview 有滚动条与页码胶囊；Program 无任何界面元素、隐藏鼠标。
- 损坏 / 加密 PDF 在应用内显示错误卡片，不调用外部程序；从内存流加载，失败不锁文件。

### Word（`Documents/OfficeSessions.cs` → `WriterSession`）

- 在操作员屏幕上打开真实 WPS 文字 / Word 编辑窗口（打开方式：自动（WPS 优先）/ WPS / Microsoft Office / 只读预览）。
- 窗口捕获：Windows Graphics Capture 优先（不捕获鼠标），失败时回退 `PrintWindow(PW_RENDERFULLCONTENT)`；
  裁切到文档画布（`_WwG`），不带菜单 / 工具栏；编辑器上的对话框不会进入 Program（只捕获文档画布，
  并检测待处理对话框，提示操作员）。
- 控制条：打开编辑 · 保存 · 另存为 · 适应宽度 · 适应页面 · 实时同步 · 冻结大屏 · 更新大屏 · 停止投屏（· 重新连接）。
- 保存失败 / 只读 / 密码保护 / 被其他程序占用：打开前检测并给出明确提示；只读时「保存」禁用、提示另存为。
- 窗口断开（编辑器被关闭、进程退出）：Program 保持最后一帧，控制条出现「重新连接」。
- 关闭时有未保存修改会询问是否保存；只结束本程序启动的进程，从不结束用户原有的 WPS / Word。
- 「只读预览」模式：转换为 PDF 后按 PDF 方式翻页（不启动编辑器）。

### PPT（`SlideShowSession`、`PresentationInspector`、`DocumentConversionCache`、`PresenterView`）

- **自动选择**：读取 pptx 包结构，含动画 / 转场 / 嵌入媒体 → 原生放映；否则 → 静态幻灯片；.ppt 无法解析时按原生处理。
  活动 / 文件的「文档播放设置」可强制原生或静态。
- **原生放映**：PowerPoint 优先，WPS 演示其次；全屏放映窗口被捕获后输出，Preview 显示演讲者视图（当前画面、下一张、备注、
  动画进度）。下一步先播完当前页动画再翻页；上一张 / 下一张 / 跳转；黑屏 / 白屏 / 暂停；结束放映（大屏保持最后画面）/
  重新开始；编辑 → 重新载入放映（保存后回到原幻灯片）。
- **静态幻灯片**：转换为 PDF 后单页显示；支持自动翻页（每页秒数）与循环；提示「不含动画、转场和嵌入媒体」。
- **降级链**：一个套件打不开 / 启动不了放映 → 换另一个套件再试一次 → 静态幻灯片（带原因）；
  放映画面 4 秒内捕获为空白 → 静态幻灯片；放映中演示程序意外退出 → 静态幻灯片（同一张，带原因）；
  都没有安装 → 有缓存则播缓存，否则显示封面与「未安装 WPS / Office」。

## 3. 活动与预加载

- `MediaFile.Document`（`DocumentPlaybackSettings`）随文件 / 活动保存：阅读模式、起止页、起始偏移、缩放、实时同步、
  每页秒数、打开方式、允许编辑、初始滚动位置、PPT 模式、循环。文件页与活动页右键「文档播放设置...」。
- 切换文档时旧文档继续显示，直到新文档的第一帧就绪（`RetireDocument` / `CompleteDocumentRetirement`），避免黑屏；
  Program 上的原生放映 / Word 捕获在 Preview 中已经打开时直接复用会话（预加载）。

## 4. 日志

`%ProgramData%\EveryStage\logs\documents\`：`document_failed`（阶段：启动应用 / 打开文件 / 获取窗口 / 启动放映 / 放映控制 /
保存 / 关闭 / 转换；套件与版本；HRESULT）以及 `capture_started`（捕获方式与回退原因）、`show_engine_fallback`、
`show_capture_blank`、`show_application_exited`、`show_window_reattached` 等事件。不记录文档内容。

## 5. 测试结果（2026-10-03，Windows 10 Enterprise 19045，单显示器 1280×1024，WPS 12.8.2.18913，Office 16.0.20430）

- 9 个项目 Release `-warnaserror`：0 错误 0 警告。
- CoreSelfTests：全部通过（新增 PDF 视口数学、文档设置保存 / 克隆）。
- WindowsSelfTests：全部通过，文档部分：
  - PDF 120 页连续滚动 373 步约 1 s、只渲染可见页、缓存 35 MB；Take 精确位置、实时同步、冻结 + 更新大屏、关闭同步、
    改尺寸保持位置、Program 无界面元素 / 鼠标；损坏 PDF 应用内报错。
  - Word（WPS，PrintWindow 捕获）：Preview / Program 共享编辑器画面、编辑实时上屏、冻结 + 更新、保存、无残留进程。
  - PowerPoint 原生放映（PrintWindow 捕获）：Program 共享捕获、下一步先播动画、跳转、黑屏、结束保持最后画面、无残留进程。
  - 静态幻灯片（转换缓存）、演示程序中途退出降级、未安装 Office 降级、主窗口启动与文档控制条布局。
- ui_snapshot SMOKE：通过；PDF / Word / PPT（静态、原生）截图检查见 `artifacts/docshots/`。

### 本机环境限制（测试时发现，非程序缺陷）

- 本会话中 WGC `CreateForWindow` 返回「拒绝访问」，所有捕获走 PrintWindow 回退（09-29 同一台机器 WGC 正常）。
- 本会话中 COM 启动的 Word 报「无法创建工作文件」、PowerPoint 放映约 5 秒后进程退出；因此 Word 测试使用 WPS，
  PowerPoint 测试在退出前完成，退出本身被用来验证降级路径。
- 本会话 `%TEMP%` 不可写，测试需设置 `TEMP` / `TMP` 到可写目录。

## 6. 残余风险

1. 未做双显示器真机验证（扩展屏上的 Program 输出、放映窗口放到扩展屏底层、拔出扩展屏），未在 Windows 11 实测。
2. Windows 10 上 WGC 捕获会给窗口加黄色边框（系统行为，Windows 11 可关闭）；本机 WGC 被拒绝时 PrintWindow 每 33 ms
   重新发布一帧（即使画面未变），CPU 占用略高。
3. WPS 演示的全屏放映在本机被捕获为黑屏 → 自动降级为静态幻灯片；装有 WPS 而无 PowerPoint 的机器上原生放映可能不可用。
4. 加密的 .doc（旧格式）无法预先检测密码，依赖 60 秒打开超时后报错。
5. 「播放触发」设置仅在本次会话内有效（与本次改动无关，沿用原状）。
