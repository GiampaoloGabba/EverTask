using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;

namespace EverTask.Monitor.AspnetCore.SignalR;

public static class AppBuilderExtensions
{
    public static IEndpointRouteBuilder MapEverTaskMonitorHub(this IEndpointRouteBuilder app, string pattern = "/monitor/evertask")
    {
        app.MapHub<SignalRTaskMonitorHub>(pattern );
        return app;
    }

    public static IEndpointRouteBuilder MapEverTaskMonitorHub(this IEndpointRouteBuilder app, string pattern, Action<HttpConnectionDispatcherOptions> hubOptions)
    {
        app.MapHub<SignalRTaskMonitorHub>(pattern,hubOptions);
        return app;
    }
}
