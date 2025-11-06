using EverTask.Monitor.Api.Middleware;
using EverTask.Monitor.Api.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace EverTask.Monitor.Api.Infrastructure;

/// <summary>
/// Startup filter that automatically registers EverTask API middleware in the pipeline.
/// This ensures JWT authentication is always configured when AddMonitoringApi() is called.
/// </summary>
internal class EverTaskApiStartupFilter : IStartupFilter
{
    private readonly EverTaskApiOptions _options;

    public EverTaskApiStartupFilter(EverTaskApiOptions options)
    {
        _options = options;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Register authentication middleware (if enabled)
            if (_options.EnableAuthentication)
            {
                app.UseAuthentication();
            }

            // Always register JWT authentication middleware
            // (it handles skip logic internally based on EnableAuthentication)
            app.UseMiddleware<JwtAuthenticationMiddleware>();

            // Continue with the rest of the pipeline
            next(app);
        };
    }
}
