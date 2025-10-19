using EverTask.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

public class EverTaskServiceBuilder
{
    public IServiceCollection Services { get; private set; }
    private readonly EverTaskServiceConfiguration _configuration;

    internal EverTaskServiceBuilder(IServiceCollection services, EverTaskServiceConfiguration configuration)
    {
        Services = services;
        _configuration = configuration;
    }

    /// <summary>
    /// Configures the default queue settings.
    /// </summary>
    /// <param name="configure">Action to configure the default queue.</param>
    /// <returns>The service builder for method chaining.</returns>
    public EverTaskServiceBuilder ConfigureDefaultQueue(Action<QueueConfiguration> configure)
    {
        if (!_configuration.Queues.TryGetValue(QueueNames.Default, out var defaultQueue))
        {
            defaultQueue = new QueueConfiguration
            {
                Name = QueueNames.Default,
                MaxDegreeOfParallelism = _configuration.MaxDegreeOfParallelism,
                ChannelOptions = _configuration.ChannelOptions,
                DefaultRetryPolicy = _configuration.DefaultRetryPolicy,
                DefaultTimeout = _configuration.DefaultTimeout
            };
            _configuration.Queues[QueueNames.Default] = defaultQueue;
        }

        configure(defaultQueue);
        return this;
    }

    /// <summary>
    /// Adds a new custom queue with the specified configuration.
    /// </summary>
    /// <param name="name">The name of the queue.</param>
    /// <param name="configure">Action to configure the queue.</param>
    /// <returns>The service builder for method chaining.</returns>
    public EverTaskServiceBuilder AddQueue(string name, Action<QueueConfiguration>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Queue name cannot be null or empty.", nameof(name));

        var queueConfig = new QueueConfiguration
        {
            Name = name,
            MaxDegreeOfParallelism = 1,
            ChannelOptions = new System.Threading.Channels.BoundedChannelOptions(500)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
            },
            QueueFullBehavior = QueueFullBehavior.FallbackToDefault
        };

        configure?.Invoke(queueConfig);
        _configuration.Queues[name] = queueConfig;

        return this;
    }

    /// <summary>
    /// Configures the recurring tasks queue. If not configured, defaults to the same settings as the default queue.
    /// </summary>
    /// <param name="configure">Action to configure the recurring queue.</param>
    /// <returns>The service builder for method chaining.</returns>
    public EverTaskServiceBuilder ConfigureRecurringQueue(Action<QueueConfiguration> configure)
    {
        if (!_configuration.Queues.TryGetValue(QueueNames.Recurring, out var recurringQueue))
        {
            // Clone default queue configuration
            var defaultQueue = _configuration.Queues.ContainsKey(QueueNames.Default)
                ? _configuration.Queues[QueueNames.Default]
                : new QueueConfiguration
                {
                    Name = QueueNames.Default,
                    MaxDegreeOfParallelism = _configuration.MaxDegreeOfParallelism,
                    ChannelOptions = _configuration.ChannelOptions,
                    DefaultRetryPolicy = _configuration.DefaultRetryPolicy,
                    DefaultTimeout = _configuration.DefaultTimeout
                };

            recurringQueue = defaultQueue.Clone();
            recurringQueue.Name = QueueNames.Recurring;
            _configuration.Queues[QueueNames.Recurring] = recurringQueue;
        }

        configure(recurringQueue);
        return this;
    }

    /// <summary>
    /// Creates the recurring queue with default settings if it doesn't exist.
    /// This is called automatically when any recurring task is dispatched.
    /// </summary>
    /// <returns>The service builder for method chaining.</returns>
    public EverTaskServiceBuilder EnsureRecurringQueue()
    {
        if (!_configuration.Queues.ContainsKey(QueueNames.Recurring))
        {
            var defaultQueue = _configuration.Queues.ContainsKey(QueueNames.Default)
                ? _configuration.Queues[QueueNames.Default]
                : new QueueConfiguration
                {
                    Name = QueueNames.Default,
                    MaxDegreeOfParallelism = _configuration.MaxDegreeOfParallelism,
                    ChannelOptions = _configuration.ChannelOptions,
                    DefaultRetryPolicy = _configuration.DefaultRetryPolicy,
                    DefaultTimeout = _configuration.DefaultTimeout
                };

            var recurringQueue = defaultQueue.Clone();
            recurringQueue.Name = QueueNames.Recurring;
            _configuration.Queues[QueueNames.Recurring] = recurringQueue;
        }

        return this;
    }
}
