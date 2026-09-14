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

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;

        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];

            // NOTE: verify GetProcessID's exact shape (method vs. property, uint vs. int) against
            // the installed NAudio version — see the Phase 0 demo's README for why this project
            // flags interop surface it couldn't compile-check in this sandbox.
            int processId = (int)session.GetProcessID;
            if (processId == ownProcessId) continue;

            if (session.State != AudioSessionState.AudioSessionStateActive) continue;
            if (session.SimpleAudioVolume.Mute) continue; // leave pre-existing mutes alone

            session.SimpleAudioVolume.Mute = true;
            _mutedProcessIds.Add(processId);
        }
    }

    private void UnmuteSessionsWeMuted()
    {
        if (_mutedProcessIds.Count == 0) return;

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;

        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            int processId = (int)session.GetProcessID;
            if (_mutedProcessIds.Contains(processId))
                session.SimpleAudioVolume.Mute = false;
        }

        _mutedProcessIds.Clear();
    }

    public void Dispose() => UnmuteSessionsWeMuted();
}
