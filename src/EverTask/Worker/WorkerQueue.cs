using EverTask.Configuration;

namespace EverTask.Worker;

public class WorkerQueue : IWorkerQueue
{
    private readonly Channel<TaskHandlerExecutor> _queue;
    private readonly ILogger _logger;
    private readonly IWorkerBlacklist _workerBlacklist;
    private readonly ITaskStorage? _taskStorage;

    /// <summary>
    /// Gets the name of this queue.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the configuration for this queue.
    /// </summary>
    public QueueConfiguration Configuration { get; }

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

    public async ValueTask Queue(TaskHandlerExecutor task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (_workerBlacklist.IsBlacklisted(task.PersistenceId))
            return;

        if (_taskStorage != null)
            await _taskStorage.SetQueued(task.PersistenceId).ConfigureAwait(false);
        try
        {
            _logger.LogDebug("Queuing task with id {TaskId} to queue '{QueueName}'", task.PersistenceId, Name);
            await _queue.Writer.WriteAsync(task).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to queue task with id {TaskId} to queue '{QueueName}'", task.PersistenceId, Name);
            if (_taskStorage != null)
                await _taskStorage.SetStatus(task.PersistenceId, QueuedTaskStatus.Failed, e, task.AuditLevel).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> TryQueue(TaskHandlerExecutor task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (_workerBlacklist.IsBlacklisted(task.PersistenceId))
            return false;

        // Try to write without waiting - returns false if queue is full
        if (!_queue.Writer.TryWrite(task))
        {
            _logger.LogDebug("Queue '{QueueName}' is full, cannot enqueue task {TaskId}", Name, task.PersistenceId);
            return false;
        }

        // Successfully queued - update storage
        _logger.LogDebug("Task {TaskId} successfully enqueued to queue '{QueueName}'", task.PersistenceId, Name);

        if (_taskStorage != null)
        {
            try
            {
                await _taskStorage.SetQueued(task.PersistenceId).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to update storage for task {TaskId} (task is queued but storage update failed)", task.PersistenceId);
                // Don't fail the operation - task is already in queue
            }
        }

        return true;
    }

    public async Task<TaskHandlerExecutor> Dequeue(CancellationToken cancellationToken) =>
        await _queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public IAsyncEnumerable<TaskHandlerExecutor> DequeueAll(CancellationToken cancellationToken) =>
        _queue.Reader.ReadAllAsync(cancellationToken);
}
