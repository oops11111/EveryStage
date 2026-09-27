using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.Playback;

/// <summary>Pure queue navigation rules shared by playback and hardware-free tests.</summary>
public static class PlaybackQueueNavigator
{
    /// <summary>Where <see cref="FindNextPlayable"/> should start scanning from, resolved by file
    /// identity rather than a cached index.
    ///
    /// A cached index goes stale the moment the activity's file list is mutated underneath a playing
    /// file, which the UI does freely: ActivitiesPanel's add/remove/reorder buttons and the playback
    /// error Toast's 移除 action all edit Activity.Files directly and none of them tell PlaybackEngine.
    /// Trusting the cached index there silently skips files - removing anything positioned before the
    /// current file shifts every later index down by one, so the next advance lands one past where it
    /// should and the file that moved into the gap never plays at all.
    ///
    /// Two cases. If the current file is still in the list, its present position is the truth,
    /// whatever the cached index says. If it is gone (removed while playing), the slot it used to
    /// occupy now holds whatever shifted into it - so scanning forward starts one slot earlier, which
    /// makes that replacement the first candidate instead of skipping over it, while scanning
    /// backward starts at the vacated slot itself, since everything before it is unmoved.</summary>
    public static int ResolveScanOrigin(Activity activity, MediaFile? currentFile, int cachedIndex, int delta)
    {
        if (delta is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(delta));

        if (currentFile != null)
        {
            int actual = activity.Files.IndexOf(currentFile);
            if (actual >= 0) return actual;
        }

        return delta > 0 ? cachedIndex - 1 : cachedIndex;
    }
    public static int FindNextPlayable(
        Activity activity,
        int currentIndex,
        int delta,
        Action<MediaFile>? backgroundAudioEncountered = null)
    {
        if (delta is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(delta));

        int candidateIndex = currentIndex;
        while (true)
        {
            candidateIndex += delta;
            if (candidateIndex < 0 || candidateIndex >= activity.Files.Count) return -1;

            var candidate = activity.Files[candidateIndex];
            if (!candidate.IsBackgroundAudio) return candidateIndex;
            backgroundAudioEncountered?.Invoke(candidate);
        }
    }
}
