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
        RateLimitParkingLot? parkingLot = null)
    {
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        _logger         = logger ?? throw new ArgumentNullException(nameof(logger));
        _queues         = new Dictionary<string, IWorkerQueue>();

        var blacklist1     = blacklist ?? throw new ArgumentNullException(nameof(blacklist));
        var loggerFactory1 = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

        // Initialize all configured queues
        foreach (var (name, config) in configurations)
        {
            var queueLogger = loggerFactory1.CreateLogger($"EverTask.Worker.WorkerQueue.{name}");
            var queue       = new WorkerQueue(config, queueLogger, blacklist1, taskStorage) { ParkingLot = parkingLot };
            _queues[name] = queue;
        }

        // Ensure default queue always exists
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
            _queues[QueueNames.Default] = new WorkerQueue(defaultConfig, queueLogger, blacklist1, taskStorage)
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
            // Attempt to enqueue based on the queue's full behavior
            switch (config.QueueFullBehavior)
            {
                case QueueFullBehavior.ThrowException:
                    // Try to queue immediately - throw if full
                    switch (await targetQueue.TryQueue(task, cancellationToken).ConfigureAwait(false))
                    {
                        case EnqueueResult.Enqueued:
                            return true;
                        case EnqueueResult.QueueFull:
                            throw new QueueFullException(targetQueueName, task.PersistenceId);
                        default: // Discarded (blacklisted): nothing to enqueue, not an error
                            return false;
                    }

                case QueueFullBehavior.FallbackToDefault:
                    // Try target queue first without waiting
                    switch (await targetQueue.TryQueue(task, cancellationToken).ConfigureAwait(false))
                    {
                        case EnqueueResult.Enqueued:
                            return true;
                        case EnqueueResult.Discarded: // blacklisted: must not be re-routed
                            return false;
                    }

                    // Queue is full - fallback to default queue if not already default
                    if (targetQueueName != QueueNames.Default)
                    {
                        _logger.LogWarning(
                            "Queue '{QueueName}' is full, falling back to 'default' queue for task {TaskId}",
                            targetQueueName,
                            task.PersistenceId);

                        if (TryGetQueue(QueueNames.Default, out var defaultQueue) && defaultQueue != null)
                        {
                            // Use Wait behavior for default queue to ensure task is eventually queued
                            await defaultQueue.Queue(task, cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Task {TaskId} enqueued to 'default' queue as fallback",
                                task.PersistenceId);
                            return true;
                        }

                        throw new QueueFullException(targetQueueName, task.PersistenceId,
                            "Target queue is full and default queue is unavailable");
                    }

                    // Already using default queue - throw
                    throw new QueueFullException(targetQueueName, task.PersistenceId,
                        "Default queue is full and no fallback is available");

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
            _logger.LogError(ex, "Failed to enqueue task {TaskId} to queue '{QueueName}'", task.PersistenceId,
                targetQueueName);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task EnqueueBlocking(string? queueName, TaskHandlerExecutor task, CancellationToken cancellationToken = default)
    {
        var (targetQueue, _, _) = ResolveQueue(queueName, task);
        await targetQueue.Queue(task, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<EnqueueResult> TryEnqueueImmediate(string? queueName, TaskHandlerExecutor task, CancellationToken cancellationToken = default)
    {
        var (targetQueue, _, _) = ResolveQueue(queueName, task);
        return await targetQueue.TryQueue(task, cancellationToken).ConfigureAwait(false);
    }

    private (IWorkerQueue Queue, QueueConfiguration Config, string Name) ResolveQueue(string? queueName, TaskHandlerExecutor task)
    {
        // Determine target queue name inline to avoid redundant ContainsKey check
        string targetQueueName = !string.IsNullOrEmpty(queueName)
                                     ? queueName
                                     : (task.RecurringTask != null && _queues.ContainsKey(QueueNames.Recurring)
                                            ? QueueNames.Recurring
                                            : QueueNames.Default);

        // Single dictionary lookup with fallback
        if (!_queues.TryGetValue(targetQueueName, out var targetQueue))
        {
            _logger.LogWarning("Queue '{QueueName}' not found, falling back to 'default' queue", targetQueueName);
            targetQueueName = QueueNames.Default;
            targetQueue     = GetQueue(QueueNames.Default);
        }

        // Get config directly from queue if possible, otherwise lookup
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
