using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class ActivitiesPanel
{
    private async Task LoadThumbnailAsync(MediaFile file, string key, int generation)
    {
        Image? thumbnail = null;
        bool unavailable = false;
        string? sourceKey = null;
        try
        {
            sourceKey = await Task.Run(() => FilesPanel.ThumbnailCacheKey(file));
            unavailable = sourceKey == null;
            // 性能审查 P-21: builds go through the shared scheduler (concurrency cap + in-flight
            // dedup shared with the 文件 page) instead of an unbounded Task.Run per row.
            try { thumbnail = await ThumbnailDiskCache.Shared.GetOrBuildSharedAsync(file); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("Activity thumbnail failed: {0}", ex);
                thumbnail = null;
            }
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Activity thumbnail failed: {0}", ex); }
        if (IsDisposed || Disposing || generation != _thumbnailGeneration) { thumbnail?.Dispose(); return; }
        _thumbnailUnavailable[key] = unavailable;
        _thumbnailSourceKeys[key] = sourceKey;
        if (thumbnail != null) _thumbnails[key] = thumbnail;
        foreach (Control row in _activityRows.Controls) row.Invalidate();
    }

    private Dictionary<string, MediaFile> VisibleFiles() => _activityRows.Controls.OfType<ActivityRowView>()
        .Where(row => row.Visible && row.Bounds.IntersectsWith(_activityRows.ClientRectangle))
        .SelectMany(row => row.VisibleFiles(_activityRows.ClientRectangle))
        .GroupBy(file => $"{file.Kind}|{file.SourcePath}", StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    private async Task RefreshVisibleFileStatesAsync()
    {
        if (_visibleFileCheckRunning || !Visible || !IsHandleCreated || IsDisposed || Disposing) return;
        int generation = _thumbnailGeneration;
        var snapshot = VisibleFiles().Where(entry => _thumbnailSourceKeys.ContainsKey(entry.Key))
            .Select(entry => (entry.Key, File: entry.Value, Previous: _thumbnailSourceKeys[entry.Key])).ToArray();
        if (snapshot.Length == 0) return;
        _visibleFileCheckRunning = true;
        try
        {
            var observed = await Task.Run(() => snapshot.Select(entry =>
                (entry.Key, entry.File, entry.Previous, Current: FilesPanel.ThumbnailCacheKey(entry.File))).ToArray());
            if (IsDisposed || Disposing || !Visible || generation != _thumbnailGeneration) return;
            var visible = VisibleFiles();
            bool changed = false;
            foreach (var entry in observed)
            {
                if (!visible.ContainsKey(entry.Key) || !_thumbnailSourceKeys.TryGetValue(entry.Key, out var current)
                    || current != entry.Previous || current == entry.Current) continue;
                if (_thumbnails.Remove(entry.Key, out var old)) old.Dispose();
                _thumbnailSourceKeys.Remove(entry.Key);
                _thumbnailUnavailable.Remove(entry.Key);
                _thumbnailsRequested.Remove(entry.Key);
                ((IActivityRowHost)this).ThumbnailFor(entry.File);
                changed = true;
            }
            if (changed) foreach (Control row in _activityRows.Controls) row.Invalidate();
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Visible activity file check failed: {0}", ex); }
        finally { _visibleFileCheckRunning = false; }
    }
}
