using EverTask.Configuration;
using EverTask.RateLimiting;

namespace EverTask.Worker;

public class WorkerQueue : IWorkerQueue
{
    private readonly Channel<TaskHandlerExecutor> _queue;
    private readonly ILogger _logger;
    private readonly IWorkerBlacklist _workerBlacklist;
    private readonly ITaskStorage? _taskStorage;

    // The conditional recovery transition must judge RunUntil against the SAME instant as the recovery
    // filter that selected the row, never the storage's own reading of the clock.
    private readonly TimeProvider _timeProvider;

    // An id is registered from the channel write until its delivery terminally ends
    // (WorkerExecutor.DoWork outer finally); a second write of the same id is rejected here. Shared
    // across all queues of the host — a task rerouted to another queue is the same task.
    private readonly TaskDeliveryRegistry _deliveryRegistry;

    /// <summary>
    /// Gets the name of this queue.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the configuration for this queue.
    /// </summary>
    public QueueConfiguration Configuration { get; }

    /// <inheritdoc />
    public int Count => _queue.Reader.CanCount ? _queue.Reader.Count : 0;

    /// <inheritdoc />
    public int Capacity => Configuration.ChannelOptions.Capacity;

    /// <summary>
    /// Parking-lot accounting hook: a successful channel write un-parks the task, the
    /// consumer-independent decrement that keeps the rate-limit backpressure from wedging. No-op for
    /// tasks that were never parked.
    /// </summary>
    internal RateLimitParkingLot? ParkingLot { get; set; }

    /// <summary>
    /// Creates a new WorkerQueue with the specified queue configuration.
    /// </summary>
    public WorkerQueue(
        QueueConfiguration configuration,
        ILogger logger,
        IWorkerBlacklist workerBlacklist,
        ITaskStorage? taskStorage = null,
        TaskDeliveryRegistry? deliveryRegistry = null)
        : this(configuration, logger, workerBlacklist, taskStorage, deliveryRegistry, null) { }

    /// <summary>
    /// <see cref="WorkerQueue(QueueConfiguration,ILogger,IWorkerBlacklist,ITaskStorage,TaskDeliveryRegistry)"/>
    /// on an explicit scheduling clock. The shorter arity above is kept as a real overload so an assembly
    /// compiled against the previous release still binds.
    /// </summary>
    public WorkerQueue(
        QueueConfiguration configuration,
        ILogger logger,
        IWorkerBlacklist workerBlacklist,
        ITaskStorage? taskStorage,
        TaskDeliveryRegistry? deliveryRegistry,
        TimeProvider? timeProvider)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerBlacklist = workerBlacklist ?? throw new ArgumentNullException(nameof(workerBlacklist));
        _taskStorage = taskStorage;
        _deliveryRegistry = deliveryRegistry ?? new TaskDeliveryRegistry();
        _timeProvider = timeProvider ?? TimeProvider.System;

        Name = configuration.Name;
        // The itemDropped callback fires only under the Drop* full modes: without it a dropped id stays
        // registered for ever and its row stays Queued, so the task is neither delivered nor recovered.
        _queue = Channel.CreateBounded<TaskHandlerExecutor>(
            configuration.ChannelOptions,
            OnItemDropped);
    }

    private void OnItemDropped(TaskHandlerExecutor dropped)
    {
        _deliveryRegistry.End(dropped.PersistenceId);

        // The evicted copy is never delivered, so this is the last hand holding its eager handler scope.
        // Fire-and-forget like the revert below: the drop callback is synchronous and the eviction has
        // already happened either way.
        _ = DroppedDelivery.ReleaseAsync(dropped, _logger).AsTask();

        // The evicted row is still Queued — it would look enqueued for ever and never run. Revert it to
        // WaitingQueue so startup recovery rescues it.
        if (_taskStorage != null)
            _ = RevertDroppedToWaitingQueueAsync(dropped);
    }

    private async Task RevertDroppedToWaitingQueueAsync(TaskHandlerExecutor dropped)
    {
        try
        {
            await _taskStorage!
                  .SetStatus(dropped.PersistenceId, QueuedTaskStatus.WaitingQueue, null, dropped.AuditLevel)
                  .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.DroppedTaskRevertFailed(e, dropped.PersistenceId, Name);
        }
    }

    /// <summary>
    /// Creates a new WorkerQueue with backward compatibility for EverTaskServiceConfiguration.
    /// </summary>
    [Obsolete("Use the constructor with QueueConfiguration instead. This constructor is for backward compatibility only.")]
    public WorkerQueue(
        EverTaskServiceConfiguration configuration,
        ILogger logger,
        IWorkerBlacklist workerBlacklist,
        ITaskStorage? taskStorage = null)
        : this(CreateDefaultConfiguration(configuration), logger, workerBlacklist, taskStorage)
    {
    }

    private static QueueConfiguration CreateDefaultConfiguration(EverTaskServiceConfiguration serviceConfig)
    {
        return new QueueConfiguration
        {
            Name = QueueNames.Default,
            MaxDegreeOfParallelism = serviceConfig.MaxDegreeOfParallelism,
            ChannelOptions = serviceConfig.ChannelOptions,
            DefaultRetryPolicy = serviceConfig.DefaultRetryPolicy,
            DefaultTimeout = serviceConfig.DefaultTimeout
        };
    }

    public ValueTask Queue(TaskHandlerExecutor task, CancellationToken cancellationToken = default)
        => QueueCore(task, enforceRecoverable: false, cancellationToken);

    /// <summary>
    /// Blocking enqueue used by the startup recovery: the SetQueued transition is CONDITIONAL on the row
    /// still being recoverable, so a task whose live copy terminally finished is never resurrected.
    /// </summary>
    internal ValueTask QueueForRecovery(TaskHandlerExecutor task, CancellationToken cancellationToken = default)
        => QueueCore(task, enforceRecoverable: true, cancellationToken);

    // Also asks the durable SCHEDULE: an occurrence already parked in the scheduler when the cancel landed
    // carries no blacklist entry of its own. The schedule's entry covers every occurrence it produced, so
    // it is never consumed here.
    private bool IsCancelled(TaskHandlerExecutor task) =>
        _workerBlacklist.IsBlacklisted(task.PersistenceId)
        || (task.ParentTaskId is { } scheduleId && _workerBlacklist.IsBlacklisted(scheduleId));

    private async ValueTask QueueCore(TaskHandlerExecutor task, bool enforceRecoverable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Every refusal below drops THIS executor for good — the blocking enqueue has no "try again"
        // result and no caller retries the same instance — so each one is also the last chance to release
        // the eager handler scope it carries (DroppedDelivery).
        if (IsCancelled(task))
        {
            await DroppedDelivery.ReleaseAsync(task, _logger).ConfigureAwait(false);
            return;
        }

        // A delivery of this id is already in flight in this process (in a channel or executing):
        // idempotent no-op, single execution. This is the write-boundary defense against the
        // recovery-vs-live-dispatch double delivery.
        if (!_deliveryRegistry.TryBegin(task.PersistenceId, task.ParentTaskId))
        {
            _logger.DuplicateEnqueueSkipped(task.PersistenceId, Name);
            await DroppedDelivery.ReleaseAsync(task, _logger).ConfigureAwait(false);
            return;
        }

        if (_taskStorage != null)
        {
            try
            {
                if (enforceRecoverable)
                {
                    // Refused transition = the row terminally finished since the recovery read it:
                    // release the registration and skip (nothing was written anywhere)
                    if (!await _taskStorage.TrySetQueuedIfRecoverable(_timeProvider.GetUtcNow(), task.PersistenceId, task.AuditLevel, cancellationToken).ConfigureAwait(false))
                    {
                        _deliveryRegistry.End(task.PersistenceId);
                        _logger.RecoveryEnqueueSkipped(task.PersistenceId);
                        await DroppedDelivery.ReleaseAsync(task, _logger).ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    await _taskStorage.SetQueued(task.PersistenceId, task.AuditLevel, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Storage failure before the write: the task was never enqueued, the status is
                // untouched (still recoverable). Propagate without marking Failed.
                _deliveryRegistry.End(task.PersistenceId);
                throw;
            }
        }

        try
        {
            _logger.QueuingTask(task.PersistenceId, Name);
            await _queue.Writer.WriteAsync(task, cancellationToken).ConfigureAwait(false);
            ParkingLot?.OnTaskEnqueued(task.PersistenceId);
        }
        catch (OperationCanceledException)
        {
            // The caller abandoned the wait (aborted request or host shutdown).
            // The task is persisted with status Queued and is re-enqueued by startup recovery,
            // so it must NOT be marked as failed here.
            _deliveryRegistry.End(task.PersistenceId);
            _logger.EnqueueCancelled(task.PersistenceId, Name);
            throw;
        }
        catch (Exception e)
        {
            _deliveryRegistry.End(task.PersistenceId);
            _logger.UnableToQueueTask(e, task.PersistenceId, Name);
            if (_taskStorage != null)
                await _taskStorage.SetStatus(task.PersistenceId, QueuedTaskStatus.Failed, e, task.AuditLevel).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask<EnqueueResult> TryQueue(TaskHandlerExecutor task, CancellationToken cancellationToken = default)
        => TryQueueCore(task, enforceRecoverable: false, cancellationToken);

    /// <summary>
    /// Non-blocking enqueue used by the schedulers (a due slot fires): the SetQueued transition is
    /// CONDITIONAL on the row still being recoverable, so a stale slot never resurrects a row that
    /// terminally finished. Returns <see cref="EnqueueResult.Discarded"/> when it is not, writing nothing.
    /// </summary>
    internal ValueTask<EnqueueResult> TryQueueForRecovery(TaskHandlerExecutor task, CancellationToken cancellationToken = default)
        => TryQueueCore(task, enforceRecoverable: true, cancellationToken);

    private async ValueTask<EnqueueResult> TryQueueCore(TaskHandlerExecutor task, bool enforceRecoverable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Discarded is terminal for this executor — every caller consumes the registration on it — so the
        // eager handler scope it carries is released here. QueueFull and DuplicateInProcess below are NOT:
        // both schedulers re-park and retry the very same instance (DroppedDelivery).
        if (IsCancelled(task))
        {
            await DroppedDelivery.ReleaseAsync(task, _logger).ConfigureAwait(false);
            return EnqueueResult.Discarded;
        }

        // Fast path: skip the storage round-trips below while the queue is saturated.
        // Callers that retry (scheduler backoff) would otherwise churn the storage on every attempt.
        // Only meaningful with FullMode=Wait: with the Drop* modes TryWrite never rejects.
        if (Configuration.ChannelOptions.FullMode == BoundedChannelFullMode.Wait
            && _queue.Reader.CanCount && _queue.Reader.Count >= Capacity)
        {
            _logger.QueueFull(Name, task.PersistenceId);
            return EnqueueResult.QueueFull;
        }

        // Already in flight in this process. The caller decides what that means: a scheduler retries
        // shortly (its slot may have fired while the previous delivery was still unwinding), a live
        // dispatch treats it as idempotent success.
        if (!_deliveryRegistry.TryBegin(task.PersistenceId, task.ParentTaskId))
        {
            _logger.DuplicateDeliveryNotEnqueued(task.PersistenceId, Name);
            return EnqueueResult.DuplicateInProcess;
        }

        // Mark as Queued BEFORE writing: once in the channel a consumer can execute immediately, and a late
        // SetQueued would overwrite InProgress/Completed and have startup recovery re-execute the task.
        // A schedule retry is exempt because it must write NOTHING — it runs no handler, only re-runs a
        // decision the occurrence provider could not answer, over a row that keeps the status and cursor the
        // outage found it in. The recoverable check moves to RetryScheduleDecisionAsync.
        if (_taskStorage != null && !task.IsScheduleRetry)
        {
            try
            {
                if (enforceRecoverable)
                {
                    // Refused = the row terminally finished since the slot was registered: release the
                    // registration and skip, nothing was written anywhere.
                    if (!await _taskStorage.TrySetQueuedIfRecoverable(_timeProvider.GetUtcNow(), task.PersistenceId, task.AuditLevel, cancellationToken).ConfigureAwait(false))
                    {
                        _deliveryRegistry.End(task.PersistenceId);
                        _logger.SchedulerEnqueueSkipped(task.PersistenceId);
                        await DroppedDelivery.ReleaseAsync(task, _logger).ConfigureAwait(false);
                        return EnqueueResult.Discarded;
                    }
                }
                else
                {
                    await _taskStorage.SetQueued(task.PersistenceId, task.AuditLevel, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Storage failure before the write: nothing was enqueued, status untouched (recoverable)
                _deliveryRegistry.End(task.PersistenceId);
                throw;
            }
        }

        if (_queue.Writer.TryWrite(task))
        {
            _logger.TaskEnqueued(task.PersistenceId, Name);
            ParkingLot?.OnTaskEnqueued(task.PersistenceId);
            return EnqueueResult.Enqueued;
        }

        // The queue filled up between the capacity check and the write: revert to WaitingQueue so the task
        // stays visible to startup recovery. The registration is released only AFTER the revert, or a
        // successor delivery races it and is clobbered back to WaitingQueue.
        _logger.QueueFull(Name, task.PersistenceId);

        if (_taskStorage != null)
        {
            try
            {
                await RevertToWaitingQueueIfStillQueued(task, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.RevertStatusFailed(e, task.PersistenceId);
            }
        }

        _deliveryRegistry.End(task.PersistenceId);
        return EnqueueResult.QueueFull;
    }

    // Downgrades the row ONLY if it is still Queued: a successor delivery that already advanced it to
    // InProgress/Completed must not be clobbered back to a recoverable status, which would re-execute it
    // at the next startup recovery.
    private async Task RevertToWaitingQueueIfStillQueued(TaskHandlerExecutor task, CancellationToken cancellationToken)
    {
        var current = (await _taskStorage!.Get(t => t.Id == task.PersistenceId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault();

        // Already advanced past Queued by a successor: leave it untouched.
        if (current != null && current.Status != QueuedTaskStatus.Queued)
            return;

        await _taskStorage
              .SetStatus(task.PersistenceId, QueuedTaskStatus.WaitingQueue, null, task.AuditLevel, null, cancellationToken)
              .ConfigureAwait(false);
    }

    // Dequeue does NOT release the delivery registration: it survives the dequeue-to-execution window on
    // purpose, and is released as the LAST act of WorkerExecutor.DoWork.
    public async Task<TaskHandlerExecutor> Dequeue(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<TaskHandlerExecutor> DequeueAll(CancellationToken cancellationToken)
    {
        return _queue.Reader.ReadAllAsync(cancellationToken);
    }
}
