using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Monitor.Api.Infrastructure;

/// <summary>
/// Applies <see cref="MonitoringAccessPolicy"/> to the monitoring endpoints that are NOT controllers — the
/// SignalR hub, the dashboard files, the OpenAPI document and whatever a companion package maps.
/// </summary>
/// <remarks>
/// Controllers carry <see cref="MonitoringAccessFilter"/> instead. The hub is not MVC and exposes no
/// convention builder of its own, so the guard is attached to the route GROUP everything non-MVC is mapped
/// into — inside routing, where a host's <c>UsePathBase</c> has already been applied (#46).
/// </remarks>
internal static class MonitoringEndpointGuard
{
    /// <summary>
    /// Wraps the request delegate of every endpoint of this builder that is really routed under the
    /// monitoring base path. The test is made once, when the endpoint is built, so nothing outside the
    /// surface pays for it at request time.
    /// </summary>
    public static T Guard<T>(this T builder, IServiceProvider services) where T : IEndpointConventionBuilder
    {
        var policy = services.GetRequiredService<MonitoringAccessPolicy>();

        builder.Add(endpoint =>
        {
            if (endpoint.RequestDelegate is not { } inner || !IsMonitoringRoute(endpoint, policy))
                return;

            endpoint.RequestDelegate = async context =>
            {
                var access = policy.Evaluate(context, context.Request.Path);

                if (access != MonitoringAccess.Allowed)
                {
                    await MonitoringAccessPolicy.RefuseAsync(context, access).ConfigureAwait(false);
                    return;
                }

                await inner(context).ConfigureAwait(false);
            };
        });

        return builder;
    }

    private static bool IsMonitoringRoute(EndpointBuilder endpoint, MonitoringAccessPolicy policy)
    {
        if (endpoint is not RouteEndpointBuilder { RoutePattern.RawText: { } template })
            return false;

        return policy.SurfaceOf(new PathString(template.StartsWith('/') ? template : $"/{template}"))
               != MonitoringSurface.None;
    }
}
