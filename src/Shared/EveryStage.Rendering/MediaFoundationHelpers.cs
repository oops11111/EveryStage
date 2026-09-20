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
}
