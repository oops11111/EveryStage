namespace EveryStage.Terminal.Data;

/// <summary>PLANNING.md §6 数据结构: 文件类型. <c>Document</c> covers both PDF and PPT/Word/Excel —
/// PLANNING.md itself treats them as one "文档" kind at this level even though they need entirely
/// different renderers (<c>PdfContentRenderer</c>'s rasterized bitmap vs.
/// <c>ContentEngine.WpsDocumentController</c>'s real, editable WPS window); <c>PlaybackEngine</c>
/// re-derives which one applies from the file extension at play time rather than this enum growing a
/// second Document-like value for it — see <c>WpsDocumentController.IsOfficeDocument</c>'s own doc
/// comment.</summary>
public enum MediaKind { Image, Video, Audio, Document }

/// <summary>播放模式，可在活动级设置默认值，也可在文件级覆盖 (PLANNING.md §6).</summary>
public enum PlayMode { SequentialAuto, ManualSelect }

/// <summary>播放完成后动作 (PLANNING.md §6): 自动下一项/循环/停留等待.</summary>
public enum CompletionAction { NextItem, Loop, HoldOnLastFrame }

/// <summary>背景音频叠加时，扩展屏应显示的内容 (PLANNING.md §6 音频特殊性).</summary>
public enum AudioVisual { Waveform, DefaultBackgroundImage, Black }
