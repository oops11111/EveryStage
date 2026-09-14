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
    /// would fail confusingly later (wrong renderer picked) instead of failing clearly now.</summary>
    public static MediaKind? InferKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" => MediaKind.Image,
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".m4v" => MediaKind.Video,
        ".pdf" => MediaKind.Document,
        ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".wma" => MediaKind.Audio,
        _ => null,
    };

    /// <summary>Returns null (and imports nothing) if <paramref name="sourcePath"/>'s extension
    /// isn't recognized.</summary>
    public MediaFile? Import(string sourcePath)
    {
        var kind = InferKind(sourcePath);
        if (kind == null) return null;

        var file = new MediaFile { SourcePath = sourcePath, Kind = kind.Value };
        _files.Add(file);
        Save();
        return file;
    }

    public void Remove(Guid id)
    {
        _files.RemoveAll(f => f.Id == id);
        Save();
    }

    private List<MediaFile> Load()
    {
        if (!File.Exists(_storePath)) return new List<MediaFile>();

        try
        {
            using var stream = File.OpenRead(_storePath);
            return JsonSerializer.Deserialize<List<MediaFile>>(stream, JsonOptions) ?? new List<MediaFile>();
        }
        catch (JsonException)
        {
            // Same fail-safe as ScenarioRepository/PairedDeviceStore: an unattended device must not
            // crash-loop on a corrupt library file.
            File.Copy(_storePath, _storePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            return new List<MediaFile>();
        }
    }

    private void Save()
    {
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
}
