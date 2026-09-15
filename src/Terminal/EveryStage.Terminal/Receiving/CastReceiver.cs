using EveryStage.Rendering.Audio;
using EveryStage.Terminal.Display;
using EveryStage.Transport;
using Vortice.Direct3D11;

namespace EveryStage.Terminal.Receiving;

/// <summary>
/// Terminal-side counterpart to Caster's <c>LiveCastSession</c>: receives the RTP/H.264 stream
/// (<c>RtpReceiver</c>, EveryStage.Transport), reassembles NAL units back into Annex-B access units
/// using the RTP marker bit (the inverse of <c>AnnexBNalSplitter</c>, which the Caster side used to
/// strip start codes before packetizing), decodes each with <see cref="H264HardwareDecoder"/>, and
/// presents the result through the shared <see cref="VideoSurface"/> bound to the overlay window's
/// video HWND — the same surface <c>ContentEngine.VideoContentController</c> uses for local file
/// playback, here fed from a live network stream instead of a file. Optionally also receives a raw
/// PCM audio stream (<c>RawRtpReceiver</c>, EveryStage.Transport) and plays it through
/// <see cref="AudioPlaybackClock"/> — the same WASAPI playback class <c>VideoContentController</c>
/// uses for local video files' audio track, here with no video-frame pacing to serve since it's
/// just played back as it arrives (see this project's README on what that costs: no A/V sync).
///
/// Takes the <see cref="VideoSurface"/> from its caller rather than creating its own — see that
/// class's doc comment for why two independent D3D11 devices/swap chains bound to the same HWND was
/// a real bug, not a hypothetical one. This class still does not enforce mutual exclusion with
/// <c>VideoContentController</c> by itself: the caller (<c>Program.cs</c>'s
/// <c>TerminalApplicationContext</c>) is responsible for making sure local playback is stopped
/// (<c>PlaybackEngine.StopForDeviceCast</c>) before constructing a <see cref="CastReceiver"/>, and
/// for stopping this receiver before local playback resumes
/// (<c>PlaybackEngine.LocalPlaybackStarting</c>).
///
/// All decode/present calls happen synchronously on whatever thread invokes
/// <see cref="RtpReceiver.NalUnitReceived"/> — that event fires from `RtpReceiver`'s own single
/// sequential background receive loop (see its doc comment), so calls into this class are never
/// concurrent with each other and no additional locking is needed here. The audio side runs on its
/// own, entirely independent <c>RawRtpReceiver</c> background loop — video and audio share no state
/// beyond both presenting into resources this class owns.
/// </summary>
public sealed class CastReceiver : IDisposable
{
    private readonly VideoSurface _surface;
    private readonly H264HardwareDecoder _decoder;
    private readonly RtpReceiver _rtpReceiver;
    private readonly List<byte[]> _pendingNals = new();

    private readonly RawRtpReceiver? _audioRtpReceiver;
    private readonly AudioPlaybackClock? _audioClock;

    public int Width { get; }
    public int Height { get; }
    public long FramesDecoded { get; private set; }
    public long BytesReceived { get; private set; }
    public string? LastError { get; private set; }

    public bool HasAudio { get; private set; }
    public long AudioBytesReceived { get; private set; }

    /// <summary>Set when audio construction fails — unlike a video-side failure (which fails this
    /// whole constructor, since there's no cast without video), an audio failure here degrades to
    /// video-only, matching <c>LiveCastSession</c>'s own audio-is-best-effort handling on the Caster
    /// side.</summary>
    public string? AudioError { get; private set; }

    public CastReceiver(VideoSurface surface, int width, int height, int listenPort,
        bool hasAudio = false, int audioSampleRate = 0, int audioChannels = 0, int audioListenPort = 0)
    {
        Width = width;
        Height = height;

        _surface = surface;
        _decoder = new H264HardwareDecoder(_surface.Gpu, width, height);
        _decoder.FrameDecoded += OnFrameDecoded;

        _rtpReceiver = new RtpReceiver(listenPort);
        _rtpReceiver.NalUnitReceived += OnNalUnitReceived;

        if (hasAudio)
        {
            try
            {
                // Constructed here (not lazily on first packet) so a WASAPI failure is caught right
                // away rather than surfacing later as an unhandled exception from inside
                // OnAudioPayloadReceived on the RTP receive thread.
                _audioClock = new AudioPlaybackClock(audioSampleRate, audioChannels);
                _audioRtpReceiver = new RawRtpReceiver(audioListenPort);
                _audioRtpReceiver.PayloadReceived += OnAudioPayloadReceived;
                HasAudio = true;
            }
            catch (Exception ex)
            {
                // Video-only is a normal, expected outcome here — a WASAPI output failure on the
                // Terminal shouldn't take down a cast whose video side is working fine.
                AudioError = ex.Message;
                _audioClock?.Dispose();
                _audioClock = null;
                _audioRtpReceiver?.Dispose();
                _audioRtpReceiver = null;
                HasAudio = false;
            }
        }
    }

    public void Start()
    {
        _rtpReceiver.Start();
        if (HasAudio)
        {
            _audioClock!.Start();
            _audioRtpReceiver!.Start();
        }
    }

    private void OnAudioPayloadReceived(byte[] pcm)
    {
        AudioBytesReceived += pcm.Length;
        // No jitter buffer, no A/V sync — enqueued straight into WASAPI playback as it arrives.
        // BufferedWaveProvider (inside AudioPlaybackClock) discards on overflow rather than
        // blocking, so a burst of late packets thins itself out instead of building unbounded
        // latency; a dropped/reordered RTP packet here just becomes an audible gap/glitch, the same
        // "no loss recovery" limitation the video side already has and documents.
        _audioClock!.Enqueue(pcm);
    }

    private void OnNalUnitReceived(byte[] nalUnit, bool isLastNalOfAccessUnit)
    {
        BytesReceived += nalUnit.Length;
        _pendingNals.Add(nalUnit);
        if (!isLastNalOfAccessUnit) return;

        byte[] accessUnit = BuildAnnexBAccessUnit(_pendingNals);
        _pendingNals.Clear();

        try
        {
            // Sample time isn't used for anything downstream yet — 0 is a placeholder rather than a
            // real presentation timestamp. Audio (when HasAudio) plays independently via its own
            // AudioPlaybackClock with no synchronization against this video timestamp at all — see
            // this project's README on the resulting lack of A/V sync.
            _decoder.SubmitAccessUnit(accessUnit, sampleTimeTicks: 0);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private static byte[] BuildAnnexBAccessUnit(List<byte[]> nalUnits)
    {
        // Inverse of AnnexBNalSplitter: prefix each NAL unit with a 4-byte start code and
        // concatenate. The Caster side stripped these off when packetizing per RFC 6184, so they
        // must be put back before handing the bytestream to a decoder MFT that expects Annex B.
        int total = 0;
        foreach (var nal in nalUnits) total += nal.Length + 4;

        var buffer = new byte[total];
        int offset = 0;
        foreach (var nal in nalUnits)
        {
            buffer[offset++] = 0;
            buffer[offset++] = 0;
            buffer[offset++] = 0;
            buffer[offset++] = 1;
            nal.CopyTo(buffer, offset);
            offset += nal.Length;
        }
        return buffer;
    }

    private void OnFrameDecoded(ID3D11Texture2D texture, int arraySlice, int width, int height)
    {
        using (texture)
        {
            _surface.Presenter.PresentFrame(texture, arraySlice, width, height, vsync: false);
        }
        FramesDecoded++;
    }

    public void Dispose()
    {
        // Does NOT dispose _surface — shared with VideoContentController, owned by
        // TerminalApplicationContext (see VideoSurface's doc comment).
        _rtpReceiver.Dispose();
        _decoder.FrameDecoded -= OnFrameDecoded;
        _decoder.Dispose();

        if (_audioRtpReceiver != null) _audioRtpReceiver.PayloadReceived -= OnAudioPayloadReceived;
        _audioRtpReceiver?.Dispose();
        _audioClock?.Dispose();
    }
}
