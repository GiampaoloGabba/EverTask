using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EverTask.Monitor.Api.Infrastructure;

/// <summary>
/// The authoritative gate of the management endpoints, attached by <c>RoutePrefixConvention</c> to every
/// controller routed under <c>{ApiBasePath}/management</c>.
/// </summary>
/// <remarks>
/// It is an MVC filter, and not middleware, because both things it depends on happen AFTER the middleware
/// has run. <c>UsePathBase</c> rewrites the request path, so a host that uses one made every path test in
/// <c>JwtAuthenticationMiddleware</c> miss and an anonymous request reached the action; and the host's
/// <c>UseAuthentication</c> is what populates <c>HttpContext.User</c>, which the authorization hook is
/// documented to read. Inside routing both have already happened, and nothing can reach the action without
/// passing here first.
/// </remarks>
internal sealed class ManagementAuthorizationFilter(EverTaskApiOptions options, IJwtTokenService jwtTokenService)
    : IAsyncAuthorizationFilter
{
    /// <summary>The challenge both this gate and the middleware answer an unauthenticated call with.</summary>
    internal const string BearerChallenge = "Bearer realm=\"EverTask Monitoring API\"";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Disabled means the routes do not exist, not that they refuse: an API that never opened a write
        // surface does not advertise having one.
        if (!options.EnableManagementEndpoints)
        {
            context.Result = new NotFoundResult();
            return;
        }

        var canManage = false;

        if (options.EnableAuthentication)
        {
            // Validated here rather than carried over from the middleware: this gate has to hold on its own
            // for the requests that never passed through it.
            var token      = BearerTokenOf(context.HttpContext);
            var validation = string.IsNullOrEmpty(token) ? null : jwtTokenService.ValidateToken(token);

            if (validation is not { IsValid: true })
            {
                context.HttpContext.Response.Headers.Append("WWW-Authenticate", BearerChallenge);
                context.Result = new UnauthorizedResult();
                return;
            }

            canManage = validation.CanManage;
        }

        // The host's hook REPLACES the role check: an application with its own authorization decides for
        // itself, and the monitoring options stop being a second, weaker door into the same operations.
        var authorized = options.ManagementAuthorization is { } authorize
                             ? await authorize(context.HttpContext).ConfigureAwait(false)
                             : canManage;

        if (!authorized)
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// The bearer token of the request, header only. <c>?access_token=</c> is deliberately not read here:
    /// it exists for the SignalR handshake, which cannot set headers, and a write surface that accepted a
    /// credential from the query string would put it in every access log — and make a cross-site link enough
    /// to carry one.
    /// </summary>
    private static string? BearerTokenOf(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.FirstOrDefault();

        return header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                   ? header["Bearer ".Length..].Trim()
                   : null;
    }
}
