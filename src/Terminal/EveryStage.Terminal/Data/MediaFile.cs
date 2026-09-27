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

    /// <summary>Fade in/out duration for video/audio content (see <c>PlaybackEngine.FadeDurationFor</c>).
    /// Fade-in is fully reliable — it only needs playback position, always available. Fade-out is
    /// best-effort: it needs the file's total duration in advance, queried through this codebase's
    /// single least-verified Media Foundation call (see <c>AudioContentController</c>'s class doc
    /// comment for why) — it may silently not happen for some files (no error, just no fade-out),
    /// while fade-in keeps working regardless.</summary>
    public TimeSpan? FadeDuration { get; set; }

    /// <summary>Gates <see cref="FadeDuration"/> entirely — see <c>PlaybackEngine.FadeDurationFor</c>.
    /// False (the default) means no fade regardless of what <see cref="FadeDuration"/> holds.</summary>
    public bool VolumeFollowsFade { get; set; }

    public CompletionAction OnCompletion { get; set; } = CompletionAction.NextItem;
    public bool AllowManualSkip { get; set; } = true;

    /// <summary>Only meaningful when Kind == Audio (PLANNING.md §6 "音频特殊性").</summary>
    public bool IsBackgroundAudio { get; set; }
    public AudioVisual BackgroundAudioVisual { get; set; } = AudioVisual.DefaultBackgroundImage;

    /// <summary>Deep-copies every setting and, by default, assigns a fresh <see cref="Id"/> so an
    /// activity's copy stays independent from the library entry it came from. Set
    /// <paramref name="preserveId"/> only for an ephemeral playback queue that must keep log identity
    /// without mutating the persisted entry. Pulled out of <c>ActivitiesPanel</c> (where this field list used to live
    /// as a private <c>CloneFile</c> method, the only caller until now) so a second caller —
    /// <c>FilesPanel</c>'s "加入活动..." (PLANNING.md §11 "批量选择") — doesn't need its own
    /// independently-drifting copy of the same field list. Whichever new field this class gains
    /// next only needs to be added here once.</summary>
    /// <param name="preserveId">Preserve the source identity for temporary, non-persisted queues.</param>
    public MediaFile Clone(bool preserveId = false) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid(),
        SourcePath = SourcePath,
        Kind = Kind,
        PlayModeOverride = PlayModeOverride,
        StayDuration = StayDuration,
        FadeDuration = FadeDuration,
        VolumeFollowsFade = VolumeFollowsFade,
        OnCompletion = OnCompletion,
        AllowManualSkip = AllowManualSkip,
        IsBackgroundAudio = IsBackgroundAudio,
        BackgroundAudioVisual = BackgroundAudioVisual,
    };
}
