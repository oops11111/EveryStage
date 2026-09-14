namespace EveryStage.Terminal.Data;

/// <summary>PLANNING.md §6 数据结构: 文件类型.</summary>
public enum MediaKind { Image, Video, Audio, Document }

/// <summary>播放模式，可在活动级设置默认值，也可在文件级覆盖 (PLANNING.md §6).</summary>
public enum PlayMode { SequentialAuto, ManualSelect }

/// <summary>播放完成后动作 (PLANNING.md §6): 自动下一项/循环/停留等待.</summary>
public enum CompletionAction { NextItem, Loop, HoldOnLastFrame }

/// <summary>背景音频叠加时，扩展屏应显示的内容 (PLANNING.md §6 音频特殊性).</summary>
public enum AudioVisual { Waveform, DefaultBackgroundImage, Black }
