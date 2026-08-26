using EverTask.Monitor.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Monitor.Api.Middleware;

/// <summary>
/// The outer shield of the monitoring surface: IP whitelist and JWT, decided by
/// <see cref="MonitoringAccessPolicy"/>.
/// </summary>
/// <remarks>
/// It reads <c>Request.Path</c> as the pipeline sees it HERE, which is before a host's <c>UsePathBase</c> has
/// moved the base out of it — so on such a host this middleware matches nothing and every check silently
/// passes. That is why the same policy is enforced again inside routing, by
/// <see cref="MonitoringAccessFilter"/> for the controllers and <see cref="MonitoringEndpointGuard"/> for
/// the hub and the dashboard files, and why nothing security-critical may rest on this middleware alone.
/// </remarks>
public class JwtAuthenticationMiddleware(RequestDelegate next)
{
    /// <summary>Invokes the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var path   = context.Request.Path;
        var policy = context.RequestServices.GetRequiredService<MonitoringAccessPolicy>();

        // The write surface does not exist while it is switched off. Answering 404 here as well as in the
        // filter keeps it from reaching MVC at all on the ordinary pipeline.
        if (policy.IsDisabledManagementPath(path))
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Not found").ConfigureAwait(false);
            return;
        }

        var access = policy.Evaluate(context, path);

        if (access != MonitoringAccess.Allowed)
        {
            await MonitoringAccessPolicy.RefuseAsync(context, access).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
