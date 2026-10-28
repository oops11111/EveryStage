using System.IO.Compression;
using System.Text.RegularExpressions;

namespace EveryStage.Terminal.Documents;

/// <summary>What a presentation contains that only a native slide show can play.</summary>
public readonly record struct PresentationFeatures(int SlideCount, bool HasAnimations, bool HasTransitions, bool HasMedia, bool Inspected)
{
    /// <summary>Default mode: native when anything would be lost as static slides (or the file could not
    /// be inspected, e.g. the binary .ppt format).</summary>
    public bool NeedsNativeShow => !Inspected || HasAnimations || HasTransitions || HasMedia;

    public string Describe()
    {
        if (!Inspected) return "无法预先分析（旧版 .ppt 格式），按原生放映处理";
        var parts = new List<string>();
        if (HasAnimations) parts.Add("动画");
        if (HasTransitions) parts.Add("转场");
        if (HasMedia) parts.Add("嵌入音视频");
        return parts.Count == 0 ? $"{SlideCount} 张幻灯片，无动画/转场/媒体" : $"{SlideCount} 张幻灯片，含" + string.Join("、", parts);
    }
}

/// <summary>Reads a .pptx package (no Office needed) to decide between native and static playback.</summary>
public static class PresentationInspector
{
    private static readonly Regex SlidePart = new(@"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SlideRels = new(@"^ppt/slides/_rels/slide\d+\.xml\.rels$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static PresentationFeatures Inspect(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".pptx" or ".ppsx")) return new PresentationFeatures(0, false, false, false, Inspected: false);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            int slides = 0;
            bool animations = false, transitions = false, media = false;
            foreach (var entry in zip.Entries)
            {
                if (SlidePart.IsMatch(entry.FullName))
                {
                    slides++;
                    string xml = Read(entry);
                    // <p:timing> with an effect in it = animations.
                    if (xml.Contains("<p:timing", StringComparison.Ordinal)
                        && (xml.Contains("<p:anim", StringComparison.Ordinal) || xml.Contains("<p:set", StringComparison.Ordinal)
                            || xml.Contains("presetClass=", StringComparison.Ordinal) || xml.Contains("<p:video", StringComparison.Ordinal)
                            || xml.Contains("<p:audio", StringComparison.Ordinal)))
                        animations = true;
                    // A slide without a transition has no <p:transition> element at all.
                    if (xml.Contains("<p:transition", StringComparison.Ordinal))
                        transitions = true;
                    if (xml.Contains("<p:videoFile", StringComparison.Ordinal) || xml.Contains("<a:videoFile", StringComparison.Ordinal)
                        || xml.Contains("<a:audioFile", StringComparison.Ordinal) || xml.Contains("<p14:media", StringComparison.Ordinal))
                        media = true;
                }
                else if (SlideRels.IsMatch(entry.FullName))
                {
                    string rels = Read(entry);
                    if (rels.Contains("/relationships/video\"", StringComparison.Ordinal)
                        || rels.Contains("/relationships/audio\"", StringComparison.Ordinal)
                        || rels.Contains("/relationships/media\"", StringComparison.Ordinal))
                        media = true;
                }
            }
            return new PresentationFeatures(slides, animations, transitions, media, Inspected: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new PresentationFeatures(0, false, false, false, Inspected: false);
        }
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
