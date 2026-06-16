using System.Diagnostics;
using EverTask.LoadHarness.Infra;
using EverTask.LoadHarness.Tasks;

namespace EverTask.LoadHarness.Scenarios;

/// <summary>
/// Shared engine driver: dispatch N <see cref="CountingTask"/> through a real EverTask host, then wait
/// for the worker to EXECUTE all of them (completion via <see cref="CountingHandler.OnCompleted"/>, i.e.
/// after the full lifecycle including the SetCompleted write). Subclasses pick the storage mode:
/// "null" (worker-only, A4W) or "storage" (real backend, L8). Latency recorded by the handler is the
/// dispatch→handler-start segment.
/// </summary>
public abstract class EngineScenarioBase : IScenario
{
    protected abstract string StorageMode { get; }
    public abstract string Id { get; }
    public abstract string Description { get; }

    private HostHandle? _host;

    public async Task SetupAsync(RunConfig cfg, CancellationToken ct)
    {
        _host = await HostFactory.CreateAsync(cfg, StorageMode, ct);
        Console.WriteLine($"  storage: {_host.Description}");
    }

    public Task TeardownAsync() => _host is null ? Task.CompletedTask : _host.DisposeAsync().AsTask();

    public async Task<IterationOutcome> RunIterationAsync(RunConfig cfg, LatencyRecorder latency, CancellationToken ct)
    {
        var gate = new CompletionGate(Math.Max(cfg.Parallelism * 4, 16));
        _host!.Context.Current = new RunContext.Run(gate, latency);

        int producerCount = Math.Max(1, cfg.Producers);
        long perProducer = cfg.Count / producerCount;
        long total = perProducer * producerCount;
        var dispatcher = _host.Dispatcher;

        var sw = Stopwatch.StartNew();
        var producers = new Task[producerCount];
        for (int p = 0; p < producerCount; p++)
        {
            producers[p] = Task.Run(async () =>
            {
                for (long i = 0; i < perProducer; i++)
                    await dispatcher.Dispatch(new CountingTask(Stopwatch.GetTimestamp()), cancellationToken: ct)
                                    .ConfigureAwait(false);
            }, ct);
        }

        await Task.WhenAll(producers).ConfigureAwait(false);
        gate.WaitFor(total, ct); // wait for actual EXECUTION (full lifecycle), not just enqueue
        sw.Stop();

        _host.Context.Current = null;
        return new IterationOutcome(total, sw.Elapsed.TotalSeconds);
    }
}
