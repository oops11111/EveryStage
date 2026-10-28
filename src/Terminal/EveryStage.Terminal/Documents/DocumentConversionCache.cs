using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EveryStage.Terminal.Logging;

namespace EveryStage.Terminal.Documents;

/// <summary>
/// PDF conversions of Office documents, for playing presentations as static slides and Word documents as
/// a read-only page view. Each conversion is stored under a key made from the source path, size and last
/// write time, so editing the source file invalidates it automatically; old entries are pruned. A cached
/// conversion keeps working on a machine (or at a moment) without WPS/Office.
/// </summary>
public sealed class DocumentConversionCache
{
    private const int KeepEntries = 40;
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(3);
    private readonly string _root;
    private readonly DocumentLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DocumentConversionCache(string? root = null, DocumentLogger? log = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveryStage", "DocumentCache");
        _log = log ?? new DocumentLogger();
    }

    public static DocumentConversionCache Shared { get; set; } = new();

    public string Root => _root;

    public sealed record Metadata(string SourcePath, long Length, long LastWriteTicks, string Converter, int Pages, string[] Notes);

    public sealed record Entry(string PdfPath, Metadata Meta);

    private string KeyFor(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        string identity = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
    }

    /// <summary>The conversion of the file as it is now, or null when it has not been converted since it
    /// last changed.</summary>
    public Entry? TryGet(string sourcePath)
    {
        try
        {
            string folder = Path.Combine(_root, KeyFor(sourcePath));
            string pdf = Path.Combine(folder, "document.pdf"), meta = Path.Combine(folder, "meta.json");
            if (!File.Exists(pdf) || !File.Exists(meta)) return null;
            var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(meta));
            if (metadata == null) return null;
            Directory.SetLastAccessTimeUtc(folder, DateTime.UtcNow);
            return new Entry(pdf, metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Returns the cached conversion or converts now with WPS/Office (Microsoft Office first:
    /// its PDF export is the most faithful). Throws <see cref="OfficeOperationException"/> when nothing
    /// can convert the file.</summary>
    public async Task<Entry> GetOrConvertAsync(string sourcePath, DocumentFormat format)
    {
        if (TryGet(sourcePath) is { } cached) return cached;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryGet(sourcePath) is { } again) return again;
            if (OfficeApplicationController.IsPasswordProtected(sourcePath))
                throw new OfficeOperationException(OfficeStage.Convert, "EveryStage", null, "文档受密码保护，无法转换。");
            var kind = format == DocumentFormat.Presentation ? OfficeAppKind.Presentation : OfficeAppKind.Writer;
            var suites = new List<OfficeSuite>();
            if (OfficeInstallation.IsRegistered(kind == OfficeAppKind.Writer ? OfficeInstallation.Word : OfficeInstallation.PowerPoint)) suites.Add(OfficeSuite.MicrosoftOffice);
            if (OfficeInstallation.IsRegistered(kind == OfficeAppKind.Writer ? OfficeInstallation.WpsWriter : OfficeInstallation.WpsPresentation)) suites.Add(OfficeSuite.Wps);
            if (suites.Count == 0)
                throw new OfficeOperationException(OfficeStage.Convert, "EveryStage", null,
                    format == DocumentFormat.Presentation
                        ? "未安装 WPS 或 PowerPoint，且此演示文稿没有可用的转换缓存。请安装 WPS/Office，或在安装了它们的电脑上先打开一次此文件。"
                        : "未安装 WPS 或 Word，无法打开此文档。请安装 WPS/Office，或将文档另存为 PDF 后导入。");
            Exception? last = null;
            foreach (var suite in suites)
            {
                try { return await ConvertWithAsync(sourcePath, suite, kind).ConfigureAwait(false); }
                catch (Exception ex) { last = ex; _log.LogFailure("转换", sourcePath, suite.ToString(), null, ex); }
            }
            throw last as OfficeOperationException
                ?? new OfficeOperationException(OfficeStage.Convert, "EveryStage", null, "文档转换失败：" + last?.Message, last);
        }
        finally { _gate.Release(); }
    }

    private async Task<Entry> ConvertWithAsync(string sourcePath, OfficeSuite suite, OfficeAppKind kind)
    {
        string key = KeyFor(sourcePath);
        string folder = Path.Combine(_root, key);
        string staging = folder + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staging);
        using var dispatcher = new StaDispatcher("EveryStage document conversion");
        var controller = new OfficeApplicationController(suite, kind);
        var (pages, notes) = await dispatcher.InvokeAsync(() =>
        {
            try
            {
                controller.Start();
                if (kind == OfficeAppKind.Writer) controller.OpenWriter(sourcePath, readOnly: true, visible: false);
                else controller.OpenPresentation(sourcePath, withWindow: false);
                string pdf = Path.Combine(staging, "document.pdf");
                controller.SaveAsPdf(pdf);
                int count = kind == OfficeAppKind.Presentation ? controller.SlideCount : 0;
                var slideNotes = new string[count];
                for (int i = 0; i < count; i++) slideNotes[i] = controller.SlideNotes(i + 1);
                return (count, slideNotes);
            }
            finally { controller.Quit(); }
        }, ConvertTimeout).ConfigureAwait(false);
        var info = new FileInfo(sourcePath);
        var meta = new Metadata(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, $"{controller.SuiteName} {controller.Version}", pages, notes);
        File.WriteAllText(Path.Combine(staging, "meta.json"), JsonSerializer.Serialize(meta));
        if (!File.Exists(Path.Combine(staging, "document.pdf")))
            throw new OfficeOperationException(OfficeStage.Convert, controller.SuiteName, controller.Version, "转换没有生成 PDF。");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.Move(staging, folder);
        _log.LogEvent("converted", sourcePath, controller.SuiteName, controller.Version, $"pages={pages}");
        Prune();
        return new Entry(Path.Combine(folder, "document.pdf"), meta);
    }

    private void Prune()
    {
        try
        {
            var folders = new DirectoryInfo(_root).GetDirectories().OrderByDescending(d => d.LastAccessTimeUtc).ToList();
            foreach (var old in folders.Skip(KeepEntries).Concat(folders.Where(d => d.Name.Contains(".tmp-"))))
            {
                try { old.Delete(true); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
