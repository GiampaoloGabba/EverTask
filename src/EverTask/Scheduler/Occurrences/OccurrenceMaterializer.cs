using System.Collections.Concurrent;
using System.Globalization;

namespace EverTask.Scheduler.Occurrences;

/// <summary>
/// The ONE place a durable schedule turns due slots into occurrence rows (M7). Everything that can move a
/// durable schedule forward calls this and nothing else: the schedule's own slot firing, the end of each
/// occurrence, startup recovery's second wave, and the operational re-park that guarantees progress when a
/// kick is lost.
/// </summary>
/// <remarks>
/// <para>
/// It is IDEMPOTENT by construction. Every run re-reads the schedule row and decides from what it finds, and
/// every write is a compare-and-swap on the cursor and the version it decided against — so two runs racing
/// each other end with one winner and one re-read, never with two occurrences of the same slot. The
/// per-schedule gate below is an optimization on top of that, not the correctness argument.
/// </para>
/// <para>
/// It never throws at its callers. One of them is the <c>finally</c> of a delivery that has already finished:
/// a materialization failure there must not turn a completed occurrence into a failed one.
/// </para>
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

    private IEverTaskWorkerExecutor? _workerExecutor;
    private OccurrenceProviderRetryRegistry? _providerRetries;
    private bool _providerRetriesResolved;
    private int _inspectionWarned;

    /// <summary>How often a schedule that is already halted repeats its event, so a stuck one stays visible
    /// without flooding the dashboard at every operational retry.</summary>
    private static readonly TimeSpan HaltReportInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many slots that already had an occurrence ONE run walks past before handing the rest to the
    /// operational retry.
    /// </summary>
    /// <remarks>
    /// Each of them costs two round trips — the materialization that comes back
    /// <see cref="OccurrenceMaterializationOutcome.AlreadyExists"/> and the cursor advance past it — plus one
    /// step of the grid, and the run holds a permit of the global materialization budget throughout, so a
    /// cursor rewound over a very long history must not turn one run into an unbounded scan. One grid step per
    /// slot is what makes this bound the whole cost of the walk: re-planning at each of them instead would
    /// hide a count and a bisection over the remaining backlog behind every one of these units. What the bound
    /// buys is the stride: a truncated run resumes at its retry from where it stopped, never from where it
    /// started.
    /// </remarks>
    private const int MaxServedSlotsPerRun = 500;

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
    /// Never throws and never observes an exception: its caller is the <c>finally</c> of a delivery that is
    /// already over. What it must NOT do is let a failure end the schedule's progress — a kick can be holding
    /// the gate for a schedule delivery that is counting on it to re-park the row — and that is
    /// <see cref="RunAsync"/>'s job, one level in, where the failure still knows which schedule it belongs to.
    /// </remarks>
    public async ValueTask KickAsync(Guid parentId, CancellationToken ct = default)
    {
        try
        {
            await RunAsync(parentId, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping. The kick is the FAST path back to the materializer, never the only one:
            // nothing is written by a run that could not finish, and the operational retry and startup
            // recovery both bring the schedule back. Reporting it as a materialization failure would make
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
    /// and then rebuilt from the row — which is also what makes the row, not the caller, the source of truth.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunAsync(Guid parentId, TaskHandlerExecutor? parentExecutor, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(parentId, static _ => new ScheduleGate());

        while (true)
        {
            // The executor travels with the work, and it is published BEFORE the flag the holder reads: a
            // caller that finds the gate taken returns without parking the schedule row, because the holder
            // has taken that over — and the holder's own failure path needs an executor it does not have to
            // rebuild from the row, since what usually just failed IS the row's storage. ToLazy first: an
            // eager executor carries the delivery's own scope, and that delivery disposes it.
            if (parentExecutor is { } delivered)
                Volatile.Write(ref gate.Delivered, delivered.ToLazy());

            // Announce the work BEFORE trying the gate: whoever holds it re-reads this flag before releasing,
            // so a run that arrives mid-flight is never simply lost — it is absorbed into the one in progress.
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
                        // The schedule's calendar could not answer, which is transient by contract (V4).
                        // Nothing was written — a plan that cannot be computed writes nothing — so the row
                        // keeps its cursor and comes back after the provider's own backoff instead of the
                        // ordinary operational retry.
                        await DeferForProviderAsync(parentId, executor, failure, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Whoever holds the gate has ALSO taken over the re-park of every run it absorbed:
                        // a schedule's own delivery that found the gate taken returned without parking the
                        // row, because the holder was going to. Letting a failure end this run quietly is
                        // what left such a schedule parked nowhere — not in the scheduler, not in a
                        // delivery — until the process was restarted. The operational retry is armed here,
                        // whichever entry point happens to be holding the gate.
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
            // Not a durable schedule any more: a task key re-registration or a reschedule wrote an INLINE
            // definition over the row. Re-parking the durable executor this run was handed is never the
            // answer — the scheduler is keyed by id and the last write wins, so it would replace whatever
            // owns the row now with a registration that materializes nothing. Normally that owner has parked
            // the row itself and this run has nothing to do; the exception is the one case that brings an old
            // durable delivery here at all — a re-park that FAILED — where assuming it had been parked left
            // the series in no scheduler, no queue and no delivery until a restart.
            await ParkInlineRowAsync(scope.ServiceProvider, recovered, row, inline).ConfigureAwait(false);
            return;
        }

        if (recovered.Recurring is not { } definition || recovered.Task is null)
        {
            ParkUnusableRow(parentId, parentExecutor, recovered, now);
            return;
        }

        var auditLevel = recovered.AuditLevel;

        // THE snapshot this whole run decides against, taken once. Every compare-and-swap below carries these
        // values and never a fresh read of the row: an expectation read after the decision absorbs whatever a
        // concurrent writer did in between, and the write that was supposed to lose wins instead — the same
        // shape as R1 in phase 1. It matters most on a storage that hands back LIVE entities (the in-memory
        // one does), where "the row" and "the row a cancel just changed" are the same object.
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
            // Reported, and NOT re-parked. The operational retry exists so a schedule that could not advance
            // gets another chance without waiting for a restart; a halt has nothing to try again — by
            // contract it never releases itself, not by aging and not by restarting, and only an explicit
            // resume or reschedule clears the marker, both of which park the row themselves. Re-parking it
            // anyway would put the row back through the worker queue every minute forever, and each of those
            // deliveries is a Queued transition plus a status-audit row written for a schedule that by
            // definition produces nothing.
            ReportHalt(executor, parentId, standingHalt, now);
            return;
        }

        var active = await ReconcileOccurrencesAsync(scope.ServiceProvider, storage, executor, parentId, auditLevel,
                             ct)
                         .ConfigureAwait(false);

        // ONE plan per run. The slots it grants may end up being written further along the grid than it named
        // them — a slot that already has a row is walked past inside the pass below — but nothing about the
        // decision changes while that happens, which is why re-deciding it per walked slot bought nothing and
        // cost a full count and a full bisection over the remaining backlog apiece.
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

        var pass = await MaterializeAsync(scope.ServiceProvider, storage, snapshot, definition, recovered, executor,
            plan, auditLevel, now, identity, ct).ConfigureAwait(false);

        if (pass.Finalized)
        {
            _logger.DurableSeriesCompleted(parentId);

            // A durable series ends HERE and nowhere else — the cursor is nulled in the same commit that
            // writes the terminal status, so it never passes through QueueNextOccourrence, which is where an
            // inline series drops its published lower bound (S4). Without this the entry of every durable
            // schedule an operator had rescheduled outlived the series for the life of the process. The
            // provider backoff of a schedule that will not ask again goes with it, for the same reason.
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

        // A lost compare-and-swap wrote nothing: the row says something this run did not expect, and the
        // right answer is to look again rather than to act on the stale reading. Re-parked at the retry
        // interval instead of at the cursor, because that cursor is exactly the value that turned out to be
        // wrong — and never simply dropped, or a race nobody else resolves would stall the schedule until a
        // restart.
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
    /// The VERDICT on such a row — a payload that no longer deserializes, a definition that no longer
    /// validates because the zone id it names has left the tz database — belongs to startup recovery: it owns
    /// the bounded retry that lets a payload heal across a redeploy and the terminal poison at the end of it,
    /// and a second copy of either here would spend the same budget twice. What belongs to this run is where
    /// the row is PARKED. The schedule's own delivery is the only entry point that consumed the registration
    /// that made it — a kick and a recovery re-dispatch both leave the row parked where it was — so that is
    /// the one that has to hand it back, at the operational retry like any other run that could not advance.
    /// Returning quietly left such a schedule in no scheduler, no queue and no delivery, with a Debug line for
    /// it, and only a restart brought the series back. An executor is never rebuilt from the row here: that is
    /// exactly what just failed, and one built without the durable definition would run the handler.
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
    /// Whoever wrote the inline definition owns the parking and normally did it, so the usual answer here is
    /// "nothing to do" and it costs one registry lookup. The case this exists for is the only one that lets an
    /// old durable delivery reach a rewritten row: a re-park that FAILED, which publishes no version and hands
    /// the row to nobody. The executor is rebuilt FROM THE ROW and never taken from this run — the delivered
    /// one carries the durable definition, and parking it would put a registration that materializes nothing
    /// over a series that now runs a handler. Without evidence (a scheduler that cannot report its
    /// registrations) the row is parked anyway: a duplicate registration of the same instant is replaced
    /// latest-wins, while a missing one stops the series until a restart.
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
    /// A slot that comes back <see cref="OccurrenceMaterializationOutcome.AlreadyExists"/> is walked past HERE,
    /// on the grid, and the occurrence the plan granted is written at the slot behind it. Nothing about the
    /// decision changes while a run walks: <c>now</c> is fixed, a slot newer than an eligible one is inside the
    /// age window a fortiori, a backlog that only shrinks cannot exceed a cap the bigger one already passed,
    /// and a slot that already had a row spends neither a run nor a unit of the concurrency budget. Handing the
    /// walk back to the caller for a fresh plan per slot asked all of that again — a bounded count and a
    /// bisection over the WHOLE remaining backlog each time, quadratic in the length of the stretch — to get
    /// the same answers back.
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

            // The cursor this write leaves behind. While the plan still has slots of its own, the next one.
            // Then: this write spends the LAST run the budget allows, so the series ends in the same commit
            // that creates it (M6/M14) — and it ends wherever the walk put that occurrence, because MaxRuns
            // counts materializations and not slots, so a slot that turned out to be already served moved the
            // write along the grid without spending anything. Otherwise the grid decides what follows the slot
            // actually being written: the plan's own answer for the slot it named, a fresh step for one the
            // walk reached, and a fresh step too when the plan's answer was a run budget that is NOT spent yet.
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

            // The slot already has a row while the cursor still points at it: a row written from outside the
            // materializer, or an episode replayed after the cursor was rewound (a durable schedule
            // re-registered with a backfill over slots it had already served). Re-reading answers the same
            // thing forever — the cursor is what is stale, and nothing else moves it — so the cursor is
            // carried past the slot that is already served and the run goes on to the next one.
            if (outcome == OccurrenceMaterializationOutcome.AlreadyExists)
            {
                served++;
                ReportSlotAlreadyServed(executorForEvents, parentId, slot);

                // A null cursor here would say the series ends with this slot, and it does not: a slot that
                // already had a row creates nothing and spends no run, so the run the plan set aside is still
                // owed and gets created at the slot behind this one — which is where the budget is spent and
                // where the series then ends, in that commit. Only a grid with nothing left after this slot
                // ends it here.
                var next = newCursor
                           ?? await _enumerator.NextSlotAfterAsync(definition, slot, identity, ct)
                                               .ConfigureAwait(false);

                if (next is not { } pastTakenSlot)
                {
                    var closed = await storage
                                       .TrySetRecurringSeriesCompleted(parentId, cursor, snapshot.Status, version, 0,
                                           auditLevel, ct)
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
            var ended = await storage
                              .TrySetRecurringSeriesCompleted(parentId, cursor, snapshot.Status, version, 0,
                                  auditLevel, ct)
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
    /// The range and the count are two halves of ONE statement — the count is how many grid slots the range
    /// holds — so a run that walked past slots the backlog no longer owes has to say so on both: the range
    /// starts at the slot being created and the count drops by exactly the number walked, since those slots
    /// were consecutive from the range's old start. Its newest end does not move: walking never touches it.
    /// Whether what is left is still MISSED work is the enumerator's own rule (M1), asked again here rather
    /// than duplicated — a backlog worn down to one slot inside the misfire threshold is a schedule that has
    /// caught up, and one still older than the threshold is not.
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
            // one that gets suppressed. No re-park either — see the standing-halt branch above: a halted
            // schedule waits for a person, not for a timer.
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
    /// The delivered executor is preferred over rebuilding one from the row: what usually just failed IS the
    /// storage, and re-reading it to recover from a read failure would fail the same way. <c>ToLazy</c> drops
    /// the EverTask-owned scope of the delivery that is ending, exactly as the ordinary re-park does. Never
    /// throws — this is already the failure path, and its caller is holding the per-schedule gate.
    /// </remarks>
    private async Task<ReParkOutcome> ReParkAfterFailureAsync(Guid parentId, TaskHandlerExecutor? parentExecutor,
                                                              TimeSpan delay, CancellationToken ct)
    {
        TaskHandlerExecutor? built = null;

        try
        {
            var retryAt = _timeProvider.GetUtcNow() + delay;

            if (parentExecutor is { } delivered)
            {
                built = delivered.ToLazy();

                // A refusal parked NOTHING, so it hands back the same empty outcome as a row there was
                // nothing to park for: the sentence "parked to ask again" is exactly what must not be said
                // over it.
                return RePark(built, parentId, retryAt) ? new ReParkOutcome(built, null) : default;
            }

            using var scope = _scopeFactory.CreateScope();

            if (scope.ServiceProvider.GetService<ITaskStorage>() is not { } storage)
                return default;

            var row = (await storage.Get(t => t.Id == parentId, ct).ConfigureAwait(false)).FirstOrDefault();

            if (row is null || !row.IsRecurring || row.Status == QueuedTaskStatus.Cancelled ||
                row.NextRunUtc is null)
                return default;

            var recovered = RecoveredTaskFactory.FromRow(row);

            if (recovered.Recurring is not { IsDurable: true } definition || recovered.Task is null)
                return default;

            built = await BuildScheduleExecutorAsync(scope.ServiceProvider, recovered, row, definition,
                row.NextRunUtc.Value).ConfigureAwait(false);

            return RePark(built, parentId, retryAt) ? new ReParkOutcome(built, null) : default;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: the row keeps its cursor and startup recovery parks it again.
            return default;
        }
        catch (Exception ex)
        {
            // Nothing else can arm the retry from here, so this is the one place the situation is visible:
            // the schedule stays unparked until the next restart, and that has to be said out loud — a log
            // line AND the monitoring event, because there is no poller behind this and a log file is not
            // where a dashboard looks (V4(2)).
            _logger.ReparkAfterFailureFailed(ex, parentId);

            PublishReParkFailedEvent(built ?? parentExecutor, parentId, ex);

            return new ReParkOutcome(built, ex);
        }
    }

    /// <summary>What a re-park after a failed run left behind.</summary>
    /// <param name="Executor">
    /// The executor the schedule was parked with, when the registration was really made. Null on the exits
    /// that park nothing — a row that is gone, cancelled or no longer durable, and a registration the
    /// scheduler REFUSED because a newer definition owns the row or because it is shutting down.
    /// </param>
    /// <param name="Failure">
    /// The exception that stopped the re-park, when it failed. Null both when it succeeded and when there was
    /// nothing to park, which are the two cases a caller must not report as a schedule left in the air.
    /// </param>
    private readonly record struct ReParkOutcome(TaskHandlerExecutor? Executor, Exception? Failure);

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
    /// that failure has earned (V4).
    /// </summary>
    /// <remarks>
    /// The ordinary operational retry would do the same thing on the wrong clock: the provider's backoff grows
    /// with its consecutive failures, so a source that is down for an hour is asked a handful of times instead
    /// of sixty. Nothing is written either way — a plan that could not be computed materialized nothing — so
    /// the row keeps its cursor and a crash costs only the wait.
    /// </remarks>
    private async Task DeferForProviderAsync(Guid parentId, TaskHandlerExecutor? parentExecutor,
                                             OccurrenceProviderException failure, CancellationToken ct)
    {
        var retryAt = _timeProvider.GetUtcNow() + failure.RetryAfter;

        var outcome = await ReParkAfterFailureAsync(parentId, parentExecutor, failure.RetryAfter, ct)
                          .ConfigureAwait(false);

        // Said only once the registration is really in, log line and event alike: before that point "parked to
        // ask again" is a promise the very next line can break. A re-park that FAILED has already reported
        // itself, as an error and not as this warning; a re-park the scheduler REFUSED hands back no executor
        // and is silent here for the same reason — this run parked nothing, and whether the row is waiting on
        // anything at all is now somebody else's business.
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
    /// paper (M7/M9).
    /// </summary>
    /// <remarks>
    /// An occurrence is really alive while a delivery holds it or the scheduler has it parked. One that is
    /// neither — a status write swallowed by a crash, a scheduling lost to a shutdown — would otherwise sit
    /// non-terminal forever and permanently consume a slot of the concurrency budget, which for the default
    /// budget of one means the schedule never moves again. It is put back in the scheduler under a
    /// compare-and-swap on the status it was found in, and it counts as active either way: a stale occurrence
    /// frees capacity only by reaching a terminal state, never by being noticed.
    /// <para>
    /// Without a scheduler that can answer "is this parked", there is no evidence to tell the two apart — the
    /// default answer is a constant "yes" — so nothing is reconciled and every non-terminal occurrence counts.
    /// Conservative in the only direction that is safe: it delays the schedule instead of running an
    /// occurrence twice.
    /// </para>
    /// <para>
    /// An occurrence whose row cannot produce a runnable task is the one exception, and it is terminalized
    /// rather than requeued — see <see cref="FailUnusableOccurrenceAsync"/>.
    /// </para>
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

            // Rebuilt BEFORE the requeue, not after it. A row that cannot produce an executor cannot be
            // rescued by a compare-and-swap back to Queued, and doing it in the other order wrote that
            // transition and its status-audit row on every single run — for ever, since the row was then
            // dropped by the next line and left exactly as it was found.
            var rebuild = await BuildOccurrenceFromRowAsync(provider, child).ConfigureAwait(false);

            if (rebuild.Executor is null)
            {
                // Registered but not activatable right now — a scoped dependency that timed out, a
                // connection that was not there — is NOT a verdict on the row: it is the same transient
                // failure the retry policy exists for, and ending the occurrence on it would drop work no
                // handler ever saw. It keeps its slot of the budget and its non-terminal status, and the
                // operational retry looks again.
                if (!rebuild.Permanent)
                {
                    _logger.OccurrenceRebuildDeferred(rebuild.Failure!, child.Id, parentId);
                    continue;
                }

                // Only a CONFIRMED terminal state frees capacity. SetStatus is best-effort on every
                // relational provider — it logs its own failure and returns — so counting the slot as free on
                // the strength of having called it would let the run create a successor while the old row is
                // still alive, over a budget that says one.
                if (await FailUnusableOccurrenceAsync(storage, executorForEvents, child, parentId, rebuild.Failure!,
                            auditLevel, ct)
                        .ConfigureAwait(false))
                {
                    active--;
                }

                continue;
            }

            var executor = rebuild.Executor;

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
    /// <remarks>
    /// The verdict cannot change while the process lives: the type is gone, its persisted payload does not
    /// deserialize against this build, or nothing here registers a handler for it, and re-reading the same
    /// row answers the same thing every minute. Left
    /// non-terminal it would go on consuming a slot of the schedule's concurrency budget, which at the default
    /// budget of one is a series that never materializes another occurrence. <c>Failed</c> is the state M13
    /// gives an occurrence that cannot run — the series moves on, the row keeps the reason it carries, and
    /// <see cref="ITaskStorage.RequeueTerminal"/> is the way back once a deploy makes the row readable again.
    /// <para>
    /// The write is unconditional, like every other poison in the codebase. This path is reached only for an
    /// occurrence no delivery holds and no scheduler carries, and one no build of this process could run
    /// anyway, so the only status a concurrent writer could put under it is another terminal one.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Whether the row really is terminal now. <see cref="ITaskStorage.SetStatus"/> is best-effort — every
    /// relational provider logs a failed transition and returns normally — so the caller cannot read "the call
    /// returned" as "the slot is free": a swallowed write would let the schedule create a successor while the
    /// old occurrence is still alive under a budget of one.
    /// </returns>
    private async Task<bool> FailUnusableOccurrenceAsync(ITaskStorage storage, TaskHandlerExecutor executorForEvents,
                                                         QueuedTask child, Guid parentId, Exception reason,
                                                         AuditLevel auditLevel, CancellationToken ct)
    {
        await storage.SetStatus(child.Id, QueuedTaskStatus.Failed, reason, auditLevel, null, ct)
                     .ConfigureAwait(false);

        _logger.OccurrenceRowUnusable(reason, child.Id, parentId);

        var written = (await storage.Get(t => t.Id == child.Id, ct).ConfigureAwait(false)).FirstOrDefault();
        var ended   = written is null || !QueuedTask.IsNonTerminalStatus(written.Status);

        if (!ended)
            _logger.OccurrenceTerminalizationLost(child.Id, parentId, written!.Status);

        if (WorkerExecutor is not { HasEventSubscribers: true } worker)
            return ended;

        worker.PublishExternalEvent(executorForEvents, SeverityLevel.Error,
            string.Create(CultureInfo.InvariantCulture,
                $"Occurrence {child.Id} of schedule {parentId} cannot be rebuilt from its row and was marked Failed: {reason.Message}"));

        return ended;
    }

    /// <summary>Builds the executor of a brand-new occurrence, before its row exists.</summary>
    /// <param name="timeZoneId">
    /// The zone of the schedule, copied onto the occurrence: a child carries no definition, so this is the only
    /// way the zone and the local slot reach its handler (C1/T13).
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
        // routed to its own queue would quietly leave it, one occurrence at a time (M4).
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
    /// A row is unusable for TWO reasons, and the second one used to escape as an exception: the payload may
    /// not rebuild, and the rebuilt payload may have no handler to run it — a type still loadable after a
    /// re-registration pointed its schedule at a different task, whose <c>IEverTaskHandler&lt;T&gt;</c> nobody
    /// registers any more. Resolution happens inside <c>Handle</c>, so that one threw out of the whole
    /// reconciliation: the schedule re-parked, the occurrence stayed non-terminal, and under the default
    /// budget of one it held the series behind something no build of this process can deliver. Both are the
    /// same verdict — see <see cref="FailUnusableOccurrenceAsync"/> — and the way back from it is the same.
    /// <para>
    /// What is NOT that verdict is a handler that exists and merely failed to be built this time: a scoped
    /// dependency whose factory threw, a connection that was not there. The exception looks identical from
    /// here, so the container is asked the question that tells them apart — is anything registered for this
    /// task at all? — and only a "no" is final. Ending an occurrence on a transient activation failure would
    /// drop work no handler ever saw, without a single one of the retries its policy promises.
    /// </para>
    /// </remarks>
    private static async Task<OccurrenceRebuild> BuildOccurrenceFromRowAsync(IServiceProvider provider,
                                                                            QueuedTask child)
    {
        var recovered = RecoveredTaskFactory.FromRow(child);

        if (recovered.Task is null)
        {
            // The two verdicts the factory keeps apart, kept apart here too: a type that is gone can never
            // run, a payload that did not deserialize did not run in THIS build. Neither answer changes for
            // this process, which is what makes the row's ending a decision and not a retry.
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
    /// Asked only after a rebuild has already failed, so the cost is paid on the rare path. An activation that
    /// throws again here is evidence of the opposite kind: something IS registered, and what failed is the
    /// building of it.
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
    /// whose window is full and whose kick was lost would otherwise wait for the next restart.
    /// </remarks>
    /// <remarks>
    /// Conditional, because the executor a run was HANDED belongs to the delivery that produced it and a
    /// reschedule may have committed a newer definition and parked it in the meantime: replacing that
    /// registration latest-wins would hand the row back to a definition nobody owns any more.
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
    /// The CAUSE is half the report. An age window that is too narrow for the outage and a per-episode cap
    /// that kept only the newest slots are different mistakes with different fixes, and reporting the second
    /// one under the first one's sentence sent an operator to widen a window that had dropped nothing.
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
    /// Taken once, at the top of the run, and never refreshed. Each compare-and-swap below carries these values
    /// rather than a fresh read of the entity: an expectation read at write time absorbs whatever a concurrent
    /// writer did in between, so the write that should have lost wins instead. That is not hypothetical on a
    /// storage that hands back LIVE entities — the in-memory one does — where "the row" and "the row a cancel
    /// just changed" are the same object. <see cref="QueueName"/> is here for the same reason and not as an
    /// expectation: it is copied onto every occurrence, and re-reading it mid-run would route half of one run's
    /// occurrences to a queue the other half never saw.
    /// </remarks>
    /// <remarks>
    /// <see cref="CursorUtc"/> is the one field a run replaces, and only with a value one of its OWN
    /// compare-and-swaps wrote: a pass that walked past a slot already served hands the next pass the cursor
    /// it put there, which is still a value this run is entitled to expect.
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

    /// <summary>Per-schedule serialization plus the pending-work flag that keeps a coalesced run from vanishing.</summary>
    private sealed class ScheduleGate
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public int Pending;

        /// <summary>
        /// The executor of the last schedule delivery this gate absorbed, already lazy. The holder takes it
        /// over together with the work — it is what re-parks the row when the run fails and the storage the
        /// row would be rebuilt from is the thing that failed.
        /// </summary>
        public TaskHandlerExecutor? Delivered;
    }
}
