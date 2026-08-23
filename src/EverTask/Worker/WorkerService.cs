using EverTask.Configuration;
using Microsoft.Extensions.Hosting;

namespace EverTask.Worker;

public class WorkerService(
    IWorkerQueueManager queueManager,
    IServiceScopeFactory serviceScopeFactory,
    ITaskDispatcherInternal taskDispatcher,
    EverTaskServiceConfiguration configuration,
    IEverTaskWorkerExecutor workerExecutor,
    IEverTaskLogger<WorkerService> logger,
    TimeProvider? timeProvider) : BackgroundService
{
    /// <summary>
    /// The pre-P9 constructor, kept as a real overload so an assembly compiled against the previous release
    /// still binds (P6/X6). <c>AddEverTask</c> constructs the clock-carrying one explicitly.
    /// </summary>
    public WorkerService(
        IWorkerQueueManager queueManager,
        IServiceScopeFactory serviceScopeFactory,
        ITaskDispatcherInternal taskDispatcher,
        EverTaskServiceConfiguration configuration,
        IEverTaskWorkerExecutor workerExecutor,
        IEverTaskLogger<WorkerService> logger)
        : this(queueManager, serviceScopeFactory, taskDispatcher, configuration, workerExecutor, logger, null) { }

    // The scheduling clock (P9): the recovery cutoff and every recoverable predicate below are evaluated
    // against it, so recovery and the schedulers can never disagree about "now".
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.BackgroundServiceRunning();

        // Surface handler-registration diagnostics collected during assembly scanning (duplicate
        // closed handlers, unsupported open-generic handlers) — see HandlerRegistrar (G1/G2).
        foreach (var warning in configuration.HandlerRegistrationWarnings)
        {
            logger.HandlerRegistrationWarning(warning);
        }

        // Warn if using suboptimal MaxDegreeOfParallelism configuration
        if (configuration.MaxDegreeOfParallelism == 1)
        {
            var recommendedParallelism = Math.Max(4, Environment.ProcessorCount * 2);
            logger.SingleDegreeOfParallelism(recommendedParallelism);
        }

        // Get all configured queues
        var queues = queueManager.GetAllQueues().ToList();

        // Guarded explicitly: the queue-name join is eager, and the generated method's own level check
        // would run only after it.
        if (logger.IsEnabled(LogLevel.Information))
            logger.StartingQueueConsumption(queues.Count, string.Join(", ", queues.Select(q => q.Name)));

        // Create N dedicated consumers for each queue using the official Microsoft pattern
        // This is the recommended approach for channel-based background workers
        var queueConsumptionTasks = queues
            .SelectMany(q => StartConsumers(q.Name, q.Queue, ct))
            .ToList();

        // Recover pending tasks from storage CONCURRENTLY with the consumers.
        // Consumers must already be draining the bounded queues while recovery re-enqueues:
        // otherwise a backlog larger than a queue's capacity deadlocks the startup
        // (recovery blocks on a full channel that nobody is consuming yet).
        queueConsumptionTasks.Add(RunRecoveryAsync(ct));

        // Wait for all consumers (and the recovery) to complete
        await Task.WhenAll(queueConsumptionTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the startup recovery, isolating its failures from the queue consumers:
    /// a recovery error must never stop task consumption.
    /// </summary>
    private async Task RunRecoveryAsync(CancellationToken ct)
    {
        try
        {
            await ProcessPendingAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.RecoveryCancelled();
        }
        catch (Exception ex)
        {
            logger.RecoveryFailed(ex);
        }
    }

    /// <summary>
    /// Starts N dedicated long-lived consumers for a queue.
    /// This is the official Microsoft-recommended pattern for channel consumption.
    /// Benefits over semaphore+list approach:
    /// - Zero per-item allocation (no Task.Run per task)
    /// - Stable memory footprint (N fixed workers)
    /// - No manual task list management
    /// - Natural backpressure with bounded channels
    /// - Graceful shutdown via cancellation token
    /// </summary>
    private IEnumerable<Task> StartConsumers(string queueName, IWorkerQueue queue, CancellationToken ct)
    {
        // Get the configuration for this specific queue
        var queueConfig = queue switch
        {
            WorkerQueue wq => wq.Configuration,
            _ => new QueueConfiguration
            {
                Name = queueName,
                MaxDegreeOfParallelism = configuration.MaxDegreeOfParallelism
            }
        };

        // Clamp to at least one consumer: a queue with zero consumers and FullMode=Wait deadlocks
        // every producer (dispatch, scheduler, recovery) once its channel fills (F5).
        var consumerCount = queueConfig.MaxDegreeOfParallelism;
        if (consumerCount < 1)
        {
            logger.QueueParallelismClamped(queueName, consumerCount);
            consumerCount = 1;
        }

        logger.StartingConsumers(consumerCount, queueName);

        // Spawn N long-lived consumers that compete for items from the channel
        for (var i = 0; i < consumerCount; i++)
        {
            var consumerId = i; // Capture for logging
            yield return Task.Run(async () =>
            {
                logger.ConsumerStarted(consumerId, queueName);

                try
                {
                    await ConsumeAsync(queue, queueName, consumerId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.ConsumerCancelled(consumerId, queueName);
                }
                catch (Exception ex)
                {
                    logger.ConsumerFaulted(ex, consumerId, queueName);
                    throw;
                }
                finally
                {
                    logger.ConsumerStopped(consumerId, queueName);
                }
            }, ct);
        }
    }

    /// <summary>
    /// Long-lived consumer loop that processes items from the queue.
    /// Each consumer competes with others for items from the same channel.
    /// </summary>
    private async Task ConsumeAsync(IWorkerQueue queue, string queueName, int consumerId, CancellationToken ct)
    {
        // Multiple consumers will compete for items from DequeueAll
        // Channel guarantees each item is delivered to exactly one consumer
        await foreach (var task in queue.DequeueAll(ct).ConfigureAwait(false))
        {
            try
            {
                // Direct execution - no Task.Run overhead, no semaphore, no list management
                // Worker is already running in its own Task from StartConsumers
                await workerExecutor.DoWork(task, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful shutdown - service is stopping
                logger.ConsumerCancelledDuringExecution(consumerId, queueName);
                return;
            }
            catch (Exception ex)
            {
                // DoWork should handle errors internally, but catch defensively
                // Don't let one task failure kill the entire consumer
                logger.ConsumerTaskProcessingError(ex, consumerId, queueName, task.PersistenceId);
                // Continue consuming next item
            }
        }

        logger.ConsumerExited(consumerId, queueName);
    }

    /// <summary>
    /// Number of consecutive failed startup-recovery re-dispatch attempts after which a task is
    /// poisoned (marked <see cref="QueuedTaskStatus.Failed"/>) instead of being retried at every
    /// restart forever (L18). Internal for testing purposes.
    /// </summary>
    internal int MaxRecoveryDispatchAttempts { get; set; } = 5;

    internal async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        using var scope       = serviceScopeFactory.CreateScope();
        var       taskStorage = scope.ServiceProvider.GetService<ITaskStorage>();

        if (taskStorage == null)
        {
            logger.PersistenceNotActive();
            return;
        }

        // Process pending tasks using keyset pagination to avoid loading large backlogs into memory
        const int pageSize = 100;
        DateTimeOffset? lastCreatedAt = null;
        Guid? lastId = null;
        var totalProcessed = 0;

        // L18 outcome accounting: a persistent re-dispatch failure must NOT be masked by a success
        // summary log. Tracked across pages, written to the summary at the end.
        var recovered         = 0;
        var transientFailures = 0;
        var permanentFailures = 0;

        // Recovery runs concurrently with live dispatching: only recover tasks created BEFORE
        // this point, so tasks dispatched while recovery is paginating are not re-enqueued twice.
        var recoveryCutoff = _timeProvider.GetUtcNow();

        // M7 barrier — children before parents, across the WHOLE recovered set and not page by page.
        // A durable schedule row, once recovered, immediately asks "how many of my occurrences are still
        // active?" to decide how many new ones to create; answering that while an occurrence of its own is
        // still sitting in a later page reads a phantom low count and overshoots the concurrency budget.
        // Only the schedule rows are held back — a handful per host, not the backlog — so the pagination
        // stays bounded in memory.
        var durableSchedules = new List<PreparedRow>();

        while (true)
        {
            // The clock travels WITH the query: the storage never resolves "now" on its own, so the
            // RunUntil / MaxRuns gating of the filter is the same instant the rest of the pipeline sees.
            var page = await taskStorage.RetrievePending(_timeProvider.GetUtcNow(), lastCreatedAt, lastId, pageSize, ct)
                                        .ConfigureAwait(false);

            if (page.Length == 0)
                break;

            // STRICT cutoff (<, not <=): live dispatches happen after this host captured the cutoff,
            // but the wall clock is coarse (DateTimeOffset.UtcNow resolves to ~15 ms on Windows), so a
            // live dispatch can land in the SAME tick as the cutoff. A <= filter then grabs that live
            // row and re-dispatches it via ExecuteDispatch(isRecovery:true), racing the live delivery —
            // wasted re-park churn for rate-limited tasks and, worse, the re-dispatch used to drop the
            // per-dispatch audit level (see the auditLevel propagation below). < excludes the same-tick
            // tie up front; the cutoff stays a best-effort first pass — the channel-write
            // TaskDeliveryRegistry is still the correctness defense for any residual race (stale page),
            // and the conditional SetQueued refuses a row that terminally finished since the page read.
            var pendingTasks = page.Where(t => t.CreatedAtUtc < recoveryCutoff).ToArray();

            logger.ProcessingPendingBatch(pendingTasks.Length, lastCreatedAt, lastId);

            // Rebuild every row ONCE, up front: the durable/ordinary split below needs the deserialized
            // schedule, and doing it here keeps a single decode per row instead of one per decision.
            var prepared = pendingTasks
                           .Select(row => new PreparedRow(row, RecoveredTaskFactory.FromRow(row)))
                           .ToArray();

            durableSchedules.AddRange(prepared.Where(p => p.Recovered.IsDurableSchedule));
            await RecoverWaveAsync(prepared.Where(p => !p.Recovered.IsDurableSchedule)).ConfigureAwait(false);

            totalProcessed += pendingTasks.Length;

            // Advance the keyset cursor using the RAW page (not the filtered one) so pagination
            // always makes progress; stop once the page reaches the recovery cutoff (>=, matching the
            // strict < filter above: every row from the cutoff tick onward is excluded anyway).
            var lastTask = page[^1];
            lastCreatedAt = lastTask.CreatedAtUtc;
            lastId = lastTask.Id;

            if (lastTask.CreatedAtUtc >= recoveryCutoff)
                break;
        }

        // Second wave: every ordinary row (occurrences included) of every page is back by now.
        await RecoverWaveAsync(durableSchedules).ConfigureAwait(false);

        // L18: the summary must reflect failures, never report plain success when re-dispatches failed.
        if (transientFailures > 0 || permanentFailures > 0)
            logger.RecoverySummaryWithFailures(totalProcessed, recovered, transientFailures, permanentFailures);
        else
            logger.RecoveryCompleted(totalProcessed);

        return;

        // Recovers one wave of rows: partitioned PER TARGET QUEUE and each group fanned out concurrently.
        // A single global Parallel.ForEachAsync let blocking enqueues toward one saturated queue occupy every
        // global slot and head-of-line-block the recovery of other, idle queues (L34). The partition key
        // mirrors ExecuteDispatch's routing (stored QueueName, else Recurring/Default), so a group maps to
        // exactly one worker queue: a wedged queue can only stall its own group's slots.
        Task RecoverWaveAsync(IEnumerable<PreparedRow> wave)
        {
            var options = new ParallelOptions
            {
                // Clamp to >= 1: ParallelOptions rejects 0, and a misconfigured zero must never abort
                // recovery (F5).
                MaxDegreeOfParallelism = Math.Max(1, configuration.MaxDegreeOfParallelism),
                CancellationToken      = ct
            };

            var byQueue = wave.GroupBy(p =>
                p.Row.QueueName ?? (p.Row.IsRecurring ? QueueNames.Recurring : QueueNames.Default));

            return Task.WhenAll(byQueue.Select(group =>
                Parallel.ForEachAsync(group, options, ProcessRecoveredTaskAsync)));
        }

        async ValueTask ProcessRecoveredTaskAsync(PreparedRow prepared, CancellationToken token)
        {
            var taskInfo   = prepared.Row;
            var task       = prepared.Recovered.Task;
            var auditLevel = prepared.Recovered.AuditLevel;

            if (prepared.Recovered.PayloadError != null)
                logger.TaskDeserializationFailed(prepared.Recovered.PayloadError, taskInfo.Id);

            if (prepared.Recovered.ScheduleError != null)
                logger.RecurringMetadataDeserializationFailed(prepared.Recovered.ScheduleError, taskInfo.Id);

            // X3 category (ii): a recurring series with nothing left to run but a cursor still set. It must be
            // FINALIZED, not executed — and before any grace decision, since a slot at or past RunUntil is not
            // a slot to grant grace to. Such a row used to match no predicate at all once RunUntil elapsed and
            // stayed Queued forever. No payload is needed to end a series, so this runs before the poison
            // guards below.
            if (taskInfo.IsRecurringSeriesToFinalize())
            {
                await FinalizeRecurringSeriesAsync(taskInfo, auditLevel, token).ConfigureAwait(false);
                return;
            }

            var scheduledTask          = prepared.Recovered.Recurring;
            var typeWasLoadable        = prepared.Recovered.TypeWasLoadable;
            var payloadError           = prepared.Recovered.PayloadError;
            var recurringMetadataError = prepared.Recovered.ScheduleError;

            // A recovery POISON must be TERMINAL for a recurring row (P0-1): SetRecurringTaskPoisoned clears
            // NextRunUtc atomically with Failed, so IsRecoverable stops returning it. A plain SetStatus(Failed)
            // leaves NextRunUtc set, and a recurring Failed row with NextRunUtc != null is revived and
            // re-poisoned at every restart (or re-executed once per restart if the cause healed). A one-shot
            // row has no NextRunUtc to clear, so SetStatus(Failed) terminalizes it correctly. This local helper
            // routes every poison site to the right primitive; it is used ONLY on poison paths (never on a
            // recurring run's transient failure, which must keep NextRunUtc to retry the next occurrence).
            Task Poison(ITaskStorage storage, Exception error) =>
                taskInfo.IsRecurring
                    ? storage.SetRecurringTaskPoisoned(taskInfo.Id, error, auditLevel, token)
                    : storage.SetStatus(taskInfo.Id, QueuedTaskStatus.Failed, error, auditLevel, null, token);

            // CU3/L44 + P0-2: a recurring row that cannot be reconstructed as a recurring schedule must NOT be
            // silently demoted to a one-shot. This covers BOTH corrupt metadata (RecurringTask present but it no
            // longer deserializes) AND missing metadata (RecurringTask null/empty): in either case scheduledTask
            // stays null. As a one-shot it would dispatch successfully (so the L18 poison counter never fires),
            // recovery would skip UpdateTask, the row would stay recurring and recoverable, and the same
            // occurrence would be re-executed once per restart forever. This is not transient (it cannot heal
            // across restarts), so poison the row TERMINALLY (Failed + NextRunUtc cleared) instead of running it.
            if (taskInfo.IsRecurring && scheduledTask == null)
            {
                var error = recurringMetadataError
                            ?? new InvalidOperationException(
                                "Recurring task metadata is missing or could not be deserialized");
                await Poison(taskStorage, error).ConfigureAwait(false);
                Interlocked.Increment(ref permanentFailures);
                logger.RecurringMetadataPoisoned(error, taskInfo.Id);
                return;
            }

            if (task != null)
            {
                try
                {
                    // For recurring tasks the cursor (NextRunUtc) is the resume point; everything else
                    // resumes from its scheduled time. Resolved once by the recovery factory.
                    var executionTime = prepared.Recovered.ExecutionTime;

                    // isRecovery: recovery must never drop tasks, so full queues exert
                    // backpressure here (consumers are draining concurrently), the stored
                    // NextRunUtc is preserved for recurring tasks and the task definition
                    // is not rewritten in storage.
                    // auditLevel: carry the PERSISTED per-task audit level through the re-dispatch.
                    // Omitting it let ExecuteDispatch fall back to the global DefaultAuditLevel, so a
                    // recovered task silently reverted to the global default (e.g. a Minimal task got
                    // Full-audited after a restart, or vice versa) — and the same loss surfaced as a
                    // flaky test whenever a same-tick cutoff tie made recovery win the delivery race.
                    // taskKey and rowMetadata: the same reasoning applied to the rest of the row's identity.
                    // Dropping them re-derived the queue from the handler attribute (while this loop groups
                    // the row by its STORED queue) and handed the executor back parentless, with no
                    // occurrence metadata and at schedule version 0.
                    await taskDispatcher.ExecuteDispatch(task, executionTime, scheduledTask,
                        taskInfo.CurrentRunCount, token, taskInfo.Id, prepared.Recovered.TaskKey,
                        auditLevel, isRecovery: true, rowMetadata: prepared.Recovered.RowMetadata)
                        .ConfigureAwait(false);

                    // L18: a task that previously failed re-dispatch but now succeeded clears its failure
                    // counter, so transient failures never accumulate toward the poison limit across restarts.
                    if ((taskInfo.RecoveryDispatchFailureCount ?? 0) > 0)
                        await taskStorage.ClearRecoveryFailure(taskInfo.Id, token).ConfigureAwait(false);

                    Interlocked.Increment(ref recovered);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Host shutdown: leave the task in its recoverable status so the next
                    // startup picks it up again. Marking it Failed here would lose it.
                    throw;
                }
                catch (Exception ex)
                {
                    // L18: a re-dispatch failure is typically transient (storage hiccup) and must stay
                    // recoverable — but a PERSISTENT failure used to be retried at every restart while the
                    // summary logged success, masking a constant error. Count the failure durably and
                    // poison the task (mark Failed) once it has failed too many times, so it stops being
                    // retried forever; genuinely transient failures still recover (counter reset on success).
                    var attempts = await taskStorage.IncrementRecoveryFailure(taskInfo.Id, token).ConfigureAwait(false);

                    if (attempts >= MaxRecoveryDispatchAttempts)
                    {
                        await Poison(taskStorage, ex).ConfigureAwait(false);
                        Interlocked.Increment(ref permanentFailures);
                        logger.RecoveryDispatchPoisoned(ex, taskInfo.Id, attempts);
                    }
                    else
                    {
                        Interlocked.Increment(ref transientFailures);
                        logger.RecoveryDispatchFailed(ex, taskInfo.Id, attempts, MaxRecoveryDispatchAttempts);
                    }
                }
            }
            else if (typeWasLoadable)
            {
                // B3/F7: the task TYPE is loadable but it produced no runnable instance (task == null). Either
                // the persisted payload could not be deserialized (payloadError != null, an unrecognized
                // legacy/serializer format) OR it deserialized to null (P2-1: e.g. a literal "null" Request) —
                // which previously fell into the "type not loadable" branch below and was poisoned immediately
                // with a misleading reason, bypassing the bounded retry. Both are a loadable type with an
                // unusable payload that MIGHT heal (a serializer fix, a code change), so do NOT terminalize on
                // the first restart: count it durably with the SAME bounded mechanism as a transient
                // re-dispatch failure (recoverable until the attempt limit, then poisoned so it does not retry
                // forever).
                var error = payloadError
                            ?? new InvalidOperationException(
                                "Task payload deserialized to null for a loadable type");
                var attempts = await taskStorage.IncrementRecoveryFailure(taskInfo.Id, token).ConfigureAwait(false);

                if (attempts >= MaxRecoveryDispatchAttempts)
                {
                    await Poison(taskStorage, error).ConfigureAwait(false);
                    Interlocked.Increment(ref permanentFailures);
                    logger.UnusablePayloadPoisoned(error, taskInfo.Id, attempts);
                }
                else
                {
                    Interlocked.Increment(ref transientFailures);
                    logger.UnusablePayloadRetry(error, taskInfo.Id, attempts, MaxRecoveryDispatchAttempts);
                }
            }
            else
            {
                // The type itself is not loadable (assembly/type gone) — it can never run, so poison it.
                // Create scope per iteration for thread safety (required for DbContext-based storage)
                using var itemScope = serviceScopeFactory.CreateScope();
                var itemStorage = itemScope.ServiceProvider.GetService<ITaskStorage>();

                if (itemStorage != null)
                {
                    await Poison(itemStorage,
                        new Exception("Unable to create the IBackground task from the specified properties"))
                        .ConfigureAwait(false);
                }

                Interlocked.Increment(ref permanentFailures);
            }
        }

        // Ends a recurring series whose remaining slots all fall past RunUntil, or whose run budget is
        // spent: Completed with the cursor cleared, in one write. No handler runs and no run is counted —
        // nothing executed. Wherever the storage can do it, that write is a compare-and-swap on the cursor,
        // status and version of THIS page's row, which is what makes it safe during recovery: a Cancel or a
        // reschedule that linearized first wins and this call reports the loss instead of overwriting a
        // status the user chose.
        async ValueTask FinalizeRecurringSeriesAsync(QueuedTask row, AuditLevel auditLevel, CancellationToken token)
        {
            bool finalized;

            try
            {
                // Conditional wherever the storage CAN be, exactly like the dispatcher's exhausted-series
                // branch. A storage without the compare-and-swap keeps the historical unconditional write:
                // calling the CAS member there raises NotSupportedException, the catch below counts a normal
                // end of series as an L18 failure, and the row is poisoned (or retried at every restart
                // forever) instead of being finalized.
                if (taskStorage.SupportsScheduleVersioning)
                {
                    finalized = await taskStorage
                                      .TrySetRecurringSeriesCompleted(row.Id, row.NextRunUtc, row.Status,
                                          row.ScheduleVersion, 0, auditLevel, token)
                                      .ConfigureAwait(false);
                }
                else
                {
                    await taskStorage.SetRecurringSeriesCompleted(row.Id, 0, auditLevel).ConfigureAwait(false);
                    finalized = true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failing finalization is counted by the SAME bounded L18 mechanism as a failing
                // re-dispatch, so a row that can never be finalized stops being retried at every restart.
                var attempts = await taskStorage.IncrementRecoveryFailure(row.Id, token).ConfigureAwait(false);

                if (attempts >= MaxRecoveryDispatchAttempts)
                {
                    await taskStorage.SetRecurringTaskPoisoned(row.Id, ex, auditLevel, token).ConfigureAwait(false);
                    Interlocked.Increment(ref permanentFailures);
                    logger.RecoveryDispatchPoisoned(ex, row.Id, attempts);
                }
                else
                {
                    Interlocked.Increment(ref transientFailures);
                    logger.RecoveryDispatchFailed(ex, row.Id, attempts, MaxRecoveryDispatchAttempts);
                }

                return;
            }

            if (finalized)
                logger.RecoverySeriesFinalized(row.Id, row.NextRunUtc, row.RunUntil);
            else
                logger.RecoverySeriesFinalizationSuperseded(row.Id);

            Interlocked.Increment(ref recovered);

            // Same L18 hygiene as a successful re-dispatch: earlier transient failures must not accumulate
            // toward the poison limit once the row reaches its terminal state. Deliberately OUTSIDE the try
            // above — the terminal write is already committed, and letting this bookkeeping share that catch
            // would let it increment the very counter it exists to clear and overwrite a Completed row with
            // Failed.
            if (!finalized || (row.RecoveryDispatchFailureCount ?? 0) == 0)
                return;

            try
            {
                await taskStorage.ClearRecoveryFailure(row.Id, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.RecoveryFailureCounterResetFailed(ex, row.Id);
            }
        }
    }

    /// <summary>A recovered row paired with everything the factory could rebuild from it.</summary>
    private readonly record struct PreparedRow(QueuedTask Row, RecoveredTask Recovered);

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        logger.BackgroundServiceStopping();
        await base.StopAsync(stoppingToken).ConfigureAwait(false);
    }
}
