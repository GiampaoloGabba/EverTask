using EverTask.Monitor.Api.Infrastructure;
using EverTask.Monitor.Api.Middleware;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.AspnetCore.SignalR;
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

        // Enable JWT authentication if configured
        if (options.EnableAuthentication)
        {
            app.UseRateLimiter();
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

        // Everything that is NOT a controller goes into this group, whose only job is to carry the access
        // guard: the hub exposes no convention builder of its own, so a group is the only way one convention
        // reaches it, the dashboard files and a companion package's endpoints. Empty prefix: no route changes.
        var guarded = endpoints.MapGroup("").Guard(endpoints.ServiceProvider);

        // The hub's authentication comes from the guard above, never from an [Authorize] attribute: the
        // handshake carries its JWT in the query string, which no host-registered scheme would look at.
        if (configureHub != null)
        {
            guarded.MapEverTaskMonitorHub(options.SignalRHubPath, configureHub);
        }
        else
        {
            guarded.MapEverTaskMonitorHub(options.SignalRHubPath);
        }

        // Map API controllers
        endpoints.MapControllers();

#if NET9_0_OR_GREATER
        // Serve the isolated OpenAPI document under the monitoring base path (net9+ only:
        // the built-in generator does not exist on net8). The host's own OpenAPI/Swagger
        // setup is never touched.
        if (options.EnableOpenApiDocument)
        {
            guarded.MapOpenApi($"{options.BasePath}/openapi/{{documentName}}.json");
        }
#endif

        // Companion packages (e.g. EverTask.Monitor.Api.Scalar) map their endpoints here — into the guarded
        // group, so what they serve is protected exactly like the rest of the surface
        foreach (var extension in endpoints.ServiceProvider.GetServices<IMonitoringApiEndpointExtension>())
        {
            extension.MapEndpoints(guarded, options);
        }

        // Conditionally serve UI: bail out early when the embedded dashboard is disabled
        if (!options.EnableUI)
        {
            return endpoints;
        }

        IFileProvider fileProvider;
        try
        {
            // Try ManifestEmbeddedFileProvider first (optimized for production)
            fileProvider = new ManifestEmbeddedFileProvider(
                typeof(EverTaskApiOptions).Assembly,
                "wwwroot"
            );
        }
        catch (InvalidOperationException)
        {
            // Fallback to EmbeddedFileProvider (works without manifest, e.g., during tests)
            fileProvider = new EmbeddedFileProvider(
                typeof(EverTaskApiOptions).Assembly,
                $"{typeof(EverTaskApiOptions).Assembly.GetName().Name}.wwwroot"
            );
        }

        // Map static files endpoint (assets)
        guarded.MapGet($"{options.UIBasePath}/assets/{{**file}}", async (string file, HttpContext context) =>
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

#pragma warning disable CA2007
            await using var stream = fileInfo.CreateReadStream();
#pragma warning restore CA2007
            await stream.CopyToAsync(context.Response.Body).ConfigureAwait(false);
        }).ExcludeFromDescription();

        // Map favicon and other root files (only files with extensions, not subroutes like /tasks)
        guarded.MapGet($"{options.UIBasePath}/{{file}}.{{ext}}", async (string file, string ext, HttpContext context) =>
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
#pragma warning disable CA2007
            await using var stream = fileInfo.CreateReadStream();
#pragma warning restore CA2007
            await stream.CopyToAsync(context.Response.Body).ConfigureAwait(false);
        }).ExcludeFromDescription();

        // Map index.html for root UI path
        guarded.MapGet(options.UIBasePath.TrimEnd('/'), async context =>
        {
            context.Response.ContentType = "text/html";
            var fileInfo = fileProvider.GetFileInfo("index.html");
            if (!fileInfo.Exists)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("Dashboard UI not found. Make sure the package was built with UI enabled.").ConfigureAwait(false);
                return;
            }

            await using var stream = fileInfo.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body).ConfigureAwait(false);
        }).ExcludeFromDescription();

        // SPA fallback routing (serve index.html for all UI subroutes). The pattern is
        // constrained to the monitoring base path so the host keeps its own fallback and
        // 404 handling: an unconstrained MapFallback would collide with the host's SPA
        // fallback (AmbiguousMatchException) and hijack every unmatched path (issue #21)
        guarded.MapFallback($"{options.UIBasePath}/{{**path}}", async context =>
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
                await context.Response.WriteAsync("Dashboard UI not found. Make sure the package was built with UI enabled.").ConfigureAwait(false);
                return;
            }

            await using var stream = fileInfo.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body).ConfigureAwait(false);
        });

        return endpoints;
    }
}
