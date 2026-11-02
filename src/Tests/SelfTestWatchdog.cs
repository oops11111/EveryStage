using System.Diagnostics;

namespace EveryStage.Tests;

/// <summary>A thread-pool deadline still fires when the UI/COM thread or shutdown is stuck.</summary>
internal sealed class SelfTestWatchdog : IDisposable
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly System.Threading.Timer _timer;
    private readonly int _timeout;
    private readonly string _suite;
    private int _timedOut;

    public SelfTestWatchdog(string suite, int defaultTimeoutSeconds)
    {
        _suite = suite;
        _timeout = int.TryParse(Environment.GetEnvironmentVariable("ES_SELFTEST_TIMEOUT_SECONDS"), out int seconds)
            && seconds > 0 ? seconds : defaultTimeoutSeconds;
        Console.WriteLine($"START: {suite}; pid={Environment.ProcessId}; deadline={_timeout}s");
        _timer = new System.Threading.Timer(_ =>
        {
            Console.WriteLine($"HEARTBEAT: {_suite}; elapsed={_elapsed.Elapsed.TotalSeconds:0}s; deadline={_timeout}s (alive does not imply progress)");
            if (_elapsed.Elapsed.TotalSeconds >= _timeout)
            {
                if (Interlocked.Exchange(ref _timedOut, 1) != 0) return;
                Console.Error.WriteLine($"TIMEOUT: {_suite}; pid={Environment.ProcessId}; elapsed={_elapsed.Elapsed}");
                CollectTimeoutDump();
                Environment.FailFast($"Self-test deadline exceeded: {_suite}");
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
    }

    private void CollectTimeoutDump()
    {
        string? tool = Environment.GetEnvironmentVariable("ES_SELFTEST_DUMP_TOOL");
        if (string.IsNullOrWhiteSpace(tool)) return;
        try
        {
            string directory = Environment.GetEnvironmentVariable("ES_SELFTEST_DUMP_DIRECTORY")
                ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "test-diagnostics");
            Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "collect", "--process-id", Environment.ProcessId.ToString(),
                "--output", Path.Combine(directory, $"{_suite}-{Environment.ProcessId}.dmp"), "--type", "Mini" })
                start.ArgumentList.Add(argument);
            using var dump = Process.Start(start);
            if (dump == null) throw new IOException("Dump collector did not start.");
            if (!dump.WaitForExit(20000))
            {
                dump.Kill(entireProcessTree: true);
                Console.Error.WriteLine("DUMP: collector exceeded 20 seconds; terminating it before failing the suite.");
            }
            else Console.Error.WriteLine($"DUMP: collector exit={dump.ExitCode}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"DUMP: unavailable: {ex.Message}"); }
    }

    public void Dispose() => _timer.Dispose();
}
