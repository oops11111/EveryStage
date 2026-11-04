namespace EveryStage.Terminal.Documents;

/// <summary>Loads presenter images and notes while ensuring only the latest request can be published.</summary>
internal sealed class PresenterContentLoader
{
    private int _version;

    internal readonly record struct Result(Bitmap? Next, Bitmap? Browse, string Notes, int Version)
    {
        internal void DisposeImages() { Next?.Dispose(); Browse?.Dispose(); }
    }

    internal void Invalidate() => Interlocked.Increment(ref _version);
    internal bool IsCurrent(Result result) => result.Version == Volatile.Read(ref _version);

    internal async Task<Result?> LoadAsync(int slide, int browseSlide,
        Func<int, int, Task<Bitmap?>> image, Func<int, Task<string>> notes)
    {
        int version = Interlocked.Increment(ref _version);
        Bitmap? next = null, browse = null;
        try
        {
            next = await image(slide + 1, 640);
            string text = await notes(slide);
            if (browseSlide > 0) browse = await image(browseSlide, 960);
            if (version != Volatile.Read(ref _version))
            {
                next?.Dispose();
                browse?.Dispose();
                return null;
            }
            return new Result(next, browse, text, version);
        }
        catch
        {
            next?.Dispose();
            browse?.Dispose();
            throw;
        }
    }
}
