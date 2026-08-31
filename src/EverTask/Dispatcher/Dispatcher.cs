using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using EverTask.Configuration;
using EverTask.RateLimiting;
using EverTask.Scheduler.Occurrences;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Dispatcher;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the Mediator.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/Mediator.cs

/// <inheritdoc />
public class Dispatcher(
    IServiceProvider serviceProvider,
    IWorkerQueueManager queueManager,
    IScheduler scheduler,
    EverTaskServiceConfiguration serviceConfiguration,
    IEverTaskLogger<Dispatcher> logger,
    IWorkerBlacklist workerBlacklist,
    ICancellationSourceProvider cancellationSourceProvider,
    ITaskStorage? taskStorage = null) : ITaskDispatcherInternal
{
    // Cache for compiled TaskHandlerWrapper constructors to avoid reflection overhead
    private static readonly ConcurrentDictionary<Type, Func<TaskHandlerWrapper>> WrapperFactoryCache = new();

    // Per-taskKey critical section: serializes the GetByTaskKey -> decide -> Persist/Update of one taskKey, so
    // two concurrent dispatches can never both insert (or one delete under the other). It lives in the
    // container because a runtime reschedule is the same read-decide-write over the same row and its
    // UpdateTask carries no schedule version, so only a shared section keeps one from overwriting the other.
    private readonly TaskKeyLockRegistry _fallbackTaskKeyLocks = new();
    private TaskKeyLockRegistry? _taskKeyLocks;

    // Resolved lazily: optional components (registered by AddEverTask, may be absent in
    // hand-wired unit-test providers)
    private IGateInvalidationRegistry? _gateInvalidation;
    private bool _gateInvalidationResolved;
    private RateLimitParkingLot? _parkingLot;
    private bool _parkingLotResolved;
    private TaskDeliveryRegistry? _deliveryRegistry;
    private bool _deliveryRegistryResolved;
    private IScheduleEvaluator? _evaluator;
    private TimeProvider? _timeProvider;
    private ScheduleVersionRegistry? _scheduleVersions;
    private bool _scheduleVersionsResolved;
    private OccurrenceProviderRegistry? _providers;
    private bool _providersResolved;
    private ScheduleCalendarRegistry? _calendars;
    private bool _calendarsResolved;
    private OccurrenceProviderRetryRegistry? _providerRetries;
    private bool _providerRetriesResolved;

    /// <summary>
    /// The single seam for every question about the occurrence grid. Falls back to the built-in evaluator
    /// for hand-wired providers that never registered one.
    /// </summary>
    private IScheduleEvaluator Evaluator =>
        _evaluator ??= serviceProvider.GetService<IScheduleEvaluator>() ?? ScheduleEvaluator.Default;

    /// <summary>The scheduling clock. Falls back to the real clock outside a configured container.</summary>
    private TimeProvider Clock => _timeProvider ??= serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>
    /// The shared per-taskKey critical section. The private fallback keeps a hand-wired dispatcher — one built
    /// outside <c>AddEverTask</c> — serializing its own dispatches exactly as it always did.
    /// </summary>
    private TaskKeyLockRegistry TaskKeyLocks =>
        _taskKeyLocks ??= serviceProvider.GetService<TaskKeyLockRegistry>() ?? _fallbackTaskKeyLocks;

    private TaskDeliveryRegistry? DeliveryRegistry
    {
        get
        {
            if (!_deliveryRegistryResolved)
            {
                _deliveryRegistry         = serviceProvider.GetService<TaskDeliveryRegistry>();
                _deliveryRegistryResolved = true;
            }

            return _deliveryRegistry;
        }
    }

    private IGateInvalidationRegistry? GateInvalidation
    {
        get
        {
            if (!_gateInvalidationResolved)
            {
                _gateInvalidation         = serviceProvider.GetService<IGateInvalidationRegistry>();
                _gateInvalidationResolved = true;
            }

            return _gateInvalidation;
        }
    }

    private ScheduleVersionRegistry? ScheduleVersions
    {
        get
        {
            if (!_scheduleVersionsResolved)
            {
                _scheduleVersions         = serviceProvider.GetService<ScheduleVersionRegistry>();
                _scheduleVersionsResolved = true;
            }

            return _scheduleVersions;
        }
    }

    /// <summary>
    /// The occurrence providers this host registered, or null in a hand-wired container that never called
    /// <c>AddEverTask</c>. A schedule that names one is refused there rather than dispatched blind.
    /// </summary>
    private OccurrenceProviderRegistry? Providers
    {
        get
        {
            if (!_providersResolved)
            {
                _providers         = serviceProvider.GetService<OccurrenceProviderRegistry>();
                _providersResolved = true;
            }

            return _providers;
        }
    }

    private ScheduleCalendarRegistry? Calendars
    {
        get
        {
            if (!_calendarsResolved)
            {
                _calendars = serviceProvider.GetService<ScheduleCalendarRegistry>();
                _calendarsResolved = true;
            }

            return _calendars;
        }
    }

    /// <summary>
    /// The consecutive provider failures this host is holding per schedule. Read only to FORGET a schedule
    /// that will not ask again, whose entry would otherwise outlive the process's interest in it.
    /// </summary>
    private OccurrenceProviderRetryRegistry? ProviderRetries
    {
        get
        {
            if (!_providerRetriesResolved)
            {
                _providerRetries         = serviceProvider.GetService<OccurrenceProviderRetryRegistry>();
                _providerRetriesResolved = true;
            }

            return _providerRetries;
        }
    }

    private RateLimitParkingLot? ParkingLot
    {
        get
        {
            if (!_parkingLotResolved)
            {
                _parkingLot         = serviceProvider.GetService<RateLimitParkingLot>();
                _parkingLotResolved = true;
            }

            return _parkingLot;
        }
    }

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, null, null, null, cancellationToken, null, taskKey, auditLevel);

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, TimeSpan executionDelay, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, executionDelay, cancellationToken, null, taskKey, auditLevel);

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, DateTimeOffset executionTime, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, executionTime, null, null, cancellationToken, null, taskKey, auditLevel);

    /// <inheritdoc />
    public async Task<Guid> Dispatch(IEverTask task, Action<IRecurringTaskBuilder> recurring, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken cancellationToken = default)
    {
        // The builder resolves RunNow and validates RunUntil on the scheduling clock, not the wall clock.
        var builder = new RecurringTaskBuilder(Clock);
        recurring(builder);

        ScheduleTimeZone.ApplyDefault(builder.RecurringTask, serviceConfiguration.DefaultScheduleTimeZoneId);

        return await ExecuteDispatch(task, null, builder.RecurringTask, null, cancellationToken, null, taskKey, auditLevel).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Cancel(Guid taskId, CancellationToken cancellationToken = default)
    {
        // Blacklist FIRST, before persisting Cancelled: a concurrent enqueue (scheduler slot / gate) racing
        // this Cancel must see the blacklist and be discarded, instead of slipping through the (still-false)
        // check and writing SetQueued over the Cancelled status we are about to persist.
        workerBlacklist.Add(taskId);

        // Must not abort the rest of the cleanup if the CTS was already disposed.
        cancellationSourceProvider.CancelTokenForTask(taskId);

        // Drop any occurrence still parked in the scheduler so it isn't even dispatched.
        scheduler.TryUnschedule(taskId);

        // Invalidate any rate-limit gate operation in flight for this task: a deferral being re-parked
        // concurrently with this Cancel must not survive it. The parking-lot entry is released too
        // (a cancelled parked task never re-enters a channel).
        GateInvalidation?.Invalidate(taskId);
        ParkingLot?.Remove(taskId);

        // A schedule that will not run again stops being a version this process publishes a lower bound for,
        // and one entry left behind per cancelled schedule is the only way the registry could grow without
        // bound. Its provider backoff goes the same way: nothing will ever answer and clear it.
        ScheduleVersions?.Remove(taskId);
        ProviderRetries?.Forget(taskId);

        // Persist Cancelled LAST so it is the final write of the cancel: any SetQueued a racing enqueue
        // managed to issue before the blacklist took effect is overwritten by Cancelled.
        if (taskStorage == null)
            return;

        // A schedule that owns occurrences is cancelled together with them, in one transaction: no occurrence
        // of a cancelled series is left waiting in a queue, or waiting to be put back in one by the next
        // startup recovery. Everything else keeps the historical single status write, byte for byte.
        if (await OwnsPendingOccurrencesAsync(taskId).ConfigureAwait(false) == true)
        {
            await taskStorage.CancelSchedule(taskId, AuditLevel.ErrorsOnly, cancellationToken).ConfigureAwait(false);
            return;
        }

        await taskStorage.SetCancelledByUser(taskId, AuditLevel.ErrorsOnly).ConfigureAwait(false);

        // Asked a SECOND time, and this is the one that closes the race: a materializer that had claimed the
        // schedule row before the first read had not inserted its occurrence yet, so that read answered "no
        // occurrences" and left a live occurrence under a cancelled series. By the time the write above is
        // committed the materialization is decided either way — both write the schedule row, so they
        // serialize on it — and every one starting from now on is refused by the status it reads.
        //
        // A read that FAILED cascades too: "the query threw" is not an answer that lets a cancel declare the
        // series terminal, and a cascade over a schedule with no occurrence is just the parent's own write.
        if (await OwnsPendingOccurrencesAsync(taskId).ConfigureAwait(false) != false)
            await taskStorage.CancelSchedule(taskId, AuditLevel.ErrorsOnly, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this row still has occurrences that a cancel has to terminalize with it, or <c>null</c> when
    /// the lookup itself failed and there is no answer.
    /// </summary>
    /// <remarks>
    /// The question is asked of the persisted RELATIONSHIP — the rows that name this one as their schedule —
    /// and never of the definition: a schedule whose JSON no longer parses, or one a task-key re-registration
    /// turned inline, still owns every occurrence it had already created.
    /// <para>
    /// It does NOT take the caller's token and it never propagates: this classification sits in front of the
    /// status write that has always been the cancel's last act, and it must not become a new way for that
    /// write not to happen. It reports a failed lookup instead of deciding it.
    /// </para>
    /// </remarks>
    private async Task<bool?> OwnsPendingOccurrencesAsync(Guid taskId)
    {
        if (taskStorage is not { SupportsDurableOccurrences: true } storage)
            return false;

        try
        {
            var occurrences = await storage.GetOccurrences(taskId, nonTerminalOnly: true, CancellationToken.None)
                                           .ConfigureAwait(false);

            return occurrences.Length > 0;
        }
        catch (Exception e)
        {
            logger.OccurrenceLookupForCancelFailed(e, taskId);
            return null;
        }
    }

    /// <summary>
    /// Refuses a durable schedule the storage cannot support, naming the missing half. Shared with the runtime
    /// schedule manager, which accepts a durable definition through the same door.
    /// </summary>
    internal static void RequireDurableOccurrenceSupport(ITaskStorage? taskStorage)
    {
        if (taskStorage is { SupportsDurableOccurrences: true })
            return;

        throw new NotSupportedException(
            taskStorage == null
                ? "Durable occurrences need persistence: every occurrence is a row. Register a storage " +
                  "provider (AddMemoryStorage, AddSqlServerStorage, …) before dispatching a schedule that " +
                  "uses WithDurableOccurrences, OnMisfire(FireOnce/CatchUp) or BackfillFrom."
                : "The registered storage does not implement the atomic durable-occurrence operations " +
                  $"({taskStorage.GetType().Name}.SupportsDurableOccurrences is false), and there is no " +
                  "non-atomic emulation to fall back to. Use a built-in provider, or implement them.");
    }

    /// <summary>
    /// Refuses a provider-driven schedule this host cannot run: an unregistered key, or a policy the provider
    /// does not support. Shared with the runtime schedule manager, which accepts a definition the same way.
    /// </summary>
    /// <remarks>
    /// The key check is also in the contextual schedule validation, which poisons
    /// a persisted row naming a key this build no longer registers; here it refuses the dispatch while the
    /// caller is still holding it. The determinism check is a DISPATCH-time gate and nothing else: it resolves
    /// the provider to ask it, and neither of the two things it can throw is an
    /// <see cref="OccurrenceProviderException"/>, so on the recovery path both would escape the transient catch
    /// and poison the series after a handful of restarts over a container that could not build the provider yet.
    /// </remarks>
    internal static async ValueTask RequireProviderSupportAsync(RecurringTask recurring,
                                                                OccurrenceProviderRegistry? providers,
                                                                bool isRecovery = false)
    {
        if (recurring.Provider is not { } provider)
            return;

        if (providers is null)
        {
            throw new ArgumentException(
                $"The schedule takes its occurrences from the provider '{provider.Key}', but this container " +
                "registers none. Register it with AddOccurrenceProvider<T>(key) on the EverTask builder.",
                nameof(recurring));
        }

        if (!providers.IsRegistered(provider.Key))
            throw providers.UnknownKey(provider.Key);

        if (isRecovery)
            return;

        if (recurring.Misfire is not { Policy: MisfirePolicy.CatchUp, OverflowPolicy: CatchUpOverflowPolicy.SkipOldest })
            return;

        if (!await providers.IsDeterministicAsync(provider.Key).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The catch-up policy of this schedule keeps only the most recent occurrences, which means " +
                $"finding where they begin by probing the grid — and the provider '{provider.Key}' does not " +
                $"declare {nameof(INextOccurrenceProvider.IsDeterministic)}, so probing it twice may answer " +
                "twice differently. Override IsDeterministic => true if the calendar really is stable, or use " +
                $"{nameof(CatchUpOverflowPolicy)}.{nameof(CatchUpOverflowPolicy.Halt)}.");
        }
    }

    /// <inheritdoc />
    public async Task<Guid> ExecuteDispatch(IEverTask task, CancellationToken ct = default, Guid? existingTaskId = null, string? taskKey = null) =>
        await ExecuteDispatch(task, null, null, null, ct, existingTaskId, taskKey, null).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Guid> ExecuteDispatch(IEverTask task, TimeSpan? executionDelay = null,
                                            CancellationToken ct = default,
                                            Guid? existingTaskId = null,
                                            string? taskKey = null,
                                            AuditLevel? auditLevel = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var executionTime = executionDelay != null
                                ? Clock.GetUtcNow().Add(executionDelay.Value)
                                : (DateTimeOffset?)null;

        return await ExecuteDispatch(task, executionTime, null, null, ct, existingTaskId, taskKey, auditLevel).ConfigureAwait(false);
    }

    /// <summary>
    /// The dispatch entry point as the previous release shipped it, kept BYTE-FOR-BYTE. Row metadata travels
    /// through the explicit <see cref="ITaskDispatcherInternal"/> implementation below instead of being
    /// appended here, where a new parameter would have replaced this method's IL signature.
    /// </summary>
    public Task<Guid> ExecuteDispatch(IEverTask task, DateTimeOffset? executionTime = null,
                                      RecurringTask? recurring = null, int? currentRun = null,
                                      CancellationToken ct = default, Guid? existingTaskId = null,
                                      string? taskKey = null, AuditLevel? auditLevel = null,
                                      bool isRecovery = false) =>
        ExecuteDispatchCore(task, executionTime, recurring, currentRun, ct, existingTaskId, taskKey, auditLevel,
            isRecovery, DispatchRowMetadata.None);

    Task<Guid> ITaskDispatcherInternal.ExecuteDispatch(IEverTask task, DateTimeOffset? executionTime,
                                                       RecurringTask? recurring, int? currentRun,
                                                       CancellationToken ct, Guid? existingTaskId, string? taskKey,
                                                       AuditLevel? auditLevel, bool isRecovery,
                                                       DispatchRowMetadata rowMetadata) =>
        ExecuteDispatchCore(task, executionTime, recurring, currentRun, ct, existingTaskId, taskKey, auditLevel,
            isRecovery, rowMetadata);

    private async Task<Guid> ExecuteDispatchCore(IEverTask task, DateTimeOffset? executionTime,
                                                 RecurringTask? recurring, int? currentRun,
                                                 CancellationToken ct, Guid? existingTaskId, string? taskKey,
                                                 AuditLevel? auditLevel, bool isRecovery,
                                                 DispatchRowMetadata rowMetadata)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Every path that accepts a schedule validates it, and this is the one the fluent builder does not go
        // through — a definition handed to the public dispatch entry point directly. Without it a corrupt
        // interval, or an OccurrenceMode outside the defined values, is read as valid all the way down to
        // IsScheduleOnly, which only ever compares against Durable. The registry is handed over so an
        // unregistered provider key is refused here too, with the key in the message.
        recurring?.Validate(new ScheduleValidationContext(Providers, Calendars));

        // A durable schedule needs the atomic occurrence operations, and there is no half-atomic emulation to
        // degrade to: a storage without them would leave occurrences without their cursor advance, or the
        // reverse. Refused HERE, where the caller is still holding the dispatch, instead of at the first
        // materialization hours later on a background thread.
        if (recurring is { OccurrenceMode: OccurrenceMode.Durable })
            RequireDurableOccurrenceSupport(taskStorage);

        if (recurring != null)
            await RequireProviderSupportAsync(recurring, Providers, isRecovery).ConfigureAwait(false);

        // Serialize the read-decide-write of this taskKey against concurrent dispatches (held for the
        // whole dispatch, including the enqueue). No-op when there is no taskKey, no storage, or the id
        // is already known (recovery / internal re-dispatch).
        using var taskKeyLock = await AcquireTaskKeyLockAsync(taskKey, existingTaskId, ct).ConfigureAwait(false);

        // ONE reading of the scheduling clock for the whole dispatch, taken AFTER the wait for the lock so
        // it reflects the moment the decision is actually made: everything below (preserved cursor, grace
        // window, skip-forward, immediate-vs-parked) must judge against the same instant, or a slow dispatch
        // can classify the same occurrence two different ways.
        var nowUtc = Clock.GetUtcNow();

        // Track existing task's NextRunUtc and CurrentRunCount for recurring tasks to preserve schedule across restarts
        DateTimeOffset? existingNextRunUtc = null;
        int? existingCurrentRunCount = null;

        // Read in the SAME breath as the cursor above, and never re-read: together with the schedule version
        // carried by the row metadata, this is the compare-and-swap expectation of the exhausted-series
        // finalization below. Reading it back after deciding would fold a Cancel (or a reschedule) that
        // linearized in between into the expectation, confirming the concurrent state instead of losing to it.
        QueuedTaskStatus? existingStatus = null;

        // The schedule this dispatch is reviving, when it is re-registering one a cancel had ended. Acted on
        // after the row carries the new definition, never before: see RestoreCancelledSchedule below.
        Guid? revivedSchedule = null;

        // Handle taskKey resolution if provided
        if (!string.IsNullOrWhiteSpace(taskKey) && taskStorage != null && existingTaskId == null)
        {
            var existingTask = await taskStorage.GetByTaskKey(taskKey, ct).ConfigureAwait(false);

            if (existingTask != null)
            {
                logger.FoundExistingTaskByKey(taskKey, existingTask.Id, existingTask.Status);

                // An IMMEDIATE one-shot re-dispatch of a row whose delivery is already in flight (in a
                // channel or executing) would either lose the new payload (the in-flight delivery already
                // captured the old one) or, on a terminal Remove, delete the row under the live delivery
                // (double execution). A delayed or recurring re-dispatch parks a fresh occurrence in the
                // scheduler, so it is left to proceed.
                if (executionTime == null && recurring == null && DeliveryRegistry?.IsDelivering(existingTask.Id) == true)
                {
                    logger.DispatchDiscardedDeliveryInFlight(taskKey, existingTask.Id);
                    return existingTask.Id;
                }

                // A recurring row must not be converted to a one-shot by a taskKey re-dispatch that carries
                // no recurring config — that would silently destroy its schedule and history.
                if (existingTask.IsRecurring && recurring == null)
                {
                    logger.DispatchDiscardedRecurringToOneShot(taskKey, existingTask.Id);
                    return existingTask.Id;
                }

                // For RECURRING tasks: preserve history and don't remove/recreate on terminal status
                // Recurring tasks with Completed/Failed status are not truly "terminated" - they should continue
                if (existingTask.IsRecurring && recurring != null)
                {
                    // If task is in progress, return existing ID (cannot modify running task)
                    if (existingTask.Status is QueuedTaskStatus.InProgress)
                    {
                        logger.DispatchDiscardedRecurringInProgress(taskKey, existingTask.Id);
                        return existingTask.Id;
                    }

                    // For all other statuses (including Completed/Failed): update, don't remove
                    logger.UpdatingRecurringTask(existingTask.Id);
                    existingTaskId = existingTask.Id;

                    // Including Cancelled, which is the documented way to restart a cancelled series. A
                    // recurring row keeps its id, and with it the cancellation's blacklist entry (about an
                    // hour, dropping every delivery the new registration produces) and the Cancelled status
                    // itself, which UpdateTask never rewrites and no recovery predicate selects.
                    if (existingTask.Status is QueuedTaskStatus.Cancelled)
                        revivedSchedule = existingTask.Id;

                    // The run counter is preserved whether or not the row still has a cursor: storage keeps
                    // counting from it, so a TERMINAL series re-registered under the same taskKey resumes at
                    // CurrentRunCount + 1. Reading it only in the cursor branch below would hand the handler
                    // a RunNumber of 1 for the run storage is about to record as the sixth.
                    existingCurrentRunCount = existingTask.CurrentRunCount;

                    // The schedule version belongs to the ROW and UpdateTask never writes that column, so the
                    // version the row is at is still the version this dispatch runs; left at its default it
                    // would tell the handler's context, and every monitoring event of the delivery, that a
                    // rescheduled series is back at version 0. It is also the compare-and-swap expectation of
                    // the finalization below, which is why it is read here with the rest of the row.
                    //
                    // The runtime state travels the same way, because UpdateTask DOES write that column: a
                    // plain re-registration hands the row's own value back, so a standing catch-up halt
                    // survives it — re-declaring your schedules at startup is not a request to replay. A
                    // REVIVAL clears it instead: the halt belongs to the series the cancel ended, and carrying
                    // it into the one being started here parks a schedule that materializes nothing.
                    rowMetadata = rowMetadata with
                    {
                        ScheduleVersion = existingTask.ScheduleVersion,
                        RuntimeInfo     = revivedSchedule == null ? existingTask.RuntimeInfo : null
                    };

                    // Preserve existing NextRunUtc (even if in the past) to maintain schedule rhythm
                    if (existingTask.NextRunUtc.HasValue)
                    {
                        existingNextRunUtc = existingTask.NextRunUtc;
                        existingStatus = existingTask.Status;
                        logger.PreservingRecurringSchedule(existingNextRunUtc, existingCurrentRunCount, existingTask.Id);
                    }
                }
                // For NON-RECURRING tasks: original behavior
                else
                {
                    // If task is terminated (Completed/Failed/Cancelled), remove it and create new
                    if (existingTask.Status is QueuedTaskStatus.Completed
                        or QueuedTaskStatus.Failed
                        or QueuedTaskStatus.Cancelled
                        or QueuedTaskStatus.ServiceStopped)
                    {
                        logger.RemovingTerminatedTask(existingTask.Id);
                        await taskStorage.Remove(existingTask.Id, ct).ConfigureAwait(false);

                        // Removal is the third end of a schedule, beside a terminal series and a cancellation,
                        // and this is the only place the library deletes a row: a published version whose row
                        // no longer exists is a lower bound nothing can ever clear.
                        ScheduleVersions?.Remove(existingTask.Id);
                        ProviderRetries?.Forget(existingTask.Id);
                    }
                    // If task is in progress, return existing ID (cannot modify running task)
                    else if (existingTask.Status is QueuedTaskStatus.InProgress)
                    {
                        logger.DispatchDiscardedTaskInProgress(taskKey, existingTask.Id);
                        return existingTask.Id;
                    }
                    // If task is pending (Queued/WaitingQueue/Pending), update it
                    else
                    {
                        logger.UpdatingPendingTask(existingTask.Id);
                        existingTaskId = existingTask.Id;
                    }
                }
            }
        }

        DateTimeOffset? nextRun = null;

        // Recovery re-dispatch of a recurring task: executionTime is the stored NextRunUtc
        // (or ScheduledExecutionUtc). Treat it as the preserved next occurrence, exactly like
        // the taskKey path: using it as a bare recalculation base would compute the occurrence
        // strictly AFTER it, skipping one occurrence at every restart (and killing the last
        // occurrence before RunUntil).
        if (isRecovery && recurring != null && existingNextRunUtc == null && executionTime.HasValue)
        {
            existingNextRunUtc      = executionTime;
            existingCurrentRunCount = currentRun;
            // The recovery page IS the read this decision is computed from, so its status — and the version
            // already on the row metadata — are the finalization's expectations. A row metadata without them
            // (a hand-wired internal re-dispatch) leaves the status null, and the finalization below falls
            // back to the unconditional write.
            existingStatus          = rowMetadata.Status;
        }

        if (recurring != null)
        {
            // Who is asking, in the terms a provider is given them: the row when there is one, the key that
            // outlives it either way, and the run this dispatch is about to be.
            var identity = new ScheduleIdentity(existingTaskId ?? Guid.Empty, taskKey,
                (existingCurrentRunCount ?? currentRun ?? 0) + 1);

            RecurringRunDecision decision;
            var recordedAdvance = isRecovery && existingNextRunUtc is { } retainedCursor &&
                                  ScheduleRuntimeInfo.TryParse(rowMetadata.RuntimeInfo)
                                                     ?.IsExclusionAdvanceRetry(retainedCursor,
                                                         existingCurrentRunCount ?? currentRun ?? 0) == true;

            try
            {
                decision = await DecideRecurringRunAsync(recurring, isRecovery, existingNextRunUtc,
                                   existingCurrentRunCount, executionTime, existingTaskId, currentRun, nowUtc,
                                   identity, recordedAdvance, ct)
                               .ConfigureAwait(false);
            }
            catch (OccurrenceProviderException failure) when (isRecovery && existingTaskId is { } scheduleId)
            {
                // The calendar this schedule reads could not answer, which is transient by contract — the
                // application's own database being briefly down must not end a series. Nothing is written, so
                // the row keeps its cursor and stays recoverable, and the schedule is parked to ask again
                // after the backoff. The provider's own exception is caught HERE and never rethrown: the
                // recovery's generic catch counts a failure against the poison budget, so a source down
                // across five restarts would otherwise mark the series Failed for ever.
                await ParkProviderRetryAsync(task, recurring, failure, scheduleId, taskKey, auditLevel,
                        rowMetadata, nowUtc)
                    .ConfigureAwait(false);

                // And the caller is told it was DEFERRED rather than dispatched: answering with the schedule
                // id reads as a dispatch that worked, and clears a recovery-failure counter a previous restart
                // had really earned. An outage of its calendar proves nothing about this row.
                throw new ScheduleDeferredByProviderException(scheduleId, failure);
            }

            if (decision.SeriesExhausted)
            {
                // A legitimately exhausted series: every remaining occurrence falls past RunUntil, so there is
                // nothing left to run. Finalize the recovered row terminally (Completed, NextRunUtc cleared, no
                // extra run counted) and do NOT re-dispatch. Throwing here would turn a normal end-of-series
                // into a recovery error — and, combined with a stale NextRunUtc, a per-restart poison (the row
                // keeps coming back recoverable). Fail-fast on a genuinely malformed expression stays on the
                // new-task path inside the decision.
                logger.RecoverySeriesExhausted(existingTaskId);

                // A CANCELLED series is already terminal and is never rewritten to Completed. Naming Cancelled
                // as the compare-and-swap expectation makes the guard MATCH, so the conditional write would
                // erase the cancellation — and with it the one status a later re-dispatch under this key needs
                // to see. Same exclusion as QueuedTask.IsRecurringSeriesToFinalize.
                if (existingStatus is QueuedTaskStatus.Cancelled)
                {
                    logger.ExhaustedSeriesLeftCancelled(existingTaskId);
                }
                else if (taskStorage != null && existingTaskId.HasValue && existingNextRunUtc.HasValue)
                {
                    await FinalizeExhaustedSeriesAsync(taskStorage, existingTaskId.Value,
                            existingNextRunUtc.Value, existingStatus, rowMetadata.ScheduleVersion,
                            auditLevel ?? serviceConfiguration.DefaultAuditLevel, ct)
                        .ConfigureAwait(false);
                }

                return existingTaskId ?? Guid.Empty;
            }

            nextRun       = decision.NextRun;
            executionTime = nextRun;
        }

        var taskType = task.GetType();

        var handler = CreateCachedWrapper(taskType);

        // Use provided audit level or fall back to global default
        var effectiveAuditLevel = auditLevel ?? serviceConfiguration.DefaultAuditLevel;

        // Lazy executors never carry a handler instance: the wrapper resolves one in a
        // short-lived scope for metadata extraction only, and the worker resolves a fresh
        // instance in its per-task scope at execution time
        var useLazyExecutor = ShouldUseLazyResolution(executionTime, recurring, nowUtc);

        // The run this delivery is about to be: the durable counter (preserved by a taskKey update, carried in
        // by recovery, absent on a brand new task) plus one, because the counter only moves once a run ends.
        // An occurrence arrives with its own number already read from the row and keeps it: its counter is the
        // one-shot's, which says nothing about the run of the series the occurrence is.
        var executor = await handler.Handle(task, executionTime, recurring, serviceProvider, effectiveAuditLevel,
                                       existingTaskId, taskKey, useLazyExecutor,
                                       rowMetadata with
                                       {
                                           RunNumber = rowMetadata.RunNumber
                                                       ?? (existingCurrentRunCount ?? currentRun ?? 0) + 1
                                       })
                                   .ConfigureAwait(false);

        // Persist or update task (lazy serialize only if storage exists).
        // Recovery dispatches skip the update entirely: the definition was just read from storage
        // unchanged, and rewriting it could overwrite a concurrent live re-registration via taskKey
        // (lost update). Recalculated schedule data is re-derived deterministically at the next
        // restart and persisted by UpdateCurrentRun after each run.
        if (taskStorage != null && !(isRecovery && existingTaskId != null))
        {
            var taskEntity = executor.ToQueuedTask(nowUtc);

            try
            {
                if (existingTaskId == null)
                {
                    // New task - persist it
                    logger.PersistingTask(taskEntity.Type);
                    await taskStorage.Persist(taskEntity, ct).ConfigureAwait(false);
                }
                else
                {
                    // Existing task - update it
                    logger.UpdatingTask(taskEntity.Type);
                    await taskStorage.UpdateTask(taskEntity, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                // A concurrent insert may have won the taskKey unique constraint (cross-process, or any
                // path that bypassed the in-process keyed lock): re-read the winner and return its id
                // instead of proceeding with our own duplicate PersistenceId.
                if (existingTaskId == null && !string.IsNullOrWhiteSpace(taskKey))
                {
                    var winner = await taskStorage.GetByTaskKey(taskKey, ct).ConfigureAwait(false);
                    if (winner != null && winner.Id != taskEntity.Id)
                    {
                        logger.TaskKeyWonByConcurrentDispatch(taskKey, winner.Id);
                        return winner.Id;
                    }
                }

                logger.UnableToPersistTask(e, existingTaskId == null ? "persist" : "update", task.GetType());
                if (serviceConfiguration.ThrowIfUnableToPersist)
                    throw;
            }
        }

        // The row now carries the new definition, so the series can be brought back to life. In this order:
        // a revival that preceded the write would leave a live row on the definition the cancel ended if the
        // write then failed.
        if (revivedSchedule is { } revived)
        {
            if (await RestoreCancelledSchedule(revived, taskKey!, rowMetadata.ScheduleVersion, effectiveAuditLevel,
                        executor, ct)
                    .ConfigureAwait(false) is not { } revivedVersion)
            {
                // The row is still terminally Cancelled and still covered by the cancel's entry, so there is
                // nothing to park over it: a registration made here is refused at its first fire and consumed.
                return executor.PersistenceId;
            }

            // The generation this dispatch runs is the one the ROW now carries: it is what every defence the
            // old deliveries are measured against reads, from the pre-gate drop to the advance's
            // compare-and-swap to the conditional re-park.
            executor = executor with { ScheduleVersion = revivedVersion };
        }

        // The executor is already lazy when useLazyExecutor is true (built by the wrapper
        // without a handler instance), eager otherwise
        var executorToSchedule = executor;

        if (executorToSchedule.ExecutionTime > nowUtc || recurring != null)
        {
            scheduler.Schedule(executorToSchedule, nextRun);

            // Published only once the registration is really in, exactly as a reschedule publishes: the lower
            // bound drops every delivery of the definition the cancel ended that is still sitting in a queue,
            // and publishing one whose executor never reached the scheduler would drop them with nothing left
            // to take their place.
            if (revivedSchedule is { } published)
                ScheduleVersions?.Publish(published, executorToSchedule.ScheduleVersion);
        }
        else
        {
            // Immediate re-dispatch of an existing task (e.g. taskKey update of a previously delayed
            // task): invalidate any stale registration still parked in the scheduler — or in the
            // dequeue->re-park gate limbo the scheduler cannot see — BEFORE enqueuing, so a concurrent
            // re-park of the SAME id (e.g. a rate-limit deferral of the new delivery) is created
            // afterwards and survives the invalidation.
            InvalidateStaleRegistration(existingTaskId, executorToSchedule.PersistenceId);

            // Determine queue name with automatic routing for recurring tasks
            var queueName = executorToSchedule.QueueName ??
                            (executorToSchedule.RecurringTask != null ? QueueNames.Recurring : QueueNames.Default);

            if (isRecovery)
            {
                // Recovery path: never fail fast, wait for queue space (consumers are draining concurrently)
                await queueManager.EnqueueBlocking(queueName, executorToSchedule, ct).ConfigureAwait(false);
            }
            else if (existingTaskId != null)
            {
                try
                {
                    await queueManager.TryEnqueue(queueName, executorToSchedule, ct).ConfigureAwait(false);
                }
                catch
                {
                    // The immediate enqueue of an already-accepted (parked) task failed (full
                    // ThrowException queue, or a cancelled Wait that threw). Its parked occurrence was
                    // just dropped above, so re-schedule it for retry instead of losing it / leaking its
                    // parking-lot reservation, then propagate the failure.
                    scheduler.Schedule(executorToSchedule with { ExecutionTime = Clock.GetUtcNow() });
                    throw;
                }
            }
            else
            {
                await queueManager.TryEnqueue(queueName, executorToSchedule, ct).ConfigureAwait(false);
            }
        }

        return executor.PersistenceId;
    }

    /// <summary>
    /// Immediate re-dispatch of an existing task: drop any registration still parked in the scheduler —
    /// or in the dequeue→re-park gate limbo the scheduler cannot see — so a stale occurrence cannot fire
    /// a duplicate. Done BEFORE the enqueue so a re-park of the same id (rate-limit deferral) survives.
    /// </summary>
    private void InvalidateStaleRegistration(Guid? existingTaskId, Guid persistenceId)
    {
        if (existingTaskId == null)
            return;

        scheduler.TryUnschedule(persistenceId);
        GateInvalidation?.Invalidate(persistenceId);
    }

    /// <summary>
    /// Brings a schedule a cancel had ended back to life, because it has just been dispatched again under its
    /// own task key — the documented way to restart one ("a cancelled schedule cannot be rescheduled, it has
    /// to be dispatched again").
    /// </summary>
    /// <remarks>
    /// A recurring re-registration REUSES the row, so both halves of the cancellation follow it: the blacklist
    /// entry outlives the cancel by about an hour and drops every delivery the new registration produces, and
    /// <c>UpdateTask</c> never writes the status column, so the row stays <c>Cancelled</c> — a status no
    /// recovery predicate selects. Both are undone here and nowhere else.
    /// <para>
    /// The entry is not simply dropped: it is the only in-process cover the occurrences the cancel already
    /// terminalized have, so it is MOVED onto them first (see
    /// <see cref="CoverOccurrencesTheCancelEndedAsync"/>) — the schedule is alive again from here, those
    /// occurrences never will be.
    /// </para>
    /// <para>
    /// The un-cancel goes FIRST and it has to answer. Nothing polls behind it — the parked registration is
    /// refused at its first fire and no recovery predicate selects the row afterwards — so a failure is a log
    /// error plus a monitoring event, and it stops the dispatch instead of finishing it.
    /// </para>
    /// <para>
    /// The write also bumps the row's <see cref="QueuedTask.ScheduleVersion"/>: an in-flight delivery of the
    /// old definition carries the very id the new registration does, so the blacklist cannot tell them apart
    /// and only the version can.
    /// </para>
    /// </remarks>
    /// <returns>The schedule version the row now carries, or null when nothing was revived.</returns>
    private async Task<int?> RestoreCancelledSchedule(Guid scheduleId, string taskKey, int expectedScheduleVersion,
                                                      AuditLevel auditLevel, TaskHandlerExecutor executor,
                                                      CancellationToken ct)
    {
        // A revival is decided from a persisted row, so there is always a store here: this is the nullable
        // field's guard, not a path.
        if (taskStorage is not { } store)
            return expectedScheduleVersion;

        bool       revived;
        Exception? failure = null;

        try
        {
            revived = await store.TryReviveCancelledSchedule(scheduleId, expectedScheduleVersion, auditLevel, ct)
                                 .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            revived = false;
            failure = e;
        }

        if (!revived)
        {
            logger.CancelledScheduleNotRestored(failure, scheduleId, taskKey);
            PublishRevivalFailedEvent(executor, failure, scheduleId, taskKey);

            if (serviceConfiguration.ThrowIfUnableToPersist)
            {
                throw new InvalidOperationException(
                    $"Schedule {scheduleId} was dispatched again under the task key '{taskKey}' but could not " +
                    "be taken out of Cancelled, so the series was not restarted", failure);
            }

            return null;
        }

        // Only now does the schedule's entry stop covering the occurrences the cancel already ended.
        await CoverOccurrencesTheCancelEndedAsync(scheduleId, ct).ConfigureAwait(false);

        workerBlacklist.Remove(scheduleId);

        logger.CancelledScheduleRedispatched(scheduleId, taskKey);

        // A storage without the compare-and-swap keeps the row at the version it had: the fallback un-cancels
        // and nothing reads a version there anyway (SupportsScheduleVersioning gates every defence that does).
        return store.SupportsScheduleVersioning ? expectedScheduleVersion + 1 : expectedScheduleVersion;
    }

    /// <summary>Tells a monitoring subscriber that a schedule a dispatch meant to restart is still cancelled.</summary>
    private void PublishRevivalFailedEvent(TaskHandlerExecutor executor, Exception? failure, Guid scheduleId,
                                           string taskKey)
    {
        if (serviceProvider.GetService<IEverTaskWorkerExecutor>() is not { HasEventSubscribers: true } worker)
            return;

        worker.PublishExternalEvent(executor, SeverityLevel.Error,
            string.Create(CultureInfo.InvariantCulture,
                $"Schedule {scheduleId} was dispatched again under the task key '{taskKey}' but could not be " +
                $"taken out of Cancelled: the series is not restarted, and no recovery brings back a " +
                $"cancelled row"), failure);
    }

    /// <summary>
    /// Gives each occurrence of <paramref name="scheduleId"/> that the cancel left <c>Cancelled</c>, and whose
    /// delivery is still in flight in this process, a blacklist entry of its OWN — before the schedule's entry
    /// stops covering it.
    /// </summary>
    /// <remarks>
    /// Past the enqueue boundary nothing re-reads the row: <c>SetInProgress</c> is unconditional, and all
    /// three cancellation checks are the blacklist. A delivery sitting in a channel, or waiting seconds at the
    /// rate-limit gate, would therefore run the OLD series' payload over a terminal <c>Cancelled</c> the
    /// moment the schedule's entry went. Everything BEFORE that boundary is safe without an entry, because the
    /// scheduler's enqueue goes through <c>TrySetQueuedIfRecoverable</c>.
    /// <para>
    /// The status is what decides, not the delivery: an occurrence the cancel found <c>InProgress</c> is
    /// allowed to finish, and covering it would suppress the completion of work that has already run its side
    /// effects. A lookup that FAILED covers them all instead — the cost of being wrong that way is a
    /// re-execution the at-least-once contract already covers.
    /// </para>
    /// </remarks>
    private async Task CoverOccurrencesTheCancelEndedAsync(Guid scheduleId, CancellationToken ct)
    {
        if (taskStorage == null || DeliveryRegistry?.OccurrencesOf(scheduleId) is not { Length: > 0 } inFlight)
            return;

        QueuedTask[] rows;

        try
        {
            rows = await taskStorage.Get(t => inFlight.Contains(t.Id), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller abandoned the dispatch: nothing is covered and nothing is uncovered either, because
            // the schedule's entry is dropped after this and never gets there.
            throw;
        }
        catch (Exception e)
        {
            foreach (var occurrenceId in inFlight)
                workerBlacklist.Add(occurrenceId);

            logger.OccurrencesOfRevivedScheduleLookupFailed(e, scheduleId, inFlight.Length);
            return;
        }

        var covered = 0;

        foreach (var row in rows)
        {
            if (row.Status != QueuedTaskStatus.Cancelled)
                continue;

            workerBlacklist.Add(row.Id);
            covered++;
        }

        if (covered > 0)
            logger.OccurrencesOfRevivedScheduleCovered(scheduleId, covered);
    }

    private ValueTask<IDisposable> AcquireTaskKeyLockAsync(string? taskKey, Guid? existingTaskId, CancellationToken ct)
    {
        // Only the taskKey resolution path needs serialization: no taskKey, no storage, or an
        // already-known id (recovery / internal re-dispatch) never reads/decides on a taskKey.
        if (taskStorage == null || existingTaskId != null)
            return TaskKeyLocks.AcquireAsync(null, ct);

        return TaskKeyLocks.AcquireAsync(taskKey, ct);
    }

    /// <summary>
    /// Determines if a task should use lazy handler resolution based on adaptive algorithm.
    /// </summary>
    /// <param name="executionTime">Scheduled execution time (null for immediate)</param>
    /// <param name="recurring">Recurring task configuration (null for one-time)</param>
    /// <param name="nowUtc">The dispatch's reading of the scheduling clock</param>
    /// <returns>True if task should use lazy mode, false for eager mode</returns>
    private bool ShouldUseLazyResolution(DateTimeOffset? executionTime, RecurringTask? recurring, DateTimeOffset nowUtc)
    {
        // Feature disabled globally
        if (!serviceConfiguration.UseLazyHandlerResolution)
        {
            logger.LazyResolutionDisabledGlobally();
            return false;
        }

        // Recurring tasks: adaptive based on interval
        if (recurring != null)
        {
            // A durable schedule row never runs a handler, so resolving one eagerly for it would build an
            // instance and a scope per slot only to dispose them untouched.
            if (recurring.IsDurable)
                return true;

            var minInterval = recurring.GetMinimumInterval(nowUtc);
            return minInterval >= TimeSpan.FromMinutes(5);
        }

        // Delayed tasks: lazy if delay >= 30 minutes
        if (executionTime.HasValue)
        {
            var delay = executionTime.Value - nowUtc;
            return delay >= TimeSpan.FromMinutes(30);
        }

        // Immediate tasks: lazy by default. An eager handler resolved at dispatch from the singleton
        // dispatcher's root provider is pinned in the root container's disposables list until shutdown;
        // the worker resolves and disposes a fresh instance per task anyway.
        return true;
    }

    /// <summary>Where a recurring dispatch stands: its next run, or the fact that the series is over.</summary>
    /// <param name="NextRun">The occurrence to park, meaningless when the series is exhausted.</param>
    /// <param name="SeriesExhausted">
    /// True when a RECOVERED series has no occurrence left inside its bounds: the caller finalizes the row
    /// instead of re-dispatching it.
    /// </param>
    private readonly record struct RecurringRunDecision(DateTimeOffset? NextRun, bool SeriesExhausted);

    /// <summary>
    /// The occurrence a recurring dispatch parks: the preserved cursor, the grace window, the skip-forward,
    /// the backfill start or the first run of a brand-new schedule.
    /// </summary>
    /// <remarks>
    /// Extracted whole so the ONE thing that can fail transiently here — a grid that comes from an
    /// <see cref="INextOccurrenceProvider"/>, which is real I/O — has a single boundary its caller can catch at.
    /// </remarks>
    private async Task<RecurringRunDecision> DecideRecurringRunAsync(
        RecurringTask recurring, bool isRecovery, DateTimeOffset? existingNextRunUtc, int? existingCurrentRunCount,
        DateTimeOffset? executionTime, Guid? existingTaskId, int? currentRun, DateTimeOffset nowUtc,
        ScheduleIdentity identity, bool resumeRecordedAdvance, CancellationToken ct)
    {
        if (resumeRecordedAdvance && existingNextRunUtc is { } retainedCursor)
        {
            var resumed = await Evaluator.CalculateNextValidRunAsync(
                    recurring, retainedCursor, existingCurrentRunCount ?? currentRun ?? 0, nowUtc,
                    isRecovery: true, computeSkippedCount: false, identity: identity, ct: ct)
                .ConfigureAwait(false);

            return new RecurringRunDecision(resumed.NextRun, resumed.NextRun == null);
        }

        if (recurring.Exclusions != null && existingNextRunUtc is { } storedCursor)
        {
            existingNextRunUtc = await Evaluator
                                      .NormalizeCursorAsync(recurring, storedCursor,
                                          existingCurrentRunCount ?? currentRun ?? 0, identity, ct)
                                      .ConfigureAwait(false);

            if (existingNextRunUtc is null)
                return new RecurringRunDecision(null, true);
        }

        // A DURABLE schedule's cursor belongs to the materializer, which owns every decision about a slot
        // that came due — the grace window, the skip-forward and the finalization are all the inline path's
        // answers to a misfire, and applying them here would silently consume the backlog the misfire policy
        // exists to replay. The row keeps the cursor it has; parking it at a past cursor simply fires it now.
        if (recurring.IsDurable && existingNextRunUtc.HasValue)
        {
            logger.UsingPreservedNextRun(existingNextRunUtc, existingTaskId);
            return new RecurringRunDecision(existingNextRunUtc, false);
        }

        // If we have a valid existing NextRunUtc from a task with TaskKey
        if (existingNextRunUtc.HasValue)
        {
            // If NextRunUtc is still in the future, use it directly
            if (existingNextRunUtc.Value > nowUtc)
            {
                logger.UsingPreservedNextRun(existingNextRunUtc, existingTaskId);
                return new RecurringRunDecision(existingNextRunUtc, false);
            }

            // On recovery, a pending occurrence that slipped into the past but is still the CURRENT one (the
            // next occurrence is not due yet) is executed instead of skipped — a short downtime across a
            // scheduled occurrence must not silently lose it. Calendar-exact: the real next occurrence, not
            // the flat GetMinimumInterval heuristic, which is wrong for OnDays/Month/Week (too narrow drops a
            // just-due slot; too wide runs a stale one).
            //
            // The successor is the NATURAL one, computed ignoring RunUntil/MaxRuns: the bounded successor is
            // null both when the slot is still current AND when the series has simply ended, and reading that
            // null as "current forever" executes a months-old slot at restart.
            if (isRecovery &&
                await IsSlipedOccurrenceStillCurrentAsync(recurring, existingNextRunUtc.Value, nowUtc, identity, ct)
                    .ConfigureAwait(false))
            {
                logger.RecoveryExecutingSlippedOccurrence(existingNextRunUtc, existingTaskId);
                return new RecurringRunDecision(existingNextRunUtc, false);
            }

            // NextRunUtc is (well) in the past - skip forward while preserving rhythm. On the
            // recovery path the initial-run config must NOT be re-applied.
            var recovered = await Evaluator.CalculateNextValidRunAsync(
                recurring,
                existingNextRunUtc.Value,
                existingCurrentRunCount ?? 0,
                nowUtc,
                isRecovery: isRecovery,
                identity: identity,
                ct: ct).ConfigureAwait(false);

            if (recovered.NextRun == null)
                return new RecurringRunDecision(null, true);

            logger.CalculatedNextRunFromPast(recovered.NextRun, existingTaskId, existingNextRunUtc,
                recovered.SkippedCount, recovered.SkippedCountIsExact);

            return new RecurringRunDecision(recovered.NextRun, false);
        }

        if (recurring.BackfillFromUtc is { } backfillFrom && !isRecovery)
        {
            // An explicit backfill starts the cursor in the past, on the first occurrence at or after the
            // instant the caller named. The replay it triggers is still bounded by the misfire policy's own
            // caps — this only decides where the schedule starts counting from.
            var backfilled = await Evaluator
                                   .FirstOccurrenceOnOrAfterAsync(recurring, backfillFrom.ToUniversalTime(),
                                       identity, ct)
                                   .ConfigureAwait(false);

            return new RecurringRunDecision(
                backfilled ?? throw new ArgumentException(
                    "The schedule has no occurrence at or after the requested backfill start.", nameof(recurring)),
                false);
        }

        // New task - calculate from current time
        var scheduledTime = (existingTaskId != null && executionTime.HasValue)
            ? executionTime.Value
            : nowUtc;

        var result = await Evaluator
                           .CalculateNextValidRunAsync(recurring, scheduledTime, currentRun ?? 0, nowUtc,
                               identity: identity, ct: ct)
                           .ConfigureAwait(false);

        if (result.NextRun == null)
            throw new ArgumentException("Invalid scheduler recurring expression", nameof(recurring));

        return new RecurringRunDecision(result.NextRun, false);
    }

    /// <summary>
    /// Parks a recovered schedule whose occurrence provider could not answer, so it asks again after the
    /// backoff instead of waiting for the next restart.
    /// </summary>
    /// <remarks>
    /// The registration is a SCHEDULE RETRY, not a delivery: when it fires, the worker re-reads the row and
    /// re-runs this very decision. Parking an ordinary delivery instead would execute a slot nobody has
    /// decided about yet — the grace window is exactly the question the provider did not answer.
    /// <para>
    /// It cannot fail silently: there is no poller behind it, so a re-park that throws leaves the series
    /// waiting for a restart and has to be said out loud, with the monitoring event beside the log line.
    /// </para>
    /// </remarks>
    private async Task ParkProviderRetryAsync(IEverTask task, RecurringTask recurring,
                                              OccurrenceProviderException failure, Guid scheduleId, string? taskKey,
                                              AuditLevel? auditLevel, DispatchRowMetadata rowMetadata,
                                              DateTimeOffset nowUtc)
    {
        var retryAt = nowUtc + failure.RetryAfter;

        await ProviderRetryParker
              .ParkAsync(scheduler, retryAt, BuildExecutorAsync,
                  static executor => executor with { IsScheduleRetry = true },
                  (_, at) => logger.ProviderRetryParkRefused(scheduleId, at),
                  (executor, at) =>
                  {
                      logger.RecoveryDeferredByProvider(failure, failure.ProviderKey, scheduleId,
                          failure.ConsecutiveFailures, at);
                      PublishProviderRetryEvent(executor, failure, scheduleId, at);
                  },
                  (executor, error) =>
                  {
                      logger.ProviderRetryParkFailed(error, scheduleId);
                      PublishProviderParkFailedEvent(executor, error, scheduleId, failure);
                  })
              .ConfigureAwait(false);

        async ValueTask<TaskHandlerExecutor?> BuildExecutorAsync() =>
            await CreateCachedWrapper(task.GetType())
                 .Handle(task, retryAt, recurring, serviceProvider,
                     auditLevel ?? serviceConfiguration.DefaultAuditLevel, scheduleId, taskKey,
                     useLazyExecutor: true, rowMetadata)
                 .ConfigureAwait(false);
    }

    /// <summary>Tells a monitoring subscriber that a schedule waiting on its provider is parked nowhere.</summary>
    private void PublishProviderParkFailedEvent(TaskHandlerExecutor? executor, Exception parkFailure,
                                                Guid scheduleId, OccurrenceProviderException failure)
    {
        if (executor is not { } target ||
            serviceProvider.GetService<IEverTaskWorkerExecutor>() is not { HasEventSubscribers: true } worker)
        {
            return;
        }

        worker.PublishExternalEvent(target, SeverityLevel.Error,
            string.Create(CultureInfo.InvariantCulture,
                $"Schedule {scheduleId} could not be parked to ask the occurrence provider " +
                $"'{failure.ProviderKey}' again: nothing was written and the series stays where it is " +
                $"until the next startup recovery"), parkFailure);
    }

    /// <summary>Tells a monitoring subscriber that a schedule is waiting on its provider, and for how long.</summary>
    private void PublishProviderRetryEvent(TaskHandlerExecutor executor, OccurrenceProviderException failure,
                                           Guid scheduleId, DateTimeOffset retryAt)
    {
        if (serviceProvider.GetService<IEverTaskWorkerExecutor>() is not { HasEventSubscribers: true } worker)
            return;

        worker.PublishExternalEvent(executor, SeverityLevel.Warning,
            string.Create(CultureInfo.InvariantCulture,
                $"Occurrence provider '{failure.ProviderKey}' could not answer for schedule {scheduleId} " +
                $"({failure.ConsecutiveFailures} consecutive failure(s)): nothing was written and the schedule " +
                $"is parked to ask again at {retryAt:O}"), failure);
    }

    /// <summary>
    /// Recovery grace window: true when the stored, already-past slot is still the one to run.
    /// </summary>
    /// <remarks>
    /// Decided on the NATURAL successor (termination bounds ignored), so the window is exactly one period —
    /// a minute for a per-minute schedule, a month for a monthly one — and never the unbounded "current
    /// forever" the bounded successor's null used to imply. A grid that cannot produce a successor at all
    /// grants no grace: the slot goes through the ordinary skip-forward, which finalizes an exhausted series.
    /// </remarks>
    private async ValueTask<bool> IsSlipedOccurrenceStillCurrentAsync(
        RecurringTask recurring, DateTimeOffset slot, DateTimeOffset nowUtc, ScheduleIdentity identity,
        CancellationToken ct)
    {
        var successor = await Evaluator.NextGridOccurrenceAfterAsync(recurring, slot, identity, ct)
                                       .ConfigureAwait(false);
        return successor.HasValue && successor.Value > nowUtc;
    }

    /// <summary>
    /// Ends a recovered series whose every remaining occurrence falls past its bounds: Completed, cursor
    /// cleared, no run counted.
    /// </summary>
    /// <remarks>
    /// CONDITIONAL wherever the storage can be, exactly like the recovery finalization in <c>WorkerService</c>:
    /// an unconditional write would replace a <c>Cancelled</c> (or a fresh cursor from a reschedule) that
    /// linearized between the evaluation above and this line. Every expectation comes from the row the decision
    /// was COMPUTED FROM and is never read back here, or the guard would confirm whatever state it finds
    /// instead of losing to it.
    /// <para>
    /// A storage without the compare-and-swap keeps the historical unconditional write rather than being
    /// refused a normal end-of-series, and so does a caller that supplied no expected status.
    /// </para>
    /// </remarks>
    private static async Task FinalizeExhaustedSeriesAsync(ITaskStorage storage, Guid taskId,
                                                           DateTimeOffset expectedCursorUtc,
                                                           QueuedTaskStatus? expectedStatus,
                                                           int expectedScheduleVersion, AuditLevel auditLevel,
                                                           CancellationToken ct)
    {
        var policy = storage.SupportsScheduleVersioning && expectedStatus.HasValue
                         ? RecurringSeriesFinalizationPolicy.Conditional
                         : RecurringSeriesFinalizationPolicy.Unconditional;

        await RecurringSeriesFinalizer
              .FinalizeAsync(storage, taskId, expectedCursorUtc, expectedStatus.GetValueOrDefault(),
                  expectedScheduleVersion, 0, auditLevel, policy, ct)
              .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates or retrieves a cached TaskHandlerWrapper instance for the specified task type.
    /// Uses compiled Expression trees to avoid reflection overhead on repeated calls.
    /// </summary>
    internal static TaskHandlerWrapper CreateCachedWrapper(Type taskType)
    {
        var factory = WrapperFactoryCache.GetOrAdd(taskType, type =>
        {
            // Create wrapper type: TaskHandlerWrapperImp<TTask>
            var wrapperType = typeof(TaskHandlerWrapperImp<>).MakeGenericType(type);

            // Get parameterless constructor
            var constructor = wrapperType.GetConstructor(Type.EmptyTypes)
                              ?? throw new InvalidOperationException(
                                  $"Could not find parameterless constructor for {wrapperType}");

            // Compile constructor call into a fast delegate: () => new TaskHandlerWrapperImp<TTask>()
            var newExpression = Expression.New(constructor);
            var lambda        = Expression.Lambda<Func<TaskHandlerWrapper>>(newExpression);
            return lambda.Compile();
        });

        return factory();
    }
}
