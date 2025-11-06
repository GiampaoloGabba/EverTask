using EverTask.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverTask.Monitor.AspnetCore.SignalR;

public static class AppBuilderExtensions
{
    public static IEndpointRouteBuilder MapEverTaskMonitorHub(this IEndpointRouteBuilder app, string pattern = "/evertask-monitoring/hub")
    {
        app.MapHub<TaskMonitorHub>(pattern);

        // Ensure the monitor is subscribed - use GetRequiredService to throw if not found
        var monitors = app.ServiceProvider.GetServices<ITaskMonitor>();
        var signalRMonitor = monitors.OfType<SignalRTaskMonitor>().FirstOrDefault();

        if (signalRMonitor != null)
        {
            signalRMonitor.SubScribe();
        }
        else
        {
            // Log warning if monitor not found - this is a configuration issue
            var logger = app.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<SignalRTaskMonitor>>();
            logger?.LogWarning("SignalRTaskMonitor not found in services. SignalR monitoring will not work. Did you call AddSignalRMonitoring()?");
        }

        return app;
    }

    public static IEndpointRouteBuilder MapEverTaskMonitorHub(this IEndpointRouteBuilder app, string pattern, Action<HttpConnectionDispatcherOptions> hubOptions)
    {
        app.MapHub<TaskMonitorHub>(pattern,hubOptions);

        // Ensure the monitor is subscribed - use GetRequiredService to throw if not found
        var monitors = app.ServiceProvider.GetServices<ITaskMonitor>();
        var signalRMonitor = monitors.OfType<SignalRTaskMonitor>().FirstOrDefault();

        if (signalRMonitor != null)
        {
            signalRMonitor.SubScribe();
        }
        else
        {
            // Log warning if monitor not found - this is a configuration issue
            var logger = app.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<SignalRTaskMonitor>>();
            logger?.LogWarning("SignalRTaskMonitor not found in services. SignalR monitoring will not work. Did you call AddSignalRMonitoring()?");
        }

        return app;
    }
}
