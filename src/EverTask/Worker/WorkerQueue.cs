using EverTask.Configuration;
using EverTask.RateLimiting;

namespace EverTask.Worker;

public class WorkerQueue : IWorkerQueue
{
    private readonly Channel<TaskHandlerExecutor> _queue;
    private readonly ILogger _logger;
    private readonly IWorkerBlacklist _workerBlacklist;
    private readonly ITaskStorage? _taskStorage;

    // The scheduling clock (P9): the conditional recovery transition must judge RunUntil against the SAME
    // instant as the recovery filter that selected the row, never the storage's own reading of the clock.
    private readonly TimeProvider _timeProvider;

    // Per-process delivery registry: an id is registered from the channel write until its
    // delivery terminally ends (WorkerExecutor.DoWork outer finally). A second write of the
    // same id is rejected here, which makes in-process double delivery impossible by
    // construction (startup recovery racing a live dispatch, scheduler slot fires, taskKey
    // re-dispatch). Shared across all queues of the host; hand-constructed queues (tests)
    // fall back to a private instance.
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
    /// Optional parking-lot accounting hook (set by WorkerQueueManager): a successful channel
    /// write un-parks the task — the consumer-independent decrement that keeps the L2
    /// backpressure from wedging. No-op for tasks that were never parked.
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
    /// on an explicit scheduling clock (P9). The pre-P9 arity above is kept as a real overload so an assembly
    /// compiled against the previous release still binds (P6/X6).
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
        // The itemDropped callback fires for items silently dropped by the Drop* full modes (never
        // invoked under FullMode.Wait): it releases the delivery registration (or the dropped id would
        // stay registered forever, blocking later re-deliveries) AND reverts the victim's storage row
        // to WaitingQueue so it stays recoverable instead of being silently lost (CU5/L12).
        _queue = Channel.CreateBounded<TaskHandlerExecutor>(
            configuration.ChannelOptions,
            OnItemDropped);
    }

    private void OnItemDropped(TaskHandlerExecutor dropped)
    {
        _deliveryRegistry.End(dropped.PersistenceId);

        // A Drop* full mode evicted a persisted task whose storage row is still Queued (it looks
        // enqueued forever and never runs in this process). Revert it to WaitingQueue so startup
        // recovery rescues it. Best-effort fire-and-forget — the channel drop callback is synchronous.
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
    /// Blocking enqueue used by the startup recovery: the SetQueued transition is CONDITIONAL on
    /// the row still being in a recoverable status, so a task whose live copy terminally finished
    /// after the recovery's page read is never resurrected.
    /// </summary>
    internal ValueTask QueueForRecovery(TaskHandlerExecutor task, CancellationToken cancellationToken = default)
        => QueueCore(task, enforceRecoverable: true, cancellationToken);

    /// <summary>
    /// Whether this delivery has been cancelled — by its own id, or by the DURABLE SCHEDULE it is an
    /// occurrence of.
    /// </summary>
    /// <remarks>
    /// Cancelling a schedule cancels its pending occurrences in storage, but an occurrence already parked in
    /// the scheduler carries no blacklist entry of its own: without the second check its enqueue would write
    /// Queued over the Cancelled the cancel had just persisted, and it would run. The schedule's entry covers
    /// every occurrence it produced, so it is never consumed here.
    /// </remarks>
    private bool IsCancelled(TaskHandlerExecutor task) =>
        _workerBlacklist.IsBlacklisted(task.PersistenceId)
        || (task.ParentTaskId is { } scheduleId && _workerBlacklist.IsBlacklisted(scheduleId));

    private async ValueTask QueueCore(TaskHandlerExecutor task, bool enforceRecoverable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (IsCancelled(task))
            return;

        // A delivery of this id is already in flight in this process (in a channel or executing):
        // idempotent no-op, single execution. This is the write-boundary defense against the
        // recovery-vs-live-dispatch double delivery.
        if (!_deliveryRegistry.TryBegin(task.PersistenceId))
        {
            _logger.DuplicateEnqueueSkipped(task.PersistenceId, Name);
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
    /// CONDITIONAL on the row still being recoverable, so a stale slot for a row that terminally
    /// finished after its registration is never resurrected (the scheduler-boundary analogue of the
    /// startup-recovery defense). Returns <see cref="EnqueueResult.Discarded"/> when the row is no
    /// longer recoverable (nothing is written anywhere).
    /// </summary>
    internal ValueTask<EnqueueResult> TryQueueForRecovery(TaskHandlerExecutor task, CancellationToken cancellationToken = default)
        => TryQueueCore(task, enforceRecoverable: true, cancellationToken);

    private async ValueTask<EnqueueResult> TryQueueCore(TaskHandlerExecutor task, bool enforceRecoverable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (IsCancelled(task))
            return EnqueueResult.Discarded;

        // Fast path: skip the storage round-trips below while the queue is saturated.
        // Callers that retry (scheduler backoff) would otherwise churn the storage on every attempt.
        // Only meaningful with FullMode=Wait: with the Drop* modes TryWrite never rejects.
        if (Configuration.ChannelOptions.FullMode == BoundedChannelFullMode.Wait
            && _queue.Reader.CanCount && _queue.Reader.Count >= Capacity)
        {
            _logger.QueueFull(Name, task.PersistenceId);
            return EnqueueResult.QueueFull;
        }

        // A delivery of this id is already in flight in this process: NOT a success lie — the
        // caller decides (schedulers retry shortly like QueueFull, because their slot may have
        // fired while the previous delivery of the same task was still unwinding; live dispatch
        // treats it as idempotent success).
        if (!_deliveryRegistry.TryBegin(task.PersistenceId))
        {
            _logger.DuplicateDeliveryNotEnqueued(task.PersistenceId, Name);
            return EnqueueResult.DuplicateInProcess;
        }

        // Mark as Queued BEFORE writing: once the task is in the channel a consumer can execute it
        // immediately, and a late SetQueued would overwrite InProgress/Completed (causing a duplicate
        // re-execution at the next startup recovery).
        if (_taskStorage != null)
        {
            try
            {
                if (enforceRecoverable)
                {
                    // Scheduler slot fired: only transition if the row is still recoverable. Refused =
                    // the row terminally finished since the slot was registered — release the
                    // registration and skip (nothing was written anywhere).
                    if (!await _taskStorage.TrySetQueuedIfRecoverable(_timeProvider.GetUtcNow(), task.PersistenceId, task.AuditLevel, cancellationToken).ConfigureAwait(false))
                    {
                        _deliveryRegistry.End(task.PersistenceId);
                        _logger.SchedulerEnqueueSkipped(task.PersistenceId);
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

        // The queue filled up between the capacity check and the write: revert to WaitingQueue so the
        // task stays visible to startup recovery instead of looking enqueued forever. The revert is
        // CONDITIONAL (compare-and-set: only if the row is still Queued) and the delivery registration
        // is released ONLY AFTER it — so a successor delivery in this window is rejected as a duplicate
        // instead of racing the revert and being clobbered back to WaitingQueue (CU1/L21).
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

    /// <summary>
    /// Compare-and-set revert: downgrades the row to WaitingQueue ONLY if it is still Queued (the
    /// status this enqueue wrote). A successor delivery that already advanced it to InProgress/
    /// Completed must not be clobbered back to a recoverable status — that lost update would
    /// re-execute it at the next startup recovery (CU1/L21).
    /// </summary>
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

    // NOTE: dequeue does NOT release the delivery registration. The registration survives the
    // dequeue->execution window on purpose (it is what makes a concurrent recovery re-delivery
    // impossible) and is released as the LAST act of WorkerExecutor.DoWork.
    public async Task<TaskHandlerExecutor> Dequeue(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<TaskHandlerExecutor> DequeueAll(CancellationToken cancellationToken)
    {
        return _queue.Reader.ReadAllAsync(cancellationToken);
    }
}
