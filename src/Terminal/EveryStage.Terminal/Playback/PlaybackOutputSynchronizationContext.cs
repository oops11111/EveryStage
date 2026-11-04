namespace EveryStage.Terminal.Playback;

/// <summary>Returns document continuations to the owning UI thread, even if the output is closed.</summary>
internal sealed class PlaybackOutputSynchronizationContext : SynchronizationContext
{
    // WinForms owns the thread's shared marshaling control. Do not dispose this wrapper: doing so
    // would dispose that shared control and interrupt other windows on the same UI thread.
    private readonly WindowsFormsSynchronizationContext _dispatcher = new();

    public override SynchronizationContext CreateCopy() => this;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        try
        {
            _dispatcher.Post(_ =>
            {
                var previous = Current;
                try { SetSynchronizationContext(this); callback(state); }
                finally { SetSynchronizationContext(previous); }
            }, null);
        }
        catch (InvalidOperationException) { } // the UI thread itself is shutting down
    }
}
