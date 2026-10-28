using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace EveryStage.Rendering;

public static class MediaFoundationHelpers
{
    public static IMFTransform ActivateFirstTransform(
        Guid category,
        EnumFlag flags,
        RegisterTypeInfo? inputType,
        RegisterTypeInfo? outputType)
    {
        MediaFactory.MFTEnumEx(category, (uint)flags, inputType, outputType, out var arrayPointer, out var count);
        if (count == 0 || arrayPointer == IntPtr.Zero)
            throw new InvalidOperationException("No matching Media Foundation transform was found.");

        var activations = new List<IMFActivate>((int)count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var activationPointer = Marshal.ReadIntPtr(arrayPointer, checked((int)index * IntPtr.Size));
                activations.Add(new IMFActivate(activationPointer));
            }

            return activations[0].ActivateObject<IMFTransform>();
        }
        finally
        {
            foreach (var activation in activations) activation.Dispose();
            Marshal.FreeCoTaskMem(arrayPointer);
        }
    }

    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");

    /// <summary>Sets MF_TRANSFORM_ASYNC_UNLOCK when the transform has an attribute store. Synchronous
    /// transforms may have none: the Microsoft AAC encoder returns E_NOTIMPL from GetAttributes (verified on
    /// Windows 10 19045), which used to make its constructor — and with it every cast's audio — fail.</summary>
    public static void UnlockAsyncIfSupported(IMFTransform transform)
    {
        IMFAttributes attributes;
        try
        {
            attributes = transform.Attributes;
        }
        catch (Exception ex) when (ex.HResult == E_NOTIMPL)
        {
            return; // synchronous transform without attributes: nothing to unlock
        }
        using (attributes) attributes.Set(MF_TRANSFORM_ASYNC_UNLOCK, 1u);
    }

    /// <summary>Whether <paramref name="transform"/> allocates its own output samples
    /// (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES or CAN_PROVIDE_SAMPLES). When it does not, the caller must
    /// pass a sample to ProcessOutput — calling with none fails every time (audit A-6).</summary>
    public static bool ProvidesOutputSamples(IMFTransform transform, int streamId = 0)
    {
        var info = transform.GetOutputStreamInfo(streamId);
        const int canProvideSamples = 0x200; // MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES
        return (info.Flags & ((int)OutputStreamInfoFlags.OutputStreamProvidesSamples | canProvideSamples)) != 0;
    }

    /// <summary>Pulls one output sample from a synchronous transform. Returns
    /// <see cref="TransformOutput.NeedMoreInput"/> for MF_E_TRANSFORM_NEED_MORE_INPUT (the normal "nothing
    /// ready" result) and <see cref="TransformOutput.StreamChanged"/> when the output type must be set
    /// again; any other failure throws — it used to be read as "nothing ready" too, so a transform that
    /// failed every call produced silence with no error anywhere (audit A-6).</summary>
    public static TransformOutput ProcessOneOutput(IMFTransform transform, bool providesSamples, out IMFSample? sample, int streamId = 0)
    {
        sample = null;
        IMFSample? own = providesSamples ? null : CreateOutputSample(transform, streamId);
        var buffer = new OutputDataBuffer { StreamID = streamId, Sample = own };
        var result = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
        if (result.Failure)
        {
            own?.Dispose();
            if (result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) return TransformOutput.NeedMoreInput;
            if (result.Code == MF_E_TRANSFORM_STREAM_CHANGE) return TransformOutput.StreamChanged;
            throw new COMException($"IMFTransform.ProcessOutput 失败（0x{result.Code:X8}）", result.Code);
        }
        sample = own ?? buffer.Sample;
        return sample == null ? TransformOutput.NeedMoreInput : TransformOutput.Sample;
    }

    private static IMFSample CreateOutputSample(IMFTransform transform, int streamId)
    {
        var info = transform.GetOutputStreamInfo(streamId);
        var mediaBuffer = info.Alignment > 0
            ? MediaFactory.MFCreateAlignedMemoryBuffer(info.Size, info.Alignment)
            : MediaFactory.MFCreateMemoryBuffer(info.Size);
        var sample = MediaFactory.MFCreateSample();
        using (mediaBuffer) sample.AddBuffer(mediaBuffer);
        return sample;
    }

    /// <summary>Copies a sample's data out as a byte array.</summary>
    public static byte[] CopySampleData(IMFSample sample)
    {
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var data, out _, out var currentLength);
        try
        {
            var bytes = new byte[currentLength];
            Marshal.Copy(data, bytes, 0, currentLength);
            return bytes;
        }
        finally
        {
            contiguous.Unlock();
        }
    }
}

public enum TransformOutput
{
    Sample,
    NeedMoreInput,
    StreamChanged,
}
