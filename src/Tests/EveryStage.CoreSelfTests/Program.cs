using EveryStage.Discovery;
using EveryStage.Transport;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

var failures = new List<string>();

await RunAsync("RTP/H.264 transport", async () =>
{
    var result = await TransportSelfTest.RunAsync();
    return (result.Success, result.FailureReason);
});

await RunAsync("Raw audio RTP transport", async () =>
{
    var result = await RawTransportSelfTest.RunAsync();
    return (result.Success, result.FailureReason);
});

await RunAsync("Discovery protocol", async () =>
{
    var result = await DiscoveryProtocolSelfTest.RunAsync();
    return (result.Success, result.FailureReason);
});

await RunAsync("Device identity persistence", () =>
{
    var result = DeviceIdentitySelfTest.Run();
    return Task.FromResult((result.Success, result.FailureReason));
});

await RunAsync("Output state machine", () => Task.FromResult(TestOutputStateMachine()));
await RunAsync("Media queue navigation", () => Task.FromResult(TestMediaQueue()));
await RunAsync("Settings migration", () => Task.FromResult(TestSettingsMigration()));

if (failures.Count == 0)
{
    Console.WriteLine("All core self-tests passed.");
    return 0;
}

Console.Error.WriteLine($"{failures.Count} core self-test(s) failed:");
foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
return 1;

async Task RunAsync(string name, Func<Task<(bool Success, string? FailureReason)>> test)
{
    try
    {
        var result = await test();
        if (result.Success)
        {
            Console.WriteLine($"PASS: {name}");
            return;
        }

        failures.Add($"{name}: {result.FailureReason ?? "unknown failure"}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
    }
}

(bool, string?) TestOutputStateMachine()
{
    var machine = new OutputStateMachine();
    var observed = new List<OutputState>();
    machine.StateChanged += _ => throw new InvalidOperationException("subscriber isolation probe");
    machine.StateChanged += observed.Add;

    machine.SetCastSwitch(false);
    if (machine.RequestLocalFilePlayback()) return (false, "Local playback bypassed the disabled cast switch.");
    if (!machine.AcceptDeviceCastRequest()) return (false, "Device cast was rejected while the switch was off.");
    machine.Disconnect();
    if (machine.State != OutputState.Idle || machine.CastSwitchOn) return (false, "Disconnect changed an independent state axis.");
    if (!observed.SequenceEqual(new[] { OutputState.Active, OutputState.Idle })) return (false, "State events were missing after a subscriber threw.");
    return (true, null);
}

(bool, string?) TestMediaQueue()
{
    static MediaFile File(string name, bool background = false) => new()
    {
        SourcePath = name,
        Kind = background ? MediaKind.Audio : MediaKind.Image,
        IsBackgroundAudio = background,
    };

    var background = File("background.mp3", true);
    var activity = new Activity { Name = "queue" };
    activity.Files.AddRange(new[] { File("first.png"), background, File("last.png") });
    var encountered = new List<MediaFile>();

    int next = PlaybackQueueNavigator.FindNextPlayable(activity, 0, 1, encountered.Add);
    if (next != 2 || encountered.Count != 1 || !ReferenceEquals(encountered[0], background))
        return (false, "Forward navigation did not transparently pass background audio.");
    if (PlaybackQueueNavigator.FindNextPlayable(activity, 2, 1) != -1)
        return (false, "Queue advanced beyond its end.");
    if (PlaybackQueueNavigator.FindNextPlayable(activity, 2, -1) != 0)
        return (false, "Reverse navigation did not skip background audio.");
    try
    {
        PlaybackQueueNavigator.FindNextPlayable(activity, 0, 2);
        return (false, "Queue accepted a non-adjacent navigation delta.");
    }
    catch (ArgumentOutOfRangeException)
    {
    }
    return (true, null);
}

(bool, string?) TestSettingsMigration()
{
    string directory = Path.Combine(Path.GetTempPath(), "EveryStage-CoreSelfTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "settings.json");
    try
    {
        File.WriteAllText(path, "{\"CastSwitchDefaultOn\":false,\"PreferredMonitorDeviceName\":\"DISPLAY-LEGACY\",\"DefaultStayDurationSeconds\":15}");
        var migrated = new SettingsStore(path).Current;
        if (migrated.SchemaVersion != AppSettings.CurrentSchemaVersion || migrated.CastSwitchDefaultOn
            || migrated.PreferredMonitorDeviceName != "DISPLAY-LEGACY" || migrated.DefaultStayDurationSeconds != 15)
            return (false, "Legacy settings were not preserved during migration.");

        File.WriteAllText(path, "{\"SchemaVersion\":999,\"CastSwitchDefaultOn\":false}");
        var future = new SettingsStore(path).Current;
        if (future.SchemaVersion != AppSettings.CurrentSchemaVersion || !future.CastSwitchDefaultOn)
            return (false, "Future-version settings did not fail safe to defaults.");
        return (true, null);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}
