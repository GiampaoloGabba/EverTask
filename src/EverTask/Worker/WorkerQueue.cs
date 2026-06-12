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

    // PersistenceIds currently written to the channel and not yet dequeued.
    // Prevents double execution when the same task is enqueued twice in this process
    // (e.g. startup recovery racing a live dispatch of the same persisted task).
    private readonly ConcurrentDictionary<Guid, byte> _inChannel = new();

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
        ITaskStorage? taskStorage = null)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerBlacklist = workerBlacklist ?? throw new ArgumentNullException(nameof(workerBlacklist));
        _taskStorage = taskStorage;

        Name = configuration.Name;
        _queue = Channel.CreateBounded<TaskHandlerExecutor>(configuration.ChannelOptions);
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

    public async ValueTask Queue(TaskHandlerExecutor task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (_workerBlacklist.IsBlacklisted(task.PersistenceId))
            return;

        // Already pending in the channel (duplicate enqueue of the same persisted task,
        // e.g. recovery racing a live dispatch): idempotent no-op, single execution.
        if (!_inChannel.TryAdd(task.PersistenceId, 0))
        {
            _logger.LogDebug("Task {TaskId} is already pending in queue '{QueueName}', skipping duplicate enqueue",
                task.PersistenceId, Name);
            return;
        }

        if (_taskStorage != null)
        {
            try
            {
                await _taskStorage.SetQueued(task.PersistenceId, task.AuditLevel, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Storage failure before the write: the task was never enqueued, the status is
                // untouched (still recoverable). Propagate without marking Failed.
                _inChannel.TryRemove(task.PersistenceId, out _);
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
            _inChannel.TryRemove(task.PersistenceId, out _);
            _logger.LogWarning(
                "Enqueue of task {TaskId} to queue '{QueueName}' was cancelled while waiting for space. " +
                "The task remains persisted and will be recovered at startup", task.PersistenceId, Name);
            throw;
        }
        catch (Exception e)
        {
            _inChannel.TryRemove(task.PersistenceId, out _);
            _logger.LogError(e, "Unable to queue task with id {TaskId} to queue '{QueueName}'", task.PersistenceId, Name);
            if (_taskStorage != null)
                await _taskStorage.SetStatus(task.PersistenceId, QueuedTaskStatus.Failed, e, task.AuditLevel).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<EnqueueResult> TryQueue(TaskHandlerExecutor task, CancellationToken cancellationToken = default)
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

        // Already pending in the channel (duplicate enqueue of the same persisted task):
        // idempotent success, single execution.
        if (!_inChannel.TryAdd(task.PersistenceId, 0))
        {
            _logger.LogDebug("Task {TaskId} is already pending in queue '{QueueName}', skipping duplicate enqueue",
                task.PersistenceId, Name);
            return EnqueueResult.Enqueued;
        }

        // Mark as Queued BEFORE writing: once the task is in the channel a consumer can execute it
        // immediately, and a late SetQueued would overwrite InProgress/Completed (causing a duplicate
        // re-execution at the next startup recovery).
        if (_taskStorage != null)
        {
            try
            {
                await _taskStorage.SetQueued(task.PersistenceId, task.AuditLevel, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Storage failure before the write: nothing was enqueued, status untouched (recoverable)
                _inChannel.TryRemove(task.PersistenceId, out _);
                throw;
            }
        }

        if (_queue.Writer.TryWrite(task))
        {
            _logger.LogDebug("Task {TaskId} successfully enqueued to queue '{QueueName}'", task.PersistenceId, Name);
            ParkingLot?.OnTaskEnqueued(task.PersistenceId);
            return EnqueueResult.Enqueued;
        }

        // The queue filled up between the capacity check and the write: revert to WaitingQueue
        // so the task stays visible to startup recovery instead of looking enqueued forever.
        _inChannel.TryRemove(task.PersistenceId, out _);
        _logger.LogDebug("Queue '{QueueName}' is full, cannot enqueue task {TaskId}", Name, task.PersistenceId);

        if (_taskStorage != null)
        {
            try
            {
                await _taskStorage
                      .SetStatus(task.PersistenceId, QueuedTaskStatus.WaitingQueue, null, task.AuditLevel, null, cancellationToken)
                      .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e,
                    "Failed to revert status for task {TaskId} after full-queue write failure (status remains Queued, " +
                    "the task will still be recovered at startup)", task.PersistenceId);
            }
        }

        return EnqueueResult.QueueFull;
    }

    public async Task<TaskHandlerExecutor> Dequeue(CancellationToken cancellationToken)
    {
        var item = await _queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        _inChannel.TryRemove(item.PersistenceId, out _);
        return item;
    }

    public async IAsyncEnumerable<TaskHandlerExecutor> DequeueAll(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            _inChannel.TryRemove(item.PersistenceId, out _);
            yield return item;
        }
    }
}
