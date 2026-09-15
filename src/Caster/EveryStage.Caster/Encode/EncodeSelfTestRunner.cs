using EveryStage.Caster.Capture;
using EveryStage.Rendering;
using Vortice.Direct3D11;

namespace EveryStage.Caster.Encode;

/// <summary>
/// Composes everything built so far — <see cref="ScreenCaptureSource"/> -> <see cref="BgraToNv12Converter"/>
/// -> <see cref="H264HardwareEncoder"/> — into the fullest local self-test possible short of actual
/// network transport. This is the first time the capture and encode pieces of this repo run
/// together; each has only been exercised in isolation until now. Given how much of
/// <see cref="H264HardwareEncoder"/> is unverified (see its own doc comment), this self-test's most
/// important signal is simply whether construction/negotiation succeeds at all — an access-unit
/// count above zero is a bonus, not the bar for "this is working".
/// </summary>
public sealed class EncodeSelfTestRunner : IDisposable
{
    private D3D11Device? _gpu;
    private ScreenCaptureSource? _capture;
    private BgraToNv12Converter? _converter;
    private H264HardwareEncoder? _encoder;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool IsRunning => _loopTask != null;
    public int AccessUnitsEncoded { get; private set; }
    public long TotalEncodedBytes { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised from the background loop or the encoder's own event loop — marshal to the
    /// UI thread before touching UI.</summary>
    public event Action? StatsUpdated;

    public void Start()
    {
        if (IsRunning) return;

        LastError = null;
        AccessUnitsEncoded = 0;
        TotalEncodedBytes = 0;

        try
        {
            _gpu = new D3D11Device();
            _capture = new ScreenCaptureSource(_gpu);
            _converter = new BgraToNv12Converter(_gpu);
            // Modest bitrate/resolution-independent test settings — this is a pipeline self-test,
            // not a tuned production encode.
            _encoder = new H264HardwareEncoder(_gpu, _capture.Width, _capture.Height, frameRateNumerator: 30, bitrateBps: 4_000_000);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StopInternal();
            StatsUpdated?.Invoke();
            return;
        }

        _encoder.AccessUnitEncoded += OnAccessUnitEncoded;
        _encoder.EncodingFailed += OnEncodingFailed;

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoop(_gpu, _capture, _converter, _encoder, _cts.Token));
    }

    private void RunLoop(D3D11Device gpu, ScreenCaptureSource capture, BgraToNv12Converter converter, H264HardwareEncoder encoder, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                CapturedFrame? frame;
                try
                {
                    frame = capture.AcquireNextFrame(500);
                }
                catch (ScreenCaptureLostException ex)
                {
                    LastError = ex.Message;
                    break;
                }

                if (frame == null) continue; // normal timeout — screen hasn't changed.

                using (frame)
                {
                    if (!frame.HasNewImage) continue;

                    var nv12 = converter.Convert(frame.Texture, frame.Width, frame.Height);
                    // The converter reuses one output texture across calls (see its own doc
                    // comment) — copy it before handing off to the encoder's async input queue, so
                    // the next Convert() call can't overwrite a frame the encoder hasn't consumed
                    // yet.
                    encoder.SubmitFrame(CopyTexture(gpu, nv12));
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            StatsUpdated?.Invoke();
        }
    }

    private static ID3D11Texture2D CopyTexture(D3D11Device gpu, ID3D11Texture2D source)
    {
        var copy = gpu.Device.CreateTexture2D(source.Description);
        gpu.ImmediateContext.CopyResource(copy, source);
        return copy;
    }

    private void OnAccessUnitEncoded(byte[] accessUnit)
    {
        AccessUnitsEncoded++;
        TotalEncodedBytes += accessUnit.Length;
        StatsUpdated?.Invoke();
    }

    private void OnEncodingFailed(Exception ex)
    {
        LastError = ex.Message;
        StatsUpdated?.Invoke();
    }

    public void Stop() => StopInternal();

    private void StopInternal()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _loopTask = null;
        _cts?.Dispose();
        _cts = null;

        if (_encoder != null)
        {
            _encoder.AccessUnitEncoded -= OnAccessUnitEncoded;
            _encoder.EncodingFailed -= OnEncodingFailed;
        }
        _encoder?.Dispose();
        _encoder = null;
        _converter?.Dispose();
        _converter = null;
        _capture?.Dispose();
        _capture = null;
        _gpu?.Dispose();
        _gpu = null;
    }

    public void Dispose() => StopInternal();
}
