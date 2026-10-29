using System.Text.Json;

namespace EveryStage.Terminal.Data;

/// <summary>How a playlist continues after a track ends.</summary>
public enum PlaylistMode { LoopList, PlayOnce, LoopOne, Shuffle }

/// <summary>A background-music playlist: an ordered list of audio files.</summary>
public sealed class Playlist
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public List<string> Tracks { get; init; } = new();
    public PlaylistMode Mode { get; set; } = PlaylistMode.LoopList;

    /// <summary>Start this playlist when the Terminal starts (at most one playlist has it set).</summary>
    public bool AutoPlayOnStartup { get; set; }
}

/// <summary>Everything the background-music channel persists: the playlists and its mixer settings.</summary>
public sealed class MusicLibrary
{
    public List<Playlist> Playlists { get; init; } = new();
    public Guid? CurrentPlaylistId { get; set; }
    public float Volume { get; set; } = 0.8f;

    /// <summary>Lower the music to <see cref="DuckLevel"/> while the big screen plays video with sound.</summary>
    public bool DuckForVideo { get; set; } = true;
    public float DuckLevel { get; set; } = 0.2f;

    public Playlist? Current => Playlists.FirstOrDefault(p => p.Id == CurrentPlaylistId) ?? Playlists.FirstOrDefault();
}

/// <summary>
/// Persists <see cref="MusicLibrary"/> as <c>music.json</c> in the data folder, with the same atomic
/// write (temp file, flush to disk, replace) and fail-safe load as <see cref="SettingsStore"/>: a corrupt
/// file is kept aside and replaced by an empty library; a locked file is left untouched and saving is
/// refused for this run so it cannot be overwritten with an empty one.
/// </summary>
public sealed class MusicLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _storePath;
    private bool _loadFailed;

    public MusicLibrary Library { get; }

    public MusicLibraryStore(string? storePathOverride = null)
    {
        _storePath = storePathOverride ?? Path.Combine(DataDirectory.Root, "music.json");
        Library = Load();
    }

    public void Save()
    {
        if (_loadFailed) throw new IOException("歌单文件读取失败，已阻止覆盖原文件；请先修复文件或重启后重试。");
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        string tempPath = _storePath + ".tmp";
        try
        {
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, Library, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_storePath)) File.Replace(tempPath, _storePath, destinationBackupFileName: null);
            else File.Move(tempPath, _storePath);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private MusicLibrary Load()
    {
        if (!File.Exists(_storePath)) return new MusicLibrary();
        try
        {
            using var stream = File.OpenRead(_storePath);
            return JsonSerializer.Deserialize<MusicLibrary>(stream, JsonOptions) ?? new MusicLibrary();
        }
        catch (JsonException)
        {
            try { File.Copy(_storePath, _storePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true); } catch { }
            return new MusicLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadFailed = true;
            return new MusicLibrary();
        }
    }
}
