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

    // Per-taskKey critical-section lock: serializes the GetByTaskKey -> decide -> Persist/Update of a
    // single taskKey across concurrent dispatches so two dispatches can never both insert (or one
    // delete under the other), the source of the taskKey dedup races (G13/G14/CU23/G17). It lives in the
    // container so a runtime reschedule — the same read-decide-write over the same row — holds the SAME
    // section: UpdateTask rewrites the definition and the cursor without touching the schedule version, so a
    // dispatch interleaved with a reschedule would overwrite it with the row it had read first.
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
    private OccurrenceProviderRetryRegistry? _providerRetries;
    private bool _providerRetriesResolved;

    /// <summary>
    /// The single seam for every question about the occurrence grid. Falls back to the built-in evaluator
    /// for hand-wired providers that never registered one.
    /// </summary>
    private IScheduleEvaluator Evaluator =>
        _evaluator ??= serviceProvider.GetService<IScheduleEvaluator>() ?? ScheduleEvaluator.Default;

    /// <summary>The scheduling clock (P9). Falls back to the real clock outside a configured container.</summary>
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

    /// <summary>
    /// The consecutive provider failures this host is holding per schedule. Read only to FORGET a schedule
    /// that will not ask again: the entry of one cancelled mid-outage would otherwise outlive the process's
    /// interest in it, exactly like a published schedule version.
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
        // Blacklist FIRST, before persisting Cancelled: a concurrent enqueue (scheduler slot / gate)
        // racing this Cancel must see the blacklist and be discarded, instead of slipping through the
        // (still-false) blacklist check and writing SetQueued over the Cancelled status we are about to
        // persist (CU13).
        workerBlacklist.Add(taskId);

        // Must not abort the rest of the cleanup if the CTS was already disposed (CU12).
        cancellationSourceProvider.CancelTokenForTask(taskId);

        // Drop any occurrence still parked in the scheduler so it isn't even dispatched.
        scheduler.TryUnschedule(taskId);

        // Invalidate any rate-limit gate operation in flight for this task: a deferral being re-parked
        // concurrently with this Cancel must not survive it. The parking-lot entry is released too
        // (a cancelled parked task never re-enters a channel).
        GateInvalidation?.Invalidate(taskId);
        ParkingLot?.Remove(taskId);

        // A schedule that will not run again stops being a version this process publishes a lower bound for
        // (S4). Nothing depends on the entry surviving a cancel, and leaving one behind per cancelled schedule
        // is the only way the registry could grow without bound. Its provider backoff goes the same way and
        // for the same reason: a schedule cancelled while its calendar was down never gets the answer that
        // would have cleared it.
        ScheduleVersions?.Remove(taskId);
        ProviderRetries?.Forget(taskId);

        // Persist Cancelled LAST so it is the final write of the cancel: any SetQueued a racing enqueue
        // managed to issue before the blacklist took effect is overwritten by Cancelled.
        if (taskStorage == null)
            return;

        // A schedule that owns occurrences is cancelled together with them, in one transaction (M15): a
        // materializer racing this can then only ever see an inactive schedule, and no occurrence of a
        // cancelled series is left waiting in a queue — or waiting to be put back in one by the next startup
        // recovery. Everything else keeps the historical single status write, byte for byte.
        if (await OwnsPendingOccurrencesAsync(taskId).ConfigureAwait(false) == true)
        {
            await taskStorage.CancelSchedule(taskId, AuditLevel.ErrorsOnly, cancellationToken).ConfigureAwait(false);
            return;
        }

        await taskStorage.SetCancelledByUser(taskId, AuditLevel.ErrorsOnly).ConfigureAwait(false);

        // Asked a SECOND time, and this is the one that closes the race: a materializer that had already
        // claimed the schedule row when the first read ran had not inserted its occurrence yet, so that read
        // answered "no occurrences" and the simple write cancelled the schedule alone, leaving a live
        // occurrence under a cancelled series — the one thing M15 says cannot happen. By the time the write
        // above is committed that materialization is decided either way, because both of them write the
        // schedule row and so serialize on it: it committed (its occurrence is visible here) or it lost, and
        // every one that starts from now on is refused by the status it reads. A cancel with nothing to
        // cascade pays one indexed read on an administrative path; one that finds something writes the
        // cascade the race deprived it of.
        //
        // A read that FAILED cascades too: it is the only lookup left, and "the query threw" is not an answer
        // that lets a cancel declare the series terminal. The cascade over a schedule with no occurrence is
        // the parent's own write, which is the one that would have been made anyway.
        if (await OwnsPendingOccurrencesAsync(taskId).ConfigureAwait(false) != false)
            await taskStorage.CancelSchedule(taskId, AuditLevel.ErrorsOnly, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this row still has occurrences that a cancel has to terminalize with it, or <c>null</c> when
    /// the lookup itself failed and there is no answer.
    /// </summary>
    /// <remarks>
    /// The question is asked of the persisted RELATIONSHIP — the rows that name this one as their schedule —
    /// and never of the definition. Reading the definition answered "nothing to cascade" for a schedule whose
    /// JSON no longer parses and for one a task-key re-registration had just turned inline, both of which
    /// still own every occurrence they had already created; and a row with no occurrence gets the historical
    /// single write either way, because there is nothing for the cascade to add.
    /// <para>
    /// One indexed read on an administrative path, skipped entirely for a storage that has no occurrences to
    /// find. It does NOT take the caller's token and it never propagates: this classification sits in front of
    /// the status write that has always been the cancel's last act, and it must not become a new way for that
    /// write not to happen. It reports the failure instead of deciding it — a failed read that answered
    /// "no occurrences" let a cancel end normally having cancelled the schedule alone, and the occurrence it
    /// could not see went on to run once the blacklist entry lapsed.
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
    /// The key check is also in <see cref="RecurringTask.Validate(OccurrenceProviderRegistry)"/>, which is what
    /// poisons a persisted row naming a key this build no longer registers; here it is what refuses the
    /// dispatch while the caller is still holding it. The determinism check lives ONLY here, deliberately: it
    /// resolves the provider to ask, and doing that inside the validation every recovery runs would turn a
    /// container hiccup into a poisoned schedule.
    /// <para>
    /// Which is why it is a DISPATCH-time gate and nothing else: on the recovery path there is no caller to
    /// refuse, the row is already written, and neither of the two things this can throw is an
    /// <see cref="OccurrenceProviderException"/> — so both would escape the transient catch, reach the
    /// recovery's own failure counter and poison the series after a handful of restarts. That is the outcome
    /// the ratification of this gate (decisions §3.7) says cannot happen: a row carrying
    /// <see cref="CatchUpOverflowPolicy.SkipOldest"/> over a provider that no longer declares itself
    /// deterministic keeps running and bisects as a deterministic grid would, and a container that cannot
    /// build the provider yet costs a retry, not a series.
    /// </para>
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
    /// The dispatch entry point as the previous release shipped it, kept BYTE-FOR-BYTE (P6/X6). Row metadata
    /// travels through the explicit <see cref="ITaskDispatcherInternal"/> implementation below instead of
    /// being appended here, where a new parameter would have replaced this method's IL signature.
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

        // T10: every path that accepts a schedule validates it, and this is the one the fluent builder does
        // not go through — a definition handed to the public dispatch entry point directly. Recovery has
        // already validated its own through RecoveredTaskFactory, so this only ever re-checks a clean one
        // there. Without it a corrupt interval, or an OccurrenceMode outside the defined values, is read as
        // valid all the way down to IsScheduleOnly, which only ever compares against Durable.
        // The registry is handed over so an unregistered provider key is refused here too, with the key in the
        // message, instead of at the first grid question.
        recurring?.Validate(Providers);

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
        // finalization below, and that write must be conditional on the values its decision was computed from.
        // Reading them back after deciding would fold a Cancel (or a reschedule) that linearized in between
        // into the expectation, so the CAS would confirm the concurrent state instead of losing to it.
        QueuedTaskStatus? existingStatus = null;

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
                // (double execution). Reject it and return the existing id (CU6/L31, G17). A delayed or
                // recurring re-dispatch parks a fresh occurrence in the scheduler, so it is left to proceed.
                if (executionTime == null && recurring == null && DeliveryRegistry?.IsDelivering(existingTask.Id) == true)
                {
                    logger.DispatchDiscardedDeliveryInFlight(taskKey, existingTask.Id);
                    return existingTask.Id;
                }

                // A recurring row must not be converted to a one-shot by a taskKey re-dispatch that carries
                // no recurring config — that would silently destroy its schedule and history (G16).
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

                    // The run counter is preserved whether or not the row still has a cursor: storage keeps
                    // counting from it, so a TERMINAL series re-registered under the same taskKey resumes at
                    // CurrentRunCount + 1. Reading it only in the cursor branch below would hand the handler
                    // a RunNumber of 1 for the run storage is about to record as the sixth.
                    existingCurrentRunCount = existingTask.CurrentRunCount;

                    // The schedule version belongs to the ROW, and a re-registration under the same taskKey
                    // updates that row in place: UpdateTask never writes the column, so the version the row
                    // is at is still the version this dispatch runs. Leaving the metadata at its default
                    // would tell the handler's context — and every monitoring event of the delivery — that a
                    // rescheduled series is back at version 0. It is also the compare-and-swap expectation of
                    // the finalization below, which is why it is read here, once, with the rest of the row.
                    rowMetadata = rowMetadata with { ScheduleVersion = existingTask.ScheduleVersion };

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

                        // Removal is the third end of a schedule S4 names, beside a terminal series and a
                        // cancellation, and this is the only place the library deletes a row. Nothing can
                        // reach it holding a published version today — a recurring row is refused a one-shot
                        // re-dispatch above, so the row deleted here has never been a schedule — but a
                        // published version whose row no longer exists is a lower bound nothing can ever
                        // clear, and the clause costs a dictionary lookup on a path that already talks to
                        // storage.
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

            try
            {
                decision = await DecideRecurringRunAsync(recurring, isRecovery, existingNextRunUtc,
                                   existingCurrentRunCount, executionTime, existingTaskId, currentRun, nowUtc,
                                   identity, ct)
                               .ConfigureAwait(false);
            }
            catch (OccurrenceProviderException failure) when (isRecovery && existingTaskId is { } scheduleId)
            {
                // V4: the calendar this schedule reads could not answer, which is transient by contract — the
                // application's own database being briefly down must not end a series. Nothing is written, so
                // the row keeps its cursor and stays recoverable, and the schedule is parked to ask again
                // after the backoff. The provider's own exception is caught HERE and never rethrown: the
                // recovery's generic catch counts a failure against the poison budget, so a source down
                // across five restarts would otherwise mark the series Failed for ever.
                await ParkProviderRetryAsync(task, recurring, failure, scheduleId, taskKey, auditLevel,
                        rowMetadata, nowUtc)
                    .ConfigureAwait(false);

                // And the caller is told it was DEFERRED rather than dispatched. Answering with the schedule
                // id reads as a dispatch that worked, which is how a recovery came to clear an L18 counter a
                // previous restart had really earned: nothing about this row was proved by an outage of its
                // calendar, so nothing about it may be forgotten either.
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

                if (taskStorage != null && existingTaskId.HasValue && existingNextRunUtc.HasValue)
                    await FinalizeExhaustedSeriesAsync(taskStorage, existingTaskId.Value,
                            existingNextRunUtc.Value, existingStatus, rowMetadata.ScheduleVersion,
                            auditLevel ?? serviceConfiguration.DefaultAuditLevel, ct)
                        .ConfigureAwait(false);

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
        // Stamped on the executor so the handler's context reports it without reading the row again. An
        // occurrence arrives with its own number already read from the row and keeps it: its counter is the
        // one-shot's, which says nothing about the run of the series the occurrence is (C1).
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
                // instead of proceeding with our own duplicate PersistenceId (G14/CU23).
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

        // The executor is already lazy when useLazyExecutor is true (built by the wrapper
        // without a handler instance), eager otherwise
        var executorToSchedule = executor;

        if (executorToSchedule.ExecutionTime > nowUtc || recurring != null)
        {
            scheduler.Schedule(executorToSchedule, nextRun);
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
                    // parking-lot reservation, then propagate the failure (CU15).
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

        // Immediate tasks: lazy by default (MEM-2). An eager handler resolved at dispatch from
        // the singleton dispatcher's root provider is pinned in the root container's disposables
        // list until shutdown; the worker resolves and disposes a fresh instance per task anyway.
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
    /// <see cref="INextOccurrenceProvider"/>, which is real I/O — has a single boundary its caller can catch
    /// at (V4). Every branch is the one it always was.
    /// </remarks>
    private async Task<RecurringRunDecision> DecideRecurringRunAsync(
        RecurringTask recurring, bool isRecovery, DateTimeOffset? existingNextRunUtc, int? existingCurrentRunCount,
        DateTimeOffset? executionTime, Guid? existingTaskId, int? currentRun, DateTimeOffset nowUtc,
        ScheduleIdentity identity, CancellationToken ct)
    {
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

            // L16: on recovery, if the pending occurrence slipped into the past but is still the CURRENT
            // one (the next occurrence is not due yet), execute IT now instead of skipping it — a short
            // downtime across a scheduled occurrence must not silently lose it. Calendar-exact: uses the
            // real next occurrence, not the flat GetMinimumInterval heuristic, which is wrong for
            // OnDays/Month/Week (too narrow drops a just-due slot; too wide runs a stale one) — U4/U5.
            //
            // X3: the successor is the NATURAL one — computed ignoring RunUntil/MaxRuns. The bounded
            // successor returns null both when the slot is still current AND when the series has simply
            // ended, and reading that null as "current forever" is what used to execute a months-old
            // slot at restart. Ignoring the bounds separates the two: "no successor yet" now really means
            // the grid produced none, which grants no grace at all.
            if (isRecovery &&
                await IsSlipedOccurrenceStillCurrentAsync(recurring, existingNextRunUtc.Value, nowUtc, identity, ct)
                    .ConfigureAwait(false))
            {
                logger.RecoveryExecutingSlippedOccurrence(existingNextRunUtc, existingTaskId);
                return new RecurringRunDecision(existingNextRunUtc, false);
            }

            // NextRunUtc is (well) in the past - skip forward while preserving rhythm. On the
            // recovery path the initial-run config must NOT be re-applied (L25-firstrun).
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
            // instant the caller named (M11). The replay it triggers is still bounded by the misfire
            // policy's own caps — this only decides where the schedule starts counting from.
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
    /// backoff instead of waiting for the next restart (V4).
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

        TaskHandlerExecutor? built = null;

        try
        {
            var executor = await CreateCachedWrapper(task.GetType())
                                 .Handle(task, retryAt, recurring, serviceProvider,
                                     auditLevel ?? serviceConfiguration.DefaultAuditLevel, scheduleId, taskKey,
                                     useLazyExecutor: true, rowMetadata)
                                 .ConfigureAwait(false);

            built = executor;

            // Conditional, like every other re-park: a reschedule may have committed a newer definition and
            // parked it while the provider was failing, and that registration owns the row now.
            if (!scheduler.TrySchedule(executor with { IsScheduleRetry = true }, retryAt))
            {
                // Refused: this recovery parked nothing. Either a newer definition owns the row — so the
                // series is not the one waiting on a provider any more — or the scheduler is stopping and
                // startup recovery finds the row again. Announcing it as "parked to ask again" is the one
                // sentence that is not true in either case.
                logger.ProviderRetryParkRefused(scheduleId, retryAt);
                return;
            }

            // Said only once the registration is really in, log line and event alike — the same order
            // WorkerExecutor.DeferScheduleForProvider keeps, and for the same reason: before that point the
            // sentence is a promise the very next line can break.
            logger.RecoveryDeferredByProvider(failure, failure.ProviderKey, scheduleId,
                failure.ConsecutiveFailures, retryAt);

            PublishProviderRetryEvent(executor, failure, scheduleId, retryAt);
        }
        catch (Exception e)
        {
            logger.ProviderRetryParkFailed(e, scheduleId);

            // V4(2): the re-park is what makes this schedule come back at all, and nothing polls behind it —
            // so a failure here is not a log line, it is the series stopping until the next restart. The
            // executor is whatever was built before the throw: without one there is no task identity to hang
            // an event on, and the log line is all this can say.
            PublishProviderParkFailedEvent(built, e, scheduleId, failure);
        }
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
    /// X3: CONDITIONAL wherever the storage can be, exactly like the recovery finalization in
    /// <c>WorkerService</c>. The unconditional write would replace a <c>Cancelled</c> (or a fresh cursor from
    /// a reschedule) that linearized between the evaluation above and this line with <c>Completed</c>.
    /// <para>
    /// Every expectation comes from the row the decision was COMPUTED FROM and is never read back here: a
    /// fresh read after deciding would see the concurrent write and hand it to the compare-and-swap as the
    /// expected value, turning the guard into a confirmation of whatever state it finds — the cancellation
    /// would be silently replaced. Losing the compare-and-swap simply means someone else owns the row now.
    /// </para>
    /// <para>
    /// A storage without the compare-and-swap keeps the historical unconditional write rather than being
    /// refused a normal end-of-series, and so does a caller that supplied no expected status (a hand-wired
    /// re-dispatch outside recovery, which carries no row metadata).
    /// </para>
    /// </remarks>
    private static async Task FinalizeExhaustedSeriesAsync(ITaskStorage storage, Guid taskId,
                                                           DateTimeOffset expectedCursorUtc,
                                                           QueuedTaskStatus? expectedStatus,
                                                           int expectedScheduleVersion, AuditLevel auditLevel,
                                                           CancellationToken ct)
    {
        if (!storage.SupportsScheduleVersioning || expectedStatus is not { } status)
        {
            await storage.SetRecurringSeriesCompleted(taskId, 0, auditLevel).ConfigureAwait(false);
            return;
        }

        await storage.TrySetRecurringSeriesCompleted(taskId, expectedCursorUtc, status, expectedScheduleVersion,
            0, auditLevel, ct).ConfigureAwait(false);
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
