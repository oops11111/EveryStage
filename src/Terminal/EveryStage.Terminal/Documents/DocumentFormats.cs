namespace EveryStage.Terminal.Documents;

/// <summary>Which document pipeline a <see cref="Data.MediaKind.Document"/> file uses. The data model keeps
/// one "文档" kind (PLANNING.md §6); the pipeline is chosen from the extension at play time.</summary>
public enum DocumentFormat { None, Pdf, Word, Presentation, Spreadsheet }

/// <summary>
/// The one list of document extensions: import (<see cref="Data.FileLibraryStore.InferKind"/>), the import
/// dialog filter and playback (<see cref="Of"/>) all read it. Spreadsheets are recognised (so an existing
/// library entry still says what it is) but NOT importable: a sheet has no page layout to show on a big
/// screen — the operator is asked to save it as PDF first.
/// </summary>
public static class DocumentFormats
{
    /// <summary>Document extensions that can be imported and played.</summary>
    public static readonly IReadOnlyList<string> ImportableExtensions = new[]
    {
        ".pdf", ".doc", ".docx", ".rtf", ".wps", ".ppt", ".pptx", ".pps", ".ppsx", ".dps",
    };

    /// <summary>Spreadsheet extensions: refused at import with 「请另存为 PDF」.</summary>
    public static readonly IReadOnlyList<string> SpreadsheetExtensions = new[] { ".xls", ".xlsx", ".et" };

    public static bool IsImportable(string path) =>
        ImportableExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsSpreadsheet(string path) =>
        SpreadsheetExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

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
