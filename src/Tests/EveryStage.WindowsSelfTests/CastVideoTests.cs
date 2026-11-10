using System.Diagnostics;
using EveryStage.Caster.Encode;
using EveryStage.Rendering;
using EveryStage.Terminal.Receiving;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// The cast video path without a network: frames through the Caster's H.264 encoder into the Terminal's
/// H.264 decoder, exactly the codecs a cast uses. On a machine with no GPU video encoder/decoder (the
/// hosted CI runner, a VM, a PC whose graphics driver offers no H.264 transform) both sides must fall
/// back to Windows' software codecs — before that fallback existed, such a machine failed every cast
/// with "No matching Media Foundation transform was found."
/// </summary>
internal static class CastVideoTests
{
    public static void Run()
    {
        const int width = 320, height = 240;
        D3D11Device gpu;
        try { gpu = new D3D11Device(); }
        catch (Exception ex)
        {
            Console.WriteLine($"SKIP: cast video encode/decode round trip (no D3D11 device: {ex.Message})");
            return;
        }
        using (gpu)
        {
            H264HardwareEncoder encoder;
            try { encoder = new H264HardwareEncoder(gpu, width, height, frameRateNumerator: 30, bitrateBps: 1_000_000); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("没有可用的 H.264 视频编码器"))
            {
                Console.WriteLine($"SKIP: cast video encode/decode round trip (no H.264 encoder at all: {ex.Message})");
                return;
            }

            var accessUnits = new List<byte[]>();
            Exception? encodeFailure = null;
            using (encoder)
            {
                encoder.AccessUnitEncoded += (unit, _) => { lock (accessUnits) accessUnits.Add(unit); };
                encoder.EncodingFailed += ex => encodeFailure = ex;
                for (int i = 0; i < 30; i++)
                {
                    encoder.SubmitFrame(CreateNv12Frame(gpu, width, height, (byte)(16 + i * 6)), captureTime: i * 333_333L);
                    Thread.Sleep(15);
                }
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(10) && encodeFailure == null)
                {
                    lock (accessUnits) if (accessUnits.Count >= 10) break;
                    Thread.Sleep(20);
                }
                if (encodeFailure != null) throw new InvalidOperationException("Cast video: the encoder failed: " + encodeFailure.Message, encodeFailure);
                Console.WriteLine($"INFO: cast encoder: {(encoder.UsesSoftwareEncoder ? "software" : "hardware")} ({encoder.TuningSummary}); {accessUnits.Count} access units");
            }
            lock (accessUnits)
                if (accessUnits.Count == 0) throw new InvalidOperationException("Cast video: the encoder produced no access units from 30 frames.");

            int decoded = 0, decodedWidth = 0, decodedHeight = 0;
            Exception? decodeFailure = null;
            using var decoder = new H264HardwareDecoder(gpu, width, height);
            decoder.FrameDecoded += (texture, _, w, h, _, sample) =>
            {
                decodedWidth = w;
                decodedHeight = h;
                Interlocked.Increment(ref decoded);
                texture.Dispose();
                sample.Dispose();
            };
            decoder.DecodingFailed += ex => decodeFailure = ex;
            long time = 0;
            foreach (var unit in accessUnits)
            {
                decoder.SubmitAccessUnit(unit, time);
                time += 333_333;
            }
            var decodeWait = Stopwatch.StartNew();
            while (decodeWait.Elapsed < TimeSpan.FromSeconds(10) && Volatile.Read(ref decoded) == 0 && decodeFailure == null) Thread.Sleep(20);
            if (decodeFailure != null) throw new InvalidOperationException("Cast video: the decoder failed: " + decodeFailure.Message, decodeFailure);
            if (Volatile.Read(ref decoded) == 0)
                throw new InvalidOperationException($"Cast video: the {(decoder.UsesSoftwareDecoder ? "software" : "hardware")} decoder produced no frame from {accessUnits.Count} access units.");
            if (decodedWidth < width || decodedHeight < height)
                throw new InvalidOperationException($"Cast video: decoded frame {decodedWidth}x{decodedHeight} is smaller than the {width}x{height} that was encoded.");
            Console.WriteLine($"PASS: cast video encode → decode round trip ({decoded} frames, decoder: {(decoder.UsesSoftwareDecoder ? "software" : "hardware")}"
                + (decoder.HardwareUnavailableReason is { } why ? $"; hardware unavailable: {why}" : "") + ")");
        }
    }

    /// <summary>A flat NV12 frame (luma <paramref name="luma"/>, neutral chroma) as a GPU texture, the
    /// form the Caster's capture pipeline hands the encoder.</summary>
    private static ID3D11Texture2D CreateNv12Frame(D3D11Device gpu, int width, int height, byte luma)
    {
        var bytes = new byte[width * height * 3 / 2];
        Array.Fill(bytes, luma, 0, width * height);
        Array.Fill(bytes, (byte)128, width * height, width * height / 2);
        var desc = new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.None,
        };
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var data = new SubresourceData(handle.AddrOfPinnedObject(), (uint)width, (uint)(width * height));
            try { return gpu.Device.CreateTexture2D(desc, new[] { data }); }
            catch (Exception)
            {
                desc.BindFlags = BindFlags.None;
                return gpu.Device.CreateTexture2D(desc, new[] { data });
            }
        }
        finally { handle.Free(); }
    }
}
