using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>Best-effort thumbnail cache; the source remains authoritative.</summary>
internal sealed class ThumbnailDiskCache
{
    private const int MaxEntries = 256;
    private const long MaxBytes = 32L * 1024 * 1024;
    private readonly string _root;
    private readonly object _gate = new();
    internal static ThumbnailDiskCache Shared { get; set; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveryStage", "ThumbnailCache"));

    internal ThumbnailDiskCache(string root) => _root = root;

    internal Image GetOrBuild(MediaFile file)
    {
        string? key = SourceKey(file);
        if (key != null)
        {
            lock (_gate)
            {
                string path = Path.Combine(_root, key + ".png");
                try
                {
                    if (File.Exists(path))
                    {
                        using var stream = File.OpenRead(path);
                        if (stream.Length > 2 * 1024 * 1024) throw new ArgumentException("Cached thumbnail is too large.");
                        using var image = Image.FromStream(stream);
                        if (image.Width != 240 || image.Height != 150) throw new ArgumentException("Invalid cached thumbnail dimensions.");
                        if (SourceKey(file) == key) return new Bitmap(image);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                    or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
                {
                    System.Diagnostics.Trace.TraceWarning("Thumbnail cache read failed: {0}", ex.Message);
                    try { File.Delete(path); } catch (Exception) { }
                }
            }
        }

        var result = FilesPanel.BuildThumbnail(file, out bool cacheable);
        if (cacheable && key != null && SourceKey(file) == key) Save(key, result);
        return result;
    }

    private void Save(string key, Image image)
    {
        lock (_gate)
        {
            string? temp = null;
            try
            {
                Directory.CreateDirectory(_root);
                temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
                image.Save(temp, ImageFormat.Png);
                File.Move(temp, Path.Combine(_root, key + ".png"), overwrite: true);
                temp = null;
                long bytes = 0;
                int count = 0;
                foreach (var entry in new DirectoryInfo(_root).EnumerateFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    count++;
                    bytes += entry.Length;
                    if (count > MaxEntries || bytes > MaxBytes) entry.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or System.Runtime.InteropServices.ExternalException)
            {
                System.Diagnostics.Trace.TraceWarning("Thumbnail cache write failed: {0}", ex.Message);
            }
            finally
            {
                if (temp != null) try { File.Delete(temp); } catch (Exception) { }
            }
        }
    }

    private static string? SourceKey(MediaFile file)
    {
        try
        {
            var info = new FileInfo(file.SourcePath);
            if (!info.Exists) return null;
            string source = $"thumbnail-contain-v1|{file.Kind}|{info.FullName.ToUpperInvariant()}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
