namespace EveryStage.Terminal.Logging;

/// <summary>
/// Structured log of document pipeline events (WPS/Office automation, window capture, conversions), for
/// finding which application/version failed at which stage: <c>stage</c> is one of 启动应用 / 打开文件 /
/// 获取窗口 / 启动放映 / 保存 / 关闭 / 捕获 / 转换; <c>suite</c> is WPS or Microsoft Office with its
/// reported version. Never logs document content.
/// </summary>
public sealed class DocumentLogger
{
    private readonly DailyRollingLogWriter _writer;

    public DocumentLogger(string? logRootOverride = null)
    {
        _writer = new DailyRollingLogWriter(Path.Combine(logRootOverride ?? LogPaths.DefaultRoot, "documents"), "documents");
    }

    public void LogEvent(string evt, string sourcePath, string? suite = null, string? version = null, string? detail = null)
    {
        try { _writer.Write(evt, new { sourcePath, ext = Path.GetExtension(sourcePath), suite, version, detail }); }
        catch (Exception) { } // logging must never break playback
    }

    public void LogFailure(string stage, string sourcePath, string? suite, string? version, Exception ex)
    {
        try
        {
            _writer.Write("document_failed", new
            {
                stage, sourcePath, ext = Path.GetExtension(sourcePath), suite, version,
                error = ex.GetType().Name, message = ex.Message,
                hresult = ex is System.Runtime.InteropServices.COMException com ? $"0x{com.HResult:X8}" : null,
            });
        }
        catch (Exception) { }
    }
}
