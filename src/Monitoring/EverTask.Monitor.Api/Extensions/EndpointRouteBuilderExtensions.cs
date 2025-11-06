using EverTask.Monitor.AspnetCore.SignalR;
using EverTask.Monitor.Api.Middleware;
using EverTask.Monitor.Api.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace EverTask.Monitor.Api.Extensions;

/// <summary>
/// Extension methods for configuring EverTask Monitoring API middleware and endpoints.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Adds EverTask Monitoring API middleware to the application pipeline.
    /// This must be called after UseRouting() and before MapEverTaskApi().
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    [Obsolete("This method is no longer required. The middleware is now automatically registered when calling AddMonitoringApi(). You can safely remove this call from your code.", false)]
    public static IApplicationBuilder UseEverTaskApiMiddleware(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<EverTaskApiOptions>();

        // TODO: Rate limiting middleware - temporarily commented out
        /*
#if NET8_0_OR_GREATER
        // Enable rate limiting for login endpoint (NET8+ only)
        if (options.EnableJwt)
        {
            app.UseRateLimiter();
        }
#endif
        */

        // Enable JWT authentication if configured
        if (options.EnableAuthentication)
        {
            app.UseAuthentication();
        }

        // Register JWT authentication middleware (always registered, handles skip logic internally)
        app.UseMiddleware<JwtAuthenticationMiddleware>();

        return app;
    }
    /// <summary>
    /// Maps EverTask Monitoring API endpoints, SignalR hub, and optionally serves the embedded dashboard UI.
    /// Automatically configures hub authentication based on EnableAuthentication setting.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="configureHub">Optional callback to configure SignalR hub options.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapEverTaskApi(
        this IEndpointRouteBuilder endpoints,
        Action<HttpConnectionDispatcherOptions>? configureHub = null)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<EverTaskApiOptions>();

        // Map SignalR hub with automatic authentication configuration + user custom options
        endpoints.MapEverTaskMonitorHub(options.SignalRHubPath, hubOptions =>
        {
            // Apply default authentication configuration if enabled
            if (options.EnableAuthentication)
            {
                hubOptions.AuthorizationData.Add(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute());
            }

            // Apply user custom configuration
            configureHub?.Invoke(hubOptions);
        });

        // Map API controllers
        endpoints.MapControllers();

        // Conditionally serve UI
        if (options.EnableUI)
        {
            var fileProvider = new ManifestEmbeddedFileProvider(
                typeof(EverTaskApiOptions).Assembly,
                "wwwroot"
            );

            // Map static files endpoint (assets)
            endpoints.MapGet($"{options.UIBasePath}/assets/{{**file}}", async (string file, HttpContext context) =>
            {
                var fileInfo = fileProvider.GetFileInfo($"assets/{file}");
                if (!fileInfo.Exists)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                // Set content type based on file extension
                var provider = new FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(file, out var contentType))
                {
                    contentType = "application/octet-stream";
                }

                context.Response.ContentType = contentType;
                context.Response.Headers.CacheControl = "public, max-age=31536000"; // 1 year cache for assets

                await using var stream = fileInfo.CreateReadStream();
                await stream.CopyToAsync(context.Response.Body);
            });

            // Map favicon and other root files (only files with extensions, not subroutes like /tasks)
            endpoints.MapGet($"{options.UIBasePath}/{{file}}.{{ext}}", async (string file, string ext, HttpContext context) =>
            {
                // Only serve specific file types (prevent directory traversal)
                if (ext != "svg" && ext != "ico" && ext != "png")
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                var fileName = $"{file}.{ext}";

                var fileInfo = fileProvider.GetFileInfo(fileName);
                if (!fileInfo.Exists)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                var provider = new FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(file, out var contentType))
                {
                    contentType = "application/octet-stream";
                }

                context.Response.ContentType = contentType;
                await using var stream = fileInfo.CreateReadStream();
                await stream.CopyToAsync(context.Response.Body);
            });

            // Map index.html for root UI path
            endpoints.MapGet(options.UIBasePath.TrimEnd('/'), async context =>
            {
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

            // SPA fallback routing (serve index.html for all UI subroutes)
            // This must be registered AFTER MapControllers to ensure API routes have priority
            endpoints.MapFallback(async context =>
            {
                // Only handle requests that start with UIBasePath
                if (!context.Request.Path.StartsWithSegments(options.UIBasePath))
                {
                    context.Response.StatusCode = 404;
                    return;
                }

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

                // Skip asset files (they're handled by the MapGet above)
                if (context.Request.Path.StartsWithSegments($"{options.UIBasePath}/assets"))
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                // Serve index.html for all other UI routes (SPA routing)
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
