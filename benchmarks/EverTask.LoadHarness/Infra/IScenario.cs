namespace EverTask.LoadHarness.Infra;

/// <summary>
/// One benchmark scenario. The scenario owns a single iteration (dispatch N, wait for completion,
/// return wall-time); the <see cref="Runner"/> owns warmup/measured loops, stats, allocation
/// measurement and reporting. Latencies are recorded into the supplied <see cref="LatencyRecorder"/>.
/// </summary>
public interface IScenario
{
    /// <summary>Short id, e.g. "A1". Used on the CLI and in the report.</summary>
    string Id { get; }

    string Description { get; }

    /// <summary>Build expensive shared state once (host, storage, container) before warmup. Optional.</summary>
    Task SetupAsync(RunConfig cfg, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Run ONE iteration. Implementations time only the work itself and return the task count plus
    /// elapsed seconds; they must record per-task latency into <paramref name="latency"/>.
    /// </summary>
    Task<IterationOutcome> RunIterationAsync(RunConfig cfg, LatencyRecorder latency, CancellationToken ct);

    /// <summary>Tear down shared state after the measured iterations. Optional.</summary>
    Task TeardownAsync() => Task.CompletedTask;
}

/// <summary>Result of a single iteration: how many tasks completed and how long it took.</summary>
public readonly record struct IterationOutcome(long Tasks, double ElapsedSeconds)
{
    public double ThroughputPerSecond => ElapsedSeconds > 0 ? Tasks / ElapsedSeconds : 0;
}
