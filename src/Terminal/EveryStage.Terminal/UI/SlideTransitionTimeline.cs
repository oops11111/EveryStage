using System.Globalization;
using MotionSoftDemo;

namespace EveryStage.Terminal.UI;

/// <summary>Private deterministic linear progress; callers apply their existing visual ease once.</summary>
internal sealed class SlideTransitionTimeline : IDisposable
{
    private readonly Control _owner;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Action<double> _apply;
    private readonly Action<bool> _completed;
    private readonly IAnimationPreference _preference;
    private readonly bool _ownsPreference;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private MotionSystem? _system;
    private AnimationHandle? _handle;
    private bool _insideNative, _draining, _cancelPending;
    private volatile bool _disposed;
    private int _generation, _activeGeneration;
    private Request? _pending;
    private readonly record struct Request(int Generation, int Duration, int Hold, bool Reduced);
    internal double Progress { get; private set; }
    internal bool IsRunning { get; private set; }
    internal bool TimerEnabled => _timer.Enabled;
    internal bool IsDisposed => _disposed;
    internal int DurationMilliseconds { get; private set; }
    internal int HoldMilliseconds { get; private set; }
    internal bool RunReduced { get; private set; }
    internal string? LastTokens { get; private set; }

    internal SlideTransitionTimeline(Control owner, Action<double> apply, Action<bool> completed,
        IAnimationPreference? preference = null)
    {
        if (owner.IsHandleCreated && owner.InvokeRequired)
            throw new InvalidOperationException("Timeline must be created on its control owner thread.");
        _owner = owner;
        _apply = apply;
        _completed = completed;
        _ownsPreference = preference is null;
        _preference = preference ?? new WindowsAnimationPreference();
        _preference.Changed += OnPreferenceChanged;
        _timer.Tick += OnFrame;
    }

    private void AssertOwner()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Timeline requires its UI owner thread.");
    }

    internal void Start(int durationMilliseconds, int holdMilliseconds = 0)
    {
        AssertOwner();
        if (_disposed) return;
        bool reduced = !_preference.AnimationsEnabled;
        int generation = ++_generation;
        _pending = new Request(generation, Math.Clamp(durationMilliseconds, 1, 3000), reduced ? 0 : Math.Clamp(holdMilliseconds, 0, 1800), reduced);
        IsRunning = true;
        _timer.Stop();
        Drain();
    }

    internal void Cancel()
    {
        AssertOwner();
        if (_disposed) return;
        _generation++;
        _pending = null;
        _cancelPending = true;
        IsRunning = false;
        _timer.Stop();
        Drain();
    }

    private void Native(Action action)
    {
        if (_insideNative) throw new InvalidOperationException("Timeline native reentry refused.");
        _insideNative = true;
        try { action(); }
        finally { _insideNative = false; }
    }

    private void TearDownNative()
    {
        var system = _system;
        var handle = _handle;
        _system = null;
        _handle = null;
        if (system is null) return;
        system.ValueChanged -= OnValue;
        Native(() =>
        {
            try
            {
                try { handle?.Cancel(); }
                finally { handle?.Release(); }
            }
            finally { system.Dispose(); }
        });
    }

    private void Drain()
    {
        if (_insideNative || _draining) return;
        _draining = true;
        try
        {
            while (true)
            {
                if (_disposed) { TearDownNative(); return; }
                if (_cancelPending) { _cancelPending = false; TearDownNative(); }
                if (_pending is not { } request)
                {
                    UpdatePump();
                    if (_disposed || _cancelPending || _pending is not null) continue;
                    break;
                }
                _pending = null;
                TearDownNative();
                if (request.Generation != _generation) continue;
                _activeGeneration = request.Generation;
                DurationMilliseconds = request.Duration;
                HoldMilliseconds = request.Hold;
                RunReduced = request.Reduced;
                Progress = request.Reduced ? 1 : 0;
                _apply(Progress);
                if (_disposed || request.Generation != _generation) continue;
                string duration = request.Duration.ToString(CultureInfo.InvariantCulture);
                string hold = request.Hold > 0 ? ", {\"hold\":\"" + request.Hold.ToString(CultureInfo.InvariantCulture) + "ms\"}" : "";
                LastTokens = "{\"version\":1,\"transitions\":{\"software.slide.progress\":{\"root\":{\"type\":\"track\",\"property\":\"progress\",\"from\":0,\"steps\":[{\"to\":1,\"duration\":\"" + duration + "ms\",\"ease\":\"linear\"}" + hold + "]},\"reduced\":\"instant\"}}}";
                try
                {
                    Native(() =>
                    {
                        _system = MotionSystem.Create();
                        _system.ValueChanged += OnValue;
                        _system.SetReducedMotion(request.Reduced);
                        string? error = _system.LoadTokens(LastTokens);
                        if (error is not null) throw new InvalidOperationException("Timeline token load failed: " + error);
                        _handle = _system.CreateAnimation(1, "software.slide.progress");
                        if (_handle.Id == 0) throw new InvalidOperationException("Timeline animation creation failed.");
                        _handle.Play();
                        if (request.Reduced) _system.OnFrame(); // Only reduced needs immediate flush; normal starts at exact0.
                    });
                    UpdatePump();
                }
                catch
                {
                    IsRunning = false;
                    _timer.Stop();
                    TearDownNative();
                    throw;
                }
            }
        }
        finally { _draining = false; }
    }

    private void OnValue(ulong target, string property, double value)
    {
        if (_disposed || !IsRunning || _activeGeneration != _generation || target != 1 || property != "progress") return;
        Progress = Math.Clamp(value, 0, 1);
        _apply(Progress);
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        AssertOwner();
        if (_disposed) return;
        if (_system is not null) Native(_system.OnFrame);
        Drain();
    }

    private void UpdatePump()
    {
        if (_disposed) return;
        if (!IsRunning || _activeGeneration != _generation || _pending is not null || _cancelPending) { _timer.Stop(); return; }
        if (_system?.IsRunning == true) { _timer.Start(); return; }
        _timer.Stop();
        int generation = _generation;
        IsRunning = false;
        Progress = 1;
        _apply(1);
        if (_disposed || generation != _generation) return;
        _completed(RunReduced); // Outer boundary only; a callback may queue the next run.
    }

    private void OnPreferenceChanged()
    {
        int generation = _generation;
        if (_disposed || !_owner.IsHandleCreated) return;
        try
        {
            _owner.BeginInvoke(() =>
            {
                if (_disposed || generation != _generation) return;
                AssertOwner();
                if (IsRunning && !_preference.AnimationsEnabled) Start(DurationMilliseconds);
            });
        }
        catch (InvalidOperationException) when (_disposed || _owner.IsDisposed || !_owner.IsHandleCreated) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        AssertOwner();
        _disposed = true;
        _generation++;
        _pending = null;
        IsRunning = false;
        _timer.Stop();
        _timer.Tick -= OnFrame;
        _timer.Dispose();
        _preference.Changed -= OnPreferenceChanged;
        if (_ownsPreference && _preference is IDisposable disposable) disposable.Dispose();
        Drain();
    }
}
