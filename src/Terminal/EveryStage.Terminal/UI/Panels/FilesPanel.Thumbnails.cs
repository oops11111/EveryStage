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

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        // One running pass per token: Refresh_ and OnHandleCreated (handles are recreated on some property
        // changes) can both start a pass with the current token, and two interleaved passes raced the cache
        // eviction below (audit B-7). A later call with the same token after the pass ended runs again.
        if (_thumbnailPassRunning && _thumbnailPassToken == token) return;
        _thumbnailPassToken = token;
        _thumbnailPassRunning = true;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var file = _grid.VisibleItems().Select(item => item.File)
                    .FirstOrDefault(candidate => !_thumbnailByFileId.ContainsKey(candidate.Id.ToString()));
                if (file == null) break;
                await _thumbnailWorker.WaitAsync(token);
                Image thumbnail;
                string? cacheKey = null;
                string meta = "", toolTip = "";
                try
                {
                    (cacheKey, meta, toolTip) = await Task.Run(() =>
                        (ThumbnailCacheKey(file), CardMeta(file), CardToolTip(file)), token);
                    token.ThrowIfCancellationRequested();
                    if (cacheKey != null && _thumbnailCache.TryGetValue(cacheKey, out var cached))
                        thumbnail = (Image)cached.Clone();
                    else
                        thumbnail = await Task.Run(() =>
                        {
                            // One unreadable file gets a warning tile; an exception type BuildThumbnail's
                            // per-kind filters miss (e.g. InvalidDataException from a PDF with bad page
                            // sizes) used to end this whole pass, leaving every later card blank.
                            try { return ThumbnailDiskCache.Shared.GetOrBuild(file); }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Trace.TraceError("Thumbnail for {0} failed: {1}", file.SourcePath, ex);
                                return BuildKindTile("预览不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65));
                            }
                        }, token);
                }
                finally { _thumbnailWorker.Release(); }
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
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Thumbnail loading failed: {0}", ex);
        }
        finally
        {
            if (_thumbnailPassToken == token) _thumbnailPassRunning = false;
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
}
