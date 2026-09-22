using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveryStage.Terminal.Data;

/// <summary>
/// The file library backing PLANNING.md §8.2's 文件面板 ("默认首页...缩略图网格") — files imported
/// onto the Terminal but not (yet, or ever) placed into any <see cref="Activity"/>. This is a gap
/// the original data model (<see cref="Scenario"/>/<see cref="Activity"/>/<see cref="MediaFile"/>)
/// didn't cover: §6 only describes files as they exist *inside* an activity, but §8.2's file panel
/// implies a broader library you drag files out of and into activities. Reuses <see cref="MediaFile"/>
/// itself rather than inventing a parallel type — a library entry and an activity's file slot are
/// the same shape, just living in a different list (this store's vs. an Activity's <c>Files</c>).
/// </summary>
public sealed class FileLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _storePath;
    private List<MediaFile> _files;
    private bool _loadFailed;

    public FileLibraryStore(string? storePathOverride = null)
    {
        _storePath = storePathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EveryStage", "file-library.json");
        _files = Load();
    }

    public IReadOnlyList<MediaFile> Files => _files;

    /// <summary>Infers <see cref="MediaKind"/> from the file extension. Returns null for anything
    /// unrecognized rather than guessing — an unsupported file silently imported as the wrong kind
    /// would fail confusingly later (wrong renderer picked) instead of failing clearly now.
    ///
    /// PPT/Word/Excel extensions map to the same <see cref="MediaKind.Document"/> value a PDF does —
    /// PLANNING.md §6 treats them as one "文档" kind at the data-model level even though
    /// <c>PlaybackEngine</c> picks an entirely different renderer for them
    /// (<c>ContentEngine.WpsDocumentController</c>'s real, editable WPS window vs.
    /// <c>PdfContentRenderer</c>'s rasterized bitmap) — see
    /// <c>ContentEngine.WpsDocumentController.IsOfficeDocument</c>'s own doc comment for why that
    /// split happens by re-checking the extension at play time instead of a second enum value
    /// here.</summary>
    public static MediaKind? InferKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" => MediaKind.Image,
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".m4v" => MediaKind.Video,
        ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => MediaKind.Document,
        ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".wma" => MediaKind.Audio,
        _ => null,
    };

    /// <summary>Returns null (and imports nothing) if <paramref name="sourcePath"/>'s extension
    /// isn't recognized.</summary>
    public MediaFile? Import(string sourcePath)
    {
        EnsureWritable();
        var kind = InferKind(sourcePath);
        if (kind == null) return null;

        var file = new MediaFile { SourcePath = sourcePath, Kind = kind.Value };
        _files.Add(file);
        try { Save(); }
        catch { _files.Remove(file); throw; }
        return file;
    }

    public void Remove(Guid id)
    {
        EnsureWritable();
        var previous = _files.ToList();
        _files.RemoveAll(f => f.Id == id);
        try { Save(); }
        catch { _files = previous; throw; }
    }

    private List<MediaFile> Load()
    {
        try
        {
            using var stream = File.OpenRead(_storePath);
            return JsonSerializer.Deserialize<List<MediaFile>>(stream, JsonOptions) ?? new List<MediaFile>();
        }
        catch (FileNotFoundException) { return new List<MediaFile>(); }
        catch (DirectoryNotFoundException) { return new List<MediaFile>(); }
        catch (JsonException)
        {
            // Same fail-safe as ScenarioRepository/PairedDeviceStore: an unattended device must not
            // crash-loop on a corrupt library file.
            File.Copy(_storePath, _storePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            return new List<MediaFile>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadFailed = true;
            // Different failure mode from JsonException above, and deliberately handled without
            // touching the file: File.Exists returning true above doesn't mean File.OpenRead can
            // actually succeed — another process (antivirus scan, backup tool) can hold an exclusive
            // lock, or a permissions problem can block the read entirely. Unlike the JsonException
            // branch, this says nothing about whether the file's CONTENT is bad, so renaming it aside
            // the same way would risk permanently hiding perfectly good data under a filename Load()
            // never looks for again just because it was momentarily locked. Fail safe to an empty
            // library for this run only, and leave the file untouched so a later restart (once
            // whatever is locking it lets go) can read it normally.
            return new List<MediaFile>();
        }
    }

    /// <summary><see cref="Files"/> hands back the live, mutable <see cref="MediaFile"/> instances
    /// themselves (not copies) — a caller can already mutate one directly (e.g.
    /// <c>FilesPanel</c>'s "循环" checkbox flipping a library entry's own
    /// <see cref="MediaFile.OnCompletion"/>, the first thing to ever need this); this just exposes the
    /// already-existing persistence step (previously only reachable from inside <see cref="Import"/>/
    /// <see cref="Remove"/>) for after doing so. Same shape as <see cref="ScenarioRepository.Save"/>,
    /// just an instance method rather than taking an external store parameter — this class owns its
    /// own list internally, unlike <see cref="ScenarioRepository"/>, which is a stateless repository
    /// operating on an externally-owned <see cref="ScenarioStore"/>.</summary>
    public void Save()
    {
        EnsureWritable();
        string directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);

        string tempPath = _storePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, _files, JsonOptions);
        }

        if (File.Exists(_storePath))
            File.Replace(tempPath, _storePath, destinationBackupFileName: null);
        else
            File.Move(tempPath, _storePath);
    }

    private void EnsureWritable()
    {
        if (_loadFailed)
            throw new IOException("文件库读取失败，已禁止保存以保护原数据。请解除文件占用或权限问题后重启程序。");
    }
}
