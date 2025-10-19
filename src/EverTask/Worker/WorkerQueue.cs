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
            _logger.LogInformation("Queuing task with id {TaskId} to queue '{QueueName}'", task.PersistenceId, Name);
            await _queue.Writer.WriteAsync(task).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to queue task with id {TaskId} to queue '{QueueName}'", task.PersistenceId, Name);
            if (_taskStorage != null)
                await _taskStorage.SetStatus(task.PersistenceId, QueuedTaskStatus.Failed, e).ConfigureAwait(false);
        }
    }

    public async Task<TaskHandlerExecutor> Dequeue(CancellationToken cancellationToken) =>
        await _queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public IAsyncEnumerable<TaskHandlerExecutor> DequeueAll(CancellationToken cancellationToken) =>
        _queue.Reader.ReadAllAsync(cancellationToken);
}
