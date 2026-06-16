using System.Text.Json;

namespace EverTask.LoadHarness.Infra;

/// <summary>
/// Drives a scenario: discard warmup iterations, measure the rest, compute throughput mean/stdev (+CV),
/// accumulate latency percentiles across the measured iterations, attribute allocations with
/// <see cref="GC.GetTotalAllocatedBytes(bool)"/> (process-wide → captures worker-thread allocations,
/// unlike the per-thread counter), then print a table and write a JSON report.
/// </summary>
public static class Runner
{
    public static async Task RunAsync(IScenario scenario, RunConfig cfg, EnvInfo env, CancellationToken ct = default)
    {
        Console.WriteLine($"=== {scenario.Id} — {scenario.Description} ===");
        Console.WriteLine($"count={cfg.Count:N0} parallelism={cfg.Parallelism} capacity={cfg.Capacity:N0} " +
                          $"warmup={cfg.Warmup} measured={cfg.Measured}");

        var latency = new LatencyRecorder();

        await scenario.SetupAsync(cfg, ct);
        try
        {
            await RunMeasuredAsync(scenario, cfg, env, latency, ct);
        }
        finally
        {
            await scenario.TeardownAsync();
        }
    }

    private static async Task RunMeasuredAsync(IScenario scenario, RunConfig cfg, EnvInfo env,
                                               LatencyRecorder latency, CancellationToken ct)
    {
        // Warmup: run and discard. Latency from these iterations is thrown away (reset below).
        for (int i = 0; i < cfg.Warmup; i++)
        {
            await scenario.RunIterationAsync(cfg, latency, ct);
            Console.Write($"  warmup {i + 1}/{cfg.Warmup}\r");
        }
        Console.WriteLine(new string(' ', 24) + "\r  warmup done");

        latency.Reset();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var throughputs = new double[cfg.Measured];
        long totalTasks = 0;
        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < cfg.Measured; i++)
        {
            var outcome = await scenario.RunIterationAsync(cfg, latency, ct);
            throughputs[i] = outcome.ThroughputPerSecond;
            totalTasks += outcome.Tasks;
            Console.Write($"  measured {i + 1}/{cfg.Measured}: {outcome.ThroughputPerSecond:N0} tasks/s\r");
        }

        long allocAfter = GC.GetTotalAllocatedBytes(precise: true);
        Console.WriteLine(new string(' ', 48) + "\r  measured done");

        var stats = ThroughputStats.From(throughputs);
        double bytesPerTask = totalTasks > 0 ? (double)(allocAfter - allocBefore) / totalTasks : 0;
        var snap = latency.Snapshot();

        PrintSummary(stats, snap, bytesPerTask);

        var report = new RunReport(
            Scenario: scenario.Id,
            Description: scenario.Description,
            TimestampUtc: DateTime.UtcNow.ToString("O"),
            Config: cfg,
            Env: env,
            Throughput: stats,
            Latency: snap,
            BytesPerTask: bytesPerTask);

        WriteJson(report, cfg.OutputDir);
        Console.WriteLine();
    }

    private static void PrintSummary(ThroughputStats t, LatencySnapshot l, double bytesPerTask)
    {
        Console.WriteLine($"  Throughput      : {t.MeanPerSecond:N0} tasks/s  (stdev {t.StdDevPerSecond:N0}, CV {t.Cv:P1})");
        if (t.Cv > 0.05)
            Console.WriteLine("  ⚠ CV > 5%: not steady-state — increase --warmup or pin affinity before trusting these numbers.");
        Console.WriteLine($"  Latency (µs)    : p50={Us(l.P50Ns)} p90={Us(l.P90Ns)} p99={Us(l.P99Ns)} " +
                          $"p999={Us(l.P999Ns)} max={Us(l.MaxNs)}");
        Console.WriteLine($"  p999/p50        : {(l.P50Ns > 0 ? (double)l.P999Ns / l.P50Ns : 0):F1}x   (tail-divergence signal)");
        Console.WriteLine($"  Allocated       : {bytesPerTask:F1} bytes/task");
    }

    private static string Us(long ns) => (ns / 1000.0).ToString("F2");

    private static void WriteJson(RunReport report, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        string path = Path.Combine(outputDir, $"{report.Scenario}-{stamp}.json");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        Console.WriteLine($"  → {path}");
    }
}

public readonly record struct ThroughputStats(double MeanPerSecond, double StdDevPerSecond, double Cv)
{
    public static ThroughputStats From(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return new ThroughputStats(0, 0, 0);
        double mean = samples.Average();
        double variance = samples.Sum(s => (s - mean) * (s - mean)) / samples.Count;
        double stdev = Math.Sqrt(variance);
        return new ThroughputStats(mean, stdev, mean > 0 ? stdev / mean : 0);
    }
}

public sealed record RunReport(
    string Scenario,
    string Description,
    string TimestampUtc,
    RunConfig Config,
    EnvInfo Env,
    ThroughputStats Throughput,
    LatencySnapshot Latency,
    double BytesPerTask);
