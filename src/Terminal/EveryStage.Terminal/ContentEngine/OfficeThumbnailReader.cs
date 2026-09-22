using System.IO.Compression;

namespace EveryStage.Terminal.ContentEngine;

public static class OfficeThumbnailReader
{
    public static Bitmap? Read(string path, Size target)
    {
        using var archive = OpenArchive(path);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith("docProps/thumbnail.", StringComparison.OrdinalIgnoreCase)
            && new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(e.FullName), StringComparer.OrdinalIgnoreCase));
        if (entry == null || entry.Length > 8 * 1024 * 1024) return null;
        using var stream = entry.Open();
        using var source = Image.FromStream(stream);
        double scale = Math.Min(Math.Max(1, target.Width) / (double)source.Width, Math.Max(1, target.Height) / (double)source.Height);
        return new Bitmap(source, new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale))));
    }

    private static ZipArchive OpenArchive(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (InvalidDataException ex) { throw new IOException("文档格式损坏，无法读取预览。", ex); }
    }
}
