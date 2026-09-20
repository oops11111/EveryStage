using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.Playback;

/// <summary>Pure queue navigation rules shared by playback and hardware-free tests.</summary>
public static class PlaybackQueueNavigator
{
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
