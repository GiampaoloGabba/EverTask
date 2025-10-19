using System.Text.Json;
using System.Text.Json.Serialization;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Services;
using EverTask.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Monitor.Api.Extensions;

/// <summary>
/// Extension methods for registering EverTask Monitoring API services.
/// This API can be used standalone or with the EverTask.Monitor.Dashboard package.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds EverTask Monitoring API services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEverTaskApi(
        this IServiceCollection services,
        Action<EverTaskApiOptions>? configure = null)
    {
        // Configure options
        var options = new EverTaskApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Auto-register SignalR if not already registered
        // Note: SignalR monitoring package should be added via AddSignalRMonitoring() separately
        if (!services.Any(s => s.ServiceType.Name == "IHubContext`1"))
        {
            services.AddSignalR();
        }

        // Register monitoring services
        services.AddScoped<ITaskQueryService, TaskQueryService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IStatisticsService, StatisticsService>();

        // Add controllers with this assembly
        services.AddControllers()
            .AddApplicationPart(typeof(ServiceCollectionExtensions).Assembly)
            .AddJsonOptions(jsonOptions =>
            {
                // Configure JSON serialization for consistency
                jsonOptions.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                jsonOptions.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                jsonOptions.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        // Add CORS if enabled
        if (options.EnableCors)
        {
            services.AddCors(corsOptions =>
            {
                corsOptions.AddPolicy("EverTaskMonitoringApi", policy =>
                {
                    if (options.CorsAllowedOrigins.Length > 0)
                    {
                        policy.WithOrigins(options.CorsAllowedOrigins)
                              .AllowCredentials();
                    }
                    else
                    {
                        policy.AllowAnyOrigin();
                    }

                    policy.AllowAnyHeader()
                          .AllowAnyMethod();
                });
            });
        }

        return services;
    }
}
