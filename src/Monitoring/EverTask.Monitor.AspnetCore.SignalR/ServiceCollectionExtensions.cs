using Microsoft.AspNetCore.SignalR;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static EverTaskServiceBuilder AddSignalRMonitoring(this EverTaskServiceBuilder builder)
    {

        builder.Services.AddSignalR();
        return builder;
    }

    public static EverTaskServiceBuilder AddSignalRMonitoring(this EverTaskServiceBuilder builder,
                                                              Action<HubOptions> hubConfiguration)
    {
        builder.Services.AddSignalR(hubConfiguration);
        return builder;
    }
}
