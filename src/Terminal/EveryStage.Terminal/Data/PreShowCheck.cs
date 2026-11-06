namespace EveryStage.Terminal.Data;

public enum PreShowProblem { Missing, Unreadable, UnsupportedFormat, Spreadsheet, NeedsTranscode }

/// <summary>One file of the scenario that will not play as it stands.</summary>
public sealed record PreShowFinding(Activity Activity, int Index, MediaFile File, PreShowProblem Problem);

/// <summary>
/// 开演前检查: every file of a scenario — does it exist, can it be opened for reading, is its format one
/// EveryStage plays. It checks ONLY that (not whether the content decodes or plays correctly) and never
/// blocks the show; the 流程 page lists what it found and marks the rows. Runs off the UI thread (a
/// network path or a removed USB drive can take a while to answer).
/// </summary>
public static class PreShowCheck
{
    public const string Scope = "只检查文件是否存在、能否读取、格式是否支持，不代表一定能正常播放；检查结果不会阻止开演。";

    public static IReadOnlyList<PreShowFinding> Run(Scenario scenario, CancellationToken cancellationToken = default)
    {
        var findings = new List<PreShowFinding>();
        foreach (var activity in scenario.Activities.ToList())
        {
            var files = activity.Files.ToList();
            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Inspect(files[i]) is { } problem) findings.Add(new PreShowFinding(activity, i, files[i], problem));
            }
        }
        return findings;
    }

    public static PreShowProblem? Inspect(MediaFile file)
    {
        string path = file.SourcePath;
        if (EveryStage.Terminal.Documents.DocumentFormats.IsSpreadsheet(path)) return PreShowProblem.Spreadsheet;
        if (FileLibraryStore.InferKind(path) == null) return PreShowProblem.UnsupportedFormat;
        try
        {
            if (!File.Exists(path)) return PreShowProblem.Missing;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The file exists and can be read, so anything more basic is already ruled out; only
            // now does the import-time transcode hint (MediaFile.NeedsTranscode) get reported. It
            // is the weakest finding here — a container-level guess, not a decode failure — which
            // is why it is checked last and, like everything in this check, never blocks the show.
            // The specific reason rides on the file itself (MediaFile.TranscodeReason); Describe
            // below only has the enum, so it stays generic.
            if (file.NeedsTranscode) return PreShowProblem.NeedsTranscode;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex is FileNotFoundException or DirectoryNotFoundException ? PreShowProblem.Missing : PreShowProblem.Unreadable;
        }
    }

    public static string Describe(PreShowProblem problem) => problem switch
    {
        PreShowProblem.Missing => "文件不存在（已移动、删除或 U 盘已拔出）",
        PreShowProblem.Unreadable => "无法读取（被占用或没有权限）",
        PreShowProblem.Spreadsheet => "表格不能直接播放，请另存为 PDF",
        PreShowProblem.NeedsTranscode => "视频容器可能不受支持，可能需要转码为 MP4（H.264 + AAC）才能播放",
        _ => "格式不支持",
    };

    public static string ShortLabel(PreShowProblem problem) => problem switch
    {
        PreShowProblem.Missing => "缺失",
        PreShowProblem.Unreadable => "无法读取",
        PreShowProblem.NeedsTranscode => "需转码",
        _ => "格式不支持",
    };
}
