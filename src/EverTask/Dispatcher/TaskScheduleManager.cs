using System.Globalization;
using EverTask.Configuration;
using EverTask.RateLimiting;
using EverTask.Scheduler.Occurrences;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Dispatcher;

/// <summary>
/// The one place a schedule already registered is changed: it re-reads the row, decides the new definition and
/// cursor, writes them under a compare-and-swap on the schedule version, and hands the row back to whatever
/// owns its parking.
/// </summary>
/// <remarks>
/// <para>
/// It holds the SAME per-taskKey critical section a dispatch holds. A dispatch under the same key rewrites the
/// definition and the cursor through <c>UpdateTask</c>, which never touches the version, so nothing but that
/// shared section keeps a reschedule from being silently overwritten by a dispatch that had read the row first.
/// </para>
/// <para>
/// Every write is conditional and nothing here is best effort: losing the compare-and-swap is reported to the
/// caller rather than retried on a stale reading, and a definition with no occurrence left is refused instead
/// of leaving a row that no restart could ever finish.
/// </para>
/// </remarks>
internal sealed class TaskScheduleManager(
    IServiceScopeFactory scopeFactory,
    IScheduler scheduler,
    IScheduleEvaluator evaluator,
    EverTaskServiceConfiguration options,
    TaskKeyLockRegistry taskKeyLocks,
    ScheduleVersionRegistry scheduleVersions,
    ITaskDispatcher dispatcher,
    IEverTaskLogger<TaskScheduleManager> logger,
    TimeProvider timeProvider,
    ITaskStorage? storage = null,
    OccurrenceProviderRegistry? providers = null,
    OccurrenceMaterializer? materializer = null,
    IGateInvalidationRegistry? gateInvalidation = null,
    IEverTaskWorkerExecutor? workerExecutor = null,
    IWorkerBlacklist? workerBlacklist = null) : ITaskScheduleManager
{
    /// <summary>
    /// How far the discarded-backlog count is allowed to walk a calendar grid. It goes to an event and to the
    /// result, never to a decision, so a lower bound reported AS a lower bound is the right answer — and a
    /// three-month one-second backlog must not be enumerated to produce it.
    /// </summary>
    /// <remarks>
    /// It is the bound for a grid walked IN MEMORY. Over an occurrence provider each step is a round trip
    /// against the application's own calendar, so the same count uses the far smaller bound
    /// <see cref="ProviderScheduleGrid.MaxDiagnosticWalk"/> — see
    /// <see cref="ProviderScheduleGrid.DiagnosticCapFor"/>.
    /// </remarks>
    private const int DiscardedBacklogCountCap = 10_000;

    /// <inheritdoc />
    public Task<ScheduleUpdateResult> Reschedule(string taskKey, Action<IRecurringTaskBuilder> configure,
                                                 RescheduleMode mode = RescheduleMode.RecalculateFromNow,
                                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (!Enum.IsDefined(mode))
            throw new ArgumentException($"Invalid RescheduleMode '{(int)mode}': not a defined value.", nameof(mode));

        return ApplyAsync(taskKey, configure, mode, keepCursor: false, ct);
    }

    /// <inheritdoc />
    public Task<ScheduleUpdateResult> ReevaluateSchedule(string taskKey, CancellationToken ct = default) =>
        ApplyAsync(taskKey, null, RescheduleMode.RecalculateFromNow, keepCursor: false, ct);

    /// <inheritdoc />
    public Task<ScheduleUpdateResult> ResumeSchedule(string taskKey, CancellationToken ct = default) =>
        ApplyAsync(taskKey, null, RescheduleMode.RecalculateFromNow, keepCursor: true, ct);

    /// <inheritdoc />
    public async Task<bool> RequeueFailedOccurrence(Guid occurrenceId, CancellationToken ct = default)
    {
        var store = RequireStorage();

        if (!store.SupportsDurableOccurrences)
        {
            throw new NotSupportedException(
                $"The registered storage does not implement durable occurrences ({store.GetType().Name}." +
                $"{nameof(ITaskStorage.SupportsDurableOccurrences)} is false), so it has no occurrence to requeue.");
        }

        var row = (await store.Get(t => t.Id == occurrenceId, ct).ConfigureAwait(false)).FirstOrDefault()
                  ?? throw new InvalidOperationException($"There is no task with id {occurrenceId}.");

        if (row.ParentTaskId is not { } parentId)
        {
            throw new InvalidOperationException(
                $"Task {occurrenceId} is not an occurrence of a schedule. A schedule row is changed with " +
                $"{nameof(Reschedule)} or {nameof(CancelSchedule)}, never requeued.");
        }

        if (row.Status is not (QueuedTaskStatus.Failed or QueuedTaskStatus.Cancelled))
            return false;

        // The schedule itself has the last word. CancelSchedule cancels the pending occurrences in the same
        // transaction as the schedule row, so by STATUS alone each of them looks exactly like an occurrence
        // waiting to be retried, and putting one back would run the handler of a series an operator has ended.
        // The blacklist is no answer either: its entries lapse after about an hour and never existed in a
        // process that did not issue the cancel.
        await RequireLiveSchedule(store, parentId, occurrenceId, ct).ConfigureAwait(false);

        var previousStatus = row.Status;
        var auditLevel     = row.AuditLevel is { } level ? (AuditLevel)level : AuditLevel.Full;

        // Rebuilt BEFORE the requeue, exactly as the reconciliation does it: a row that cannot produce an
        // executor cannot be rescued by putting it back in Queued, and doing it the other way round writes
        // that transition and its audit row for a delivery that never happens.
        using var scope = scopeFactory.CreateScope();

        var executor = await BuildOccurrenceExecutorAsync(scope.ServiceProvider, row).ConfigureAwait(false);

        if (!await store.RequeueTerminal(occurrenceId, auditLevel, ct).ConfigureAwait(false))
            return false;

        // The parent is asked AGAIN, now that the requeue is committed, and this is the reading that decides.
        // The check above and the write are two round trips, so a Cancel of the SCHEDULE can linearize between
        // them — and a Failed occurrence is not in the pending set that cancel cascades to. One of the two
        // orderings always sees the other, because the cancel asks for occurrences after persisting the status.
        if (!await StillLiveAfterRequeueAsync(store, parentId, occurrenceId, auditLevel, ct).ConfigureAwait(false))
            return false;

        // The occurrence's OWN cancellation is undone here, and nowhere else: its blacklist entry lives about
        // an hour and nothing on this path consumes it, so the row would sit non-terminal with no queue entry,
        // no parking and no delivery until a restart, while this call reported success. The schedule's entry
        // is untouched — it covers the siblings, and a cancelled schedule was already refused above.
        workerBlacklist?.Remove(occurrenceId);

        logger.OccurrenceRequeued(occurrenceId, parentId, previousStatus);
        Publish(executor, SeverityLevel.Information,
            $"Occurrence {occurrenceId} of schedule {parentId} was requeued from {previousStatus}");

        // Handed to the scheduler, never enqueued: this can be called from anywhere, including a handler
        // running on a queue consumer, and a blocking enqueue there can wait on the very consumer that drains it.
        scheduler.Schedule(executor, row.ScheduledExecutionUtc ?? timeProvider.GetUtcNow());

        return true;
    }

    /// <summary>
    /// Re-reads the schedule after an occurrence has been put back, and undoes the requeue when a cancel of
    /// the whole series won the race.
    /// </summary>
    /// <returns>True when the occurrence may be delivered; false when the schedule was cancelled under it.</returns>
    private async Task<bool> StillLiveAfterRequeueAsync(ITaskStorage store, Guid parentId, Guid occurrenceId,
                                                        AuditLevel auditLevel, CancellationToken ct)
    {
        var parent = (await store.Get(t => t.Id == parentId, ct).ConfigureAwait(false)).FirstOrDefault();

        if (parent?.Status != QueuedTaskStatus.Cancelled)
            return true;

        // Put back where the cancel would have left it. Nothing has been handed to the scheduler yet, so this
        // is the whole of the compensation.
        await store.SetStatus(occurrenceId, QueuedTaskStatus.Cancelled, null, auditLevel, null, ct)
                   .ConfigureAwait(false);

        logger.OccurrenceRequeueUndoneByCancel(occurrenceId, parentId);

        return false;
    }

    /// <inheritdoc />
    public async Task CancelSchedule(string taskKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        var store = RequireStorage();

        using var section = await taskKeyLocks.AcquireAsync(taskKey, ct).ConfigureAwait(false);

        var row = await store.GetByTaskKey(taskKey, ct).ConfigureAwait(false) ?? throw NoSuchSchedule(taskKey);

        if (!row.IsRecurring)
            throw NotASchedule(taskKey);

        // The whole cancel pipeline, not a status write: blacklist first, per-task token, the parked
        // registration dropped, the rate-limit gate invalidated, and — for a durable schedule — every pending
        // occurrence cancelled in the same transaction as the schedule itself.
        await dispatcher.Cancel(row.Id, ct).ConfigureAwait(false);

        logger.ScheduleCancelled(row.Id);
    }

    /// <summary>
    /// Refuses an occurrence whose schedule has been cancelled, which is terminal for every row under it.
    /// </summary>
    /// <remarks>
    /// A schedule that is simply over — its budget spent or its bound passed — is NOT refused: the occurrence
    /// was owed and failed, and replaying it materializes nothing and spends no run.
    /// </remarks>
    private static async Task RequireLiveSchedule(ITaskStorage store, Guid parentId, Guid occurrenceId,
                                                  CancellationToken ct)
    {
        var parent = (await store.Get(t => t.Id == parentId, ct).ConfigureAwait(false)).FirstOrDefault();

        if (parent?.Status == QueuedTaskStatus.Cancelled)
        {
            throw new InvalidOperationException(
                $"The schedule {parentId} of occurrence {occurrenceId} was cancelled, and a cancellation is " +
                "terminal for the whole series: requeuing one of its occurrences would run a schedule that " +
                "was ended on purpose. Dispatch the schedule again to start a new series.");
        }
    }

    /// <summary>
    /// The body every schedule change shares: read, decide, compare-and-swap, re-park, publish.
    /// </summary>
    /// <param name="configure">
    /// The new definition, or null to keep the one the row carries (<see cref="ReevaluateSchedule"/>,
    /// <see cref="ResumeSchedule"/>).
    /// </param>
    /// <param name="keepCursor">
    /// True for <see cref="ResumeSchedule"/>: the cursor is left exactly where it is, so the backlog that
    /// halted the schedule is planned again instead of being skipped past.
    /// </param>
    private async Task<ScheduleUpdateResult> ApplyAsync(string taskKey, Action<IRecurringTaskBuilder>? configure,
                                                        RescheduleMode mode, bool keepCursor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        var store = RequireVersionedStorage();

        using var section = await taskKeyLocks.AcquireAsync(taskKey, ct).ConfigureAwait(false);

        var row = await store.GetByTaskKey(taskKey, ct).ConfigureAwait(false) ?? throw NoSuchSchedule(taskKey);

        if (!row.IsRecurring)
            throw NotASchedule(taskKey);

        if (row.Status == QueuedTaskStatus.Cancelled)
        {
            throw new InvalidOperationException(
                $"The schedule with key '{taskKey}' was cancelled, and a cancellation is terminal: dispatch it " +
                "again to start a new series.");
        }

        var recovered = RecoveredTaskFactory.FromRow(row);

        if (recovered.Recurring is not { } current)
        {
            throw new InvalidOperationException(
                $"The schedule with key '{taskKey}' carries no definition this build can read" +
                $"{Because(recovered.ScheduleError)}.");
        }

        if (recovered.Task is null)
        {
            throw new InvalidOperationException(
                $"The payload of the schedule with key '{taskKey}' cannot be rebuilt, so there is nothing to " +
                $"schedule{Because(recovered.PayloadError)}.");
        }

        // THE snapshot this call decides against, taken once. The in-memory store hands back LIVE entities, so
        // "the row" and "the row this call is about to write" are the same object there: reading the version
        // or the cursor back after the update would report what was just written as what was there before.
        var previousVersion = row.ScheduleVersion;
        var previousCursor  = row.NextRunUtc;

        var definition = configure is null
                             ? current
                             : await BuildAsync(configure, store).ConfigureAwait(false);
        var now        = timeProvider.GetUtcNow();
        var cursor     = await DecideCursorAsync(row, current, definition, mode, keepCursor,
                                 storedDefinition: configure is null, now, ct)
                             .ConfigureAwait(false);

        if (cursor is null)
        {
            throw new InvalidOperationException(
                $"The new schedule for key '{taskKey}' has no occurrence left to run — its bounds are already " +
                $"past. Use {nameof(CancelSchedule)} to end a series on purpose.");
        }

        var backlog     = await MeasureDiscardedBacklogAsync(row, current, mode, keepCursor, now, ct)
                              .ConfigureAwait(false);
        var runtimeInfo = ClearHalt(row.RuntimeInfo, out var releasedHalt);

        // The cursor travels with the version into the compare-and-swap, because a successful advance moves it
        // — and the run counter with it — while leaving the version alone. Keying on the version only let a
        // decision taken on a run count and a cursor a completion had already superseded commit over it, and a
        // rebase computed from that reading is one occurrence past the budget it was just given.
        var applied = await store
                            .UpdateSchedule(row.Id, previousVersion, previousCursor,
                                EverTaskJson.Serialize(definition), definition.ToString(), cursor,
                                definition.MaxRuns, definition.RunUntil, runtimeInfo, ct)
                            .ConfigureAwait(false);

        if (!applied)
            throw await DescribeLostUpdateAsync(store, row.Id, taskKey, previousVersion, previousCursor, ct)
                      .ConfigureAwait(false);

        var result = new ScheduleUpdateResult
        {
            TaskId                  = row.Id,
            ScheduleVersion         = previousVersion + 1,
            PreviousScheduleVersion = previousVersion,
            NextRunUtc              = cursor,
            PreviousNextRunUtc      = previousCursor,
            Mode                    = mode,
            DiscardedBacklog        = backlog.Count,
            DiscardedBacklogIsExact = backlog.IsExact,
            ReleasedHalt            = releasedHalt
        };

        logger.ScheduleRescheduled(row.Id, previousVersion, result.ScheduleVersion, mode, previousCursor, cursor);

        if (releasedHalt)
            logger.HaltReleased(row.Id, cursor);

        if (backlog.Count > 0)
            logger.BacklogDiscarded(row.Id, backlog.Count, backlog.IsExact, previousCursor!.Value);

        await RePublishAsync(store, row, recovered, definition, result, backlog, ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Says what took the row, for a compare-and-swap that lost. Read only on the failure path.
    /// </summary>
    /// <remarks>
    /// The guard covers three things a caller cannot tell apart from a false: the version, the cursor and a
    /// cancellation that landed between the read at the top of this call and the write. Reporting all three as
    /// "no longer at version N" sent an operator looking for a concurrent reschedule that never happened, and
    /// hid the one outcome that is terminal.
    /// </remarks>
    private static async Task<InvalidOperationException> DescribeLostUpdateAsync(
        ITaskStorage store, Guid taskId, string taskKey, int previousVersion, DateTimeOffset? previousCursor,
        CancellationToken ct)
    {
        var current = (await store.Get(t => t.Id == taskId, ct).ConfigureAwait(false)).FirstOrDefault();

        if (current is null)
            return new InvalidOperationException($"The schedule with key '{taskKey}' was removed under this call.");

        if (current.Status == QueuedTaskStatus.Cancelled)
        {
            return new InvalidOperationException(
                $"The schedule with key '{taskKey}' was cancelled under this call, and a cancellation is " +
                "terminal: nothing was written. Dispatch it again to start a new series.");
        }

        return new InvalidOperationException(
            $"The schedule with key '{taskKey}' changed under this call (it is no longer at version " +
            $"{previousVersion} standing at {previousCursor:O}): nothing was written. Read it again and retry.");
    }

    /// <summary>
    /// Builds the new definition from the caller's chain, under the same rules a dispatch applies to it.
    /// </summary>
    private async ValueTask<RecurringTask> BuildAsync(Action<IRecurringTaskBuilder> configure, ITaskStorage store)
    {
        // The scheduling clock, like every other builder: RunNow and the RunUntil guards resolve on it.
        var builder = new RecurringTaskBuilder(timeProvider);
        configure(builder);

        var definition = builder.RecurringTask;

        ScheduleTimeZone.ApplyDefault(definition, options.DefaultScheduleTimeZoneId);
        definition.Validate(providers);

        // Refused here, where the caller is still holding the call, for the same reason a dispatch refuses it:
        // there is no half-atomic emulation of the occurrence operations to degrade to.
        if (definition.IsDurable)
            Dispatcher.RequireDurableOccurrenceSupport(store);

        // Same door a dispatch goes through: an unregistered provider key, or a catch-up that probes a grid
        // the provider does not promise to answer twice the same way. The caller is holding this call, so the
        // determinism probe applies here exactly as at a dispatch.
        await Dispatcher.RequireProviderSupportAsync(definition, providers).ConfigureAwait(false);

        return definition;
    }

    /// <summary>Where the schedule stands after the change, according to <paramref name="mode"/>.</summary>
    /// <param name="storedDefinition">
    /// True when the definition is the row's own (nothing new was built), which is what decides whether its
    /// first-run configuration may be applied again.
    /// </param>
    private async ValueTask<DateTimeOffset?> DecideCursorAsync(QueuedTask row, RecurringTask current,
                                                               RecurringTask definition, RescheduleMode mode,
                                                               bool keepCursor, bool storedDefinition,
                                                               DateTimeOffset now, CancellationToken ct)
    {
        // Resume: the cursor is the whole point. A halted catch-up is released by planning its backlog again
        // against the definition as it stands, so moving the cursor here would silently do what the halt was
        // put there to prevent — drop the work nobody has looked at yet.
        if (keepCursor)
        {
            return row.NextRunUtc
                   ?? throw new InvalidOperationException(
                       $"The schedule {row.Id} has already ended, so there is no cursor to resume from.");
        }

        if (mode == RescheduleMode.RebaseFromCursor)
        {
            // The run budget is the one bound the rebase cannot see: it reaches the grid through
            // FirstOccurrenceOnOrAfter, which applies RunUntil but never MaxRuns, so a definition whose budget
            // is already spent would still answer with a cursor and run one occurrence past it — where
            // RecalculateFromNow refuses the very same definition.
            if ((row.CurrentRunCount ?? 0) >= definition.MaxRuns)
                return null;

            return ScheduleRebase.Rebase(current, definition,
                row.NextRunUtc ?? throw new InvalidOperationException(
                    $"The schedule {row.Id} has already ended, so there is no cursor to rebase."));
        }

        // isRecovery suppresses the first-run configuration (RunNow / InitialDelay / SpecificRunTime) of a
        // series that has never run. A definition the caller just wrote is honoured verbatim — asking for
        // RunNow means now — while re-evaluating the STORED one must not re-apply a delay that was already
        // consumed when the schedule was dispatched.
        var next = await evaluator
                         .CalculateNextValidRunAsync(definition, now, row.CurrentRunCount ?? 0, now,
                             isRecovery: storedDefinition,
                             identity: new ScheduleIdentity(row.Id, row.TaskKey, (row.CurrentRunCount ?? 0) + 1),
                             ct: ct)
                         .ConfigureAwait(false);

        return next.NextRun;
    }

    /// <summary>
    /// How many slots the old definition still owed that moving the cursor to a future one throws away.
    /// </summary>
    /// <remarks>
    /// Only a DURABLE schedule can owe any: an inline one has a single pending occurrence, which the skip
    /// forward has always been free to move. And only a cursor that is already due counts — a schedule that was
    /// keeping up owes nothing.
    /// </remarks>
    private async ValueTask<(int Count, bool IsExact)> MeasureDiscardedBacklogAsync(
        QueuedTask row, RecurringTask current, RescheduleMode mode, bool keepCursor, DateTimeOffset now,
        CancellationToken ct)
    {
        if (keepCursor || mode != RescheduleMode.RecalculateFromNow || !current.IsDurable ||
            row.NextRunUtc is not { } owed || owed > now)
        {
            return (0, true);
        }

        var cap = ProviderScheduleGrid.DiagnosticCapFor(current, DiscardedBacklogCountCap);

        var count = await evaluator
                          .CountMissedAsync(current, owed, now, cap,
                              new ScheduleIdentity(row.Id, row.TaskKey, (row.CurrentRunCount ?? 0) + 1), ct)
                          .ConfigureAwait(false);

        // The count stops one past the cap, so anything above it is a lower bound and says so rather than
        // being reported as a total.
        return (count, count <= cap);
    }

    /// <summary>
    /// Hands the schedule back to whatever owns its parking, then publishes the new version as the lower bound
    /// stale deliveries are measured against.
    /// </summary>
    /// <remarks>
    /// The order is the contract. The registration is replaced LATEST-WINS — never unscheduled first, which
    /// would leave a window with the schedule parked nowhere — and the version is published only once that
    /// replacement is in, because a published version with no executor behind it drops the old delivery and
    /// puts nothing in its place. If the re-park throws, nothing is published: the old occurrence runs once
    /// more, and its advance loses the compare-and-swap and applies the new definition.
    /// </remarks>
    private async Task RePublishAsync(ITaskStorage store, QueuedTask row, RecoveredTask recovered,
                                      RecurringTask definition, ScheduleUpdateResult result,
                                      (int Count, bool IsExact) backlog, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();

        TaskHandlerExecutor executor;

        try
        {
            // BUILDING the executor is guarded too, not just handing it over: resolving a handler runs the
            // container, and a failure there would otherwise escape a call whose write has already committed —
            // leaving the new definition on a row parked nowhere, with nothing said about it. There is no event
            // for this one, because publishing an event needs the executor that just failed to build.
            executor = await BuildScheduleExecutorAsync(scope.ServiceProvider, recovered, row, definition, result)
                           .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.ReparkFailed(e, row.Id, result.ScheduleVersion);
            return;
        }

        // The change is COMMITTED from here on, so the event is published whatever happens to the parking: a
        // subscriber told only that a re-park failed would have no record of the version, the cursors, the
        // mode or the backlog the row now carries.
        var rescheduled = Describe(result, backlog);
        var severity    = backlog.Count > 0 ? SeverityLevel.Warning : SeverityLevel.Information;

        // A durable schedule is parked by the materializer and by nothing else: it is what decides which slot
        // the row waits for, and it re-reads the row this call has just written.
        var occurrences = definition.IsDurable ? materializer : null;

        try
        {
            if (occurrences is not null)
                await occurrences.RunAsync(row.Id, executor, ct).ConfigureAwait(false);
            else
                scheduler.Schedule(executor, result.NextRunUtc);
        }
        catch (Exception e)
        {
            logger.ReparkFailed(e, row.Id, result.ScheduleVersion);
            Publish(executor, severity, rescheduled);
            Publish(executor, SeverityLevel.Error,
                $"Schedule {row.Id} is at version {result.ScheduleVersion} but could not be handed back to the " +
                $"scheduler: {e.Message}", e);

            return;
        }

        // Only now, with a registration really in place. Any gate operation in flight for this row belongs to
        // the definition just replaced, and bumping the epoch tells it to drop the registration it is about to
        // make — but a re-park that FAILED makes no registration at all, and that stranded delivery's own
        // re-park is then the only thing standing between the series and the next restart. Bumping first had
        // the gate delete exactly it. Nothing is lost by waiting: a stale re-park landing after this point is
        // refused by IScheduler.TrySchedule, which compares versions inside the registry's own swap.
        gateInvalidation?.Invalidate(row.Id);

        if (!await SeriesEndedWhileParkingAsync(store, row.Id, occurrences is not null, ct).ConfigureAwait(false))
            scheduleVersions.Publish(row.Id, result.ScheduleVersion);

        Publish(executor, severity, rescheduled);
    }

    /// <summary>
    /// Whether the materialization this call just ran ENDED the series, so there is no version left to publish
    /// a lower bound for.
    /// </summary>
    /// <remarks>
    /// A durable series ends inside the materializer, which drops its registry entry there — and this call is
    /// one of the entry points that can reach that end synchronously: a resume whose replanned backlog spends
    /// the last run the budget allows closes the series in the same commit that creates the occurrence.
    /// Publishing afterwards would put the entry straight back for a schedule that will never run again, which
    /// is the one way the registry grows for the life of the process.
    /// </remarks>
    private static async ValueTask<bool> SeriesEndedWhileParkingAsync(ITaskStorage store, Guid taskId,
                                                                      bool materialized, CancellationToken ct)
    {
        if (!materialized)
            return false;

        var row = (await store.Get(t => t.Id == taskId, ct).ConfigureAwait(false)).FirstOrDefault();

        return row is null || row.NextRunUtc is null;
    }

    /// <summary>The sentence a monitoring subscriber reads for one schedule change.</summary>
    private static string Describe(ScheduleUpdateResult result, (int Count, bool IsExact) backlog)
    {
        var sentence = string.Create(CultureInfo.InvariantCulture,
            $"Schedule {result.TaskId} rescheduled from version {result.PreviousScheduleVersion} to version {result.ScheduleVersion} ({result.Mode}): cursor {result.PreviousNextRunUtc:O} -> {result.NextRunUtc:O}");

        if (result.ReleasedHalt)
            sentence += "; the catch-up halt was released";

        if (backlog.Count == 0)
            return sentence;

        var count = backlog.IsExact
                        ? backlog.Count.ToString(CultureInfo.InvariantCulture)
                        : string.Create(CultureInfo.InvariantCulture, $"at least {backlog.Count}");

        return sentence + string.Create(CultureInfo.InvariantCulture, $"; {count} due slot(s) were discarded");
    }

    /// <summary>The schedule row's own executor, carrying the definition and the version just written.</summary>
    private static async Task<TaskHandlerExecutor> BuildScheduleExecutorAsync(
        IServiceProvider provider, RecoveredTask recovered, QueuedTask row, RecurringTask definition,
        ScheduleUpdateResult result) =>
        await Dispatcher.CreateCachedWrapper(recovered.Task!.GetType())
                        .Handle(recovered.Task, result.NextRunUtc, definition, provider, recovered.AuditLevel,
                            row.Id, row.TaskKey, useLazyExecutor: true,
                            recovered.RowMetadata with
                            {
                                ScheduleVersion = result.ScheduleVersion,
                                RunNumber       = (row.CurrentRunCount ?? 0) + 1
                            })
                        .ConfigureAwait(false);

    /// <summary>The executor of an occurrence that already has a row.</summary>
    private static async Task<TaskHandlerExecutor> BuildOccurrenceExecutorAsync(IServiceProvider provider,
                                                                                QueuedTask row)
    {
        var recovered = RecoveredTaskFactory.FromRow(row);

        if (recovered.Task is null)
        {
            throw new InvalidOperationException(
                $"The payload of occurrence {row.Id} cannot be rebuilt, so requeuing it would only put a row " +
                $"nothing can deliver back in the queue{Because(recovered.PayloadError)}.");
        }

        return await Dispatcher.CreateCachedWrapper(recovered.Task.GetType())
                               .Handle(recovered.Task, row.ScheduledExecutionUtc, recurring: null, provider,
                                   recovered.AuditLevel, row.Id, taskKey: null, useLazyExecutor: true,
                                   recovered.RowMetadata)
                               .ConfigureAwait(false);
    }

    /// <summary>
    /// Drops a durable catch-up halt from the row's runtime state. Any schedule change releases it: the halt
    /// exists to stop a replay nobody asked for, and asking is exactly what these calls are.
    /// </summary>
    /// <remarks>
    /// The marker is the only thing the schedule half of that column carries, so clearing it clears the column.
    /// Unreadable JSON is left verbatim: it is not a halt, and rewriting what this build cannot read would
    /// destroy whatever wrote it.
    /// </remarks>
    private static string? ClearHalt(string? runtimeInfo, out bool released)
    {
        released = ScheduleRuntimeInfo.TryParse(runtimeInfo)?.Halted is not null;
        return released ? null : runtimeInfo;
    }

    private void Publish(TaskHandlerExecutor executor, SeverityLevel severity, string message,
                         Exception? exception = null)
    {
        if (workerExecutor is not { HasEventSubscribers: true } worker)
            return;

        worker.PublishExternalEvent(executor, severity, message, exception);
    }

    private ITaskStorage RequireStorage() =>
        storage ?? throw new NotSupportedException(
            "Runtime schedule management needs persistence: a schedule is a row, and this call changes it. " +
            "Register a storage provider (AddMemoryStorage, AddSqlServerStorage, …) before using " +
            $"{nameof(ITaskScheduleManager)}.");

    private ITaskStorage RequireVersionedStorage()
    {
        var store = RequireStorage();

        if (!store.SupportsScheduleVersioning)
        {
            throw new NotSupportedException(
                $"The registered storage does not implement schedule versioning ({store.GetType().Name}." +
                $"{nameof(ITaskStorage.SupportsScheduleVersioning)} is false). Without the compare-and-swap a " +
                "reschedule could report success while a run finishing at the same moment overwrote it, so it " +
                "is refused rather than emulated.");
        }

        return store;
    }

    private static InvalidOperationException NoSuchSchedule(string taskKey) =>
        new($"There is no task with key '{taskKey}'. A schedule is addressed by the taskKey it was dispatched " +
            "with.");

    private static InvalidOperationException NotASchedule(string taskKey) =>
        new($"The task with key '{taskKey}' is a one-shot, not a recurring schedule.");

    /// <summary>The reason clause of a failure message, when there is a reason to give.</summary>
    private static string Because(Exception? error) => error is null ? "" : $": {error.Message}";
}
