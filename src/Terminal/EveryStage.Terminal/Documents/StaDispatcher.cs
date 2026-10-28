using System.Collections.Concurrent;

namespace EveryStage.Terminal.Documents;

/// <summary>
/// A dedicated STA thread for one WPS/Office automation session. Every COM call for that session runs
/// here, so a slow or blocked call (the application starting, a modal dialog inside WPS/Word) never
/// freezes the EveryStage UI — the UI awaits the returned task instead, with a timeout.
/// </summary>
internal sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaDispatcher(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception) { } // each work item completes its own task; nothing may escape the loop
        }
    }

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsCurrentThread)
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
            return completion.Task;
        }
        try
        {
            _queue.Add(() =>
            {
                try { completion.SetResult(work()); }
                catch (Exception ex) { completion.SetException(ex); }
            });
        }
        catch (InvalidOperationException)
        {
            completion.SetException(new ObjectDisposedException(nameof(StaDispatcher)));
        }
        return completion.Task;
    }

    public Task InvokeAsync(Action work) => InvokeAsync(() => { work(); return true; });

    /// <summary>Runs <paramref name="work"/> and waits at most <paramref name="timeout"/>. A timed-out call
    /// keeps running on the STA thread (COM cannot be interrupted); the caller treats the session as broken.</summary>
    public async Task<T> InvokeAsync<T>(Func<T> work, TimeSpan timeout)
    {
        var task = InvokeAsync(work);
        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
            throw new TimeoutException("WPS/Office 没有响应（可能弹出了对话框）。");
        return await task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsCurrentThread) _thread.Join(TimeSpan.FromSeconds(10));
    }
}
