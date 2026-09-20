namespace EveryStage.Terminal.Logging;

public static class LogPaths
{
    // Same ProgramData root as ScenarioRepository's default store path — one place to look for
    // both the Terminal's persisted data and its logs on an unattended, shared-account device.
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EveryStage", "logs");
}
