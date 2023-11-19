using EverTask.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Monitor.AspnetCore.SignalR;

public static class AppBuilderExtensions
{
    public static IEndpointRouteBuilder MapEverTaskMonitorHub(this IEndpointRouteBuilder app, string pattern = "/evertask/monitor")
    {
        app.MapHub<TaskMonitorHub>(pattern);
        var taskMonitor = app.ServiceProvider.GetServices<ITaskMonitor>().OfType<SignalRTaskMonitor>().FirstOrDefault();
        taskMonitor?.SubScribe();
        return app;
    }

    public static IEndpointRouteBuilder MapEverTaskMonitorHub(this IEndpointRouteBuilder app, string pattern, Action<HttpConnectionDispatcherOptions> hubOptions)
    {
        app.MapHub<TaskMonitorHub>(pattern,hubOptions);
        var taskMonitor = app.ServiceProvider.GetServices<ITaskMonitor>().OfType<SignalRTaskMonitor>().FirstOrDefault();
        taskMonitor?.SubScribe();
        return app;
    }
}
