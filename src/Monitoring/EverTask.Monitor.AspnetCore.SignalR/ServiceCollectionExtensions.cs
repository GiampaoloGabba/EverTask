using EverTask.Monitor.AspnetCore.SignalR;
using EverTask.Monitoring;
using Microsoft.AspNetCore.SignalR;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static EverTaskServiceBuilder AddSignalRMonitoring(this EverTaskServiceBuilder builder)
    {

        builder.Services.AddSignalR();
        builder.Services.AddSingleton<ITaskMonitor, SignalRTaskMonitor>();

        return builder;
    }

    public static EverTaskServiceBuilder AddSignalRMonitoring(this EverTaskServiceBuilder builder,
                                                              Action<HubOptions> hubConfiguration)
    {
        builder.Services.AddSignalR(hubConfiguration);
        builder.Services.AddSingleton<ITaskMonitor, SignalRTaskMonitor>();
        return builder;
    }
}
