using EverTask.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static EverTaskServiceBuilder AddEverTask(this IServiceCollection services,
                                                     Action<EverTaskServiceConfiguration>? configure = null)
    {
        var options = new EverTaskServiceConfiguration();
        configure?.Invoke(options);

        if (!options.AssembliesToRegister.Any())
        {
            throw new ArgumentException("No assemblies found to scan. Supply at least one assembly to scan for handlers.");
        }

        services.TryAddSingleton(options);
        services.TryAddSingleton(typeof(IEverTaskLogger<>), typeof(EverTaskLogger<>));
        services.TryAddSingleton<IWorkerBlacklist, WorkerBlacklist>();

        // Register WorkerQueueManager instead of single WorkerQueue
        RegisterQueueManager(services, options);

        services.TryAddSingleton<IScheduler, TimerScheduler>();
        services.TryAddSingleton<ITaskDispatcherInternal, Dispatcher>();
        services.TryAddSingleton<ITaskDispatcher>(provider => provider.GetRequiredService<ITaskDispatcherInternal>());
        services.TryAddSingleton<ICancellationSourceProvider, CancellationSourceProvider>();
        services.TryAddSingleton<IEverTaskWorkerExecutor, WorkerExecutor>();
        services.AddHostedService<WorkerService>();
        services.AddEverTaskHandlers(options);

        return new EverTaskServiceBuilder(services, options);
    }

    private static void RegisterQueueManager(IServiceCollection services, EverTaskServiceConfiguration options)
    {
        // Create default queue configuration from legacy settings if no queues configured
        if (!options.Queues.Any())
        {
            options.Queues[QueueNames.Default] = new QueueConfiguration
            {
                Name = QueueNames.Default,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                ChannelOptions = options.ChannelOptions,
                DefaultRetryPolicy = options.DefaultRetryPolicy,
                DefaultTimeout = options.DefaultTimeout,
                QueueFullBehavior = QueueFullBehavior.Wait
            };
        }

        // Ensure default queue always exists
        if (!options.Queues.ContainsKey(QueueNames.Default))
        {
            options.Queues[QueueNames.Default] = new QueueConfiguration
            {
                Name = QueueNames.Default,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                ChannelOptions = options.ChannelOptions,
                DefaultRetryPolicy = options.DefaultRetryPolicy,
                DefaultTimeout = options.DefaultTimeout,
                QueueFullBehavior = QueueFullBehavior.Wait
            };
        }

        // Automatically create recurring queue if not configured
        if (!options.Queues.ContainsKey(QueueNames.Recurring))
        {
            var defaultQueue = options.Queues[QueueNames.Default];
            var recurringQueue = defaultQueue.Clone();
            recurringQueue.Name = QueueNames.Recurring;
            options.Queues[QueueNames.Recurring] = recurringQueue;
        }

        // Register the queue manager
        services.TryAddSingleton<IWorkerQueueManager>(provider =>
        {
            var logger = provider.GetRequiredService<IEverTaskLogger<WorkerQueueManager>>();
            var blacklist = provider.GetRequiredService<IWorkerBlacklist>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var taskStorage = provider.GetService<ITaskStorage>();
            return new WorkerQueueManager(options.Queues, logger, blacklist, loggerFactory, taskStorage);
        });

        // Register backward compatibility IWorkerQueue (points to default queue)
        services.TryAddSingleton<IWorkerQueue>(provider =>
        {
            var queueManager = provider.GetRequiredService<IWorkerQueueManager>();
            return queueManager.GetQueue(QueueNames.Default);
        });
    }
    private static void AddEverTaskHandlers(this IServiceCollection services, EverTaskServiceConfiguration configuration)
    {
        var assembliesToScan = configuration.AssembliesToRegister.Distinct().ToArray();
        HandlerRegistrar.RegisterConnectedImplementations(services, assembliesToScan);
    }

    public static EverTaskServiceBuilder AddMemoryStorage(this EverTaskServiceBuilder builder)
    {
        builder.Services.TryAddSingleton<ITaskStorage, MemoryTaskStorage>();
        return builder;
    }
}
