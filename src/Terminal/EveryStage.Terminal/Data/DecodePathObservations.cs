namespace EveryStage.Terminal.Data;

/// <summary>DEV-16: what decode paths this machine has actually been observed using, across every
/// video played since startup. Observation only: a path is recorded when a video's first frame
/// (or its end / stop / failure, if the first frame never logged) reports it — a codec never
/// played is never judged, and an Unknown report records nothing, so an untested path is never
/// labelled hardware. The pre-show badge keys off <see cref="OnlySoftwareObserved"/>: true only
/// when software decoding was observed and hardware decoding never was. Process-lifetime state;
/// <see cref="Clear"/> exists for tests and for a fresh diagnostic run. Dependency-free (no
/// Rendering reference) so the CoreSelfTests can link this file like <see cref="PreShowCheck"/>.</summary>
public static class DecodePathObservations
{
    private static readonly object _lock = new();
    private static bool _sawHardware;
    private static bool _sawSoftware;

    public static void RecordHardware()
    {
        lock (_lock) _sawHardware = true;
    }

    public static void RecordSoftware()
    {
        lock (_lock) _sawSoftware = true;
    }

    /// <summary>True only when software decoding was observed and hardware decoding never was.
    /// Nothing observed yet reads false — no evidence, no badge.</summary>
    public static bool OnlySoftwareObserved
    {
        get { lock (_lock) return _sawSoftware && !_sawHardware; }
    }

    public static bool AnyObserved
    {
        get { lock (_lock) return _sawHardware || _sawSoftware; }
    }

    public static void Clear()
    {
        lock (_lock) { _sawHardware = false; _sawSoftware = false; }
    }
}
