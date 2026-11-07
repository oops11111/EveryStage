using System.Drawing.Drawing2D;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class FilesPanel
{
    private void OnThumbnailViewportChanged()
    {
        if (_thumbnailLoad == null || IsDisposed || Disposing) return;
        var visibleIds = _grid.VisibleItems().Select(item => item.File.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string key in _thumbnailByFileId.Keys.Where(key => !visibleIds.Contains(key)).ToArray())
        {
            _thumbnailByFileId[key].Dispose();
            _thumbnailByFileId.Remove(key);
            _visibleThumbnailKeys.Remove(key);
        }
        if (IsHandleCreated) _ = LoadThumbnailsAsync(_thumbnailLoad.Token);
    }

    private async Task RefreshVisibleFileStatesAsync()
    {
        if (_visibleFileCheckRunning || !Visible || !IsHandleCreated || _thumbnailLoad == null || IsDisposed || Disposing) return;
        var token = _thumbnailLoad.Token;
        var snapshot = _grid.VisibleItems()
            .Where(item => _visibleThumbnailKeys.ContainsKey(item.File.Id.ToString()))
            .Select(item => (Item: item, PreviousKey: _visibleThumbnailKeys[item.File.Id.ToString()])).ToArray();
        if (snapshot.Length == 0) return;
        _visibleFileCheckRunning = true;
        try
        {
            var observed = await Task.Run(() => snapshot.Select(entry =>
                (entry.Item, entry.PreviousKey, CurrentKey: ThumbnailCacheKey(entry.Item.File))).ToArray());
            if (token.IsCancellationRequested || IsDisposed || Disposing || !Visible) return;
            var visible = _grid.VisibleItems().ToHashSet();
            bool changed = false;
            foreach (var entry in observed)
            {
                string id = entry.Item.File.Id.ToString();
                if (!visible.Contains(entry.Item) || !_visibleThumbnailKeys.TryGetValue(id, out var current)
                    || current != entry.PreviousKey || current == entry.CurrentKey) continue;
                if (_thumbnailByFileId.Remove(id, out var old)) old.Dispose();
                _visibleThumbnailKeys.Remove(id);
                changed = true;
            }
            if (changed)
            {
                _grid.Invalidate();
                _ = LoadThumbnailsAsync(token);
            }
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Visible file check failed: {0}", ex); }
        finally { _visibleFileCheckRunning = false; }
    }

    /// <summary>How many thumbnails one pass has in flight at once (性能审查 P-18: the pass used
    /// to build strictly one at a time). The builds themselves run through
    /// <see cref="ThumbnailDiskCache"/>'s shared scheduler — same cap, plus in-flight dedup shared
    /// with the 流程 page — and results are still APPLIED strictly in visible order below, so
    /// cards fill in top-to-bottom exactly as before, just sooner.</summary>
    private const int ThumbnailBatchSize = 3;

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        // One running pass per token: Refresh_ and OnHandleCreated (handles are recreated on some property
        // changes) can both start a pass with the current token, and two interleaved passes raced the cache
        // eviction below (audit B-7). A later call with the same token after the pass ended runs again.
        if (_thumbnailPassRunning && _thumbnailPassToken == token) return;
        _thumbnailPassToken = token;
        _thumbnailPassRunning = true;
        var pending = new List<(MediaFile File, Task<(string? CacheKey, string Meta, string ToolTip, Image Thumbnail)> Load)>();
        var applied = new List<bool>();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var batch = _grid.VisibleItems().Select(item => item.File)
                    .Where(candidate => !_thumbnailByFileId.ContainsKey(candidate.Id.ToString()))
                    .Take(ThumbnailBatchSize).ToList();
                if (batch.Count == 0) break;
                pending.Clear();
                applied.Clear();
                foreach (var file in batch)
                {
                    pending.Add((file, LoadOneThumbnailAsync(file, token)));
                    applied.Add(false);
                }
                for (int i = 0; i < pending.Count; i++)
                {
                    var file = pending[i].File;
                    var (cacheKey, meta, toolTip, thumbnail) = await pending[i].Load;
                    using (thumbnail)
                    {
                        if (token.IsCancellationRequested || IsDisposed || Disposing) return;
                        if (cacheKey != null && !_thumbnailCache.ContainsKey(cacheKey))
                        {
                            while (_thumbnailCache.Count >= ThumbnailCacheCapacity && _thumbnailCacheOrder.TryDequeue(out var oldest))
                            {
                                _thumbnailCache.Remove(oldest, out var evicted);
                                evicted?.Dispose();
                            }
                            _thumbnailCache.Add(cacheKey, (Image)thumbnail.Clone());
                            _thumbnailCacheOrder.Enqueue(cacheKey);
                        }
                        string key = file.Id.ToString();
                        var visibleItem = _grid.VisibleItems().FirstOrDefault(item => item.File.Id == file.Id);
                        if (visibleItem != null && !_thumbnailByFileId.ContainsKey(key))
                        {
                            visibleItem.Unavailable = cacheKey == null;
                            visibleItem.Meta = meta;
                            visibleItem.ToolTip = toolTip;
                            _thumbnailByFileId.Add(key, (Image)thumbnail.Clone());
                            _visibleThumbnailKeys[key] = cacheKey;
                        }
                        _grid.Invalidate();
                    }
                    applied[i] = true;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Thumbnail loading failed: {0}", ex);
        }
        finally
        {
            // Loads of the last (interrupted) batch that finished but were never applied own an
            // image nobody else will dispose — release those here instead of leaking them.
            for (int i = 0; i < pending.Count; i++)
            {
                if (applied[i]) continue;
                if (pending[i].Load.IsCompletedSuccessfully) pending[i].Load.Result.Thumbnail.Dispose();
                else _ = pending[i].Load.ContinueWith(
                    t => { if (t.IsCompletedSuccessfully) t.Result.Thumbnail.Dispose(); }, TaskScheduler.Default);
            }
            if (_thumbnailPassToken == token) _thumbnailPassRunning = false;
        }
    }

    /// <summary>Loads one card's thumbnail for <see cref="LoadThumbnailsAsync"/>: memory cache
    /// first, then the shared disk-cache scheduler. Never throws except for cancellation — a
    /// file that fails to build becomes the warning tile, as before.</summary>
    private async Task<(string? CacheKey, string Meta, string ToolTip, Image Thumbnail)> LoadOneThumbnailAsync(
        MediaFile file, CancellationToken token)
    {
        var (cacheKey, meta, toolTip) = await Task.Run(() =>
            (ThumbnailCacheKey(file), CardMeta(file), CardToolTip(file)), token);
        token.ThrowIfCancellationRequested();
        if (cacheKey != null && _thumbnailCache.TryGetValue(cacheKey, out var cached))
            return (cacheKey, meta, toolTip, (Image)cached.Clone());
        try
        {
            var built = await ThumbnailDiskCache.Shared.GetOrBuildSharedAsync(file, token);
            return (cacheKey, meta, toolTip, built);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreadable file gets a warning tile; an exception type BuildThumbnail's
            // per-kind filters miss (e.g. InvalidDataException from a PDF with bad page
            // sizes) used to end this whole pass, leaving every later card blank.
            System.Diagnostics.Trace.TraceError("Thumbnail for {0} failed: {1}", file.SourcePath, ex);
            return (cacheKey, meta, toolTip, BuildKindTile("预览不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65)));
        }
    }

    internal static string? ThumbnailCacheKey(MediaFile file)
    {
        try
        {
            var info = new FileInfo(file.SourcePath);
            return info.Exists ? $"{file.Kind}|{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
    internal static Image BuildThumbnail(MediaFile file) => BuildThumbnail(file, out _);

    internal static Image BuildThumbnail(MediaFile file, out bool cacheable)
    {
        cacheable = false;
        const int tw = 240, th = 150;
        if (file.Kind == MediaKind.Document && new[] { ".docx", ".pptx", ".xlsx" }.Contains(Path.GetExtension(file.SourcePath), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var embedded = EveryStage.Terminal.ContentEngine.OfficeThumbnailReader.Read(file.SourcePath, new Size(tw, th));
                if (embedded != null)
                {
                    var thumbnail = new Bitmap(tw, th);
                    try
                    {
                        using var graphics = Graphics.FromImage(thumbnail);
                        graphics.Clear(Color.FromArgb(20, 37, 58));
                        graphics.DrawImageUnscaled(embedded, (tw - embedded.Width) / 2, (th - embedded.Height) / 2);
                        cacheable = true;
                        return thumbnail;
                    }
                    catch { thumbnail.Dispose(); throw; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
            {
                return BuildKindTile("文档预览不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65));
            }
        }
        if (file.Kind == MediaKind.Document && string.Equals(Path.GetExtension(file.SourcePath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var page = EveryStage.Terminal.ContentEngine.PdfContentRenderer.CreateThumbnail(file.SourcePath, new Size(tw, th));
                var thumbnail = new Bitmap(tw, th);
                try
                {
                    using var graphics = Graphics.FromImage(thumbnail);
                    graphics.Clear(Color.FromArgb(20, 37, 58));
                    graphics.DrawImageUnscaled(page, (tw - page.Width) / 2, (th - page.Height) / 2);
                    cacheable = true;
                    return thumbnail;
                }
                catch { thumbnail.Dispose(); throw; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException
                or PdfiumViewer.PdfException or DllNotFoundException)
            {
                return BuildKindTile("PDF 预览不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65));
            }
        }
        if (file.Kind == MediaKind.Image)
        {
            // Fast path (性能审查 P-18): a plain JPEG whose EXIF orientation is provably identity
            // (tag absent or 1) goes through the Shell thumbnail provider — the same reader the
            // video branch below already uses — which decodes AT the target size instead of
            // full-decoding a 24-48MP original just to shrink it to 240x150. Orientation is the
            // one behavioural difference from the GDI+ path (which applies the tag via
            // ExifOrientation.Apply): for an identity tag there is nothing to apply, so the two
            // paths agree by construction. Any other image — rotated phone photos, PNG/GIF/TIFF —
            // keeps the GDI+ path below, and a Shell failure falls through to it as well.
            if (IsJpeg(file.SourcePath) && JpegExifOrientationIsIdentity(file.SourcePath))
            {
                try
                {
                    using var shellPreview = EveryStage.Terminal.ContentEngine.ShellThumbnailReader.Read(file.SourcePath, new Size(tw, th));
                    if (shellPreview != null)
                    {
                        var thumbnail = new Bitmap(tw, th);
                        try
                        {
                            using var graphics = Graphics.FromImage(thumbnail);
                            graphics.Clear(Color.FromArgb(20, 37, 58));
                            ThumbnailDrawing.DrawContained(graphics, shellPreview, new Rectangle(0, 0, tw, th));
                            cacheable = true;
                            return thumbnail;
                        }
                        catch { thumbnail.Dispose(); throw; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                    or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
                {
                    // Shell provider unavailable/refused: fall through to the GDI+ decode below.
                }
            }
            try
            {
                using var stream = File.OpenRead(file.SourcePath);
                using var loaded = Image.FromStream(stream);
                EveryStage.Terminal.ContentEngine.ExifOrientation.Apply(loaded); // same orientation as playback
                var thumbnail = new Bitmap(tw, th);
                using var graphics = Graphics.FromImage(thumbnail);
                graphics.Clear(Color.FromArgb(20, 37, 58));
                ThumbnailDrawing.DrawContained(graphics, loaded, new Rectangle(0, 0, tw, th));
                cacheable = true;
                return thumbnail;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException or ArgumentException)
            {
                // Missing/moved/corrupt file since it was imported — show a warning tile rather
                // than let a bad file crash the whole library view.
                return BuildKindTile("文件不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65));
            }
        }

        if (file.Kind == MediaKind.Video)
        {
            try
            {
                using var shellPreview = EveryStage.Terminal.ContentEngine.ShellThumbnailReader.Read(file.SourcePath, new Size(tw, th));
                if (shellPreview != null)
                {
                    var thumbnail = new Bitmap(tw, th);
                    try
                    {
                        using var graphics = Graphics.FromImage(thumbnail);
                        graphics.Clear(Color.FromArgb(20, 37, 58));
                        ThumbnailDrawing.DrawContained(graphics, shellPreview, new Rectangle(0, 0, tw, th));
                        cacheable = true;
                        return thumbnail;
                    }
                    catch { thumbnail.Dispose(); throw; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                return BuildKindTile("视频预览不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65));
            }
        }

        // Non-image kinds: a clean tinted tile; the colored type badge is drawn over it by
        // DrawMediaCard (so the tile itself no longer carries a letter/caption).
        return file.Kind switch
        {
            MediaKind.Video => BuildKindTile(null, Color.FromArgb(28, 24, 54), Color.FromArgb(40, 34, 78)),
            MediaKind.Document => BuildKindTile(null, Color.FromArgb(30, 44, 70), Color.FromArgb(24, 36, 58)),
            MediaKind.Audio => BuildKindTile(null, Color.FromArgb(20, 44, 42), Color.FromArgb(18, 38, 40)),
            _ => BuildKindTile(null, Color.FromArgb(24, 42, 66), Color.FromArgb(20, 37, 58)),
        };
    }

    private static bool IsJpeg(string path) =>
        Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads just the JPEG header (≤64KB, never a full decode) and reports whether the
    /// EXIF orientation tag (0x0112) is absent or 1 — the only two cases where displaying the raw
    /// pixels unrotated is correct, which is what makes the Shell fast path in
    /// <see cref="BuildThumbnail(MediaFile, out bool)"/> safe. Anything unparseable returns false
    /// (not a JPEG, truncated header, malformed TIFF), so doubt always stays on the GDI+ path
    /// that applies the tag for real.</summary>
    private static bool JpegExifOrientationIsIdentity(string path)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            int filled = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                while (filled < buffer.Length)
                {
                    int read = stream.Read(buffer, filled, buffer.Length - filled);
                    if (read <= 0) break;
                    filled += read;
                }
            }
            var data = buffer.AsSpan(0, filled);
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false; // not a JPEG
            int pos = 2;
            while (pos + 4 <= data.Length)
            {
                if (data[pos] != 0xFF) return false;
                byte marker = data[pos + 1];
                if (marker == 0xFF) { pos++; continue; } // padding byte before a marker
                if (marker == 0xDA || marker == 0xD9) break; // SOS / EOI: metadata segments are over
                if (marker == 0x01 || marker is >= 0xD0 and <= 0xD8) { pos += 2; continue; } // standalone markers carry no length
                int length = (data[pos + 2] << 8) | data[pos + 3];
                if (length < 2) return false;
                if (marker == 0xE1 && pos + 2 + length <= data.Length
                    && data[pos + 4] == (byte)'E' && data[pos + 5] == (byte)'x' && data[pos + 6] == (byte)'i'
                    && data[pos + 7] == (byte)'f' && data[pos + 8] == 0 && data[pos + 9] == 0)
                    return TiffOrientationIsIdentity(data.Slice(pos + 10, length - 8));
                pos += 2 + length;
            }
            return true; // no EXIF segment at all: no orientation to apply
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false; // cannot prove identity: stay on the GDI+ path
        }
    }

    /// <summary>The TIFF block inside a JPEG Exif APP1 segment: finds IFD0's orientation entry.
    /// A SHORT with count 1 keeps its value in the first two bytes of the 4-byte value field,
    /// in the TIFF's own byte order.</summary>
    private static bool TiffOrientationIsIdentity(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return false;
        bool littleEndian = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (!littleEndian && !(tiff[0] == (byte)'M' && tiff[1] == (byte)'M')) return false;
        if (TiffU16(tiff, 2, littleEndian) != 42) return false;
        int ifd = (int)TiffU32(tiff, 4, littleEndian);
        if (ifd < 8 || ifd + 2 > tiff.Length) return false;
        int entries = TiffU16(tiff, ifd, littleEndian);
        for (int i = 0; i < entries; i++)
        {
            int entry = ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) return false; // truncated IFD: cannot prove identity
            if (TiffU16(tiff, entry, littleEndian) != 0x0112) continue;
            return TiffU16(tiff, entry + 8, littleEndian) == 1;
        }
        return true; // EXIF present but carries no orientation tag
    }

    private static ushort TiffU16(ReadOnlySpan<byte> data, int offset, bool littleEndian) => littleEndian
        ? (ushort)(data[offset] | (data[offset + 1] << 8))
        : (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint TiffU32(ReadOnlySpan<byte> data, int offset, bool littleEndian) => littleEndian
        ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
        : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
