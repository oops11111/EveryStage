using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace EveryStage.Terminal.Audio;

/// <summary>
/// Implements the two-stage audio takeover from PLANNING.md §9.3: on cast start, send the global
/// media-play-pause key first (works for any app with a system media transport control session,
/// which is most modern players); then, for whatever is still actually producing sound after a
/// short settle delay, mute it as a fallback. On disconnect, unmute what we muted and send the
/// media key again so paused content resumes from where it left off.
///
/// "媒体键暂停 > 静音兜底，两段式处理，不是二选一" — both stages always run, in order; this is not
/// an if/else choice between them.
/// </summary>
public sealed class AudioTakeoverService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1;
    private const uint KEYEVENTF_KEYUP = 0x2;

    // Sessions we muted ourselves, so Restore() only ever touches what it took over — never a
    // session the user (or some other app) muted independently before we got involved.
    private readonly HashSet<int> _mutedProcessIds = new();

    public async Task TakeoverAsync(TimeSpan? settleDelay = null)
    {
        SendMediaPlayPauseKey();
        await Task.Delay(settleDelay ?? TimeSpan.FromMilliseconds(300));
        MuteStillActiveSessions();
    }

    public void Restore()
    {
        UnmuteSessionsWeMuted();
        SendMediaPlayPauseKey();
    }

    private static void SendMediaPlayPauseKey()
    {
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void MuteStillActiveSessions()
    {
        int ownProcessId = Environment.ProcessId;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    using var session = sessions[i];

                    // NOTE: verify GetProcessID's exact shape (method vs. property, uint vs. int) against
                    // the installed NAudio version — see the Phase 0 demo's README for why this project
                    // flags interop surface it couldn't compile-check in this sandbox.
                    int processId = (int)session.GetProcessID;
                    if (processId == ownProcessId) continue;

                    if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                    if (session.SimpleAudioVolume.Mute) continue; // leave pre-existing mutes alone

                    session.SimpleAudioVolume.Mute = true;
                    _mutedProcessIds.Add(processId);
                }
                catch (Exception)
                {
                    // One session's COM call failing (the process could exit between enumeration and
                    // this loop reaching it, or the GetProcessID interop shape flagged above could be
                    // wrong for the installed NAudio version) must not abort muting every OTHER session
                    // in this sweep — best-effort per session, like every other opportunistic
                    // OS-interop call in this repo.
                }
            }
        }
        catch (Exception)
        {
            // No default render endpoint at all (COMException, "Element not found") is a real
            // condition on this repo's own hardware target — a Terminal is an extended-display box
            // and may have no audio output device configured/plugged in. See this method's caller
            // for why this must never propagate: TakeoverAsync is fire-and-forget from the UI thread
            // (an unhandled exception there would only ever surface, if at all, whenever the .NET GC
            // happens to finalize the abandoned Task and raise TaskScheduler.UnobservedTaskException —
            // see Program.cs's top-level handler for that), and Restore() below is called synchronously
            // from Program.OnOutputStateChanged() immediately before StopCasting() with no try/catch of
            // its own — an exception escaping here would skip StopCasting() entirely, leaving a
            // CastReceiver running against a hidden overlay forever instead of being torn down. Both
            // callers already treat "无法接管系统音频" as acceptable to just not happen; they were never
            // written to survive it throwing instead.
        }
    }

    private void UnmuteSessionsWeMuted()
    {
        if (_mutedProcessIds.Count == 0) return;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    using var session = sessions[i];
                    int processId = (int)session.GetProcessID;
                    if (_mutedProcessIds.Contains(processId))
                        session.SimpleAudioVolume.Mute = false;
                }
                catch (Exception)
                {
                    // Same "one session's failure must not abort the rest" reasoning as
                    // MuteStillActiveSessions above.
                }
            }
        }
        catch (Exception)
        {
            // Same "no default render endpoint" reasoning as MuteStillActiveSessions above — this
            // path matters even more here, since Restore() (this method's only caller besides
            // Dispose()) runs synchronously and unguarded right before Program.OnOutputStateChanged's
            // StopCasting() call.
        }

        // Cleared even on failure above: if the takeover-time mute genuinely didn't happen (no
        // default device), there is nothing left for a later Restore() to still be responsible for,
        // and if it did happen but this unmute attempt failed, there is no retry path anyway — an
        // empty set here is more honest than one that claims sessions are still muted when nothing
        // further will ever act on that claim.
        _mutedProcessIds.Clear();
    }

    public void Dispose() => UnmuteSessionsWeMuted();
}
