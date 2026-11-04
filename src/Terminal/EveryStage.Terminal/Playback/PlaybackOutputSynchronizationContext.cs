using EveryStage.Terminal.Display;

namespace EveryStage.Terminal.Playback;

/// <summary>Returns asynchronous document continuations to their output's owning UI thread.</summary>
internal sealed class PlaybackOutputSynchronizationContext(IPlaybackOutput output) : SynchronizationContext
{
    public override SynchronizationContext CreateCopy() => this;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        try
        {
            output.Post(() =>
            {
                var previous = Current;
                try { SetSynchronizationContext(this); callback(state); }
                finally { SetSynchronizationContext(previous); }
            });
        }
        catch (InvalidOperationException) { } // output was closed while the operation was finishing
    }
}
