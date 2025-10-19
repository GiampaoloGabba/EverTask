using System.Threading.Channels;
using EverTask.Abstractions;
using EverTask.Resilience;

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
    /// Default capacity is 500 with Wait behavior when full.
    /// </summary>
    public BoundedChannelOptions ChannelOptions { get; set; } = new(500)
    {
        FullMode = BoundedChannelFullMode.Wait
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