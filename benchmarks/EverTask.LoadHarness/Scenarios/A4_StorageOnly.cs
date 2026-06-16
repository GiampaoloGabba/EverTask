using System.Diagnostics;
using EverTask.Abstractions;
using EverTask.LoadHarness.Infra;
using EverTask.LoadHarness.Tasks;
using EverTask.Storage;

namespace EverTask.LoadHarness.Scenarios;

/// <summary>
/// A4-storage — the three synchronous writes a task's lifecycle costs (Persist + SetInProgress +
/// SetCompleted), against the chosen storage, with NO engine. Isolates the persistence side: the gap
/// between this and L8 is everything the engine adds; the gap between storages is the DB cost
/// (BENCHMARK_PLAN §4). Audit is None here — write amplification is L-audit's job, not this anchor's.
///
/// Note: against In-Memory this degrades across iterations because MemoryTaskStorage looks up by id with
/// an O(n) scan under a global lock — that's the L-slowdown-mem signal, not a fair write-cost floor.
/// The real target of A4-storage is the relational providers (indexed by PK).
/// </summary>
public sealed class A4StorageOnly : IScenario
{
    public string Id => "A4S";
    public string Description => "storage-only: 3 writes/task (Persist+SetInProgress+SetCompleted)";

    private static readonly string TaskType = typeof(CountingTask).FullName!;
    private static readonly string HandlerType = typeof(CountingHandler).FullName!;

    private StorageHandle? _handle;

    public async Task SetupAsync(RunConfig cfg, CancellationToken ct)
    {
        _handle = await StorageMatrix.CreateAsync(cfg.Storage, ct);
        Console.WriteLine($"  storage: {_handle.Description}  (3 round-trips/task)");
    }

    public Task TeardownAsync() => _handle is null ? Task.CompletedTask : _handle.DisposeAsync().AsTask();

    public async Task<IterationOutcome> RunIterationAsync(RunConfig cfg, LatencyRecorder latency, CancellationToken ct)
    {
        var storage = _handle!.Storage;
        int lanes = Math.Max(1, cfg.Parallelism);
        long perLane = cfg.Count / lanes;
        long total = perLane * lanes;

        var sw = Stopwatch.StartNew();
        var tasks = new Task[lanes];
        for (int l = 0; l < lanes; l++)
        {
            tasks[l] = Task.Run(async () =>
            {
                for (long i = 0; i < perLane; i++)
                {
                    var id = Guid.NewGuid();
                    long t0 = Stopwatch.GetTimestamp();
                    await storage.Persist(BuildTask(id), ct).ConfigureAwait(false);
                    await storage.SetInProgress(id, AuditLevel.None, ct).ConfigureAwait(false);
                    await storage.SetCompleted(id, 0.0, AuditLevel.None).ConfigureAwait(false);
                    latency.Record(Stopwatch.GetTimestamp() - t0); // latency of the 3-write lifecycle
                }
            }, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        return new IterationOutcome(total, sw.Elapsed.TotalSeconds);
    }

    private static QueuedTask BuildTask(Guid id) => new()
    {
        Id = id,
        Type = TaskType,
        Request = "{}",
        Handler = HandlerType,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Status = QueuedTaskStatus.Queued
    };
}
