using System.Text.Json;
using System.Text.Json.Serialization;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Services;
using EverTask.Monitoring;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EverTask.Monitor.Api.Extensions;

/// <summary>
/// Extension methods for registering EverTask Monitoring API services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds EverTask Monitoring API services to the EverTask service builder.
    /// </summary>
    /// <param name="builder">The EverTask service builder.</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>The EverTask service builder for chaining.</returns>
    public static EverTaskServiceBuilder AddMonitoringApi(
        this EverTaskServiceBuilder builder,
        Action<EverTaskApiOptions>? configure = null)
    {
        var services = builder.Services;

        // Configure options
        var options = new EverTaskApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Auto-register SignalR monitoring if not already registered
        if (!services.Any(s => s.ServiceType.Name.Contains("SignalRTaskMonitor")))
        {
            builder.AddSignalRMonitoring();
        }

        // Register monitoring services
        services.AddScoped<ITaskQueryService, TaskQueryService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IStatisticsService, StatisticsService>();

        // Add controllers with this assembly and route prefix convention
        services.AddControllers(mvcOptions =>
            {
                // Add route prefix convention to prepend BasePath to all controller routes
                mvcOptions.Conventions.Add(new Conventions.RoutePrefixConvention(options.BasePath));
            })
            .AddApplicationPart(typeof(ServiceCollectionExtensions).Assembly)
            .AddJsonOptions(jsonOptions =>
            {
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

        // Add Swagger if enabled
        if (options.EnableSwagger)
        {
            services.ConfigureOptions<MonitoringSwaggerConfiguration>();
        }

        return builder;
    }

    /// <summary>
    /// Adds EverTask Monitoring API services directly to IServiceCollection.
    /// Use this when you need standalone API without EverTask integration.
    /// Note: This does NOT auto-configure SignalR. Add SignalR manually if needed.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEverTaskMonitoringApiStandalone(
        this IServiceCollection services,
        Action<EverTaskApiOptions>? configure = null)
    {
        // Configure options
        var options = new EverTaskApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Register monitoring services
        services.AddScoped<ITaskQueryService, TaskQueryService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IStatisticsService, StatisticsService>();

        // Add controllers with this assembly and route prefix convention
        services.AddControllers(mvcOptions =>
            {
                // Add route prefix convention to prepend BasePath to all controller routes
                mvcOptions.Conventions.Add(new Conventions.RoutePrefixConvention(options.BasePath));
            })
            .AddApplicationPart(typeof(ServiceCollectionExtensions).Assembly)
            .AddJsonOptions(jsonOptions =>
            {
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

        // Add Swagger if enabled
        if (options.EnableSwagger)
        {
            services.ConfigureOptions<MonitoringSwaggerConfiguration>();
        }

        return services;
    }

    /// <summary>
    /// Configures Swagger to create a separate document for EverTask Monitoring API
    /// and filters out EverTask controllers from other Swagger documents.
    /// </summary>
    private class MonitoringSwaggerConfiguration : IConfigureOptions<SwaggerGenOptions>
    {
        public void Configure(SwaggerGenOptions options)
        {
            // Create separate Swagger document for monitoring API
            options.SwaggerDoc("evertask-monitoring", new OpenApiInfo
            {
                Title = "EverTask Monitoring API",
                Version = "v1",
                Description = "Background task monitoring and analytics endpoints"
            });

            // Filter controllers in a cooperative way
            // - EverTask controllers → ONLY in "evertask-monitoring" document
            // - Other controllers → EXCLUDED from "evertask-monitoring" document
            options.DocInclusionPredicate((docName, apiDesc) =>
            {
                if (apiDesc.ActionDescriptor is not ControllerActionDescriptor controllerActionDescriptor)
                    return false;

                var controllerNamespace = controllerActionDescriptor.ControllerTypeInfo.Namespace ?? string.Empty;
                bool isEverTaskController = controllerNamespace.StartsWith("EverTask.Monitor.Api");

                // EverTask controllers → ONLY in "evertask-monitoring"
                if (isEverTaskController)
                    return docName == "evertask-monitoring";

                // Other controllers → EXCLUDE from "evertask-monitoring"
                return docName != "evertask-monitoring";
            });
        }
    }
}
