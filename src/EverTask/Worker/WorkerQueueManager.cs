using EverTask.Configuration;
using EverTask.RateLimiting;

namespace EverTask.Worker;

/// <summary>
/// Default implementation of IWorkerQueueManager that manages multiple execution queues.
/// </summary>
internal sealed class WorkerQueueManager : IWorkerQueueManager
{
    private readonly Dictionary<string, IWorkerQueue> _queues;
    private readonly Dictionary<string, QueueConfiguration> _configurations;
    private readonly IEverTaskLogger<WorkerQueueManager> _logger;

    public WorkerQueueManager(
        Dictionary<string, QueueConfiguration> configurations,
        IEverTaskLogger<WorkerQueueManager> logger,
        IWorkerBlacklist blacklist,
        ILoggerFactory loggerFactory,
        ITaskStorage? taskStorage = null,
        RateLimitParkingLot? parkingLot = null,
        TaskDeliveryRegistry? deliveryRegistry = null,
        TimeProvider? timeProvider = null)
    {
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        _logger         = logger ?? throw new ArgumentNullException(nameof(logger));
        _queues         = new Dictionary<string, IWorkerQueue>();

        var blacklist1     = blacklist ?? throw new ArgumentNullException(nameof(blacklist));
        var loggerFactory1 = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

        // ONE delivery registry shared by every queue of this host: the in-process double-delivery
        // defense must be global (a task rerouted to another queue is the same task)
        var registry = deliveryRegistry ?? new TaskDeliveryRegistry();

        foreach (var (name, config) in configurations)
        {
            var queueLogger = loggerFactory1.CreateLogger($"EverTask.Worker.WorkerQueue.{name}");
            var queue       = new WorkerQueue(config, queueLogger, blacklist1, taskStorage, registry, timeProvider) { ParkingLot = parkingLot };
            _queues[name] = queue;
        }

        if (!_queues.ContainsKey(QueueNames.Default))
        {
            var defaultConfig = new QueueConfiguration
            {
                Name                   = QueueNames.Default,
                MaxDegreeOfParallelism = 1,
                ChannelOptions = new BoundedChannelOptions(500)
                {
                    FullMode = BoundedChannelFullMode.Wait
                }
            };
            var queueLogger = loggerFactory1.CreateLogger("EverTask.Worker.WorkerQueue.default");
            _queues[QueueNames.Default] = new WorkerQueue(defaultConfig, queueLogger, blacklist1, taskStorage, registry, timeProvider)
            {
                ParkingLot = parkingLot
            };
        }
    }

    /// <inheritdoc/>
    public IWorkerQueue GetQueue(string name)
    {
        if (_queues.TryGetValue(name, out var queue))
        {
            return queue;
        }

        throw new InvalidOperationException(
            $"Queue '{name}' does not exist. Available queues: {string.Join(", ", _queues.Keys)}");
    }

    /// <inheritdoc/>
    public bool TryGetQueue(string name, out IWorkerQueue? queue)
    {
        return _queues.TryGetValue(name, out queue);
    }

    /// <inheritdoc/>
    public async Task<bool> TryEnqueue(string? queueName, TaskHandlerExecutor task, CancellationToken cancellationToken = default)
    {
        var (targetQueue, config, targetQueueName) = ResolveQueue(queueName, task);

        try
        {
            switch (config.QueueFullBehavior)
            {
                case QueueFullBehavior.ThrowException:
                    switch (await targetQueue.TryQueue(task, cancellationToken).ConfigureAwait(false))
                    {
                        case EnqueueResult.Enqueued:
                            return true;
                        case EnqueueResult.DuplicateInProcess: // already in flight: idempotent success
                            // The executor's life ends here — unlike a scheduler, this caller never retries
                            // the same instance — so the eager scope it carries is released.
                            await DroppedDelivery.ReleaseAsync(task, _logger).ConfigureAwait(false);
                            return true;
                        case EnqueueResult.QueueFull:
                            await DroppedDelivery.ReleaseAsync(task, _logger).ConfigureAwait(false);
                            throw new QueueFullException(targetQueueName, task.PersistenceId);
                        default: // Discarded (blacklisted): nothing to enqueue, not an error
                            return false;
                    }

                case QueueFullBehavior.FallbackToDefault:
                    switch (await targetQueue.TryQueue(task, cancellationToken).ConfigureAwait(false))
                    {
                        case EnqueueResult.Enqueued:
                            return true;
                        case EnqueueResult.DuplicateInProcess: // already in flight: must NOT be re-routed
                            await DroppedDelivery.ReleaseAsync(task, _logger).ConfigureAwait(false);
                            return true;
                        case EnqueueResult.Discarded: // blacklisted: must not be re-routed
                            return false;
                    }

                    if (targetQueueName != QueueNames.Default)
                    {
                        _logger.QueueFullFallingBackToDefault(targetQueueName, task.PersistenceId);

                        if (TryGetQueue(QueueNames.Default, out var defaultQueue) && defaultQueue != null)
                        {
                            // Use Wait behavior for default queue to ensure task is eventually queued
                            await defaultQueue.Queue(task, cancellationToken).ConfigureAwait(false);
                            _logger.EnqueuedToDefaultFallback(task.PersistenceId);
                            return true;
                        }

                        await DroppedDelivery.ReleaseAsync(task, _logger).ConfigureAwait(false);

                        throw new QueueFullException(targetQueueName, task.PersistenceId,
                            "Target queue is full and default queue is unavailable");
                    }

                    // Already on the Default queue: FallbackToDefault is a self-reference here, so apply Wait
                    // backpressure instead of throwing QueueFullException at the caller.
                    await targetQueue.Queue(task, cancellationToken).ConfigureAwait(false);
                    return true;

                case QueueFullBehavior.Wait:
                default:
                    // Wait until space is available (cancellable backpressure)
                    await targetQueue.Queue(task, cancellationToken).ConfigureAwait(false);
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            // Caller abandoned the wait: the task stays persisted and is recovered at startup.
            throw;
        }
        catch (Exception ex)
        {
            _logger.EnqueueFailed(ex, task.PersistenceId, targetQueueName);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task EnqueueBlocking(string? queueName, TaskHandlerExecutor task, CancellationToken cancellationToken = default)
    {
        var (targetQueue, _, _) = ResolveQueue(queueName, task);

        // Startup-recovery entry point: the SetQueued transition must be conditional on the row still being
        // recoverable, since a live copy may have terminally finished after the recovery's page read.
        if (targetQueue is WorkerQueue workerQueue)
            await workerQueue.QueueForRecovery(task, cancellationToken).ConfigureAwait(false);
        else
            await targetQueue.Queue(task, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<EnqueueResult> TryEnqueueImmediate(string? queueName, TaskHandlerExecutor task, CancellationToken cancellationToken = default)
    {
        var (targetQueue, _, _) = ResolveQueue(queueName, task);

        // Scheduler dispatch (a due slot fires): the SetQueued transition must be conditional on the row
        // still being recoverable, so a stale slot never resurrects a row that terminally finished.
        if (targetQueue is WorkerQueue workerQueue)
            return await workerQueue.TryQueueForRecovery(task, cancellationToken).ConfigureAwait(false);

        return await targetQueue.TryQueue(task, cancellationToken).ConfigureAwait(false);
    }

    private (IWorkerQueue Queue, QueueConfiguration Config, string Name) ResolveQueue(string? queueName, TaskHandlerExecutor task)
    {
        var targetQueueName = !string.IsNullOrEmpty(queueName)
                                     ? queueName
                                     : (task.RecurringTask != null && _queues.ContainsKey(QueueNames.Recurring)
                                            ? QueueNames.Recurring
                                            : QueueNames.Default);

        if (!_queues.TryGetValue(targetQueueName, out var targetQueue))
        {
            _logger.QueueNotFoundFallingBackToDefault(targetQueueName);
            targetQueueName = QueueNames.Default;
            targetQueue     = GetQueue(QueueNames.Default);
        }

        var config = targetQueue switch
        {
            WorkerQueue wq => wq.Configuration,
            _ => _configurations.TryGetValue(targetQueueName, out var cfg)
                     ? cfg
                     : _configurations[QueueNames.Default]
        };

        return (targetQueue, config, targetQueueName);
    }

    /// <inheritdoc/>
    public IEnumerable<(string Name, IWorkerQueue Queue)> GetAllQueues()
    {
        return _queues.Select(kvp => (kvp.Key, kvp.Value));
    }
}
