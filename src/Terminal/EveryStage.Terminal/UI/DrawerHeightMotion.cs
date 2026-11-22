using System.Runtime.InteropServices;
using Microsoft.Win32;
using MotionSoftDemo;

namespace EveryStage.Terminal.UI;

internal interface IAnimationPreference
{
    bool AnimationsEnabled { get; }
    event Action? Changed;
}

/// <summary>Read-only system preference, owned by one control and detached on disposal.</summary>
internal sealed class WindowsAnimationPreference : IAnimationPreference, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint size, out int value, uint flags);

    public bool AnimationsEnabled
    {
        get
        {
            if (!SystemParametersInfo(0x1042, 0, out int value, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return value != 0;
        }
    }

    public event Action? Changed;
    public WindowsAnimationPreference() => SystemEvents.UserPreferenceChanged += OnChanged;
    private void OnChanged(object sender, UserPreferenceChangedEventArgs args) => Changed?.Invoke();
    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnChanged;
}

/// <summary>UI-owned height animation. Native callbacks apply values only; the outer timer pumps.</summary>
internal sealed class DrawerHeightMotion : IDisposable
{
    private readonly Control _owner;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Action<double> _apply;
    private readonly Action _settled;
    private readonly IAnimationPreference _preference;
    private readonly bool _ownsPreference;
    private readonly MotionSystem _system;
    private readonly AnimationHandle _handle;
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
    internal double Target => _target;

    internal DrawerHeightMotion(Control owner, Action<double> apply, Action settled,
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
            _handle = handle = _system.CreateAnimation(1);
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
            throw new InvalidOperationException("Drawer animation requires its UI owner thread.");
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
            // A layout/resize caused by applying height cannot reenter fm_*.
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
            if (!_seeded) _handle.Play();
            if (_disposed) return;
            if (!_handle.Retarget("height", target, new SpringParams(0.20, 1.0),
                from: _seeded ? null : initial))
                throw new InvalidOperationException("Drawer height retarget was rejected.");
            _seeded = true;
        });
        if (_disposed) return;
        DrainPending();
        UpdatePump();
    }

    private void OnValue(ulong target, string property, double value)
    {
        if (!_disposed && target == 1 && property == "height") _apply(value);
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
        if (_pending is not { } move || _disposed) return;
        _pending = null;
        MoveTo(move.Target, move.Initial);
    }

    private void UpdatePump()
    {
        if (_disposed) return;
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
        try { _handle.Cancel(); }
        finally
        {
            try { _handle.Release(); }
            finally { _system.Dispose(); }
        }
    }
}
