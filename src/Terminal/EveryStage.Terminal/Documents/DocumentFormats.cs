namespace EveryStage.Terminal.Documents;

/// <summary>Which document pipeline a <see cref="Data.MediaKind.Document"/> file uses. The data model keeps
/// one "文档" kind (PLANNING.md §6); the pipeline is chosen from the extension at play time.</summary>
public enum DocumentFormat { None, Pdf, Word, Presentation, Spreadsheet }

public static class DocumentFormats
{
    public static DocumentFormat Of(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => DocumentFormat.Pdf,
        ".doc" or ".docx" or ".rtf" or ".wps" => DocumentFormat.Word,
        ".ppt" or ".pptx" or ".pps" or ".ppsx" or ".dps" => DocumentFormat.Presentation,
        ".xls" or ".xlsx" or ".et" => DocumentFormat.Spreadsheet,
        _ => DocumentFormat.None,
    };

    /// <summary>Documents opened through WPS/Office automation (everything except PDF).</summary>
    public static bool IsOffice(string path) => Of(path) is DocumentFormat.Word or DocumentFormat.Presentation or DocumentFormat.Spreadsheet;

    public static string DisplayName(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => "PDF",
        DocumentFormat.Word => "Word 文档",
        DocumentFormat.Presentation => "演示文稿",
        DocumentFormat.Spreadsheet => "表格",
        _ => "文件",
    };
}
