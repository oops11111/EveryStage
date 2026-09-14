using System.Drawing;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// "谁输出谁负责" (PLANNING.md §3): for Terminal-local files, the Content Engine decodes and
/// renders directly — no screen-capture round trip, unlike content coming from a Caster. Each
/// <see cref="MediaKind"/> gets its own renderer; this interface only covers the static/paged
/// content kinds (image, document). Video is intentionally out of scope here — it belongs to the
/// D3D11/Media Foundation pipeline validated in <c>src/Poc/ZeroCopyRenderDemo</c>, which is a
/// fundamentally different rendering path (GPU texture streaming, not "decode a bitmap").
/// </summary>
public interface IContentRenderer : IDisposable
{
    MediaKind SupportedKind { get; }

    Task LoadAsync(string path);

    /// <summary>Current page/frame as a drawable bitmap. Null before <see cref="LoadAsync"/> completes.</summary>
    Bitmap? CurrentFrame { get; }

    /// <summary>1 for single-page content (plain images); &gt;1 for paged documents.</summary>
    int PageCount { get; }
    int CurrentPageIndex { get; }

    /// <summary>No-ops (returns false) at the last/first page — callers should treat that as "can't
    /// advance", not an error, since PLANNING.md §6 lets a file's completion action decide what
    /// happens next (advance activity, loop, hold).</summary>
    bool NextPage();
    bool PreviousPage();
}
