using System.IO.Compression;

namespace EveryStage.Terminal.Logging;

public static class LogExporter
{
    /// <summary>Zips every log under the log root into <paramref name="destinationZipPath"/>. Built in a
    /// temporary file and moved into place only when complete, so a failure (a log locked by antivirus, a
    /// full disk) leaves any previous export intact instead of having deleted it first (audit C-3). Logs
    /// being written at that moment are read with sharing allowed rather than failing the export.</summary>
    public static int Export(string destinationZipPath, string? logRootOverride = null)
    {
        string root = logRootOverride ?? LogPaths.DefaultRoot;
        string? destinationDirectory = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);

        string temporary = destinationZipPath + ".tmp-" + Guid.NewGuid().ToString("N");
        int count = 0;
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                if (Directory.Exists(root))
                {
                    foreach (string path in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories))
                    {
                        string entryName = Path.GetRelativePath(root, path).Replace('\\', '/');
                        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest).Open();
                        source.CopyTo(entry);
                        count++;
                    }
                }
            }
            File.Move(temporary, destinationZipPath, overwrite: true);
            return count;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
