using System.Collections.Concurrent;
using System.Diagnostics;
using EverTask.Abstractions;
using EverTask.LoadHarness.Infra;
using EverTask.LoadHarness.Tasks;
using EverTask.Storage;

namespace EverTask.LoadHarness.Scenarios;

/// <summary>
/// A3 — naive DB-polling dispatcher: the anti-pattern EverTask's push-via-Channel design replaces.
/// A producer persists tasks (Queued) with arrivals spread across a few poll intervals; a consumer
/// polls <see cref="ITaskStorage.RetrievePending"/> every <c>--poll-interval</c> and "processes" each
/// (marks it Completed to drop it from the pending set). The pickup latency it measures is dominated by
/// the interval (≈ interval/2 on average) — that's the number that quantifies the polling penalty,
/// WITHOUT naming any competitor (BENCHMARK_PLAN §4). Compare its latency to L-lat's push latency.
///
/// A3 is a reference, not a throughput test: keep --count modest and --poll-interval realistic. It uses
/// SetCompleted (not SetInProgress) to remove a task from pending, because RetrievePending also returns
/// InProgress (it doubles as the recovery query) — marking InProgress would re-pick the same row forever.
/// </summary>
public sealed class A3NaivePolling : IScenario
{
    public string Id => "A3";
    public string Description => "naive DB-polling dispatcher (anti-polling reference)";

    private static readonly string TaskType = typeof(CountingTask).FullName!;
    private static readonly string HandlerType = typeof(CountingHandler).FullName!;

    private StorageHandle? _handle;

    public async Task SetupAsync(RunConfig cfg, CancellationToken ct)
    {
        _handle = await StorageMatrix.CreateAsync(cfg.Storage, ct);
        Console.WriteLine($"  storage: {_handle.Description}  poll-interval={cfg.PollIntervalMs}ms");
    }

    public Task TeardownAsync() => _handle is null ? Task.CompletedTask : _handle.DisposeAsync().AsTask();

    public async Task<IterationOutcome> RunIterationAsync(RunConfig cfg, LatencyRecorder latency, CancellationToken ct)
    {
        var storage = _handle!.Storage;
        long count = cfg.Count;
        long intervalTicks = (long)(cfg.PollIntervalMs * (Stopwatch.Frequency / 1000.0));
        long spreadTicks = intervalTicks * 3;             // arrivals spread over ~3 intervals
        long spacing = count > 0 ? Math.Max(1, spreadTicks / count) : 1;
        const int pageSize = 1000;

        var dispatchTicks = new ConcurrentDictionary<Guid, long>();
        long drained = 0;
        var producerDone = false;

        var sw = Stopwatch.StartNew();
        long start = Stopwatch.GetTimestamp();

        // Producer: persist Queued tasks at scheduled arrival times (open-loop arrivals).
        var producer = Task.Run(async () =>
        {
            for (long i = 0; i < count; i++)
            {
                long planned = start + i * spacing;
                while (Stopwatch.GetTimestamp() < planned) Thread.SpinWait(20);

                var id = Guid.NewGuid();
                dispatchTicks[id] = Stopwatch.GetTimestamp();
                await storage.Persist(BuildTask(id), ct).ConfigureAwait(false);
            }
            Volatile.Write(ref producerDone, true);
        }, ct);

        // Consumer: fixed-cadence polling. Drain everything pending each tick, then sleep the interval.
        while (true)
        {
            // Drain all currently-pending rows in pages.
            while (true)
            {
                var page = await storage.RetrievePending(null, null, pageSize, ct).ConfigureAwait(false);
                if (page.Length == 0) break;

                long now = Stopwatch.GetTimestamp();
                foreach (var row in page)
                {
                    if (dispatchTicks.TryGetValue(row.Id, out long dt))
                        latency.Record(now - dt);
                    await storage.SetCompleted(row.Id, 0.0, AuditLevel.None).ConfigureAwait(false);
                    drained++;
                }
                if (page.Length < pageSize) break;
            }

            if (Volatile.Read(ref producerDone) && drained >= count) break;
            await Task.Delay(cfg.PollIntervalMs, ct).ConfigureAwait(false);
        }

        await producer.ConfigureAwait(false);
        sw.Stop();
        return new IterationOutcome(drained, sw.Elapsed.TotalSeconds);
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
