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

    // ── Shared build scheduling (性能审查 P-21) ─────────────────────────────────────────────
    // Both thumbnail consumers (文件 page and 流程 page) used to schedule builds on their own —
    // one strictly serial, the other unbounded — and the same source file visible on both pages
    // was decoded twice. All builds now funnel through GetOrBuildSharedAsync: at most
    // MaxConcurrentBuilds run at once across both pages, and concurrent requests for the same
    // cache key share ONE build instead of decoding in parallel.

    private const int MaxConcurrentBuilds = 3;
    private readonly SemaphoreSlim _buildGate = new(MaxConcurrentBuilds, MaxConcurrentBuilds);
    private readonly object _sharedGate = new();
    private readonly Dictionary<string, SharedBuild> _sharedBuilds = new();

    /// <summary>One in-flight build of a thumbnail. The master image is owned by the entry, never
    /// by a caller: every caller clones it (under <see cref="_sharedGate"/>, before dropping its
    /// waiter count), and the entry disposes the master once the build has completed AND the last
    /// waiter has taken its clone.</summary>
    private sealed class SharedBuild
    {
        public required Task<Image> Task;
        public int Waiters;
        public bool Completed;
    }

    /// <summary>Builds (or joins the in-flight build of) the thumbnail for <paramref name="file"/>,
    /// returning an image the CALLER owns and must dispose. Concurrent calls for the same source
    /// file share a single underlying build; a caller's cancellation only detaches that caller —
    /// the build itself continues, since its result also lands in the disk cache for everyone
    /// else. Build failures fault every joined caller with the same exception, exactly as if each
    /// had called <see cref="GetOrBuild"/> itself.</summary>
    internal async Task<Image> GetOrBuildSharedAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        string? key = SourceKey(file);
        if (key == null)
        {
            // No stable identity (the file is missing or unreadable): nothing to dedup by, but
            // the global concurrency cap still applies.
            await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await Task.Run(() => GetOrBuild(file)).ConfigureAwait(false); }
            finally { _buildGate.Release(); }
        }

        SharedBuild entry;
        lock (_sharedGate)
        {
            if (!_sharedBuilds.TryGetValue(key, out var found))
            {
                entry = new SharedBuild { Task = BuildMasterAsync(file) };
                _sharedBuilds[key] = entry;
                _ = entry.Task.ContinueWith(_ =>
                {
                    lock (_sharedGate)
                    {
                        entry.Completed = true;
                        ReleaseIfDone(key, entry);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else entry = found;
            entry.Waiters++;
        }

        try
        {
            var master = await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_sharedGate)
            {
                var clone = (Image)master.Clone();
                entry.Waiters--;
                ReleaseIfDone(key, entry);
                return clone;
            }
        }
        catch
        {
            lock (_sharedGate)
            {
                entry.Waiters--;
                ReleaseIfDone(key, entry);
            }
            throw;
        }
    }

    private async Task<Image> BuildMasterAsync(MediaFile file)
    {
        await _buildGate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => GetOrBuild(file)).ConfigureAwait(false); }
        finally { _buildGate.Release(); }
    }

    /// <summary>Removes <paramref name="entry"/> and disposes its master image once the build has
    /// finished and every waiter has cloned (or detached). Caller must hold
    /// <see cref="_sharedGate"/>.</summary>
    private void ReleaseIfDone(string key, SharedBuild entry)
    {
        if (!entry.Completed || entry.Waiters > 0) return;
        if (_sharedBuilds.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            _sharedBuilds.Remove(key);
        if (entry.Task.IsCompletedSuccessfully) entry.Task.Result.Dispose();
    }

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
