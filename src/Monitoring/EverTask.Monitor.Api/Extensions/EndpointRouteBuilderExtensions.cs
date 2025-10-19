using EverTask.Monitor.AspnetCore.SignalR;
using EverTask.Monitor.Api.Middleware;
using EverTask.Monitor.Api.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace EverTask.Monitor.Api.Extensions;

/// <summary>
/// Extension methods for mapping EverTask Monitoring API endpoints.
/// This API can be used standalone or with the embedded dashboard UI.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps EverTask Monitoring API endpoints and optionally serves the embedded dashboard UI.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapEverTaskApi(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<EverTaskApiOptions>();

        // Map SignalR hub using the existing extension method from SignalR monitoring package
        endpoints.MapEverTaskMonitorHub(options.SignalRHubPath);

        // Map API controllers
        endpoints.MapControllers();

        // Conditionally serve UI
        if (options.EnableUI)
        {
            var fileProvider = new ManifestEmbeddedFileProvider(
                typeof(EverTaskApiOptions).Assembly,
                "wwwroot"
            );

            // Serve static files from embedded wwwroot
            var app = (IApplicationBuilder)endpoints.CreateApplicationBuilder();
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                RequestPath = options.UIBasePath
            });

            // SPA fallback routing (serve index.html for all non-API routes)
            endpoints.MapFallback($"{options.UIBasePath}/{{**path}}", async context =>
            {
                // Skip API routes
                if (context.Request.Path.StartsWithSegments(options.ApiBasePath))
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                // Skip SignalR hub routes
                if (context.Request.Path.StartsWithSegments(options.SignalRHubPath))
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                context.Response.ContentType = "text/html";
                var fileInfo = fileProvider.GetFileInfo("index.html");
                if (!fileInfo.Exists)
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("Dashboard UI not found. Make sure the package was built with UI enabled.");
                    return;
                }

                await using var stream = fileInfo.CreateReadStream();
                await stream.CopyToAsync(context.Response.Body);
            });
        }

        return endpoints;
    }
}
