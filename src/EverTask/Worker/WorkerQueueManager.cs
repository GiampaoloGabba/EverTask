using System.Threading.Channels;
using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverTask.Worker;

/// <summary>
/// Default implementation of IWorkerQueueManager that manages multiple execution queues.
/// </summary>
internal sealed class WorkerQueueManager : IWorkerQueueManager
{
    private readonly Dictionary<string, IWorkerQueue> _queues;
    private readonly Dictionary<string, QueueConfiguration> _configurations;
    private readonly IEverTaskLogger<WorkerQueueManager> _logger;
    private readonly IWorkerBlacklist _blacklist;
    private readonly ITaskStorage? _taskStorage;
    private readonly ILoggerFactory _loggerFactory;

    public WorkerQueueManager(
        Dictionary<string, QueueConfiguration> configurations,
        IEverTaskLogger<WorkerQueueManager> logger,
        IWorkerBlacklist blacklist,
        ILoggerFactory loggerFactory,
        ITaskStorage? taskStorage = null)
    {
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _blacklist = blacklist ?? throw new ArgumentNullException(nameof(blacklist));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _taskStorage = taskStorage;
        _queues = new Dictionary<string, IWorkerQueue>();

        // Initialize all configured queues
        foreach (var (name, config) in configurations)
        {
            var queueLogger = _loggerFactory.CreateLogger($"EverTask.Worker.WorkerQueue.{name}");
            var queue = new WorkerQueue(config, queueLogger, _blacklist, _taskStorage);
            _queues[name] = queue;
        }

        // Ensure default queue always exists
        if (!_queues.ContainsKey(QueueNames.Default))
        {
            var defaultConfig = new QueueConfiguration
            {
                Name = QueueNames.Default,
                MaxDegreeOfParallelism = 1,
                ChannelOptions = new BoundedChannelOptions(500)
                {
                    FullMode = BoundedChannelFullMode.Wait
                }
            };
            var queueLogger = _loggerFactory.CreateLogger("EverTask.Worker.WorkerQueue.default");
            _queues[QueueNames.Default] = new WorkerQueue(defaultConfig, queueLogger, _blacklist, _taskStorage);
        }
    }

    /// <inheritdoc/>
    public IWorkerQueue GetQueue(string name)
    {
        if (_queues.TryGetValue(name, out var queue))
        {
            return queue;
        }

        throw new InvalidOperationException($"Queue '{name}' does not exist. Available queues: {string.Join(", ", _queues.Keys)}");
    }

    /// <inheritdoc/>
    public bool TryGetQueue(string name, out IWorkerQueue? queue)
    {
        return _queues.TryGetValue(name, out queue);
    }

    /// <inheritdoc/>
    public async Task<bool> TryEnqueue(string? queueName, TaskHandlerExecutor task)
    {
        // Determine the target queue name with recurring task auto-routing
        string targetQueueName = DetermineQueueName(queueName, task);

        // Try to get the configuration for the target queue
        if (!_configurations.TryGetValue(targetQueueName, out var config))
        {
            _logger.LogWarning("Queue '{QueueName}' not found, falling back to 'default' queue", targetQueueName);
            targetQueueName = QueueNames.Default;
            config = _configurations[QueueNames.Default];
        }

        // Get the target queue
        if (!TryGetQueue(targetQueueName, out var targetQueue) || targetQueue == null)
        {
            _logger.LogError("Failed to get queue '{QueueName}' even after fallback", targetQueueName);
            return false;
        }

        try
        {
            // Attempt to enqueue based on the queue's full behavior
            switch (config.QueueFullBehavior)
            {
                case QueueFullBehavior.Wait:
                    // Default behavior - wait until space is available
                    await targetQueue.Queue(task).ConfigureAwait(false);
                    return true;

                case QueueFullBehavior.ThrowException:
                    // Try to queue with immediate failure if full
                    await targetQueue.Queue(task).ConfigureAwait(false);
                    return true;

                case QueueFullBehavior.FallbackToDefault:
                    // Try target queue first
                    try
                    {
                        await targetQueue.Queue(task).ConfigureAwait(false);
                        return true;
                    }
                    catch (ChannelClosedException)
                    {
                        throw; // Re-throw if channel is closed
                    }
                    catch (Exception ex) when (targetQueueName != QueueNames.Default)
                    {
                        // Fallback to default queue if target queue fails and it's not already default
                        _logger.LogWarning(ex, "Queue '{QueueName}' is full or unavailable, falling back to 'default' queue", targetQueueName);

                        if (TryGetQueue(QueueNames.Default, out var defaultQueue) && defaultQueue != null)
                        {
                            await defaultQueue.Queue(task).ConfigureAwait(false);
                            _logger.LogInformation("Task {TaskId} enqueued to 'default' queue as fallback", task.PersistenceId);
                            return true;
                        }

                        throw;
                    }

                default:
                    // Default to Wait behavior if unknown
                    await targetQueue.Queue(task).ConfigureAwait(false);
                    return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue task {TaskId} to queue '{QueueName}'", task.PersistenceId, targetQueueName);
            throw;
        }
    }

    /// <inheritdoc/>
    public IEnumerable<(string Name, IWorkerQueue Queue)> GetAllQueues()
    {
        return _queues.Select(kvp => (kvp.Key, kvp.Value));
    }

    /// <summary>
    /// Determines the appropriate queue name for a task.
    /// </summary>
    private string DetermineQueueName(string? queueName, TaskHandlerExecutor task)
    {
        // If queue name is explicitly specified, use it
        if (!string.IsNullOrEmpty(queueName))
        {
            return queueName;
        }

        // If no explicit queue name and task is recurring, route to QueueNames.Recurring queue if it exists
        if (task.RecurringTask != null && _queues.ContainsKey(QueueNames.Recurring))
        {
            return QueueNames.Recurring;
        }

        // Default to QueueNames.Default queue
        return QueueNames.Default;
    }
}
