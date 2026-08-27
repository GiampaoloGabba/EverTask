using EverTask.Monitor.Api.Infrastructure;
using EverTask.Monitor.Api.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Monitor.Api.Middleware;

/// <summary>
/// Shields the disabled management prefix before it can reach MVC. Access itself — the IP whitelist and the
/// JWT — is decided by <see cref="MonitoringAccessPolicy"/> INSIDE routing.
/// </summary>
/// <remarks>
/// A startup filter registers this before everything the host adds, and both inputs of an access decision are
/// host business: <c>UsePathBase</c> moves the base out of <c>Request.Path</c> (#46) and
/// <c>UseForwardedHeaders</c> rewrites <c>Connection.RemoteIpAddress</c> (#47). The decision therefore belongs
/// where both have already happened — <see cref="MonitoringAccessFilter"/> for the controllers,
/// <see cref="MonitoringEndpointGuard"/> for the hub and the dashboard files.
/// </remarks>
public class JwtAuthenticationMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Initializes the middleware while the current request policy remains resolved from request services.
    /// </summary>
    public JwtAuthenticationMiddleware(RequestDelegate next, EverTaskApiOptions options) : this(next)
    {
        _ = options;
    }

    /// <summary>Invokes the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var policy = context.RequestServices.GetRequiredService<MonitoringAccessPolicy>();

        // Path-only, so it stays true whatever the host does to the address: while the write surface is off,
        // it does not exist, and keeping it out of MVC entirely costs nothing.
        if (policy.IsDisabledManagementPath(context.Request.Path))
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Not found").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
