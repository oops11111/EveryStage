using System.Text.Json;

namespace EveryStage.Caster.UI;

/// <summary>The Caster's two personal preferences: the colour theme and whether casts carry the
/// computer's sound. Per Windows user (%LOCALAPPDATA%\EveryStage\caster-settings.json); a missing or
/// unreadable file simply means the defaults (follow Windows, sound on).</summary>
internal sealed class CasterSettings
{
    public CasterTheme Theme { get; set; } = CasterTheme.System;
    public bool IncludeAudio { get; set; } = true;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveryStage", "caster-settings.json");

    public static CasterSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<CasterSettings>(File.ReadAllText(FilePath)) ?? new CasterSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        return new CasterSettings();
    }

    /// <summary>False when the file could not be written (OP-37: the caller must say so — a
    /// preference that silently reverts next launch looks like the app forgot the choice).</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
