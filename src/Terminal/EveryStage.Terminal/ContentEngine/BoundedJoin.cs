using System.Diagnostics;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// Joins a worker thread for at most <see cref="Timeout"/>: Stop, 停止输出 / 急停 and device-cast
/// teardown must return even when a decoder or audio device call has hung. A thread that does not end in
/// time is abandoned — the caller then leaves whatever that thread may still touch undisposed (a bounded
/// leak instead of a hung UI or a use-after-dispose crash) — and counted in <see cref="Abandoned"/>.
/// </summary>
internal static class BoundedJoin
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static int _abandoned;

    /// <summary>Worker threads abandoned since start (diagnostics).</summary>
    public static int Abandoned => Volatile.Read(ref _abandoned);

    /// <summary>True when <paramref name="thread"/> is null, is the calling thread, or ended in time.</summary>
    public static bool Join(Thread? thread, string what, TimeSpan? timeout = null)
    {
        if (thread == null || thread == Thread.CurrentThread) return true;
        if (thread.Join(timeout ?? Timeout)) return true;
        Interlocked.Increment(ref _abandoned);
        Trace.WriteLine($"EveryStage: {what} did not stop within {(timeout ?? Timeout).TotalSeconds:0.#} s; abandoned.");
        return false;
    }
}
