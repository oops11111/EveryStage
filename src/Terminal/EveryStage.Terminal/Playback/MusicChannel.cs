using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.Playback;

/// <summary>
/// The background-music channel: plays the current <see cref="Playlist"/> on its own audio output,
/// independent of the Preview and Program engines, so switching what is on screen never interrupts the
/// music. Tracks fade in and out (2 s) for smooth changes; 「淡出停止」 ramps the volume down before
/// stopping; while the big screen plays video with sound (<see cref="ShouldDuck"/>) the music drops to the
/// library's duck level and comes back afterwards.
///
/// All members are called on the UI thread; the controller's completion and failure events are marshalled
/// back to it.
/// </summary>
public sealed class MusicChannel : IDisposable
{
    private static readonly TimeSpan TrackFade = TimeSpan.FromSeconds(2);
    private const double FadeOutSeconds = 3;

    private readonly MusicLibraryStore _store;
    private readonly AudioContentController _audio = new();
    private readonly SynchronizationContext? _ui;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Random _random = new();
    private double _duck = 1.0;          // 1 = full, DuckLevel = ducked; eased toward its target
    private double _fadeOut = 1.0;       // 1 = no fade-out; 0 = silent (then stopped)
    private bool _fadingOut;
    private int _generation;             // bumps on every Play/Stop, so stale completions are ignored

    public MusicChannel(MusicLibraryStore store)
    {
        _store = store;
        _ui = SynchronizationContext.Current;
        // The generation is read on the playback thread when the event fires: a completion that was
        // already on its way when the operator skipped to another track must not skip that one too.
        _audio.PlaybackCompleted += () =>
        {
            int generation = Volatile.Read(ref _generation);
            Post(() => { if (generation == _generation) OnTrackCompleted(); });
        };
        _audio.PlaybackFailed += ex =>
        {
            int generation = Volatile.Read(ref _generation);
            Post(() => { if (generation == _generation) OnTrackFailed(ex); });
        };
        _timer = new System.Windows.Forms.Timer { Interval = 100 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        ApplyVolume();
    }

    public MusicLibrary Library => _store.Library;
    public MusicLibraryStore Store => _store;

    /// <summary>True while the big screen plays something whose sound the music should give way to.</summary>
    public Func<bool>? ShouldDuck { get; set; }

    public Playlist? Playlist { get; private set; }
    public int TrackIndex { get; private set; } = -1;
    public string? CurrentTrack => Playlist != null && TrackIndex >= 0 && TrackIndex < Playlist.Tracks.Count ? Playlist.Tracks[TrackIndex] : null;
    public bool IsPlaying { get; private set; }
    public bool IsPaused => _audio.IsPaused;
    public bool IsDucked => _duck < 0.999;
    public bool IsFadingOut => _fadingOut;
    public TimeSpan Position => IsPlaying ? _audio.CurrentPosition : TimeSpan.Zero;
    public TimeSpan? Duration => IsPlaying ? _audio.TotalDuration : null;
    public string? LastError { get; private set; }

    /// <summary>Raised on the UI thread whenever the track, play state or playlist changes.</summary>
    public event Action? Changed;

    public float Volume
    {
        get => Library.Volume;
        set
        {
            Library.Volume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    /// <summary>Starts <paramref name="playlist"/> at <paramref name="index"/> (and makes it current).</summary>
    public void Play(Playlist playlist, int index = 0)
    {
        Library.CurrentPlaylistId = playlist.Id;
        Playlist = playlist;
        PlayTrack(index);
    }

    /// <summary>⏯: pause / resume, or start the current playlist when nothing plays.</summary>
    public void PlayPause()
    {
        if (!IsPlaying)
        {
            if (Library.Current is { Tracks.Count: > 0 } current) Play(current, Math.Max(0, Playlist == current ? TrackIndex : 0));
            return;
        }
        if (_audio.IsPaused) _audio.Resume(); else _audio.Pause();
        Changed?.Invoke();
    }

    public void Next()
    {
        if (Playlist is not { Tracks.Count: > 0 } playlist) return;
        PlayTrack(Playlist.Mode == PlaylistMode.Shuffle ? RandomOtherIndex(playlist) : (TrackIndex + 1) % playlist.Tracks.Count);
    }

    public void Previous()
    {
        if (Playlist is not { Tracks.Count: > 0 } playlist) return;
        // Like most players: more than 3 s in, ⏮ restarts the track.
        if (Position > TimeSpan.FromSeconds(3)) { PlayTrack(TrackIndex); return; }
        PlayTrack((TrackIndex - 1 + playlist.Tracks.Count) % playlist.Tracks.Count);
    }

    public bool Seek(double fraction)
    {
        if (Duration is not { Ticks: > 0 } duration) return false;
        bool paused = _audio.IsPaused;
        bool ok = _audio.TrySeekTo(TimeSpan.FromTicks((long)(duration.Ticks * Math.Clamp(fraction, 0, 1))));
        if (paused) _audio.Pause();
        return ok;
    }

    /// <summary>「淡出停止」: ramps down over 3 s, then stops.</summary>
    public void FadeOutStop()
    {
        if (!IsPlaying || _fadingOut) return;
        if (_audio.IsPaused) { Stop(); return; }
        _fadingOut = true;
        Changed?.Invoke();
    }

    public void Stop()
    {
        _generation++;
        _audio.Stop();
        IsPlaying = false;
        _fadingOut = false;
        _fadeOut = 1.0;
        ApplyVolume();
        Changed?.Invoke();
    }

    /// <summary>Starts the playlist marked 「启动后自动播放」, if any.</summary>
    public void AutoPlayOnStartup()
    {
        if (Library.Playlists.FirstOrDefault(p => p.AutoPlayOnStartup && p.Tracks.Count > 0) is { } playlist) Play(playlist);
    }

    /// <summary>Call after the playlists were edited: keeps the playing track if it is still there.</summary>
    public void PlaylistsEdited()
    {
        if (Playlist != null && !Library.Playlists.Contains(Playlist))
        {
            Stop();
            Playlist = null;
            TrackIndex = -1;
        }
        else if (Playlist != null && TrackIndex >= Playlist.Tracks.Count) TrackIndex = Playlist.Tracks.Count - 1;
        Changed?.Invoke();
    }

    private void PlayTrack(int index)
    {
        if (Playlist is not { } playlist || playlist.Tracks.Count == 0) { Stop(); return; }
        index = Math.Clamp(index, 0, playlist.Tracks.Count - 1);
        _generation++;
        TrackIndex = index;
        _fadingOut = false;
        _fadeOut = 1.0;
        ApplyVolume();
        try
        {
            _audio.Play(playlist.Tracks[index], TrackFade);
            IsPlaying = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            IsPlaying = false;
            LastError = $"无法播放 {Path.GetFileName(playlist.Tracks[index])}：{ex.Message}";
        }
        Changed?.Invoke();
    }

    private void OnTrackCompleted()
    {
        if (!IsPlaying || Playlist is not { } playlist) return;
        switch (playlist.Mode)
        {
            case PlaylistMode.LoopOne: PlayTrack(TrackIndex); break;
            case PlaylistMode.Shuffle: PlayTrack(RandomOtherIndex(playlist)); break;
            case PlaylistMode.PlayOnce when TrackIndex + 1 >= playlist.Tracks.Count: Stop(); break;
            default: PlayTrack((TrackIndex + 1) % playlist.Tracks.Count); break;
        }
    }

    private void OnTrackFailed(Exception ex)
    {
        LastError = $"播放中断：{ex.Message}";
        IsPlaying = false;
        Changed?.Invoke();
    }

    private int RandomOtherIndex(Playlist playlist)
    {
        if (playlist.Tracks.Count <= 1) return 0;
        int next;
        do next = _random.Next(playlist.Tracks.Count); while (next == TrackIndex);
        return next;
    }

    private void Tick()
    {
        bool duck = Library.DuckForVideo && IsPlaying && ShouldDuck?.Invoke() == true;
        double target = duck ? Math.Clamp(Library.DuckLevel, 0f, 1f) : 1.0;
        bool changed = false;
        if (Math.Abs(_duck - target) > 0.001)
        {
            // ~1 s to duck, ~2 s to come back up.
            double step = _duck > target ? 0.1 : 0.05;
            _duck = _duck > target ? Math.Max(target, _duck - step) : Math.Min(target, _duck + step);
            changed = true;
        }
        if (_fadingOut)
        {
            _fadeOut = Math.Max(0, _fadeOut - 0.1 / FadeOutSeconds);
            changed = true;
            if (_fadeOut <= 0) { Stop(); return; }
        }
        if (changed) ApplyVolume();
    }

    private void ApplyVolume() => _audio.Volume = (float)(Library.Volume * _duck * _fadeOut);

    private void Post(Action action)
    {
        if (_ui != null) _ui.Post(_ => action(), null);
        else action();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _audio.Dispose();
    }
}
