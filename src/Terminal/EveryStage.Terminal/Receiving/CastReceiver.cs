using EveryStage.Rendering;
using EveryStage.Transport;
using Vortice.Direct3D11;

namespace EveryStage.Terminal.Receiving;

/// <summary>
/// Terminal-side counterpart to Caster's <c>LiveCastSession</c>: receives the RTP/H.264 stream
/// (<c>RtpReceiver</c>, EveryStage.Transport), reassembles NAL units back into Annex-B access units
/// using the RTP marker bit (the inverse of <c>AnnexBNalSplitter</c>, which the Caster side used to
/// strip start codes before packetizing), decodes each with <see cref="H264HardwareDecoder"/>, and
/// presents the result onto the overlay window's video surface via <c>SwapChainPresenter</c> — the
/// same presenter class <c>VideoContentController</c> already uses for local file playback, here fed
/// from a live network stream instead of a file.
///
/// Owns its own <see cref="D3D11Device"/> rather than sharing one with <c>VideoContentController</c>
/// — PLANNING.md's model has local file playback and device casting as mutually exclusive on a
/// given Terminal at any moment (the overlay shows one or the other), so nothing here needs to
/// coordinate GPU state with local video playback; a future revision could consider consolidating
/// devices if that assumption changes.
///
/// All decode/present calls happen synchronously on whatever thread invokes
/// <see cref="RtpReceiver.NalUnitReceived"/> — that event fires from `RtpReceiver`'s own single
/// sequential background receive loop (see its doc comment), so calls into this class are never
/// concurrent with each other and no additional locking is needed here.
/// </summary>
public sealed class CastReceiver : IDisposable
{
    private readonly D3D11Device _gpu;
    private readonly SwapChainPresenter _presenter;
    private readonly H264HardwareDecoder _decoder;
    private readonly RtpReceiver _rtpReceiver;
    private readonly List<byte[]> _pendingNals = new();

    public int Width { get; }
    public int Height { get; }
    public long FramesDecoded { get; private set; }
    public long BytesReceived { get; private set; }
    public string? LastError { get; private set; }

    public CastReceiver(IntPtr videoHostHandle, int width, int height, int listenPort)
    {
        Width = width;
        Height = height;

        _gpu = new D3D11Device();
        _presenter = new SwapChainPresenter(_gpu, videoHostHandle, width, height);
        _decoder = new H264HardwareDecoder(_gpu, width, height);
        _decoder.FrameDecoded += OnFrameDecoded;

        _rtpReceiver = new RtpReceiver(listenPort);
        _rtpReceiver.NalUnitReceived += OnNalUnitReceived;
    }

    public void Start() => _rtpReceiver.Start();

    private void OnNalUnitReceived(byte[] nalUnit, bool isLastNalOfAccessUnit)
    {
        BytesReceived += nalUnit.Length;
        _pendingNals.Add(nalUnit);
        if (!isLastNalOfAccessUnit) return;

        byte[] accessUnit = BuildAnnexBAccessUnit(_pendingNals);
        _pendingNals.Clear();

        try
        {
            // Sample time isn't used for anything downstream yet (no A/V sync — this pipeline
            // carries no audio at all, see this project's README) — 0 is a placeholder rather than
            // a real presentation timestamp.
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
            _presenter.PresentFrame(texture, arraySlice, width, height, vsync: false);
        }
        FramesDecoded++;
    }

    public void Dispose()
    {
        _rtpReceiver.Dispose();
        _decoder.FrameDecoded -= OnFrameDecoded;
        _decoder.Dispose();
        _presenter.Dispose();
        _gpu.Dispose();
    }
}
