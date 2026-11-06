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
    /// <summary>The schema this build writes. Version 0 is the original on-disk shape — the file
    /// was a bare JSON array of <see cref="MediaFile"/> with no version anywhere — and version 1
    /// wraps that same list in an envelope (<see cref="LibraryEnvelope"/>) so a future format
    /// change can be detected instead of silently misread, the same reason
    /// <c>AppSettings.SchemaVersion</c> and <c>ScenarioStore.SchemaVersion</c> exist.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>On-disk envelope for schema version 1+. The bare-array shape it replaces is still
    /// accepted on load (see <see cref="DeserializeLibrary"/>); saves always write this.</summary>
    private sealed class LibraryEnvelope
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<MediaFile> Files { get; set; } = new();
    }

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
        // .webp is deliberately NOT accepted: images decode through GDI+ (ImageContentRenderer), which
        // has no WebP codec, so a WebP import could never preview. Existing library entries keep their
        // stored kind and show a clear "unsupported format" card. (Follow-up: decode WebP via WIC.)
        // GIF shows its first frame and TIFF its first page (labelled in Preview).
        ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff" => MediaKind.Image,
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".m4v" => MediaKind.Video,
        _ when EveryStage.Terminal.Documents.DocumentFormats.IsImportable(path) => MediaKind.Document, // spreadsheets excluded
        ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".wma" => MediaKind.Audio,
        _ => null,
    };

    /// <summary>Returns null (and imports nothing) if <paramref name="sourcePath"/>'s extension
    /// isn't recognized.</summary>
    /// <summary>「播完后」 given to newly imported files (设置 · 播放行为; 定格 unless changed).</summary>
    public CompletionAction DefaultCompletion { get; set; } = CompletionAction.HoldOnLastFrame;

    /// <summary>Container-level transcode heuristic for a video file, judged from its extension
    /// alone at import time. MP4 is the one container this product commits to playing
    /// (H.264 + AAC); the other accepted video containers may hold codecs the decode pipeline
    /// cannot handle, and there is no way to know from the name — so they are flagged as
    /// possibly needing a transcode rather than failing silently at show time. This is only a
    /// hint: a decode probe at playback time always outranks it, and the flag never blocks
    /// anything. Non-video kinds and unknown extensions are not judged here (null).</summary>
    public static string? TranscodeRiskFor(string path, MediaKind kind)
    {
        if (kind != MediaKind.Video) return null;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".mkv" or ".mov" or ".avi" or ".wmv" or ".m4v" =>
                $"容器为 {extension.TrimStart('.').ToUpperInvariant()}，导入时无法确认内部编码，可能需要转码为 MP4（H.264 + AAC）才能播放",
            _ => null,
        };
    }

    public MediaFile? Import(string sourcePath)
    {
        EnsureWritable();
        var kind = InferKind(sourcePath);
        if (kind == null) return null;

        var file = new MediaFile { SourcePath = sourcePath, Kind = kind.Value, OnCompletion = DefaultCompletion };
        if (TranscodeRiskFor(sourcePath, kind.Value) is { } transcodeReason)
        {
            file.NeedsTranscode = true;
            file.TranscodeReason = transcodeReason;
        }
        _files.Add(file);
        try { Save(); }
        catch { _files.Remove(file); throw; }
        return file;
    }

    /// <summary>Moves <paramref name="ids"/> (kept in their current relative order) so they sit just before
    /// <paramref name="beforeId"/>, or at the end when it is null. Saved; restored if the save fails.</summary>
    public void Move(IReadOnlyCollection<Guid> ids, Guid? beforeId)
    {
        var previous = _files.ToList();
        var moving = _files.Where(f => ids.Contains(f.Id)).ToList();
        if (moving.Count == 0) return;
        _files.RemoveAll(f => ids.Contains(f.Id));
        int index = beforeId is { } id ? _files.FindIndex(f => f.Id == id) : -1;
        if (index < 0) index = _files.Count;
        _files.InsertRange(index, moving);
        try { Save(); }
        catch { _files = previous; throw; }
    }

    public void Remove(Guid id)
    {
        EnsureWritable();
        var previous = _files.ToList();
        _files.RemoveAll(f => f.Id == id);
        try { Save(); }
        catch { _files = previous; throw; }
    }

    /// <summary>Parses a library file in either on-disk shape: a bare JSON array (schema v0,
    /// everything written before the envelope existed) or the v1+ envelope object.
    /// <paramref name="newerSchema"/> is set instead of throwing when an envelope's version is
    /// higher than this build understands, so the caller can protect the file from saves while
    /// still running on an empty library.</summary>
    private static List<MediaFile> DeserializeLibrary(string path, out bool newerSchema)
    {
        newerSchema = false;
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return document.RootElement.Deserialize<List<MediaFile>>(JsonOptions) ?? new List<MediaFile>();
        var envelope = document.RootElement.Deserialize<LibraryEnvelope>(JsonOptions);
        if (envelope == null) return new List<MediaFile>();
        if (envelope.SchemaVersion > CurrentSchemaVersion)
        {
            newerSchema = true;
            return new List<MediaFile>();
        }
        return envelope.Files ?? new List<MediaFile>();
    }

    private List<MediaFile> Load()
    {
        try
        {
            var files = DeserializeLibrary(_storePath, out bool newerSchema);
            // Envelope written by a newer EveryStage: leave the file untouched and refuse every
            // save (EnsureWritable checks _loadFailed), exactly like the settings/scenario
            // stores' newer-schema guards, so a downgrade can't flatten data it doesn't know.
            if (newerSchema) _loadFailed = true;
            return files;
        }
        catch (FileNotFoundException) { return new List<MediaFile>(); }
        catch (DirectoryNotFoundException) { return new List<MediaFile>(); }
        catch (JsonException)
        {
            // Same fail-safe as ScenarioRepository/PairedDeviceStore: an unattended device must not
            // crash-loop on a corrupt library file.
            File.Copy(_storePath, _storePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            // Before giving up to an empty library, try the previous version Save kept as .bak —
            // one corrupted write then recovers the library as of the save before it. The .bak is
            // read by the same format-detecting parser (it may predate the envelope), and a .bak
            // that is itself corrupt or newer-schema is no better than no .bak at all.
            try
            {
                var backup = DeserializeLibrary(_storePath + ".bak", out bool backupNewer);
                if (backupNewer) _loadFailed = true;
                return backup;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new List<MediaFile>();
            }
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
            // Always the envelope shape, even when the file on disk was a legacy bare array —
            // loading an old library and saving it once upgrades it in place.
            JsonSerializer.Serialize(stream, new LibraryEnvelope { SchemaVersion = CurrentSchemaVersion, Files = _files }, JsonOptions);
            // Flushed all the way to the device, not just out of the .NET buffer. File.Replace's rename is a
            // journalled NTFS metadata operation, but the temp file's DATA blocks are not journalled with it -
            // closing the stream only hands them to the OS cache. Lose power in the window where the rename is
            // durable and the contents are not, and a zero-length or truncated file has atomically replaced a
            // good one: exactly the outcome this class's atomic-write comment promises cannot happen. One
            // FlushFileBuffers closes that window.
            stream.Flush(flushToDisk: true);
        }

        // The replaced file is kept as .bak (rather than discarded) so Load can fall back to
        // the previous version if this one is later found corrupt.
        if (File.Exists(_storePath))
            File.Replace(tempPath, _storePath, destinationBackupFileName: _storePath + ".bak");
        else
            File.Move(tempPath, _storePath);
    }

    private void EnsureWritable()
    {
        if (_loadFailed)
            throw new IOException("文件库读取失败，已禁止保存以保护原数据。请解除文件占用或权限问题后重启程序。");
    }
}
