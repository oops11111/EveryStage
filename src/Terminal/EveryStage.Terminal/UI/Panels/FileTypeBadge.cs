using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>The type chip drawn on a file thumbnail — extension in white on a type colour, as in
/// design/terminal_ui_prototype.html (JPG blue, MP4 translucent black, PPT orange, PDF red, DOC blue).
/// Shared by the 文件 grid and the 活动 rows, which used to carry two copies that had drifted apart
/// (a different XLS green, and a slate video chip the prototype does not have).</summary>
internal static class FileTypeBadge
{
    public static (string Text, Color Color) For(MediaFile file)
    {
        string ext = Path.GetExtension(file.SourcePath).TrimStart('.').ToUpperInvariant();
        string shortExt = ext.Length is > 0 and <= 4 ? ext : "";
        return ext switch
        {
            "PPT" or "PPTX" => ("PPT", Color.FromArgb(232, 98, 46)),
            "PDF" => ("PDF", Color.FromArgb(226, 59, 59)),
            "DOC" or "DOCX" => ("DOC", Color.FromArgb(46, 107, 224)),
            "XLS" or "XLSX" => ("XLS", Color.FromArgb(33, 140, 84)),
            _ when file.Kind == MediaKind.Video => (shortExt.Length > 0 ? shortExt : "视频", Color.FromArgb(140, 0, 0, 0)),
            _ when file.Kind == MediaKind.Audio => (shortExt.Length > 0 ? shortExt : "音频", Color.FromArgb(107, 75, 208)),
            _ => (shortExt.Length > 0 ? shortExt : file.Kind == MediaKind.Image ? "图片" : file.Kind == MediaKind.Document ? "文档" : "文件",
                Color.FromArgb(30, 111, 224)),
        };
    }
}
