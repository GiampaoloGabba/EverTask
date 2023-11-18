using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Scheduler;

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
        services.TryAddSingleton<IWorkerBlacklist, WorkerBlacklist>();
        services.TryAddSingleton(typeof(IEverTaskLogger<>), typeof(EverTaskLogger<>));
        services.TryAddSingleton<IScheduler, TimerScheduler>();
        services.TryAddSingleton<IWorkerQueue, WorkerQueue>();
        services.AddSingleton<ITaskDispatcherInternal, TaskDispatcher>();
        services.AddSingleton<ITaskDispatcher>(provider => provider.GetRequiredService<ITaskDispatcherInternal>());
        services.AddHostedService<WorkerService>();
        services.AddEverTaskHandlers(options);

        return new EverTaskServiceBuilder(services);
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
