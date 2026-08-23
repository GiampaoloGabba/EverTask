using System.Diagnostics;
using EverTask.Abstractions;
using EverTask.LoadHarness.Infra;
using EverTask.LoadHarness.Tasks;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;

namespace EverTask.LoadHarness.Scenarios;

/// <summary>
/// LRA — the RECURRING ADVANCE, the step a schedule pays once per occurrence: work out the next slot, then
/// write it back with the run counter. Per <c>--storage</c>.
/// </summary>
/// <remarks>
/// The engine cells cannot measure this. A live recurring task is paced by its own grid — the tightest one
/// the fluent API offers is one second — so wall time would be dominated by waiting and the advance itself
/// would never show. Driving the two halves directly, the way <c>A4S</c> drives the three lifecycle writes,
/// leaves a signal that scales with the machine instead of the clock.
///
/// Both halves are what phase 1 of issue #23 touched on this path: the occurrence math now goes through the
/// schedule evaluator and takes the scheduling clock, and <c>UpdateCurrentRun</c> sits on a table that grew
/// three columns, an index and a self-referencing foreign key. Compare base against patched on this cell to
/// see whether either cost anything.
///
/// Each lane owns its own schedule row, so the lanes never contend on the same row and the number measured
/// is the cost of one advance, not of a lock.
/// </remarks>
public sealed class LRARecurringAdvance : IScenario
{
    public string Id => "LRA";
    public string Description => "recurring advance: next-occurrence + UpdateCurrentRun per --storage";

    private static readonly string TaskType = typeof(CountingTask).FullName!;
    private static readonly string HandlerType = typeof(CountingHandler).FullName!;

    /// <summary>Anchored, not "now": the grid must be identical on every run and on both sides of a comparison.</summary>
    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private StorageHandle? _handle;
    private Guid[] _schedules = [];

    public async Task SetupAsync(RunConfig cfg, CancellationToken ct)
    {
        _handle = await StorageMatrix.CreateAsync(cfg, ct);
        Console.WriteLine($"  storage: {_handle.Description}  (evaluate + 1 write per advance)");

        var lanes = Math.Max(1, cfg.Parallelism);
        _schedules = new Guid[lanes];

        for (var l = 0; l < lanes; l++)
        {
            _schedules[l] = Guid.NewGuid();
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
            var scheduleId = _schedules[l];
            tasks[l] = Task.Run(async () =>
            {
                var definition = NewDefinition();
                var cursor     = Anchor;

                for (long i = 0; i < perLane; i++)
                {
                    var t0 = Stopwatch.GetTimestamp();

                    // The occurrence math, exactly as the worker asks it after a run.
                    var next = definition.CalculateNextValidRun(cursor, (int)(i % int.MaxValue),
                        referenceTime: cursor);
                    cursor = next.NextRun ?? cursor.AddMinutes(5);

                    await storage.UpdateCurrentRun(scheduleId, 0.0, cursor, AuditLevel.None).ConfigureAwait(false);

                    latency.Record(Stopwatch.GetTimestamp() - t0);
                }
            }, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        return new IterationOutcome(total, sw.Elapsed.TotalSeconds);
    }

    private static RecurringTask NewDefinition() => new() { MinuteInterval = new MinuteInterval(5) };

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
}
