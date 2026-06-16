using System.Diagnostics;
using EverTask.LoadHarness.Infra;
using EverTask.LoadHarness.Tasks;

namespace EverTask.LoadHarness.Scenarios;

/// <summary>
/// L-dispatch-prod — the latency a real app FEELS: how long <c>await Dispatch()</c> takes on the calling
/// thread, per <c>--storage</c>. Because dispatch persists synchronously before returning, on a DB this
/// includes a write round-trip (and any full-queue back-pressure) — exactly what an ASP.NET request
/// thread pays. On In-Memory it's µs; on a DB it's ms. This is the perceived-performance headline
/// (BENCHMARK_PLAN §5), distinct from L8's throughput.
///
/// The reported latency is the dispatch CALL duration; the handler records into a throwaway recorder so
/// it doesn't mix the execution-side latency into this number.
/// </summary>
public sealed class LDispatchProd : IScenario
{
    public string Id => "LDP";
    public string Description => "dispatch-call latency on the caller thread, per --storage (perceived latency)";

    private HostHandle? _host;

    public async Task SetupAsync(RunConfig cfg, CancellationToken ct)
    {
        _host = await HostFactory.CreateAsync(cfg, "storage", ct);
        Console.WriteLine($"  storage: {_host.Description}");
    }

    public Task TeardownAsync() => _host is null ? Task.CompletedTask : _host.DisposeAsync().AsTask();

    public async Task<IterationOutcome> RunIterationAsync(RunConfig cfg, LatencyRecorder latency, CancellationToken ct)
    {
        var gate = new CompletionGate(Math.Max(cfg.Parallelism * 4, 16));
        var handlerDummy = new LatencyRecorder(); // keep execution-side latency out of the dispatch number
        _host!.Context.Current = new RunContext.Run(gate, handlerDummy);

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
                {
                    long t0 = Stopwatch.GetTimestamp();
                    await dispatcher.Dispatch(new CountingTask(t0), cancellationToken: ct).ConfigureAwait(false);
                    latency.Record(Stopwatch.GetTimestamp() - t0); // caller-side dispatch latency
                }
            }, ct);
        }

        await Task.WhenAll(producers).ConfigureAwait(false);
        gate.WaitFor(total, ct); // drain so the next iteration starts from an empty pipeline
        sw.Stop();

        _host.Context.Current = null;
        return new IterationOutcome(total, sw.Elapsed.TotalSeconds);
    }
}
