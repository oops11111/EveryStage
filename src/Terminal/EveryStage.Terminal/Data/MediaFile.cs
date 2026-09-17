namespace EveryStage.Terminal.Data;

/// <summary>
/// One entry in an <see cref="Activity"/>'s file list (PLANNING.md §6). A single physical file
/// can only belong to one Activity slot at a time in this model — "adding" the same source file
/// to two activities is represented as two separate <see cref="MediaFile"/> entries pointing at
/// the same <see cref="SourcePath"/>, each with its own independent playback settings.
/// </summary>
public sealed class MediaFile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string SourcePath { get; set; }
    public required MediaKind Kind { get; set; }

    /// <summary>Null = inherit the owning Activity's default play mode.</summary>
    public PlayMode? PlayModeOverride { get; set; }

    /// <summary>Stay duration for image/document content, e.g. "how long before auto-advance".</summary>
    public TimeSpan? StayDuration { get; set; }

    /// <summary>Fade in/out duration for video/audio content. Only fade-IN is currently wired up
    /// (see <c>PlaybackEngine.FadeInDurationFor</c>, and <c>AudioContentController</c>'s class doc
    /// comment for why fade-OUT is deliberately deferred) — this field is read but its second half,
    /// the fade-out window at end of playback, has no effect yet.</summary>
    public TimeSpan? FadeDuration { get; set; }

    /// <summary>Gates <see cref="FadeDuration"/> entirely — see <c>PlaybackEngine.FadeInDurationFor</c>.
    /// False (the default) means no fade regardless of what <see cref="FadeDuration"/> holds.</summary>
    public bool VolumeFollowsFade { get; set; }

    public CompletionAction OnCompletion { get; set; } = CompletionAction.NextItem;
    public bool AllowManualSkip { get; set; } = true;

    /// <summary>Only meaningful when Kind == Audio (PLANNING.md §6 "音频特殊性").</summary>
    public bool IsBackgroundAudio { get; set; }
    public AudioVisual BackgroundAudioVisual { get; set; } = AudioVisual.DefaultBackgroundImage;
}
