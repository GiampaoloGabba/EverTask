using EverTask.Monitor.Api.Middleware;
using EverTask.Monitor.Api.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace EverTask.Monitor.Api.Infrastructure;

/// <summary>
/// Startup filter that automatically registers EverTask API middleware in the pipeline.
/// This ensures JWT authentication is always configured when AddMonitoringApi() is called.
/// </summary>
internal class EverTaskApiStartupFilter(EverTaskApiOptions options) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Apply the monitoring CORS policy to monitoring requests only: the branch predicate
            // keeps the host's own CORS setup (or lack of one) untouched (issue #21)
            if (options.EnableCors)
            {
                app.UseWhen(
                    context => context.Request.Path.StartsWithSegments(options.BasePath),
                    branch => branch.UseCors(EverTaskApiOptions.CorsPolicyName));
            }

            // Register custom JWT authentication middleware
            // This middleware handles IP whitelist + JWT authentication for API/Hub
            // (it applies skip logic internally based on EnableAuthentication and path)
            app.UseMiddleware<JwtAuthenticationMiddleware>();

            // Continue with the rest of the pipeline
            next(app);
        };
    }
}
