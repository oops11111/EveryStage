using EveryStage.Rendering.Decode;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.Playback;

/// <summary>Which of the two playback channels an engine instance drives.</summary>
public enum PlaybackChannel
{
    /// <summary>In-app monitor ("本地预览 / 播控窗口"). Always available: no extended display and no
    /// cast switch required.</summary>
    Preview,

    /// <summary>The extended display ("正式输出"). Only plays while the display is bound and the cast
    /// switch allows local output.</summary>
    Program,
}

/// <summary>Per-channel playback state (PreviewIdle … PreviewFailed for the Preview engine; the Program
/// engine uses the same values for its own content).</summary>
public enum PlaybackChannelState
{
    Idle,
    Loading,
    Playing,
    Paused,
    Failed,
}

/// <summary>What the extended display is doing, independent of Preview.</summary>
public enum ProgramOutputState
{
    /// <summary>No extended display bound.</summary>
    Disconnected,
    /// <summary>Display bound, nothing on air.</summary>
    Standby,
    /// <summary>On air — see <see cref="EveryStage.Terminal.StateMachine.ProgramSource"/> for what.</summary>
    Live,
    /// <summary>The last attempt to put local content on air failed.</summary>
    Failed,
}

/// <summary>
/// Everything the Preview error card shows for a failed file: name, stage, container extension,
/// detected codecs and a suggested fix. Built from the exception so every failure path produces the
/// same card. Carries no file contents — only the name/extension/codec labels (the structured log
/// records file id + stage + message, never the path's contents).
/// </summary>
public sealed record PlaybackError(
    MediaFile File,
    string Stage,
    string Extension,
    string? VideoCodec,
    string? AudioCodec,
    string Message,
    string Suggestion)
{
    public string FileName => Path.GetFileName(File.SourcePath);

    public static PlaybackError From(MediaFile file, Exception ex)
    {
        string extension = Path.GetExtension(file.SourcePath).TrimStart('.').ToUpperInvariant();
        if (!System.IO.File.Exists(file.SourcePath))
            return new(file, "打开文件", extension, null, null, "文件不存在或已被移动。",
                "确认文件仍在原位置（或所在的移动存储已连接），然后重试；若文件已删除，请从文件库移除。");

        if (ex is MediaOpenException media)
        {
            (string stage, string suggestion) = media.Stage switch
            {
                MediaFailureStage.Open => ("打开文件",
                    "文件可能已损坏，或容器格式不受 Windows 媒体组件支持。建议用转码工具转成 MP4（H.264 + AAC）后重新导入。"),
                MediaFailureStage.VideoFormat => ("协商视频解码",
                    "系统缺少该视频编码的解码器。建议转码为 MP4（H.264），或安装对应的 Windows 视频扩展（如 HEVC 视频扩展）。"),
                MediaFailureStage.AudioFormat => ("协商音频解码",
                    "系统无法解码该音频编码。建议转码为 MP3、AAC 或 WAV。"),
                MediaFailureStage.Decode => ("播放中解码",
                    "文件可能部分损坏。可以重试；若反复失败，请重新导出或转码该文件。"),
                _ => ("呈现画面",
                    "显卡输出失败。可以重试；若反复失败，请检查显卡驱动。"),
            };
            return new(file, stage, extension, media.VideoCodec, media.AudioCodec, media.Message, suggestion);
        }

        return file.Kind switch
        {
            MediaKind.Image => new(file, "图片解码", extension, null, null, ex.Message,
                "图片已损坏或格式不受支持。可靠支持 JPG、PNG、BMP、GIF（首帧）、TIFF（首页）；请另存为 PNG/JPG 后重新导入。"),
            MediaKind.Document => new(file, "文档渲染", extension, null, null, ex.Message,
                "文档可能已损坏或受密码保护。请用原程序打开确认，另存后重新导入。"),
            MediaKind.Video => new(file, "播放中解码", extension, null, null, ex.Message,
                "可以重试；若反复失败，建议转码为 MP4（H.264 + AAC）。"),
            _ => new(file, "播放中解码", extension, null, null, ex.Message,
                "可以重试；若反复失败，建议转码为 MP3、AAC 或 WAV。"),
        };
    }
}
