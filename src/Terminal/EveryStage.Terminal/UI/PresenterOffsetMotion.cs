using MotionSoftDemo;

namespace EveryStage.Terminal.UI;

/// <summary>Private UI-owned presenter offset animation. Native callbacks apply values only; the outer timer pumps.</summary>
internal sealed class PresenterOffsetMotion : IDisposable
{
    private readonly Control _owner;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Action<double> _apply;
    private readonly Action _settled;
    private readonly IAnimationPreference _preference;
    private readonly bool _ownsPreference;
    private MotionSystem _system;
    private AnimationHandle? _handle;
    private const ulong TargetId = 1;
    private bool _freezePending;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private volatile bool _disposed;
    private bool _nativeDisposed;
    private bool _insideNative;
    private bool _seeded;
    private bool _moving;
    private bool? _animationsEnabled;
    private int _generation;
    private int _moveGeneration;
    private double _target;
    private (double Target, double Initial)? _pending;

    internal bool IsRunning => _moving;
    internal bool TimerEnabled => _timer.Enabled;
    internal bool IsDisposed => _disposed;
    internal double Target => _pending?.Target ?? _target;

    internal PresenterOffsetMotion(Control owner, Action<double> apply, Action settled,
        IAnimationPreference? preference = null)
    {
        _owner = owner;
        _apply = apply;
        _settled = settled;
        _ownsPreference = preference is null;
        _preference = preference ?? new WindowsAnimationPreference();
        MotionSystem? system = null;
        AnimationHandle? handle = null;
        try
        {
            _system = system = MotionSystem.Create();
            _system.ValueChanged += OnValue;
            _handle = handle = _system.CreateAnimation(TargetId);
            _timer.Tick += OnFrame;
            _preference.Changed += OnPreferenceChanged;
            RefreshPreference();
        }
        catch
        {
            _preference.Changed -= OnPreferenceChanged;
            _timer.Tick -= OnFrame;
            _timer.Dispose();
            if (system != null) system.ValueChanged -= OnValue;
            try
            {
                try { handle?.Cancel(); }
                finally { handle?.Release(); }
            }
            finally
            {
                try { system?.Dispose(); }
                finally
                {
                    if (_ownsPreference && _preference is IDisposable disposable) disposable.Dispose();
                }
            }
            throw;
        }
    }

    private void AssertOwner()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Presenter offset requires its UI owner thread.");
    }

    private void Native(Action action)
    {
        _insideNative = true;
        try { action(); }
        finally
        {
            _insideNative = false;
            if (_disposed) DisposeNative();
        }
    }

    internal void MoveTo(double target, double initial)
    {
        AssertOwner();
        if (_disposed) return;
        if (!double.IsFinite(target) || target < 0 || !double.IsFinite(initial))
            throw new ArgumentOutOfRangeException(nameof(target));
        if (_insideNative)
        {
            // A layout/resize caused by applying offset cannot reenter fm_*.
            _pending = target == _target ? null : (target, initial);
            return;
        }
        _moveGeneration++;
        _target = target;
        _moving = true;
        RefreshPreference();
        if (_disposed) return;
        Native(() =>
        {
            if (!_seeded) _handle!.Play();
            if (_disposed) return;
            if (!_handle!.Retarget("offset", target, new SpringParams(0.20, 1.0),
                from: _seeded ? null : initial))
                throw new InvalidOperationException("Presenter offset retarget was rejected.");
            _seeded = true;
        });
        if (_disposed) return;
        DrainPending();
        UpdatePump();
    }

    /// <summary>Freeze the presented value. Cancelled handles are terminal: never replay them.
    /// A fresh private native system avoids old rest state overriding an explicit drag seed.</summary>
    internal void Freeze(double presented)
    {
        AssertOwner();
        if (_disposed) return;
        if (!double.IsFinite(presented) || presented < 0) throw new ArgumentOutOfRangeException(nameof(presented));
        bool needsReset = _seeded || _moving || _freezePending;
        _moveGeneration++;
        _target = presented;
        _moving = false;
        _seeded = false;
        _pending = null;
        _freezePending = needsReset;
        _timer.Stop();
        if (_insideNative) return;
        DrainPending();
    }

    private void OnValue(ulong target, string property, double value)
    {
        if (!_disposed && _moving && target == TargetId && property == "offset") _apply(value);
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed) return;
        AssertOwner();
        Native(_system.OnFrame);
        if (_disposed) return;
        DrainPending();
        UpdatePump();
    }

    private void DrainPending()
    {
        if (_disposed) return;
        if (_freezePending)
        {
            _freezePending = false;
            Native(() =>
            {
                // Cancelled handles are terminal and their rest state survives Release.
                // Replace this private native system at the safe outer boundary to bound rest state.
                var oldHandle = _handle;
                var oldSystem = _system;
                _handle = null;
                oldSystem.ValueChanged -= OnValue;
                try
                {
                    try { oldHandle?.Cancel(); }
                    finally { oldHandle?.Release(); }
                }
                finally { oldSystem.Dispose(); }
                if (_disposed) return;
                MotionSystem? fresh = null;
                AnimationHandle? freshHandle = null;
                try
                {
                    fresh = MotionSystem.Create();
                    fresh.ValueChanged += OnValue;
                    freshHandle = fresh.CreateAnimation(TargetId);
                    fresh.SetReducedMotion(!(_animationsEnabled ?? true));
                    _system = fresh;
                    _handle = freshHandle;
                }
                catch
                {
                    if (fresh != null) fresh.ValueChanged -= OnValue;
                    try { freshHandle?.Release(); }
                    finally { fresh?.Dispose(); }
                    throw;
                }
            });
            if (_disposed) return;
        }
        if (_pending is not { } move) return;
        _pending = null;
        MoveTo(move.Target, move.Initial);
    }

    private void UpdatePump()
    {
        if (_disposed) return;
        if (!_moving) { _timer.Stop(); return; }
        if (_system.IsRunning) _timer.Start();
        else
        {
            _timer.Stop();
            if (_moving)
            {
                int completedGeneration = _moveGeneration;
                double completedTarget = _target;
                _moving = false;
                _apply(completedTarget);
                if (_disposed || completedGeneration != _moveGeneration || _moving) return;
                _settled();
            }
        }
    }

    private void RefreshPreference()
    {
        bool enabled = _preference.AnimationsEnabled;
        if (_animationsEnabled == enabled) return;
        Native(() => _system.SetReducedMotion(!enabled));
        if (!_disposed) _animationsEnabled = enabled;
    }

    private void OnPreferenceChanged()
    {
        int generation = _generation;
        if (_disposed || !_owner.IsHandleCreated) return;
        // Always queue: preference events may arrive from a system-event worker.
        try
        {
            _owner.BeginInvoke(() =>
            {
                if (_disposed || generation != _generation) return;
                AssertOwner();
                RefreshPreference();
                if (_disposed) return;
                DrainPending();
                UpdatePump();
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
        _freezePending = false;
        _timer.Stop();
        _timer.Tick -= OnFrame;
        _timer.Dispose();
        _preference.Changed -= OnPreferenceChanged;
        if (_ownsPreference && _preference is IDisposable disposable) disposable.Dispose();
        _system.ValueChanged -= OnValue;
        _moving = false;
        if (!_insideNative) DisposeNative();
    }

    private void DisposeNative()
    {
        if (_nativeDisposed) return;
        _nativeDisposed = true;
        try { _handle?.Cancel(); }
        finally
        {
            try { _handle?.Release(); }
            finally { _system.Dispose(); }
        }
    }
}
