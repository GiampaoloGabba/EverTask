using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EverTask.Configuration;
using EverTask.RateLimiting;

namespace EverTask.Worker;

public class WorkerQueue : IWorkerQueue
{
    private readonly Channel<TaskHandlerExecutor> _queue;
    private readonly ILogger _logger;
    private readonly IWorkerBlacklist _workerBlacklist;
    private readonly ITaskStorage? _taskStorage;

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
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerBlacklist = workerBlacklist ?? throw new ArgumentNullException(nameof(workerBlacklist));
        _taskStorage = taskStorage;
        _deliveryRegistry = deliveryRegistry ?? new TaskDeliveryRegistry();

        Name = configuration.Name;
        // The itemDropped callback releases the delivery registration for items silently dropped
        // by the Drop* full modes (never invoked under FullMode.Wait): without it a dropped item
        // would leave its id permanently registered, blocking later re-deliveries
        _queue = Channel.CreateBounded<TaskHandlerExecutor>(
            configuration.ChannelOptions,
            dropped => _deliveryRegistry.End(dropped.PersistenceId));
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

    private async ValueTask QueueCore(TaskHandlerExecutor task, bool enforceRecoverable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (_workerBlacklist.IsBlacklisted(task.PersistenceId))
            return;

        // A delivery of this id is already in flight in this process (in a channel or executing):
        // idempotent no-op, single execution. This is the write-boundary defense against the
        // recovery-vs-live-dispatch double delivery.
        if (!_deliveryRegistry.TryBegin(task.PersistenceId))
        {
            _logger.LogDebug("Task {TaskId} already has a delivery in flight, skipping duplicate enqueue to queue '{QueueName}'",
                task.PersistenceId, Name);
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
                    if (!await _taskStorage.TrySetQueuedIfRecoverable(task.PersistenceId, task.AuditLevel, cancellationToken).ConfigureAwait(false))
                    {
                        _deliveryRegistry.End(task.PersistenceId);
                        _logger.LogDebug("Task {TaskId} is no longer recoverable, recovery enqueue skipped", task.PersistenceId);
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
            _logger.LogDebug("Queuing task with id {TaskId} to queue '{QueueName}'", task.PersistenceId, Name);
            await _queue.Writer.WriteAsync(task, cancellationToken).ConfigureAwait(false);
            ParkingLot?.OnTaskEnqueued(task.PersistenceId);
        }
        catch (OperationCanceledException)
        {
            // The caller abandoned the wait (aborted request or host shutdown).
            // The task is persisted with status Queued and is re-enqueued by startup recovery,
            // so it must NOT be marked as failed here.
            _deliveryRegistry.End(task.PersistenceId);
            _logger.LogWarning(
                "Enqueue of task {TaskId} to queue '{QueueName}' was cancelled while waiting for space. " +
                "The task remains persisted and will be recovered at startup", task.PersistenceId, Name);
            throw;
        }
        catch (Exception e)
        {
            _deliveryRegistry.End(task.PersistenceId);
            _logger.LogError(e, "Unable to queue task with id {TaskId} to queue '{QueueName}'", task.PersistenceId, Name);
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

        if (_workerBlacklist.IsBlacklisted(task.PersistenceId))
            return EnqueueResult.Discarded;

        // Fast path: skip the storage round-trips below while the queue is saturated.
        // Callers that retry (scheduler backoff) would otherwise churn the storage on every attempt.
        // Only meaningful with FullMode=Wait: with the Drop* modes TryWrite never rejects.
        if (Configuration.ChannelOptions.FullMode == BoundedChannelFullMode.Wait
            && _queue.Reader.CanCount && _queue.Reader.Count >= Capacity)
        {
            _logger.LogDebug("Queue '{QueueName}' is full, cannot enqueue task {TaskId}", Name, task.PersistenceId);
            return EnqueueResult.QueueFull;
        }

        // A delivery of this id is already in flight in this process: NOT a success lie — the
        // caller decides (schedulers retry shortly like QueueFull, because their slot may have
        // fired while the previous delivery of the same task was still unwinding; live dispatch
        // treats it as idempotent success).
        if (!_deliveryRegistry.TryBegin(task.PersistenceId))
        {
            _logger.LogDebug("Task {TaskId} already has a delivery in flight, not enqueued to queue '{QueueName}'",
                task.PersistenceId, Name);
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
                    if (!await _taskStorage.TrySetQueuedIfRecoverable(task.PersistenceId, task.AuditLevel, cancellationToken).ConfigureAwait(false))
                    {
                        _deliveryRegistry.End(task.PersistenceId);
                        _logger.LogDebug("Task {TaskId} is no longer recoverable, scheduler enqueue skipped", task.PersistenceId);
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
            _logger.LogDebug("Task {TaskId} successfully enqueued to queue '{QueueName}'", task.PersistenceId, Name);
            ParkingLot?.OnTaskEnqueued(task.PersistenceId);
            return EnqueueResult.Enqueued;
        }

        // The queue filled up between the capacity check and the write: revert to WaitingQueue so the
        // task stays visible to startup recovery instead of looking enqueued forever. The revert is
        // CONDITIONAL (compare-and-set: only if the row is still Queued) and the delivery registration
        // is released ONLY AFTER it — so a successor delivery in this window is rejected as a duplicate
        // instead of racing the revert and being clobbered back to WaitingQueue (CU1/L21).
        _logger.LogDebug("Queue '{QueueName}' is full, cannot enqueue task {TaskId}", Name, task.PersistenceId);

        if (_taskStorage != null)
        {
            try
            {
                await RevertToWaitingQueueIfStillQueued(task, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e,
                    "Failed to revert status for task {TaskId} after full-queue write failure (status remains Queued, " +
                    "the task will still be recovered at startup)", task.PersistenceId);
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
