using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EverTask.Monitor.Api.Infrastructure;

/// <summary>
/// The IP whitelist and the JWT check for every monitoring CONTROLLER, attached by
/// <c>RoutePrefixConvention</c>.
/// </summary>
/// <remarks>
/// The same protections live in <c>JwtAuthenticationMiddleware</c>, but that runs before a host's
/// <c>UsePathBase</c> has taken the base out of <c>Request.Path</c>, so under one it matched nothing and
/// every read endpoint answered anonymously. Here the path is the one routing resolved, so the protection
/// follows the surface wherever the application is hosted.
/// </remarks>
internal sealed class MonitoringAccessFilter(MonitoringAccessPolicy policy) : IAsyncAuthorizationFilter
{
    /// <summary>Runs before the management gate: an IP that may not look must not reach a write decision.</summary>
    internal const int FilterOrder = -1000;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var access = policy.Evaluate(context.HttpContext, context.HttpContext.Request.Path);

        if (access == MonitoringAccess.Allowed)
            return;

        await MonitoringAccessPolicy.RefuseAsync(context.HttpContext, access).ConfigureAwait(false);

        // The response is already written, so the result must produce nothing of its own.
        context.Result = new EmptyResult();
    }
}
