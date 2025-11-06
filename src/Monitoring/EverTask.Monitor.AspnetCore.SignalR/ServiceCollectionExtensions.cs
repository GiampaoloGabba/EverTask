using EverTask.Monitor.AspnetCore.SignalR;
using EverTask.Monitoring;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds SignalR monitoring with default configuration.
    /// Execution logs are excluded from SignalR events by default (available via ILogger and database).
    /// </summary>
    public static EverTaskServiceBuilder AddSignalRMonitoring(this EverTaskServiceBuilder builder)
    {
        // Register default options
        builder.Services.Configure<SignalRMonitoringOptions>(options => { });
        builder.Services.AddSignalR();
        builder.Services.TryAddSingleton<ITaskMonitor, SignalRTaskMonitor>();

        return builder;
    }

    /// <summary>
    /// Adds SignalR monitoring with custom monitoring options.
    /// </summary>
    /// <param name="builder">The EverTask service builder.</param>
    /// <param name="monitoringConfiguration">Action to configure SignalR monitoring options (e.g., IncludeExecutionLogs).</param>
    public static EverTaskServiceBuilder AddSignalRMonitoring(this EverTaskServiceBuilder builder,
                                                              Action<SignalRMonitoringOptions> monitoringConfiguration)
    {
        builder.Services.Configure(monitoringConfiguration);
        builder.Services.AddSignalR();
        builder.Services.TryAddSingleton<ITaskMonitor, SignalRTaskMonitor>();

        return builder;
    }

    /// <summary>
    /// Adds SignalR monitoring with custom hub configuration.
    /// Execution logs are excluded from SignalR events by default.
    /// </summary>
    /// <param name="builder">The EverTask service builder.</param>
    /// <param name="hubConfiguration">Action to configure SignalR hub options.</param>
    public static EverTaskServiceBuilder AddSignalRMonitoring(this EverTaskServiceBuilder builder,
                                                              Action<HubOptions> hubConfiguration)
    {
        // Register default monitoring options
        builder.Services.Configure<SignalRMonitoringOptions>(options => { });
        builder.Services.AddSignalR(hubConfiguration);
        builder.Services.TryAddSingleton<ITaskMonitor, SignalRTaskMonitor>();

        return builder;
    }

    /// <summary>
    /// Adds SignalR monitoring with custom hub and monitoring configuration.
    /// </summary>
    /// <param name="builder">The EverTask service builder.</param>
    /// <param name="hubConfiguration">Action to configure SignalR hub options.</param>
    /// <param name="monitoringConfiguration">Action to configure SignalR monitoring options (e.g., IncludeExecutionLogs).</param>
    public static EverTaskServiceBuilder AddSignalRMonitoring(this EverTaskServiceBuilder builder,
                                                              Action<HubOptions> hubConfiguration,
                                                              Action<SignalRMonitoringOptions> monitoringConfiguration)
    {
        builder.Services.Configure(monitoringConfiguration);
        builder.Services.AddSignalR(hubConfiguration);
        builder.Services.TryAddSingleton<ITaskMonitor, SignalRTaskMonitor>();
        return builder;
    }
}
