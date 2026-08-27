using System.Collections.Concurrent;
using System.Globalization;

namespace EverTask.Scheduler.Occurrences;

/// <summary>
/// The one place a durable schedule turns due slots into occurrence rows: the schedule's own slot firing, the
/// end of each occurrence, startup recovery's second wave and the operational re-park all come through here.
/// </summary>
/// <remarks>
/// Idempotent by construction: every run re-reads the schedule row and every write is a compare-and-swap on
/// the cursor and version it decided against, so two racing runs end with one winner and one re-read. The
/// per-schedule gate is an optimization on top of that, not the correctness argument. It never throws at its
/// callers — one of them is the <c>finally</c> of a delivery that has already finished.
/// </remarks>
internal sealed class OccurrenceMaterializer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IScheduler _scheduler;
    private readonly EverTaskServiceConfiguration _options;
    private readonly IEverTaskLogger<OccurrenceMaterializer> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly DueSlotEnumerator _enumerator;
    private readonly ScheduleVersionRegistry _scheduleVersions;
    private readonly TaskDeliveryRegistry? _deliveryRegistry;
    private readonly SemaphoreSlim _budget;

    private readonly ConcurrentDictionary<Guid, ScheduleGate> _gates = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _haltReported = new();
    private readonly ConcurrentDictionary<Guid, CatchUpEpisode> _catchUps = new();

    private IEverTaskWorkerExecutor? _workerExecutor;
    private OccurrenceProviderRetryRegistry? _providerRetries;
    private bool _providerRetriesResolved;
    private int _inspectionWarned;

    /// <summary>How often a schedule that is already halted repeats its event, so a stuck one stays visible
    /// without flooding the dashboard at every operational retry.</summary>
    private static readonly TimeSpan HaltReportInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many slots that already had an occurrence one run walks past before handing the rest to the
    /// operational retry.
    /// </summary>
    /// <remarks>
    /// The run holds a permit of the global materialization budget throughout, so a cursor rewound over a very
    /// long history must not turn one run into an unbounded scan. A truncated run resumes at its retry from
    /// where it stopped, never from where it started.
    /// </remarks>
    private const int MaxServedSlotsPerRun = 500;

    /// <summary>
    /// How many process starts may fail to rebuild an occurrence before the failure stops being read as
    /// transient and the row is ended.
    /// </summary>
    /// <remarks>
    /// The twin of <c>WorkerService.MaxRecoveryDispatchAttempts</c>: same default, same durable counter, one
    /// attempt spent per process start. Settable for the tests that have to reach the ceiling.
    /// </remarks>
    internal int MaxOccurrenceRebuildAttempts { get; set; } = 5;

    /// <summary>
    /// The occurrences whose rebuild failure this process has already counted, so the run at
    /// <c>BacklogRetryInterval</c> does not spend a second attempt on the same outage.
    /// </summary>
    /// <remarks>
    /// Deliberately not durable: what has to survive a restart is the counter on the row, not the memory of an
    /// outage this process is still inside. Entries leave the moment the row heals or ends.
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, byte> _rebuildFailuresCounted = new();

    public OccurrenceMaterializer(IServiceScopeFactory scopeFactory, IServiceProvider serviceProvider,
                                  IScheduler scheduler, EverTaskServiceConfiguration options,
                                  IScheduleEvaluator evaluator,
                                  IEverTaskLogger<OccurrenceMaterializer> logger, TimeProvider timeProvider,
                                  ScheduleVersionRegistry scheduleVersions,
                                  TaskDeliveryRegistry? deliveryRegistry = null)
    {
        _scopeFactory     = scopeFactory;
        _serviceProvider  = serviceProvider;
        _scheduler        = scheduler;
        _options          = options;
        _logger           = logger;
        _timeProvider     = timeProvider;
        _scheduleVersions = scheduleVersions;
        _deliveryRegistry = deliveryRegistry;
        _enumerator       = new DueSlotEnumerator(evaluator, options.MisfireThreshold);
        _budget           = new SemaphoreSlim(options.MaterializationConcurrency,
            options.MaterializationConcurrency);
    }

    /// <summary>
    /// The kick an occurrence gives its schedule when it ends: the fast path back to the materializer, so a
    /// serial catch-up does not wait for the operational retry between two slots.
    /// </summary>
    /// <remarks>
    /// Never throws: its caller is the <c>finally</c> of a delivery that is already over. Re-parking the row
    /// after a failure is <see cref="RunAsync"/>'s job, one level in, where the failure still knows which
    /// schedule it belongs to — a kick can be holding the gate for a delivery counting on that re-park.
    /// </remarks>
    public async ValueTask KickAsync(Guid parentId, CancellationToken ct = default)
    {
        try
        {
            await RunAsync(parentId, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping: nothing is written by a run that could not finish, and the operational
            // retry and startup recovery both bring the schedule back. Reporting it as a failure would make
            // every occurrence that ends during a shutdown say the schedule broke.
            _logger.MaterializationSkipped(parentId, "the host is stopping");
        }
        catch (Exception e)
        {
            _logger.MaterializationFailed(e, parentId);
        }
    }

    /// <summary>
    /// Brings one durable schedule up to date: reconcile its occurrences, materialize what is due, re-park it.
    /// </summary>
    /// <param name="parentId">The schedule row.</param>
    /// <param name="parentExecutor">
    /// The executor the schedule's own slot fired with, when there is one. Absent on every other entry point,
    /// and then rebuilt from the row.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunAsync(Guid parentId, TaskHandlerExecutor? parentExecutor, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(parentId, static _ => new ScheduleGate());

        while (true)
        {
            // Published BEFORE the flag the holder reads: a caller that finds the gate taken returns without
            // parking the row, so the holder's failure path needs an executor it does not have to rebuild
            // from storage — which is what usually just failed. ToLazy first: an eager executor carries the
            // delivery's own scope, and that delivery disposes it.
            if (parentExecutor is { } delivered)
                Volatile.Write(ref gate.Delivered, delivered.ToLazy());

            // Announce the work BEFORE trying the gate: whoever holds it re-reads this flag before releasing,
            // so a run that arrives mid-flight is absorbed into the one in progress rather than lost.
            Volatile.Write(ref gate.Pending, 1);

            if (!gate.Lock.Wait(0, ct))
                return;

            try
            {
                while (Interlocked.Exchange(ref gate.Pending, 0) == 1)
                {
                    // Consumed, not merely read: an executor is only as current as the run that handed it
                    // over, and holding on to it past that run would re-park a definition nobody delivered.
                    var executor = parentExecutor ?? Interlocked.Exchange(ref gate.Delivered, null);

                    await _budget.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await RunCoreAsync(parentId, executor, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OccurrenceProviderException failure)
                    {
                        // The schedule's calendar could not answer, which is transient by contract. Nothing
                        // was written, so the row keeps its cursor and comes back after the provider's own
                        // backoff instead of the ordinary operational retry.
                        await DeferForProviderAsync(parentId, executor, failure, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Whoever holds the gate has also taken over the re-park of every run it absorbed, so
                        // the operational retry is armed here whichever entry point is holding it. Ending
                        // this run quietly leaves such a schedule parked nowhere until a restart.
                        _logger.MaterializationFailed(ex, parentId);
                        await ReParkAfterFailureAsync(parentId, executor, _options.BacklogRetryInterval, ct)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        _budget.Release();
                    }
                }
            }
            finally
            {
                gate.Lock.Release();
            }

            // A request that landed between the inner loop's last check and the release above would otherwise
            // find the gate free and nobody running: pick it up here instead of leaving the schedule stalled.
            if (Volatile.Read(ref gate.Pending) == 0)
                return;
        }
    }

    private async Task RunCoreAsync(Guid parentId, TaskHandlerExecutor? parentExecutor, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var storage = scope.ServiceProvider.GetService<ITaskStorage>();
        if (storage == null)
        {
            _logger.MaterializationSkipped(parentId, "no storage is registered");
            return;
        }

        var row = (await storage.Get(t => t.Id == parentId, ct).ConfigureAwait(false)).FirstOrDefault();

        // Gone, cancelled, or already finalized: a schedule with no cursor has nothing left to advance, and
        // one that was cancelled must never grow another occurrence.
        if (row is null || !row.IsRecurring || row.Status == QueuedTaskStatus.Cancelled || row.NextRunUtc is null)
        {
            DropGate(parentId);
            return;
        }

        var now       = _timeProvider.GetUtcNow();
        var recovered = RecoveredTaskFactory.FromRow(row,
            scope.ServiceProvider.GetService<OccurrenceProviderRegistry>());

        if (recovered.Recurring is { IsDurable: false } inline)
        {
            // Not a durable schedule any more: a task key re-registration or a reschedule wrote an inline
            // definition over the row. Never re-park the durable executor here — the scheduler is keyed by id
            // and last write wins, so it would replace the current owner with a registration that
            // materializes nothing. But a re-park that FAILED is exactly what brings an old durable delivery
            // here, so assuming the row is already parked leaves the series parked nowhere.
            await ParkInlineRowAsync(scope.ServiceProvider, recovered, row, inline).ConfigureAwait(false);
            return;
        }

        if (recovered.Recurring is not { } definition || recovered.Task is null)
        {
            ParkUnusableRow(parentId, parentExecutor, recovered, now);
            return;
        }

        var auditLevel = recovered.AuditLevel;

        // The snapshot this whole run decides against, taken once: every compare-and-swap below carries these
        // values and never a fresh read of the row, or an expectation read after the decision absorbs
        // whatever a concurrent writer did in between and the write that should lose wins. It matters most on
        // a storage handing back LIVE entities (the in-memory one does).
        var snapshot = new ScheduleSnapshot(row.Id, row.ScheduleVersion, row.NextRunUtc.Value, row.Status,
            row.CurrentRunCount ?? 0, row.QueueName);

        var cursor = snapshot.CursorUtc;

        // ToLazy on the delivered executor: an eager one carries the EverTask-owned scope of the delivery that
        // just ended, which its own finally disposes — re-parking it would park a handler already gone.
        var executor = parentExecutor?.ToLazy()
                       ?? await BuildScheduleExecutorAsync(scope.ServiceProvider, recovered, row, definition, cursor)
                           .ConfigureAwait(false);

        if (ScheduleRuntimeInfo.TryParse(row.RuntimeInfo)?.Halted is { } standingHalt)
        {
            // Reported, and NOT re-parked. A halt never releases itself: only an explicit resume or
            // reschedule clears the marker, and both park the row themselves. Re-parking it anyway sends the
            // row through the worker queue every minute for ever, each pass writing a status audit row.
            ReportHalt(executor, parentId, standingHalt, now);
            return;
        }

        var active = await ReconcileOccurrencesAsync(scope.ServiceProvider, storage, executor, parentId, auditLevel,
                             ct)
                         .ConfigureAwait(false);

        // ONE plan per run: the slots it grants may be written further along the grid than it named them, but
        // nothing about the decision changes while the pass walks past already-served slots. Re-deciding per
        // walked slot costs a full count and a full bisection over the remaining backlog apiece.
        var identity = new ScheduleIdentity(parentId, row.TaskKey, snapshot.CurrentRunCount + 1);

        var plan = await _enumerator
                         .PlanAsync(definition, snapshot.CursorUtc, now, snapshot.CurrentRunCount, active, identity,
                             ct)
                         .ConfigureAwait(false);

        foreach (var loss in plan.Losses)
            ReportLoss(executor, parentId, loss);

        if (plan.StopReason == DueSlotStopReason.Halted)
        {
            await HaltAsync(storage, executor, snapshot, plan, now, ct).ConfigureAwait(false);
            return;
        }

        // The episode boundary, decided from the plan and reported before the rows exist: a consumer that only
        // sees the per-occurrence events cannot tell where one replay begins and ends.
        TrackCatchUpEpisode(executor, parentId, plan, now);

        var pass = await MaterializeAsync(scope.ServiceProvider, storage, snapshot, definition, recovered, executor,
            plan, auditLevel, now, identity, ct).ConfigureAwait(false);

        CountCatchUpOccurrences(parentId, pass.Created);

        if (pass.Finalized)
        {
            // The series ended in the middle of its own replay: the episode is over whatever the backlog
            // still held, and saying so here is the only chance to close it — the row is done being read.
            ReportCatchUpCompleted(executor, parentId, now);

            _logger.DurableSeriesCompleted(parentId);

            // A durable series ends HERE and nowhere else — the cursor is nulled in the same commit that
            // writes the terminal status, so it never passes through QueueNextOccourrence, where an inline
            // series drops its published lower bound. Otherwise the registry entry of every finished durable
            // schedule outlives the series for the life of the process; the provider backoff goes with it.
            _scheduleVersions.Remove(parentId);
            ProviderRetries?.Forget(parentId);

            DropGate(parentId);
            return;
        }

        // A stretch of already-served slots longer than one run may walk: what is left of it goes to the
        // operational retry, which resumes from the cursor this run carried forward and never from where this
        // run started.
        var truncated = pass.ServedSlots >= MaxServedSlotsPerRun;

        if (truncated)
            _logger.ServedSlotWalkTruncated(parentId, pass.ServedSlots);

        // A lost compare-and-swap wrote nothing, so re-park at the retry interval rather than at the cursor:
        // that cursor is exactly the value that turned out to be wrong. Never simply dropped, or a race
        // nobody else resolves stalls the schedule until a restart.
        RePark(executor, parentId,
            !pass.WonEveryWrite || truncated || plan.StopReason == DueSlotStopReason.WindowFull
                ? now + _options.BacklogRetryInterval
                : pass.CursorUtc);
    }

    /// <summary>
    /// Reports a schedule row nothing in this build can rebuild, and hands it back to the scheduler when this
    /// run is what took it out of it.
    /// </summary>
    /// <remarks>
    /// The verdict on such a row belongs to startup recovery, which owns the bounded retry and the terminal
    /// poison; a second copy here would spend the same budget twice. Only the schedule's own delivery
    /// consumed a registration, so only that entry point has to hand the row back — returning quietly leaves
    /// the series parked nowhere until a restart. The executor is never rebuilt from the row: that is exactly
    /// what just failed, and one built without the durable definition would run the handler.
    /// </remarks>
    private void ParkUnusableRow(Guid parentId, TaskHandlerExecutor? parentExecutor, RecoveredTask recovered,
                                 DateTimeOffset now)
    {
        var reason = recovered.PayloadError
                     ?? recovered.ScheduleError
                     ?? new InvalidOperationException(recovered.Recurring is null
                         ? "The schedule row carries no usable recurring definition"
                         : recovered.TypeWasLoadable
                             ? "The schedule's persisted payload did not produce a runnable task"
                             : "The schedule's task type could not be loaded");

        _logger.ScheduleRowUnusable(reason, parentId);

        if (parentExecutor is not { } delivered)
            return;

        // ToLazy, as everywhere else a delivery's executor is re-parked: an eager one carries the
        // EverTask-owned scope of the delivery that is ending, and that delivery disposes it.
        RePark(delivered.ToLazy(), parentId, now + _options.BacklogRetryInterval);

        if (WorkerExecutor is not { HasEventSubscribers: true } worker)
            return;

        worker.PublishExternalEvent(delivered, SeverityLevel.Error,
            string.Create(CultureInfo.InvariantCulture,
                $"Schedule {parentId} cannot be rebuilt from its row and materializes nothing: {reason.Message}"));
    }

    /// <summary>
    /// Makes sure a row that has turned INLINE under an old durable delivery is parked, rebuilding its
    /// executor from the row when nothing holds it.
    /// </summary>
    /// <remarks>
    /// Whoever wrote the inline definition owns the parking and normally did it; this exists for the one case
    /// that lets an old durable delivery reach a rewritten row, a re-park that FAILED and handed the row to
    /// nobody. The executor is rebuilt FROM THE ROW: the delivered one carries the durable definition, and
    /// parking it would put a registration that materializes nothing over a series that now runs a handler.
    /// Without evidence (a scheduler that cannot report its registrations) the row is parked anyway — a
    /// duplicate registration is replaced latest-wins, a missing one stops the series until a restart.
    /// </remarks>
    private async Task ParkInlineRowAsync(IServiceProvider provider, RecoveredTask recovered, QueuedTask row,
                                          RecurringTask inline)
    {
        if (_scheduler.SupportsScheduleInspection && _scheduler.IsScheduled(row.Id))
        {
            _logger.MaterializationSkipped(row.Id, "the row is no longer a durable schedule");
            return;
        }

        if (recovered.Task is null)
        {
            _logger.ScheduleRowUnusable(
                recovered.PayloadError ?? new InvalidOperationException(
                    "The schedule turned inline but its persisted payload did not produce a runnable task"),
                row.Id);

            return;
        }

        TaskHandlerExecutor? built = null;

        try
        {
            var cursor = row.NextRunUtc!.Value;

            built = await BuildScheduleExecutorAsync(provider, recovered, row, inline, cursor)
                        .ConfigureAwait(false);

            RePark(built, row.Id, cursor);
        }
        catch (Exception ex)
        {
            // Nothing else parks this row from here, so a failure has to be visible: the series waits for
            // startup recovery, and a subscriber is told rather than left to read the log.
            _logger.ReparkAfterFailureFailed(ex, row.Id);

            PublishReParkFailedEvent(built, row.Id, ex);
        }
    }

    /// <summary>
    /// Writes the slots the plan chose, one compare-and-swapped commit each, and hands every occurrence that
    /// was really created to the scheduler.
    /// </summary>
    /// <remarks>
    /// A slot that comes back <see cref="OccurrenceMaterializationOutcome.AlreadyExists"/> is walked past
    /// here, on the grid, and the occurrence the plan granted is written at the slot behind it. Nothing about
    /// the decision changes while a run walks, and handing the walk back for a fresh plan per slot is
    /// quadratic in the length of the stretch.
    /// </remarks>
    /// <returns>
    /// Where the cursor ended up, what the pass produced, whether the series finished, and whether every write
    /// it attempted was won — a loss means someone else owns the row now and this run stops touching it.
    /// </returns>
    private async Task<MaterializationPass> MaterializeAsync(
        IServiceProvider provider, ITaskStorage storage, ScheduleSnapshot snapshot, RecurringTask definition,
        RecoveredTask recovered, TaskHandlerExecutor executorForEvents, DueSlotPlan plan, AuditLevel auditLevel,
        DateTimeOffset now, ScheduleIdentity identity, CancellationToken ct)
    {
        var parentId  = snapshot.Id;
        var version   = snapshot.ScheduleVersion;
        var cursor    = snapshot.CursorUtc;
        var runNumber = snapshot.CurrentRunCount + 1;
        var created   = 0;
        var served    = 0;

        // How many occurrences this plan authorises. A slot that turns out to be already served creates
        // nothing, so it spends none of them: the authorisation moves on to the slot behind it.
        var grants = plan.Slots.Count;

        for (var i = 0; created < grants && served < MaxServedSlotsPerRun; i++)
        {
            // Past the plan's own list the run is walking slots it never named, every one of them already
            // served, so the cursor IS the next due slot and the grid answers what follows it.
            var planned = i < grants;

            if (!planned && cursor > now)
                break;

            var slot = planned ? plan.Slots[i] : cursor;

            // The cursor this write leaves behind. When it spends the last run the budget allows, the series
            // ends in the same commit that creates the occurrence, wherever the walk put it: MaxRuns counts
            // materializations and not slots, so an already-served slot moved the write along the grid
            // without spending anything.
            DateTimeOffset? newCursor;

            if (planned && i + 1 < grants)
                newCursor = plan.Slots[i + 1];
            else if (created + 1 == grants && plan.RunBudgetEndsSeries)
                newCursor = null;
            else if (planned && !plan.RunBudgetEndsSeries)
                newCursor = plan.NextCursorUtc;
            else
                newCursor = await _enumerator.NextSlotAfterAsync(definition, slot, identity, ct)
                                             .ConfigureAwait(false);

            var occurrence = await BuildOccurrenceAsync(provider, recovered.Task!, snapshot, slot, runNumber,
                MisfireAfterWalking(plan, slot, now, served), auditLevel, definition.TimeZoneId)
                .ConfigureAwait(false);

            var childRow = occurrence.ToQueuedTask(now);

            var outcome = await storage
                                .MaterializeOccurrence(parentId, version, cursor, childRow, newCursor, auditLevel, ct)
                                .ConfigureAwait(false);

            // The slot already has a row while the cursor still points at it (a rewound cursor replaying an
            // episode, or a row written from outside). Re-reading answers the same thing for ever — the
            // cursor is what is stale and nothing else moves it — so carry it past the slot and walk on.
            if (outcome == OccurrenceMaterializationOutcome.AlreadyExists)
            {
                served++;
                ReportSlotAlreadyServed(executorForEvents, parentId, slot);

                // A null cursor here would end the series at this slot, and it does not: an already-served
                // slot spends no run, so the run the plan set aside is still owed at the slot behind it. Only
                // a grid with nothing left after this slot ends it here.
                var next = newCursor
                           ?? await _enumerator.NextSlotAfterAsync(definition, slot, identity, ct)
                                               .ConfigureAwait(false);

                if (next is not { } pastTakenSlot)
                {
                    var closed = await RecurringSeriesFinalizer
                                       .FinalizeAsync(storage, parentId, cursor, snapshot.Status, version, 0,
                                           auditLevel, RecurringSeriesFinalizationPolicy.Conditional, ct)
                                       .ConfigureAwait(false);

                    return new MaterializationPass(cursor, created, served, closed, closed);
                }

                if (!await storage.TryAdvanceScheduleCursor(parentId, version, cursor, pastTakenSlot, ct)
                                  .ConfigureAwait(false))
                {
                    _logger.CursorAdvanceLost(parentId);
                    return new MaterializationPass(cursor, created, served, false, false);
                }

                // No run counted and no run number spent: the occurrence that owns this slot was counted when
                // it was created, and the schedule's own counter did not move here.
                cursor = pastTakenSlot;
                continue;
            }

            if (outcome != OccurrenceMaterializationOutcome.Created)
            {
                _logger.MaterializationLostRace(parentId, outcome);
                return new MaterializationPass(cursor, created, served, false, false);
            }

            created++;
            _logger.OccurrenceMaterialized(occurrence.PersistenceId, parentId, slot, runNumber);
            PublishOccurrenceEvent(occurrence, parentId, slot, runNumber);

            // Handed to the SCHEDULER, never enqueued directly: this code runs on a queue consumer, and a
            // blocking enqueue into a full queue could wait on the very consumer that would drain it.
            _scheduler.Schedule(occurrence, slot);

            runNumber++;

            if (newCursor is not { } advanced)
                return new MaterializationPass(cursor, created, served, true, true);

            cursor = advanced;
        }

        if (grants > 0)
            return new MaterializationPass(cursor, created, served, false, true);

        // Nothing survived the policy, so no materialization carried the cursor: move it on its own.
        if (plan.NextCursorUtc is not { } skipTo)
        {
            // Losing this compare-and-swap means the row is not the one the decision was computed from, so it
            // is a lost race like any other — never a finished series this run may stop watching.
            var ended = await RecurringSeriesFinalizer
                              .FinalizeAsync(storage, parentId, cursor, snapshot.Status, version, 0,
                                  auditLevel, RecurringSeriesFinalizationPolicy.Conditional, ct)
                              .ConfigureAwait(false);

            return new MaterializationPass(cursor, created, served, ended, ended);
        }

        if (skipTo == cursor)
            return new MaterializationPass(cursor, created, served, false, true);

        if (await storage.TryAdvanceScheduleCursor(parentId, version, cursor, skipTo, ct).ConfigureAwait(false))
            return new MaterializationPass(skipTo, created, served, false, true);

        _logger.CursorAdvanceLost(parentId);
        return new MaterializationPass(cursor, created, served, false, false);
    }

    /// <summary>
    /// The misfire an occurrence carries when the run reached its slot by walking past
    /// <paramref name="served"/> slots that already had a row.
    /// </summary>
    /// <remarks>
    /// The range and the count are two halves of one statement, so both move: the range starts at the slot
    /// being created and the count drops by the number walked, those slots having been consecutive from the
    /// old start. The newest end never moves. Whether what is left is still missed work is the enumerator's
    /// rule, asked again here rather than duplicated.
    /// </remarks>
    private OccurrenceMisfire? MisfireAfterWalking(DueSlotPlan plan, DateTimeOffset slot, DateTimeOffset now,
                                                   int served)
    {
        if (served == 0 || plan.Misfire is not { } misfire)
            return plan.Misfire;

        var remaining = misfire.MissedCount - served;

        return remaining > 0 && _enumerator.IsMissed(slot, now, remaining)
                   ? misfire with { MissedFromUtc = slot, MissedCount = remaining }
                   : null;
    }

    /// <summary>
    /// Persists the durable circuit breaker of an overflowing catch-up: nothing is materialized, and the
    /// schedule stops being parked until an operator resumes it.
    /// </summary>
    private async Task HaltAsync(ITaskStorage storage, TaskHandlerExecutor executor, ScheduleSnapshot snapshot,
                                 DueSlotPlan plan, DateTimeOffset now, CancellationToken ct)
    {
        var halt = new ScheduleHaltInfo
        {
            AtUtc           = now,
            Reason          = "the backlog exceeds the configured catch-up cap",
            DetectedAtLeast = plan.DetectedAtLeast,
            IsExact         = plan.IsExact,
            CursorUtc       = snapshot.CursorUtc,
            ScheduleVersion = snapshot.ScheduleVersion
        };

        var written = await storage
                            .TryHaltSchedule(snapshot.Id, snapshot.ScheduleVersion, snapshot.CursorUtc,
                                snapshot.Status, new ScheduleRuntimeInfo { Halted = halt }.Serialize(), ct)
                            .ConfigureAwait(false);

        if (written)
        {
            // First report of THIS halt: force it past the rate limit, so the event that matters is never the
            // one that gets suppressed. No re-park — a halted schedule waits for a person, not for a timer.
            _haltReported.TryRemove(snapshot.Id, out _);
            ReportHalt(executor, snapshot.Id, halt, now);
            return;
        }

        // The marker did not land, so the row is not the one this decision was computed from: a lost race
        // like any other, and the schedule must come back to look again.
        _logger.HaltNotPersisted(snapshot.Id);
        RePark(executor, snapshot.Id, now + _options.BacklogRetryInterval);
    }

    /// <summary>
    /// Arms the operational retry for a schedule whose materialization run threw, so the row is parked
    /// somewhere whatever the failure was.
    /// </summary>
    /// <remarks>
    /// The delivered executor is preferred over rebuilding one from the row: what usually just failed is the
    /// storage. <c>ToLazy</c> drops the EverTask-owned scope of the delivery that is ending. Never throws —
    /// this is already the failure path, and its caller is holding the per-schedule gate.
    /// </remarks>
    private async Task<ProviderRetryParkOutcome> ReParkAfterFailureAsync(
        Guid parentId, TaskHandlerExecutor? parentExecutor, TimeSpan delay, CancellationToken ct)
    {
        var retryAt = _timeProvider.GetUtcNow() + delay;

        return await ProviderRetryParker
                     .ParkAsync(_scheduler, retryAt, BuildExecutorAsync,
                         executor => executor with { ExecutionTime = retryAt },
                         (executor, at) => _logger.ScheduleReparkRefused(parentId, at, executor.ScheduleVersion),
                         (_, at) => _logger.ScheduleReparked(parentId, at),
                         (executor, error) =>
                         {
                             _logger.ReparkAfterFailureFailed(error, parentId);
                             PublishReParkFailedEvent(executor ?? parentExecutor, parentId, error);
                         },
                         error => error is OperationCanceledException && ct.IsCancellationRequested)
                     .ConfigureAwait(false);

        async ValueTask<TaskHandlerExecutor?> BuildExecutorAsync()
        {
            if (parentExecutor is { } delivered)
                return delivered.ToLazy();

            using var scope = _scopeFactory.CreateScope();

            if (scope.ServiceProvider.GetService<ITaskStorage>() is not { } storage)
                return null;

            var row = (await storage.Get(t => t.Id == parentId, ct).ConfigureAwait(false)).FirstOrDefault();

            if (row is null || !row.IsRecurring || row.Status == QueuedTaskStatus.Cancelled ||
                row.NextRunUtc is null)
                return null;

            var recovered = RecoveredTaskFactory.FromRow(row);

            if (recovered.Recurring is not { IsDurable: true } definition || recovered.Task is null)
                return null;

            return await BuildScheduleExecutorAsync(scope.ServiceProvider, recovered, row, definition,
                             row.NextRunUtc.Value)
                         .ConfigureAwait(false);
        }
    }

    /// <summary>Tells a monitoring subscriber that a schedule is parked nowhere until the next restart.</summary>
    private void PublishReParkFailedEvent(TaskHandlerExecutor? executor, Guid parentId, Exception failure)
    {
        if (executor is not { } target || WorkerExecutor is not { HasEventSubscribers: true } worker)
            return;

        worker.PublishExternalEvent(target, SeverityLevel.Error,
            string.Create(CultureInfo.InvariantCulture,
                $"Schedule {parentId} could not be re-parked after a failed materialization: it is parked " +
                $"nowhere and only the next startup recovery brings it back"), failure);
    }

    /// <summary>
    /// Hands a schedule whose occurrence provider could not answer back to the scheduler, after the backoff
    /// that failure has earned.
    /// </summary>
    /// <remarks>
    /// The ordinary operational retry would do the same thing on the wrong clock: the provider's backoff grows
    /// with its consecutive failures, so a source down for an hour is asked a handful of times instead of
    /// sixty. Nothing is written either way, so the row keeps its cursor.
    /// </remarks>
    private async Task DeferForProviderAsync(Guid parentId, TaskHandlerExecutor? parentExecutor,
                                             OccurrenceProviderException failure, CancellationToken ct)
    {
        var retryAt = _timeProvider.GetUtcNow() + failure.RetryAfter;

        var outcome = await ReParkAfterFailureAsync(parentId, parentExecutor, failure.RetryAfter, ct)
                          .ConfigureAwait(false);

        // Said only once the registration is really in, log line and event alike: before that point "parked
        // to ask again" is a promise the very next line can break. A failed re-park has already reported
        // itself as an error; a refused one parked nothing and belongs to whoever owns the row now.
        if (outcome.Failure != null || outcome.Executor is not { } executor)
            return;

        _logger.MaterializationDeferredByProvider(failure, parentId, failure.ProviderKey,
            failure.ConsecutiveFailures, retryAt);

        if (WorkerExecutor is not { HasEventSubscribers: true } worker)
            return;

        worker.PublishExternalEvent(executor, SeverityLevel.Warning,
            string.Create(CultureInfo.InvariantCulture,
                $"Occurrence provider '{failure.ProviderKey}' could not answer for schedule {parentId} " +
                $"({failure.ConsecutiveFailures} consecutive failure(s)): nothing was materialized and the " +
                $"schedule is parked to ask again at {retryAt:O}"), failure);
    }

    /// <summary>
    /// Counts the occurrences of a schedule that are still alive and rescues the ones that are alive only on
    /// paper.
    /// </summary>
    /// <remarks>
    /// An occurrence is really alive while a delivery holds it or the scheduler has it parked; one that is
    /// neither would sit non-terminal for ever and permanently consume a slot of the concurrency budget. It is
    /// requeued under a compare-and-swap on the status it was found in, and it counts as active either way:
    /// capacity is freed by reaching a terminal state, never by being noticed. Without a scheduler that can
    /// answer "is this parked" nothing is reconciled and every non-terminal occurrence counts — that delays
    /// the schedule instead of running an occurrence twice. A row that cannot produce a runnable task is the
    /// exception and is terminalized instead, see <see cref="FailUnusableOccurrenceAsync"/>.
    /// </remarks>
    private async Task<int> ReconcileOccurrencesAsync(IServiceProvider provider, ITaskStorage storage,
                                                      TaskHandlerExecutor executorForEvents, Guid parentId,
                                                      AuditLevel auditLevel, CancellationToken ct)
    {
        var children = await storage.GetOccurrences(parentId, nonTerminalOnly: true, ct).ConfigureAwait(false);

        if (children.Length == 0)
            return 0;

        if (!_scheduler.SupportsScheduleInspection)
        {
            if (Interlocked.Exchange(ref _inspectionWarned, 1) == 0)
                _logger.ScheduleInspectionUnsupported();

            return children.Length;
        }

        var active = children.Length;

        foreach (var child in children)
        {
            if (_deliveryRegistry?.IsDelivering(child.Id) == true || _scheduler.IsScheduled(child.Id))
                continue;

            var expected = child.Status;

            // Rebuilt BEFORE the requeue: a row that cannot produce an executor is not rescued by a
            // compare-and-swap back to Queued, and the other order writes that transition and its status
            // audit row on every run for ever, since the row is then left exactly as it was found.
            var rebuild = await BuildOccurrenceFromRowAsync(provider, child).ConfigureAwait(false);

            if (rebuild.Executor is null)
            {
                int? exhaustedAfter = null;

                // Registered but not activatable right now — a scoped dependency that timed out, a
                // connection that was not there — is NOT a verdict on the row: ending the occurrence on it
                // drops work no handler ever saw. It keeps its slot of the budget and its non-terminal
                // status, and the operational retry looks again.
                if (!rebuild.Permanent)
                {
                    // But not for ever: a constructor that throws every time is a misconfiguration, and
                    // "look again next run" then holds the series for the life of the process. The bound is
                    // the row's own recovery-failure counter, answering the same question as the recovery's
                    // poison; a storage that does not persist it answers 0 and stays unbounded as before.
                    //
                    // ONE attempt per PROCESS START, the cadence the counter is borrowed from: a stale
                    // occurrence is re-planned every BacklogRetryInterval, so counting per run burns the
                    // ceiling in five minutes and makes a database failover indistinguishable from the
                    // misconfiguration this bound exists to catch.
                    var counted  = _rebuildFailuresCounted.TryAdd(child.Id, 0);
                    var attempts = counted
                                       ? await storage.IncrementRecoveryFailure(child.Id, ct).ConfigureAwait(false)
                                       : child.RecoveryDispatchFailureCount ?? 0;

                    if (attempts < MaxOccurrenceRebuildAttempts)
                    {
                        _logger.OccurrenceRebuildDeferred(rebuild.Failure!, child.Id, parentId, attempts,
                            MaxOccurrenceRebuildAttempts);
                        continue;
                    }

                    exhaustedAfter = attempts;
                }

                // Only a CONFIRMED terminal state frees capacity: SetStatus is best effort on every
                // relational provider, so counting the slot free on the strength of having called it lets
                // the run create a successor while the old row is still alive.
                if (await FailUnusableOccurrenceAsync(storage, executorForEvents, child, parentId, rebuild.Failure!,
                            exhaustedAfter, auditLevel, ct)
                        .ConfigureAwait(false))
                {
                    active--;
                }

                // RequeueTerminal is the way back and it clears the durable counter, so the in-process mark
                // goes with it or a requeued occurrence meets the ceiling again on its first failure.
                _rebuildFailuresCounted.TryRemove(child.Id, out _);

                continue;
            }

            var executor = rebuild.Executor;

            // A rebuild that healed leaves no failures behind for a later transient one to inherit and tip
            // over the ceiling with. Conditional on the WRITE, so reconciling a healthy row still costs no
            // round trip; the in-process mark is dropped either way, since it makes the next outage a new one.
            _rebuildFailuresCounted.TryRemove(child.Id, out _);

            if ((child.RecoveryDispatchFailureCount ?? 0) > 0)
                await storage.ClearRecoveryFailure(child.Id, ct).ConfigureAwait(false);

            // Lost: a cancel, or a delivery that picked it up between the read and this write. Either way the
            // occurrence has an owner again and this run must not hand it to the scheduler a second time.
            if (!await storage.TryRequeueStaleOccurrence(child.Id, expected, auditLevel, ct).ConfigureAwait(false))
                continue;

            _logger.StaleOccurrenceDetected(child.Id, parentId, expected);
            PublishStaleEvent(executor, parentId, expected);

            _scheduler.Schedule(executor, child.ScheduledExecutionUtc ?? _timeProvider.GetUtcNow());
        }

        return active;
    }

    /// <summary>
    /// Ends an occurrence whose row cannot be turned back into a task, so the series is not held behind
    /// something nothing can deliver.
    /// </summary>
    /// <param name="exhaustedAfter">
    /// The number of consecutive process starts that failed to rebuild the row, when that is what ended it,
    /// and null when the verdict was final on the first look. Same write, two different sentences: one names
    /// a build nothing can fix, the other a handler that is there and never builds.
    /// </param>
    /// <remarks>
    /// The verdict cannot change while the process lives, and left non-terminal the row goes on consuming a
    /// slot of the schedule's concurrency budget. <c>Failed</c> lets the series move on with the reason kept
    /// on the row; <see cref="ITaskStorage.RequeueTerminal"/> is the way back once a deploy makes it readable
    /// again. The write is unconditional: nothing holds this row, so the only status a concurrent writer
    /// could put under it is another terminal one.
    /// </remarks>
    /// <returns>
    /// Whether the row really is terminal now. <see cref="ITaskStorage.SetStatus"/> is best effort, so "the
    /// call returned" is not "the slot is free" — a swallowed write would let the schedule create a successor
    /// while the old occurrence is still alive.
    /// </returns>
    private async Task<bool> FailUnusableOccurrenceAsync(ITaskStorage storage, TaskHandlerExecutor executorForEvents,
                                                         QueuedTask child, Guid parentId, Exception reason,
                                                         int? exhaustedAfter, AuditLevel auditLevel,
                                                         CancellationToken ct)
    {
        await storage.SetStatus(child.Id, QueuedTaskStatus.Failed, reason, auditLevel, null, ct)
                     .ConfigureAwait(false);

        // Read back BEFORE saying anything: "was marked Failed" is exactly what a swallowed write did not do,
        // and reported that way an operator reads a slot as freed while the schedule is still held.
        var written = (await storage.Get(t => t.Id == child.Id, ct).ConfigureAwait(false)).FirstOrDefault();
        var ended   = written is null || !QueuedTask.IsNonTerminalStatus(written.Status);

        if (!ended)
            _logger.OccurrenceTerminalizationLost(reason, child.Id, parentId, written!.Status);
        else if (exhaustedAfter is { } attempts)
            _logger.OccurrenceRebuildExhausted(reason, child.Id, parentId, attempts);
        else
            _logger.OccurrenceRowUnusable(reason, child.Id, parentId);

        if (WorkerExecutor is not { HasEventSubscribers: true } worker)
            return ended;

        // Why the row ended, in the event too: "cannot be rebuilt from its row" sends an operator to look for
        // a type or a payload, which is not what a handler that never builds needs.
        var cause = exhaustedAfter is { } burned
            ? string.Create(CultureInfo.InvariantCulture,
                $"could not be rebuilt in {burned} consecutive process start(s)")
            : "cannot be rebuilt from its row";

        worker.PublishExternalEvent(executorForEvents, SeverityLevel.Error,
            ended
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Occurrence {child.Id} of schedule {parentId} {cause} and was marked Failed: {reason.Message}")
                : string.Create(CultureInfo.InvariantCulture,
                    $"Occurrence {child.Id} of schedule {parentId} {cause} and could not be marked Failed (it is still {written!.Status}): {reason.Message}"));

        return ended;
    }

    /// <summary>Builds the executor of a brand-new occurrence, before its row exists.</summary>
    /// <param name="timeZoneId">
    /// The zone of the schedule, copied onto the occurrence: a child carries no definition, so this is the
    /// only way the zone and the local slot reach its handler.
    /// </param>
    private static async Task<TaskHandlerExecutor> BuildOccurrenceAsync(IServiceProvider provider, IEverTask payload,
                                                                        ScheduleSnapshot parent, DateTimeOffset slot,
                                                                        int runNumber, OccurrenceMisfire? misfire,
                                                                        AuditLevel auditLevel, string? timeZoneId)
    {
        var runtimeInfo = EverTaskJson.Serialize(new OccurrenceRuntimeInfo
        {
            SlotUtc            = slot,
            RunNumber          = runNumber,
            TimeZoneId         = timeZoneId,
            MisfireKind        = misfire?.Kind,
            MissedFromUtc      = misfire?.MissedFromUtc,
            MissedThroughUtc   = misfire?.MissedThroughUtc,
            MissedCount        = misfire?.MissedCount,
            MissedCountIsExact = misfire?.MissedCountIsExact
        });

        // The queue is COPIED from the schedule row, not re-derived: an occurrence is dispatched with no
        // recurring definition, and the fallback for one of those is the default queue — so a durable series
        // routed to its own queue would quietly leave it, one occurrence at a time.
        var metadata = new DispatchRowMetadata(parent.Id, runtimeInfo, parent.ScheduleVersion, parent.QueueName,
            null, runNumber, slot);

        // Always lazy: an occurrence's slot is usually already past, which is exactly the shape the adaptive
        // rule resolves eagerly — and an eager handler resolved here would be pinned until the delivery runs.
        return await EverTask.Dispatcher.Dispatcher.CreateCachedWrapper(payload.GetType())
                               .Handle(payload, slot, recurring: null, provider, auditLevel, existingTaskId: null,
                                   taskKey: null, useLazyExecutor: true, metadata)
                               .ConfigureAwait(false);
    }

    /// <summary>Rebuilds the executor of an occurrence that already has a row (the reconciliation path).</summary>
    /// <returns>The executor, or the reason this row did not produce one and whether that reason is final.</returns>
    /// <remarks>
    /// A row is unusable for two reasons: the payload may not rebuild, and the rebuilt payload may have no
    /// handler left to run it. Handler resolution happens inside <c>Handle</c>, so the second one arrives as
    /// an exception; both are the same verdict, see <see cref="FailUnusableOccurrenceAsync"/>. What is NOT
    /// that verdict is a handler that exists and merely failed to be built this time, which looks identical
    /// from here — so the container is asked whether anything is registered for the task at all, and only a
    /// "no" is final. Ending an occurrence on a transient activation failure drops work no handler ever saw.
    /// </remarks>
    private static async Task<OccurrenceRebuild> BuildOccurrenceFromRowAsync(IServiceProvider provider,
                                                                            QueuedTask child)
    {
        var recovered = RecoveredTaskFactory.FromRow(child);

        if (recovered.Task is null)
        {
            // Neither answer changes while this process lives, which is what makes the row's ending a
            // decision and not a retry.
            return new OccurrenceRebuild(null, recovered.PayloadError
                                               ?? new InvalidOperationException(recovered.TypeWasLoadable
                                                   ? "The occurrence's persisted payload did not produce a runnable task"
                                                   : "The occurrence's task type could not be loaded"), true);
        }

        try
        {
            var executor = await EverTask.Dispatcher.Dispatcher.CreateCachedWrapper(recovered.Task.GetType())
                                           .Handle(recovered.Task, child.ScheduledExecutionUtc, recurring: null,
                                               provider, recovered.AuditLevel, child.Id, taskKey: null,
                                               useLazyExecutor: true, recovered.RowMetadata)
                                           .ConfigureAwait(false);

            return new OccurrenceRebuild(executor, null, false);
        }
        catch (Exception e)
        {
            return new OccurrenceRebuild(null, e, NoHandlerRegisteredFor(provider, recovered.Task.GetType()));
        }
    }

    /// <summary>
    /// Whether the container has nothing at all to run <paramref name="taskType"/> — the one rebuild failure
    /// that can never heal while the process lives.
    /// </summary>
    /// <remarks>
    /// Asked only after a rebuild has already failed, so the cost is paid on the rare path. An activation
    /// that throws again here is evidence of the opposite: something is registered, and building it failed.
    /// </remarks>
    private static bool NoHandlerRegisteredFor(IServiceProvider provider, Type taskType)
    {
        try
        {
            return provider.GetService(typeof(IEverTaskHandler<>).MakeGenericType(taskType)) is null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Rebuilds the SCHEDULE row's own executor, for the entry points that were not handed one.</summary>
    private static async Task<TaskHandlerExecutor> BuildScheduleExecutorAsync(
        IServiceProvider provider, RecoveredTask recovered, QueuedTask row, RecurringTask definition,
        DateTimeOffset cursor) =>
        await EverTask.Dispatcher.Dispatcher.CreateCachedWrapper(recovered.Task!.GetType())
                        .Handle(recovered.Task, cursor, definition, provider, recovered.AuditLevel, row.Id,
                            row.TaskKey, useLazyExecutor: true, recovered.RowMetadata)
                        .ConfigureAwait(false);

    /// <summary>
    /// Puts the schedule row back in the scheduler, at its next slot or at the operational retry.
    /// </summary>
    /// <remarks>
    /// The retry is what makes progress independent of the kick: recovery runs once at startup, so a schedule
    /// whose window is full and whose kick was lost would otherwise wait for the next restart. Conditional,
    /// because a reschedule may have committed a newer definition and parked it in the meantime, and
    /// replacing that registration latest-wins hands the row back to a definition nobody owns any more.
    /// </remarks>
    /// <returns>
    /// False when the scheduler refused the registration — a newer definition owns the row's parking, or the
    /// scheduler is shutting down. Nothing was parked either way, so no caller may report this row as parked.
    /// </returns>
    private bool RePark(TaskHandlerExecutor executor, Guid parentId, DateTimeOffset at)
    {
        if (_scheduler.TrySchedule(executor with { ExecutionTime = at }, at))
        {
            _logger.ScheduleReparked(parentId, at);
            return true;
        }

        _logger.ScheduleReparkRefused(parentId, at, executor.ScheduleVersion);
        return false;
    }

    /// <summary>
    /// Drops the per-schedule bookkeeping of a schedule that will not run again, so a host that creates and
    /// finishes many durable schedules does not accumulate one entry per schedule it ever had.
    /// </summary>
    /// <remarks>
    /// A concurrent run that recreates the gate right after is harmless: the gate only reduces contention, and
    /// the storage compare-and-swaps are what actually keep two runs from both materializing a slot.
    /// </remarks>
    private void DropGate(Guid parentId)
    {
        _haltReported.TryRemove(parentId, out _);
        _catchUps.TryRemove(parentId, out _);
        _enumerator.Forget(parentId);

        if (_gates.TryGetValue(parentId, out var gate))
            _gates.TryRemove(new KeyValuePair<Guid, ScheduleGate>(parentId, gate));
    }

    private IEverTaskWorkerExecutor? WorkerExecutor =>
        _workerExecutor ??= _serviceProvider.GetService<IEverTaskWorkerExecutor>();

    /// <summary>
    /// The consecutive provider failures this host holds per schedule, resolved only to FORGET a series that
    /// has ended. Absent in a container that registers no occurrence providers.
    /// </summary>
    private OccurrenceProviderRetryRegistry? ProviderRetries
    {
        get
        {
            if (!_providerRetriesResolved)
            {
                _providerRetries         = _serviceProvider.GetService<OccurrenceProviderRetryRegistry>();
                _providerRetriesResolved = true;
            }

            return _providerRetries;
        }
    }

    private void PublishOccurrenceEvent(TaskHandlerExecutor occurrence, Guid parentId, DateTimeOffset slot,
                                        int runNumber)
    {
        if (WorkerExecutor is not { HasEventSubscribers: true } worker)
            return;

        worker.PublishExternalEvent(occurrence, SeverityLevel.Information,
            string.Create(CultureInfo.InvariantCulture,
                $"Materialized occurrence {occurrence.PersistenceId} of schedule {parentId} for slot {slot:O} (run {runNumber})"));
    }

    private void PublishStaleEvent(TaskHandlerExecutor occurrence, Guid parentId, QueuedTaskStatus status)
    {
        if (WorkerExecutor is not { HasEventSubscribers: true } worker)
            return;

        worker.PublishExternalEvent(occurrence, SeverityLevel.Warning,
            string.Create(CultureInfo.InvariantCulture,
                $"Stale occurrence {occurrence.PersistenceId} of schedule {parentId} was stranded in {status} and has been requeued"));
    }

    /// <summary>
    /// Reports a slot the cursor was pointing at that already had an occurrence, and which the run therefore
    /// skipped over instead of materializing again.
    /// </summary>
    /// <remarks>
    /// A warning rather than a debug line: it means the cursor and the rows disagreed about what has already
    /// been served, which is either a row written outside the materializer or a cursor that was rewound over
    /// work already done. Neither is routine, and the schedule advanced without producing anything.
    /// </remarks>
    private void ReportSlotAlreadyServed(TaskHandlerExecutor executor, Guid parentId, DateTimeOffset slot)
    {
        _logger.SlotAlreadyServed(parentId, slot);

        if (WorkerExecutor is not { HasEventSubscribers: true } worker)
            return;

        worker.PublishExternalEvent(executor, SeverityLevel.Warning,
            string.Create(CultureInfo.InvariantCulture,
                $"Slot {slot:O} of schedule {parentId} already has an occurrence: the cursor was carried past it"));
    }

    /// <summary>
    /// Reports one run of slots a plan dropped — the one loss a durable schedule is allowed — with the rule
    /// that dropped it, and with whether the number is the real total or a lower bound (a calendar grid is
    /// counted under a cap).
    /// </summary>
    /// <remarks>
    /// The cause is half the report: an age window too narrow for the outage and a per-episode cap that kept
    /// only the newest slots are different mistakes with different fixes, and merging them sends an operator
    /// to widen a window that dropped nothing.
    /// </remarks>
    private void ReportLoss(TaskHandlerExecutor executor, Guid parentId, SlotLoss loss)
    {
        switch (loss.Reason)
        {
            case SlotLossReason.CatchUpOverflow:
                _logger.OccurrencesDroppedByOverflow(parentId, loss.Count, loss.IsExact, loss.FromUtc);
                break;

            case SlotLossReason.SkippedByPolicy:
                _logger.OccurrencesSkippedByPolicy(parentId, loss.Count, loss.IsExact, loss.FromUtc);
                break;

            default:
                _logger.OccurrenceSkipped(parentId, loss.Count, loss.IsExact, loss.FromUtc);
                break;
        }

        if (WorkerExecutor is not { HasEventSubscribers: true } worker)
            return;

        var count = loss.IsExact
                        ? loss.Count.ToString(CultureInfo.InvariantCulture)
                        : string.Create(CultureInfo.InvariantCulture, $"at least {loss.Count}");

        var cause = loss.Reason switch
        {
            SlotLossReason.CatchUpOverflow => "older than the most recent slots the catch-up cap keeps",
            SlotLossReason.SkippedByPolicy => "the skip policy does not replay a slot that is no longer current",
            _                              => "outside the misfire window"
        };

        worker.PublishExternalEvent(executor, SeverityLevel.Warning,
            string.Create(CultureInfo.InvariantCulture,
                $"Schedule {parentId} skipped {count} due slot(s) from {loss.FromUtc:O}: {cause}"));
    }

    /// <summary>
    /// Opens or closes the catch-up EPISODE a schedule is in, which is what the two boundary events report.
    /// </summary>
    /// <remarks>
    /// A catch-up spans as many runs as the budget takes to drain the backlog and each run reports only the
    /// rows it wrote, so without a boundary a consumer cannot tell a replay from the ordinary occurrences
    /// around it. Decided from the PLAN, so it opens before the rows exist. Per-process state: a restart mid
    /// replay opens a new episode, which is what the materializer itself does with the backlog.
    /// </remarks>
    private void TrackCatchUpEpisode(TaskHandlerExecutor executor, Guid parentId, DueSlotPlan plan,
                                     DateTimeOffset now)
    {
        if (plan.Misfire is { Kind: MisfireKind.CatchUp } misfire)
        {
            if (!_catchUps.TryAdd(parentId, new CatchUpEpisode(now)))
                return;

            _logger.CatchUpStarted(parentId, misfire.MissedFromUtc, misfire.MissedCount,
                misfire.MissedCountIsExact);

            if (WorkerExecutor is not { HasEventSubscribers: true } worker)
                return;

            var due = misfire.MissedCountIsExact
                          ? misfire.MissedCount.ToString(CultureInfo.InvariantCulture)
                          : string.Create(CultureInfo.InvariantCulture, $"at least {misfire.MissedCount}");

            worker.PublishExternalEvent(executor, SeverityLevel.Information,
                string.Create(CultureInfo.InvariantCulture,
                    $"Catch-up of schedule {parentId} started from slot {misfire.MissedFromUtc:O}: {due} slot(s) are due"));

            return;
        }

        // Only a plan that ran out of WORK ends the episode: one that ran out of concurrency budget plans
        // nothing at all while the replay is at its busiest, and reading that as the end closes and reopens
        // the episode between every two occurrences of a serial catch-up.
        if (plan.StopReason == DueSlotStopReason.Exhausted)
            ReportCatchUpCompleted(executor, parentId, now);
    }

    /// <summary>Adds what a pass really wrote to the episode's tally, when one is open.</summary>
    private void CountCatchUpOccurrences(Guid parentId, int created)
    {
        if (created > 0 && _catchUps.TryGetValue(parentId, out var episode))
            Interlocked.Add(ref episode.Materialized, created);
    }

    /// <summary>Closes the episode a schedule was in, if it was in one.</summary>
    private void ReportCatchUpCompleted(TaskHandlerExecutor executor, Guid parentId, DateTimeOffset now)
    {
        if (!_catchUps.TryRemove(parentId, out var episode))
            return;

        var materialized = Volatile.Read(ref episode.Materialized);

        _logger.CatchUpCompleted(parentId, materialized, now - episode.StartedAtUtc);

        if (WorkerExecutor is not { HasEventSubscribers: true } worker)
            return;

        worker.PublishExternalEvent(executor, SeverityLevel.Information,
            string.Create(CultureInfo.InvariantCulture,
                $"Catch-up of schedule {parentId} completed: {materialized} occurrence(s) materialized since {episode.StartedAtUtc:O}"));
    }

    /// <summary>
    /// Reports a halt, rate-limited per schedule: the operational retry comes back every minute, and a
    /// schedule nobody has resumed yet must stay visible without filling the dashboard.
    /// </summary>
    private void ReportHalt(TaskHandlerExecutor executor, Guid parentId, ScheduleHaltInfo halt, DateTimeOffset now)
    {
        var last = _haltReported.GetOrAdd(parentId, DateTimeOffset.MinValue);

        if (last != DateTimeOffset.MinValue && now - last < HaltReportInterval)
            return;

        _haltReported[parentId] = now;

        _logger.CatchUpHalted(parentId, halt.CursorUtc, halt.DetectedAtLeast, halt.IsExact);

        if (WorkerExecutor is not { HasEventSubscribers: true } worker)
            return;

        var detected = halt.IsExact
                           ? halt.DetectedAtLeast.ToString(CultureInfo.InvariantCulture)
                           : string.Create(CultureInfo.InvariantCulture, $"at least {halt.DetectedAtLeast}");

        worker.PublishExternalEvent(executor, SeverityLevel.Error,
            string.Create(CultureInfo.InvariantCulture,
                $"Catch-up of schedule {parentId} halted at cursor {halt.CursorUtc:O}: {detected} slots are due, more than the configured cap"));
    }

    /// <summary>The schedule row as ONE run read it: what every decision and every write of that run is about.</summary>
    /// <remarks>
    /// Taken once and never refreshed, because an expectation read at write time absorbs whatever a concurrent
    /// writer did in between and the write that should have lost wins instead — a storage handing back LIVE
    /// entities (the in-memory one) makes that concrete. <see cref="QueueName"/> is here for the same reason
    /// and not as an expectation: re-reading it mid-run would route half of one run's occurrences to a queue
    /// the other half never saw. <see cref="CursorUtc"/> is the one field a run replaces, and only with a
    /// value one of its own compare-and-swaps wrote.
    /// </remarks>
    private readonly record struct ScheduleSnapshot(
        Guid Id,
        int ScheduleVersion,
        DateTimeOffset CursorUtc,
        QueuedTaskStatus Status,
        int CurrentRunCount,
        string? QueueName);

    /// <summary>What a row gave back when the reconciliation tried to turn it into a delivery again.</summary>
    /// <param name="Executor">The rebuilt executor, or null when the row produced none.</param>
    /// <param name="Failure">Why it produced none.</param>
    /// <param name="Permanent">
    /// Whether that reason is final for this process — the type, the payload or the handler registration is
    /// gone — as opposed to an activation that failed once and may well succeed at the next retry.
    /// </param>
    private readonly record struct OccurrenceRebuild(
        TaskHandlerExecutor? Executor,
        Exception? Failure,
        bool Permanent);

    /// <summary>What one pass over a plan's slots did.</summary>
    /// <param name="CursorUtc">Where the cursor stands after the pass.</param>
    /// <param name="Created">Occurrences this pass really wrote — the only thing that spends the budget.</param>
    /// <param name="ServedSlots">Slots it walked past because a row already existed for them.</param>
    /// <param name="Finalized">Whether the series ended.</param>
    /// <param name="WonEveryWrite">
    /// False when a compare-and-swap lost: someone else owns the row now and this run stops touching it.
    /// </param>
    private readonly record struct MaterializationPass(
        DateTimeOffset CursorUtc,
        int Created,
        int ServedSlots,
        bool Finalized,
        bool WonEveryWrite);

    /// <summary>One catch-up episode of a schedule: when this process saw it start, and what it has written.</summary>
    private sealed class CatchUpEpisode(DateTimeOffset startedAtUtc)
    {
        public readonly DateTimeOffset StartedAtUtc = startedAtUtc;

        /// <summary>Occurrences materialized since the episode opened. Written by one run at a time, but the
        /// gate is per schedule and a re-entrant run may add to it, so the add is interlocked.</summary>
        public int Materialized;
    }

    /// <summary>Per-schedule serialization plus the pending-work flag that keeps a coalesced run from vanishing.</summary>
    private sealed class ScheduleGate
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public int Pending;

        /// <summary>
        /// The executor of the last schedule delivery this gate absorbed, already lazy. The holder takes it
        /// over with the work: it is what re-parks the row when the run fails and what failed is the storage
        /// the row would be rebuilt from.
        /// </summary>
        public TaskHandlerExecutor? Delivered;
    }
}
