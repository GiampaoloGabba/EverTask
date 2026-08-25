using System.Diagnostics;
using EverTask.Abstractions;
using EverTask.LoadHarness.Infra;
using EverTask.LoadHarness.Tasks;
using EverTask.Storage;

namespace EverTask.LoadHarness.Scenarios;

/// <summary>
/// LDM — the DURABLE MATERIALIZATION, the step a durable schedule pays once per occurrence: create the
/// occurrence row and advance the schedule's cursor, in one compare-and-swapped commit. Per <c>--storage</c>.
/// </summary>
/// <remarks>
/// The companion of <c>LRA</c> for the other half of the D7 gate. An inline schedule pays one UPDATE per
/// occurrence (that is LRA); a durable one pays this instead — an INSERT plus the same advance, atomically —
/// and the whole point of implementing it at each provider's own tier (procedures on SQL Server and MySQL, a
/// writable CTE on Postgres, one <c>SaveChanges</c> on the EF base) is that the difference stays a difference
/// of one write and not of a round-trip.
///
/// A live schedule cannot be made to produce this at speed: it is paced by its own grid. Driving the storage
/// operation directly, as <c>A4S</c> drives the three lifecycle writes, leaves a signal that scales with the
/// machine instead of with the clock.
///
/// <c>--parallelism</c> IS the global materialization budget of a real host
/// (<c>SetMaterializationConcurrency</c>): every lane owns its own schedule row, so N lanes are N schedules
/// materializing at once, which is exactly what that budget bounds at startup. Lanes never contend on the
/// same row, so what the number reports is the cost of one materialization and not of a lock.
/// </remarks>
public sealed class LDMDurableMaterialization : IScenario
{
    public string Id => "LDM";
    public string Description => "durable materialization: MaterializeOccurrence per --storage";

    private static readonly string TaskType = typeof(CountingTask).FullName!;
    private static readonly string HandlerType = typeof(CountingHandler).FullName!;

    /// <summary>Anchored, not "now": the grid must be identical on every run and on both sides of a comparison.</summary>
    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Step = TimeSpan.FromMinutes(5);

    private StorageHandle? _handle;
    private Guid[] _schedules = [];

    /// <summary>
    /// Where each lane's cursor is. It survives ACROSS iterations on purpose: the unique index on (schedule,
    /// slot) is real, so a lane that restarted from the anchor every iteration would stop measuring a
    /// materialization and start measuring a rejected duplicate.
    /// </summary>
    private DateTimeOffset[] _cursors = [];

    public async Task SetupAsync(RunConfig cfg, CancellationToken ct)
    {
        _handle = await StorageMatrix.CreateAsync(cfg, ct);

        if (!_handle.Storage.SupportsDurableOccurrences)
            throw new NotSupportedException($"{_handle.Description} does not support durable occurrences.");

        Console.WriteLine($"  storage: {_handle.Description}  (1 atomic insert+advance per occurrence)");

        var lanes = Math.Max(1, cfg.Parallelism);
        _schedules = new Guid[lanes];
        _cursors   = new DateTimeOffset[lanes];

        for (var l = 0; l < lanes; l++)
        {
            _schedules[l] = Guid.NewGuid();
            _cursors[l]   = Anchor;
            await _handle.Storage.Persist(BuildSchedule(_schedules[l]), ct).ConfigureAwait(false);
        }
    }

    public Task TeardownAsync() => _handle is null ? Task.CompletedTask : _handle.DisposeAsync().AsTask();

    public async Task<IterationOutcome> RunIterationAsync(RunConfig cfg, LatencyRecorder latency,
                                                          CancellationToken ct)
    {
        var storage = _handle!.Storage;
        var lanes   = _schedules.Length;
        var perLane = cfg.Count / lanes;
        var total   = perLane * lanes;

        var sw    = Stopwatch.StartNew();
        var tasks = new Task[lanes];
        for (var l = 0; l < lanes; l++)
        {
            var lane       = l;
            var scheduleId = _schedules[lane];

            tasks[lane] = Task.Run(async () =>
            {
                for (long i = 0; i < perLane; i++)
                {
                    var slot      = _cursors[lane];
                    var newCursor = slot + Step;

                    var t0 = Stopwatch.GetTimestamp();

                    var outcome = await storage
                                        .MaterializeOccurrence(scheduleId, 0, slot, BuildOccurrence(scheduleId, slot),
                                            newCursor, AuditLevel.None, ct)
                                        .ConfigureAwait(false);

                    latency.Record(Stopwatch.GetTimestamp() - t0);

                    if (outcome != OccurrenceMaterializationOutcome.Created)
                        throw new InvalidOperationException($"materialization answered {outcome}, not Created");

                    _cursors[lane] = newCursor;
                }
            }, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        return new IterationOutcome(total, sw.Elapsed.TotalSeconds);
    }

    private static QueuedTask BuildSchedule(Guid id) => new()
    {
        Id              = id,
        Type            = TaskType,
        Request         = "{}",
        Handler         = HandlerType,
        CreatedAtUtc    = DateTimeOffset.UtcNow,
        Status          = QueuedTaskStatus.Queued,
        IsRecurring     = true,
        NextRunUtc      = Anchor,
        CurrentRunCount = 0,
        QueueName       = "recurring"
    };

    private static QueuedTask BuildOccurrence(Guid scheduleId, DateTimeOffset slot) => new()
    {
        Id                    = Guid.NewGuid(),
        Type                  = TaskType,
        Request               = "{}",
        Handler               = HandlerType,
        CreatedAtUtc          = DateTimeOffset.UtcNow,
        Status                = QueuedTaskStatus.WaitingQueue,
        ParentTaskId          = scheduleId,
        ScheduledExecutionUtc = slot,
        QueueName             = "recurring"
    };
}
