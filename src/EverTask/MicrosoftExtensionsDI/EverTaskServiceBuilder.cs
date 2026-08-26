using EverTask.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

public class EverTaskServiceBuilder
{
    /// <summary>
    /// Gets the service collection to which EverTask services are being added.
    /// Use this to register additional services or customize the service registration.
    /// </summary>
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
            ChannelOptions = new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.Wait
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
            var defaultQueue = _configuration.Queues.TryGetValue(QueueNames.Default, out var configuredDefault)
                ? configuredDefault
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
    /// Registers an <see cref="INextOccurrenceProvider"/> under <paramref name="key"/>, so a schedule can take
    /// its occurrences from it with <c>UseOccurrenceProvider(key, config)</c>.
    /// </summary>
    /// <typeparam name="TProvider">The implementation. Registered as scoped unless it is already registered.</typeparam>
    /// <param name="key">
    /// What schedules name it by. It is what gets PERSISTED on every row that uses the provider, so treat it
    /// as part of the durable contract: renaming it orphans the schedules that carry the old one. Matched
    /// ordinally.
    /// </param>
    /// <returns>The service builder for method chaining.</returns>
    /// <exception cref="ArgumentException">
    /// The key is empty, or a DIFFERENT implementation is already registered under it.
    /// </exception>
    /// <remarks>
    /// The provider is resolved from a fresh scope on every call, so it may depend on scoped services — a
    /// DbContext holding the holiday table is the ordinary case. Registering the same type under the same key
    /// twice is a no-op, which keeps a registration that runs on every startup idempotent.
    /// </remarks>
    public EverTaskServiceBuilder AddOccurrenceProvider<TProvider>(string key)
        where TProvider : class, INextOccurrenceProvider
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_configuration.OccurrenceProviders.TryGetValue(key, out var registered) &&
            registered != typeof(TProvider))
        {
            throw new ArgumentException(
                $"The occurrence provider key '{key}' is already registered for {registered.Name}. A key is " +
                "persisted on every schedule that uses it, so it can only mean one thing.", nameof(key));
        }

        _configuration.OccurrenceProviders[key] = typeof(TProvider);

        // TryAdd: an application that wants its provider on another lifetime (a singleton holding a cached
        // calendar) registers it itself and keeps that registration.
        Services.TryAddScoped<TProvider>();

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
            var defaultQueue = _configuration.Queues.TryGetValue(QueueNames.Default, out var configuredDefault)
                ? configuredDefault
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
