using EveryStage.Discovery;
using EveryStage.Transport;

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
