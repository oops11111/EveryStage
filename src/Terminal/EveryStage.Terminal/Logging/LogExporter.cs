using System.IO.Compression;

namespace EveryStage.Terminal.Logging;

public static class LogExporter
{
    public static int Export(string destinationZipPath, string? logRootOverride = null)
    {
        string root = logRootOverride ?? LogPaths.DefaultRoot;
        string? destinationDirectory = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);
        if (File.Exists(destinationZipPath)) File.Delete(destinationZipPath);

        int count = 0;
        using var archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);
        if (!Directory.Exists(root)) return count;
        foreach (string path in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories))
        {
            string entryName = Path.GetRelativePath(root, path).Replace('\\', '/');
            archive.CreateEntryFromFile(path, entryName, CompressionLevel.Fastest);
            count++;
        }
        return count;
    }
}
