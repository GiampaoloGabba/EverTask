namespace EverTask.Configuration;

/// <summary>
/// Configuration for an individual execution queue.
/// </summary>
public class QueueConfiguration
{
    /// <summary>
    /// Gets or sets the name of the queue.
    /// </summary>
    public string Name { get; set; } = QueueNames.Default;

    /// <summary>
    /// Gets or sets the maximum degree of parallelism for task execution in this queue.
    /// Default is 1 for sequential execution.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>
    /// Gets or sets the channel options for the bounded queue.
    ///
    /// Default capacity: 2000 items with Wait behavior when full.
    /// This provides a balance between memory usage and spike handling capability.
    ///
    /// Configuration guidelines:
    /// - Small projects / low throughput: 500-1000
    /// - Medium projects / moderate spikes: 2000-5000 (default)
    /// - High-throughput / large spikes: 10000+
    ///
    /// Backpressure behavior:
    /// When the channel is full, the dispatcher will block until space becomes available.
    /// This is intentional backpressure to protect against memory exhaustion during traffic spikes.
    /// Tasks are persisted to storage before entering the channel, ensuring no data loss.
    /// Any tasks not processed before shutdown are recovered via ProcessPendingAsync on restart.
    ///
    /// Channel configuration:
    /// - SingleReader = false: N competing consumers (Microsoft-recommended pattern for parallel consumption)
    /// - SingleWriter = true: Typically one dispatcher writes (can be false if multiple concurrent writers)
    /// - AllowSynchronousContinuations = false: Safer default, prevents consumer work from blocking writer thread
    /// </summary>
    public BoundedChannelOptions ChannelOptions { get; set; } = new(2000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,  // N consumers compete for items
        SingleWriter = true,   // Typically one dispatcher writes
        AllowSynchronousContinuations = false  // Safer default
    };

    /// <summary>
    /// Gets or sets the default retry policy for tasks in this queue.
    /// If null, uses the global default retry policy.
    /// </summary>
    public IRetryPolicy? DefaultRetryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the default timeout for tasks in this queue.
    /// If null, tasks have no timeout unless specified by the handler.
    /// </summary>
    public TimeSpan? DefaultTimeout { get; set; }

    /// <summary>
    /// Gets or sets the behavior when the queue is full.
    /// Default is FallbackToDefault for graceful degradation.
    /// </summary>
    public QueueFullBehavior QueueFullBehavior { get; set; } = QueueFullBehavior.FallbackToDefault;

    /// <summary>
    /// Creates a copy of this configuration.
    /// </summary>
    public QueueConfiguration Clone()
    {
        return new QueueConfiguration
        {
            Name = Name,
            MaxDegreeOfParallelism = MaxDegreeOfParallelism,
            ChannelOptions = new BoundedChannelOptions(ChannelOptions.Capacity)
            {
                FullMode = ChannelOptions.FullMode,
                AllowSynchronousContinuations = ChannelOptions.AllowSynchronousContinuations,
                SingleReader = ChannelOptions.SingleReader,
                SingleWriter = ChannelOptions.SingleWriter
            },
            DefaultRetryPolicy = DefaultRetryPolicy,
            DefaultTimeout = DefaultTimeout,
            QueueFullBehavior = QueueFullBehavior
        };
    }

    /// <summary>
    /// Fluent method to set the maximum degree of parallelism.
    /// </summary>
    public QueueConfiguration SetMaxDegreeOfParallelism(int parallelism)
    {
        MaxDegreeOfParallelism = parallelism;
        return this;
    }

    /// <summary>
    /// Fluent method to set the channel capacity.
    /// </summary>
    public QueueConfiguration SetChannelCapacity(int capacity)
    {
        ChannelOptions.Capacity = capacity;
        return this;
    }

    /// <summary>
    /// Fluent method to set the channel options.
    /// </summary>
    public QueueConfiguration SetChannelOptions(BoundedChannelOptions options)
    {
        ChannelOptions = options;
        return this;
    }

    /// <summary>
    /// Fluent method to set the default retry policy.
    /// </summary>
    public QueueConfiguration SetDefaultRetryPolicy(IRetryPolicy? policy)
    {
        DefaultRetryPolicy = policy;
        return this;
    }

    /// <summary>
    /// Fluent method to set the default timeout.
    /// </summary>
    public QueueConfiguration SetDefaultTimeout(TimeSpan? timeout)
    {
        DefaultTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Fluent method to set the queue full behavior.
    /// </summary>
    public QueueConfiguration SetFullBehavior(QueueFullBehavior behavior)
    {
        QueueFullBehavior = behavior;
        return this;
    }
}