using EverTask.Configuration;
using EverTask.Scheduler.Occurrences;
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
    /// The clock-less constructor, kept as a real overload so an assembly compiled against the previous
    /// release still binds. <c>AddEverTask</c> constructs the clock-carrying one explicitly.
    /// </summary>
    public WorkerService(
        IWorkerQueueManager queueManager,
        IServiceScopeFactory serviceScopeFactory,
        ITaskDispatcherInternal taskDispatcher,
        EverTaskServiceConfiguration configuration,
        IEverTaskWorkerExecutor workerExecutor,
        IEverTaskLogger<WorkerService> logger)
        : this(queueManager, serviceScopeFactory, taskDispatcher, configuration, workerExecutor, logger, null) { }

    // The recovery cutoff and every recoverable predicate below are evaluated against it, so recovery and
    // the schedulers can never disagree about "now".
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.BackgroundServiceRunning();

        // Diagnostics collected during assembly scanning (duplicate closed handlers, unsupported
        // open-generic handlers) have no other place to surface — see HandlerRegistrar.
        foreach (var warning in configuration.HandlerRegistrationWarnings)
        {
            logger.HandlerRegistrationWarning(warning);
        }

        if (configuration.MaxDegreeOfParallelism == 1)
        {
            var recommendedParallelism = Math.Max(4, Environment.ProcessorCount * 2);
            logger.SingleDegreeOfParallelism(recommendedParallelism);
        }

        var queues = queueManager.GetAllQueues().ToList();

        // Guarded explicitly: the queue-name join is eager, and the generated method's own level check
        // would run only after it.
        if (logger.IsEnabled(LogLevel.Information))
            logger.StartingQueueConsumption(queues.Count, string.Join(", ", queues.Select(q => q.Name)));

        var queueConsumptionTasks = queues
            .SelectMany(q => StartConsumers(q.Name, q.Queue, ct))
            .ToList();

        // Recovery runs CONCURRENTLY with the consumers: they must already be draining the bounded queues
        // while it re-enqueues, or a backlog larger than a queue's capacity deadlocks the startup.
        queueConsumptionTasks.Add(RunRecoveryAsync(ct));

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

    // Starts N dedicated long-lived consumers competing for the same channel.
    private IEnumerable<Task> StartConsumers(string queueName, IWorkerQueue queue, CancellationToken ct)
    {
        var queueConfig = queue switch
        {
            WorkerQueue wq => wq.Configuration,
            _ => new QueueConfiguration
            {
                Name = queueName,
                MaxDegreeOfParallelism = configuration.MaxDegreeOfParallelism
            }
        };

        // Clamp to at least one consumer: a queue with zero consumers and FullMode=Wait deadlocks every
        // producer (dispatch, scheduler, recovery) once its channel fills.
        var consumerCount = queueConfig.MaxDegreeOfParallelism;
        if (consumerCount < 1)
        {
            logger.QueueParallelismClamped(queueName, consumerCount);
            consumerCount = 1;
        }

        logger.StartingConsumers(consumerCount, queueName);

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

    private async Task ConsumeAsync(IWorkerQueue queue, string queueName, int consumerId, CancellationToken ct)
    {
        await foreach (var task in queue.DequeueAll(ct).ConfigureAwait(false))
        {
            try
            {
                await workerExecutor.DoWork(task, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.ConsumerCancelledDuringExecution(consumerId, queueName);
                return;
            }
            catch (Exception ex)
            {
                // DoWork handles its own errors: this is the defensive net that keeps one task failure
                // from killing the consumer.
                logger.ConsumerTaskProcessingError(ex, consumerId, queueName, task.PersistenceId);
            }
        }

        logger.ConsumerExited(consumerId, queueName);
    }

    /// <summary>
    /// Number of consecutive failed startup-recovery re-dispatch attempts after which a task is poisoned
    /// (marked <see cref="QueuedTaskStatus.Failed"/>) instead of being retried at every restart forever.
    /// Internal for testing purposes.
    /// </summary>
    internal int MaxRecoveryDispatchAttempts { get; set; } = 5;

    /// <summary>
    /// How many recovery pages may be in flight at the same time (#39). It bounds the recovery's memory by a
    /// constant instead of by the backlog, and the concurrent re-dispatches at that many times the per-queue
    /// fan-out of one wave. Internal for testing purposes.
    /// </summary>
    internal int MaxRecoveryPagesInFlight { get; set; } = 4;

    internal async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        using var scope       = serviceScopeFactory.CreateScope();
        var       taskStorage = scope.ServiceProvider.GetService<ITaskStorage>();

        // Resolved once for the whole recovery: it is what turns "this row names provider X" into "nothing
        // answers to X in this build", which is corrupt schedule metadata and takes the terminal poison route
        // like an unparseable cron — instead of failing at every next-run of every restart.
        var providers = scope.ServiceProvider.GetService<OccurrenceProviderRegistry>();

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

        // Tracked across pages so a persistent re-dispatch failure is not masked by a success summary log.
        var recovered         = 0;
        var transientFailures = 0;
        var permanentFailures = 0;

        // Recovery runs concurrently with live dispatching: only recover tasks created BEFORE
        // this point, so tasks dispatched while recovery is paginating are not re-enqueued twice.
        var recoveryCutoff = _timeProvider.GetUtcNow();

        // Children before parents, across the WHOLE recovered set and not page by page: a recovered durable
        // schedule immediately counts its active occurrences to decide how many to create, and one still
        // sitting in a later page reads a phantom low count. The second wave is a SECOND KEYSET SCAN rather
        // than a buffer, so only two scalars cross the loop — whether a durable schedule was seen at all,
        // and the keyset position just before the first one.
        var durableSchedules = 0;
        DateTimeOffset? durableFromCreatedAt = null;
        Guid? durableFromId = null;

        // The reader does not wait for the page it has just handed over (#39), or one blocking enqueue
        // toward a saturated queue holds the rows behind it — including those belonging to idle queues.
        // It blocks only at MaxRecoveryPagesInFlight, on whichever wave finishes first.
        var pipeline = new WavePipeline(MaxRecoveryPagesInFlight);

        try
        {
            while (true)
            {
                // The gate sits BEFORE the read, so a page is fetched only when there is a slot to recover it
                // in: reading first would keep one more page of rebuilt rows alive while waiting here.
                await pipeline.WaitForSlotAsync().ConfigureAwait(false);

                // The clock travels WITH the query: the storage never resolves "now" on its own, so the
                // RunUntil / MaxRuns gating of the filter is the same instant the rest of the pipeline sees.
                var page = await taskStorage
                                 .RetrievePending(_timeProvider.GetUtcNow(), lastCreatedAt, lastId, pageSize, ct)
                                 .ConfigureAwait(false);

                if (page.Length == 0)
                    break;

                // STRICT (<, not <=): the wall clock is coarse (~15 ms on Windows), so a live dispatch can
                // land in the same tick as the cutoff and <= would re-dispatch it. The cutoff is only a
                // first pass — TaskDeliveryRegistry is the actual defense against a residual race.
                var pendingTasks = page.Where(t => t.CreatedAtUtc < recoveryCutoff).ToArray();

                logger.ProcessingPendingBatch(pendingTasks.Length, lastCreatedAt, lastId);

                // Rebuild every row ONCE, up front: the durable/ordinary split below needs the deserialized
                // schedule, and doing it here keeps a single decode per row instead of one per decision.
                var prepared = pendingTasks
                               .Select(row => new PreparedRow(row, RecoveredTaskFactory.FromRow(row, providers)))
                               .ToArray();

                if (durableSchedules == 0)
                {
                    var first = Array.FindIndex(prepared, p => p.Recovered.IsDurableSchedule);

                    if (first >= 0)
                    {
                        // The keyset position of the row before it — or the page's own entry cursor when the
                        // schedule IS the first row — is where the second scan resumes from.
                        durableFromCreatedAt = first == 0 ? lastCreatedAt : prepared[first - 1].Row.CreatedAtUtc;
                        durableFromId        = first == 0 ? lastId : prepared[first - 1].Row.Id;
                    }
                }

                // Both of these are decided by the READER, in page order, before the wave is handed over: the
                // pipeline overlaps the recoveries, never the reading that positions the second scan.
                durableSchedules += prepared.Count(p => p.Recovered.IsDurableSchedule);
                pipeline.Add(RecoverWaveAsync(prepared.Where(p => !p.Recovered.IsDurableSchedule)));

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

            // The barrier is the whole recovered set, not the pages the reader happens to have handed over:
            // every ordinary row must really be BACK before a durable schedule counts its live occurrences,
            // so the pipeline is DRAINED here, not merely emptied of its finished waves.
            await pipeline.DrainAsync().ConfigureAwait(false);
        }
        catch
        {
            // The running waves are not abandoned: one would go on re-dispatching rows after the summary
            // line, and its own failure would surface as an unobserved task exception.
            await pipeline.SettleAsync().ConfigureAwait(false);
            throw;
        }

        // Second wave: every ordinary row (occurrences included) is back by now, so a schedule row asking
        // how many of its occurrences are alive gets the real answer. A row that stopped matching the filter
        // in between is one the second wave has nothing left to do for.
        if (durableSchedules > 0)
            await RecoverDurableSchedulesAsync().ConfigureAwait(false);

        // The summary must reflect failures, never report plain success when re-dispatches failed.
        if (transientFailures > 0 || permanentFailures > 0)
            logger.RecoverySummaryWithFailures(totalProcessed, recovered, transientFailures, permanentFailures);
        else
            logger.RecoveryCompleted(totalProcessed);

        return;

        // Re-walks the recovery page query from the position just before the first durable schedule the first
        // pass met, keeping only the durable schedules: no list grows with the backlog.
        async Task RecoverDurableSchedulesAsync()
        {
            var fromCreatedAt = durableFromCreatedAt;
            var fromId        = durableFromId;

            // Pipelined exactly like the first pass, and for the same reason (#39): a schedule whose
            // re-dispatch is slow must not hold the page of schedules behind it.
            var schedulePipeline = new WavePipeline(MaxRecoveryPagesInFlight);

            try
            {
                while (true)
                {
                    await schedulePipeline.WaitForSlotAsync().ConfigureAwait(false);

                    var page = await taskStorage.RetrievePending(_timeProvider.GetUtcNow(), fromCreatedAt, fromId,
                                                    pageSize, ct)
                                                .ConfigureAwait(false);

                    if (page.Length == 0)
                        break;

                    // The column check comes FIRST and costs nothing: only a row carrying a serialized
                    // definition can be a durable schedule, so the occurrences and one-shots this scan meets
                    // again are dropped without deserializing a payload for the second time.
                    var schedules = page
                                    .Where(row => row.CreatedAtUtc < recoveryCutoff
                                                  && !string.IsNullOrEmpty(row.RecurringTask))
                                    .Select(row => new PreparedRow(row, RecoveredTaskFactory.FromRow(row, providers)))
                                    .Where(p => p.Recovered.IsDurableSchedule);

                    schedulePipeline.Add(RecoverWaveAsync(schedules));

                    var lastRow = page[^1];
                    fromCreatedAt = lastRow.CreatedAtUtc;
                    fromId        = lastRow.Id;

                    if (lastRow.CreatedAtUtc >= recoveryCutoff)
                        break;
                }

                await schedulePipeline.DrainAsync().ConfigureAwait(false);
            }
            catch
            {
                await schedulePipeline.SettleAsync().ConfigureAwait(false);
                throw;
            }
        }

        // Partitioned PER TARGET QUEUE, each group fanned out concurrently: under a single global fan-out,
        // blocking enqueues toward one saturated queue occupy every slot and head-of-line-block the idle
        // queues. The key mirrors ExecuteDispatch's routing, so a group maps to exactly one worker queue.
        async Task RecoverWaveAsync(IEnumerable<PreparedRow> wave)
        {
            var rows = wave as PreparedRow[] ?? wave.ToArray();

            var options = new ParallelOptions
            {
                // Clamp to >= 1: ParallelOptions rejects 0, and a misconfigured zero must never abort recovery.
                MaxDegreeOfParallelism = Math.Max(1, configuration.MaxDegreeOfParallelism),
                CancellationToken      = ct
            };

            var cancelledSchedules = await ReadCancelledSchedulesAsync(rows).ConfigureAwait(false);

            var byQueue = rows.GroupBy(p =>
                p.Row.QueueName ?? (p.Row.IsRecurring ? QueueNames.Recurring : QueueNames.Default));

            await Task.WhenAll(byQueue.Select(group =>
                       Parallel.ForEachAsync(group, options,
                           (prepared, token) => ProcessRecoveredTaskAsync(prepared, cancelledSchedules, token))))
                      .ConfigureAwait(false);
        }

        // Which of this wave's occurrences belong to a schedule the user cancelled. Only the InProgress ones
        // a cancel deliberately leaves running can reach here, frozen by a hard crash before any delivery
        // wrote their outcome; requeuing one would execute an occurrence of a cancelled series.
        async ValueTask<HashSet<Guid>> ReadCancelledSchedulesAsync(PreparedRow[] rows)
        {
            var scheduleIds = rows.Where(p => p.Row.ParentTaskId != null)
                                  .Select(p => p.Row.ParentTaskId!.Value)
                                  .Distinct()
                                  .ToArray();

            if (scheduleIds.Length == 0)
                return [];

            var cancelled = await taskStorage
                                  .Get(t => scheduleIds.Contains(t.Id) && t.Status == QueuedTaskStatus.Cancelled, ct)
                                  .ConfigureAwait(false);

            return cancelled.Select(t => t.Id).ToHashSet();
        }

        async ValueTask ProcessRecoveredTaskAsync(PreparedRow prepared, HashSet<Guid> cancelledSchedules,
                                                  CancellationToken token)
        {
            var taskInfo   = prepared.Row;
            var task       = prepared.Recovered.Task;
            var auditLevel = prepared.Recovered.AuditLevel;

            if (prepared.Recovered.PayloadError != null)
                logger.TaskDeserializationFailed(prepared.Recovered.PayloadError, taskInfo.Id);

            if (prepared.Recovered.ScheduleError != null)
                logger.RecurringMetadataDeserializationFailed(prepared.Recovered.ScheduleError, taskInfo.Id);

            // The in-memory blacklist covering this case while the host lives does not survive a restart and
            // no recovery predicate consults the parent, so the schedule's Cancelled status has to be
            // honoured here: terminalize the row instead of handing it to a queue.
            if (taskInfo.ParentTaskId is { } scheduleId && cancelledSchedules.Contains(scheduleId))
            {
                await taskStorage.SetCancelledByUser(taskInfo.Id, auditLevel).ConfigureAwait(false);
                logger.OccurrenceOfCancelledScheduleDropped(taskInfo.Id, scheduleId);
                return;
            }

            // A recurring series with nothing left to run but a cursor still set must be FINALIZED, not
            // executed — and before any grace decision, since a slot at or past RunUntil is not one to grant
            // grace to. No payload is needed to end a series, so this runs before the poison guards below.
            if (taskInfo.IsRecurringSeriesToFinalize())
            {
                await FinalizeRecurringSeriesAsync(taskInfo, auditLevel, token).ConfigureAwait(false);
                return;
            }

            var scheduledTask          = prepared.Recovered.Recurring;
            var typeWasLoadable        = prepared.Recovered.TypeWasLoadable;
            var payloadError           = prepared.Recovered.PayloadError;
            var recurringMetadataError = prepared.Recovered.ScheduleError;

            // The accounting follows what the poison actually achieved: a poison that did NOT land leaves the
            // row recoverable, so counting it as permanent tells an operator a restart made progress it did
            // not.
            async Task<bool> PoisonAsync(ITaskStorage storage, Exception error)
            {
                var poisoned = await TryPoisonAsync(storage, taskInfo, auditLevel, error, token)
                                   .ConfigureAwait(false);

                if (poisoned)
                    Interlocked.Increment(ref permanentFailures);
                else
                    Interlocked.Increment(ref transientFailures);

                return poisoned;
            }

            // A recurring row that cannot be reconstructed as a schedule — corrupt OR missing metadata — must
            // NOT be demoted to a one-shot: it would dispatch successfully, stay recurring and recoverable,
            // and re-execute the same occurrence once per restart for ever. It cannot heal across restarts,
            // so the row is poisoned terminally instead of run.
            if (taskInfo.IsRecurring && scheduledTask == null)
            {
                var error = recurringMetadataError
                            ?? new InvalidOperationException(
                                "Recurring task metadata is missing or could not be deserialized");
                if (await PoisonAsync(taskStorage, error).ConfigureAwait(false))
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

                    // isRecovery: never drop a task (full queues exert backpressure, consumers are draining
                    // concurrently), preserve the stored NextRunUtc, do not rewrite the task definition.
                    // auditLevel, taskKey and rowMetadata carry the row's PERSISTED identity: without them the
                    // re-dispatch falls back to the global audit level, re-derives the queue from the handler
                    // attribute rather than the stored one, and rebuilds the executor parentless at version 0.
                    await taskDispatcher.ExecuteDispatch(task, executionTime, scheduledTask,
                        taskInfo.CurrentRunCount, token, taskInfo.Id, prepared.Recovered.TaskKey,
                        auditLevel, isRecovery: true, rowMetadata: prepared.Recovered.RowMetadata)
                        .ConfigureAwait(false);

                    // A success clears the failure counter, so transient failures never accumulate toward the
                    // poison limit across restarts.
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
                catch (ScheduleDeferredByProviderException)
                {
                    // The occurrence provider could not answer, so the dispatcher wrote nothing and parked the
                    // row to ask again, saying so itself. The row IS back in the scheduler, so it counts as
                    // recovered; the failure counter is neither cleared nor incremented, since an outage of
                    // somebody else's calendar proves nothing about this row.
                    Interlocked.Increment(ref recovered);
                }
                catch (Exception ex)
                {
                    // A re-dispatch failure is usually transient and must stay recoverable, so the count is
                    // durable and only the limit poisons: a permanently failing row must stop being retried
                    // at every restart.
                    var attempts = await taskStorage.IncrementRecoveryFailure(taskInfo.Id, token).ConfigureAwait(false);

                    if (attempts >= MaxRecoveryDispatchAttempts)
                    {
                        if (await PoisonAsync(taskStorage, ex).ConfigureAwait(false))
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
                // The TYPE is loadable but produced no instance: the payload either failed to deserialize or
                // deserialized to null. Both MIGHT heal (a serializer fix, a code change), so the row goes
                // through the same bounded retry as a transient re-dispatch failure rather than being
                // terminalized on the first restart.
                var error = payloadError
                            ?? new InvalidOperationException(
                                "Task payload deserialized to null for a loadable type");
                var attempts = await taskStorage.IncrementRecoveryFailure(taskInfo.Id, token).ConfigureAwait(false);

                if (attempts >= MaxRecoveryDispatchAttempts)
                {
                    if (await PoisonAsync(taskStorage, error).ConfigureAwait(false))
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

                // The accounting is PoisonAsync's, on both branches: a row that could not be written to — no
                // storage in this scope, or a write that did not land — is back in the next recovery page, so
                // it is a transient failure however permanent its cause is.
                if (itemStorage != null)
                    await PoisonAsync(itemStorage,
                            new Exception("Unable to create the IBackground task from the specified properties"))
                        .ConfigureAwait(false);
                else
                    Interlocked.Increment(ref transientFailures);
            }
        }

        // Ends a recurring series with nothing left to run: Completed with the cursor cleared, in one write,
        // counting no run. Wherever the storage can, that write compare-and-swaps on the cursor, status and
        // version of THIS page's row, so a Cancel or a reschedule that linearized first wins and this call
        // reports the loss instead of overwriting a status the user chose.
        async ValueTask FinalizeRecurringSeriesAsync(QueuedTask row, AuditLevel auditLevel, CancellationToken token)
        {
            bool finalized;

            try
            {
                finalized = await RecurringSeriesFinalizer
                                  .FinalizeAsync(taskStorage, row.Id, row.NextRunUtc, row.Status,
                                      row.ScheduleVersion, 0, auditLevel,
                                      RecurringSeriesFinalizationPolicy.Recovery, token)
                                  .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Counted by the same bounded mechanism as a failing re-dispatch, so a row that can never be
                // finalized stops being retried at every restart.
                var attempts = await taskStorage.IncrementRecoveryFailure(row.Id, token).ConfigureAwait(false);

                if (attempts >= MaxRecoveryDispatchAttempts)
                {
                    // Same confirmation as every other poison site: the write is best effort, so the row
                    // itself says whether the series was terminalized or is simply back at the next restart.
                    if (await TryPoisonAsync(taskStorage, row, auditLevel, ex, token).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref permanentFailures);
                        logger.RecoveryDispatchPoisoned(ex, row.Id, attempts);
                    }
                    else
                    {
                        Interlocked.Increment(ref transientFailures);
                    }
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

            // Deliberately OUTSIDE the try above: the terminal write is already committed, and sharing that
            // catch would let this bookkeeping increment the very counter it exists to clear, and overwrite a
            // Completed row with Failed.
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

    // Terminalizes a row the recovery cannot run, and answers whether it REALLY left the recovery set.
    // A recurring row needs the cursor cleared atomically with Failed, or it is revived and re-poisoned at
    // every restart. Both writes are best effort on every relational provider, so the outcome is CONFIRMED by
    // re-reading the row against both recovery predicates; a write that throws answers the same way, since
    // letting it out aborts the whole wave over one unusable row. Poison paths only — a recurring run's
    // transient failure must keep its cursor.
    private async Task<bool> TryPoisonAsync(ITaskStorage storage, QueuedTask row, AuditLevel auditLevel,
                                            Exception error, CancellationToken ct)
    {
        try
        {
            if (row.IsRecurring)
                await storage.SetRecurringTaskPoisoned(row.Id, error, auditLevel, ct).ConfigureAwait(false);
            else
                await storage.SetStatus(row.Id, QueuedTaskStatus.Failed, error, auditLevel, null, ct)
                             .ConfigureAwait(false);

            var persisted = await storage.Get(t => t.Id == row.Id, ct).ConfigureAwait(false);

            // A row that is GONE is out of the recovery set as surely as a poisoned one; anything still
            // there has to fail BOTH predicates, since either one puts it back in a recovery page.
            if (persisted.Length == 0)
                return true;

            var current = persisted[0];

            if (!current.IsRecoverableForExecution(_timeProvider.GetUtcNow())
                && !current.IsRecurringSeriesToFinalize())
                return true;

            logger.RecoveryPoisonNotApplied(error, row.Id);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.RecoveryPoisonFailed(ex, row.Id);
            return false;
        }
    }

    /// <summary>A recovered row paired with everything the factory could rebuild from it.</summary>
    private readonly record struct PreparedRow(QueuedTask Row, RecoveredTask Recovered);

    // The bounded pipeline of recovery waves a paginating pass hands its pages to (#39). A slot is freed by
    // whichever wave finishes FIRST, not by the oldest, so a page wedged behind a slow delivery costs the one
    // slot it holds instead of stopping the pipeline.
    private sealed class WavePipeline(int maxInFlight)
    {
        // Clamped to >= 1, like every other parallelism knob: at zero the reader would wait for a wave it has
        // not added yet.
        private readonly int _maxInFlight = Math.Max(1, maxInFlight);

        private readonly List<Task> _waves = [];

        /// <summary>
        /// Waits until a wave may be added. A failure is reported HERE, on the wave that carried it, so a
        /// broken recovery stops reading pages.
        /// </summary>
        public async ValueTask WaitForSlotAsync()
        {
            if (_waves.Count < _maxInFlight)
                return;

            var completed = await Task.WhenAny(_waves).ConfigureAwait(false);
            _waves.Remove(completed);
            await completed.ConfigureAwait(false);
        }

        public void Add(Task wave) => _waves.Add(wave);

        /// <summary>Waits for every wave and reports the first failure.</summary>
        public Task DrainAsync() => Task.WhenAll(_waves);

        /// <summary>
        /// Waits for every wave and reports nothing: used on the way out of a failure that has already
        /// decided the recovery's outcome, where the point is that no wave is left running behind it.
        /// </summary>
        public Task SettleAsync() =>
            Task.WhenAll(_waves).ContinueWith(static _ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        logger.BackgroundServiceStopping();
        await base.StopAsync(stoppingToken).ConfigureAwait(false);
    }
}
