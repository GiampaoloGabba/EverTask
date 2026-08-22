using System.Text.Json;
using System.Text.Json.Serialization;
using EverTask.Monitor.Api.Infrastructure;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Services;
using EverTask.Monitoring;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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

        // Register options both as singleton instance AND as IOptions<T> wrapper
        // This allows injection of both EverTaskApiOptions and IOptions<EverTaskApiOptions>
        services.AddSingleton(options);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        // Auto-register SignalR monitoring if not already registered
        // Check if SignalRTaskMonitor is already registered as ITaskMonitor
        var hasSignalRMonitor = services.Any(s =>
            s.ServiceType == typeof(ITaskMonitor) &&
            s.ImplementationType?.Name == "SignalRTaskMonitor");

        if (!hasSignalRMonitor)
        {
            builder.AddSignalRMonitoring();
        }

        // Register monitoring services
        services.AddScoped<ITaskQueryService, TaskQueryService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IStatisticsService, StatisticsService>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();

        // NOTE: JWT authentication is handled by JwtAuthenticationMiddleware (custom middleware)
        // We do NOT use ASP.NET Core's .AddAuthentication().AddJwtBearer() because:
        // 1. Our middleware supports IP whitelist protection on ALL paths (API + Hub + UI)
        // 2. Our middleware handles JWT via both Authorization header AND query string (?access_token=...)
        // 3. Our middleware applies layered protection (IP first, then JWT only for API/Hub)

// TODO: Rate limiting - requires NuGet package or framework reference fix
        // Temporarily commented out to allow build to succeed
        /*
#if NET8_0_OR_GREATER
        services.AddRateLimiter(rateLimiterOptions =>
        {
            rateLimiterOptions.AddFixedWindowLimiter("login", limiterOptions =>
            {
                limiterOptions.Window = TimeSpan.FromMinutes(15);
                limiterOptions.PermitLimit = 5;
                limiterOptions.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
                limiterOptions.QueueLimit = 0;
            });
        });
#endif
        */

        // Add controllers with this assembly and route prefix convention
        services.AddControllers(mvcOptions =>
            {
                // Prefix + ApiExplorer group for the monitoring controllers only (host controllers untouched)
                mvcOptions.Conventions.Add(
                    new Conventions.RoutePrefixConvention(options.BasePath, options.OpenApiDocumentName));
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

#if NET9_0_OR_GREATER
        // Register the isolated OpenAPI document (built-in ASP.NET Core generator). Registration is
        // unconditional so the EverTask.Monitor.Api.Scalar package can enable the document after this
        // call; the endpoint is only mapped when EnableOpenApiDocument is true (see MapEverTaskApi).
        services.AddOpenApi(options.OpenApiDocumentName, openApiOptions =>
        {
            // Strictly this document's group: ungrouped host endpoints stay in the host's documents
            openApiOptions.ShouldInclude = description => description.GroupName == options.OpenApiDocumentName;
        });
#endif

        // Register startup filter to automatically configure middleware pipeline
        services.AddSingleton<IStartupFilter>(sp =>
            new EverTaskApiStartupFilter(sp.GetRequiredService<EverTaskApiOptions>()));

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

        // Register options both as singleton instance AND as IOptions<T> wrapper
        // This allows injection of both EverTaskApiOptions and IOptions<EverTaskApiOptions>
        services.AddSingleton(options);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        // Register monitoring services
        services.AddScoped<ITaskQueryService, TaskQueryService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IStatisticsService, StatisticsService>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();

        // NOTE: JWT authentication is handled by JwtAuthenticationMiddleware (custom middleware)
        // We do NOT use ASP.NET Core's .AddAuthentication().AddJwtBearer() because:
        // 1. Our middleware supports IP whitelist protection on ALL paths (API + Hub + UI)
        // 2. Our middleware handles JWT via both Authorization header AND query string (?access_token=...)
        // 3. Our middleware applies layered protection (IP first, then JWT only for API/Hub)

// TODO: Rate limiting - requires NuGet package or framework reference fix
        // Temporarily commented out to allow build to succeed
        /*
#if NET8_0_OR_GREATER
        services.AddRateLimiter(rateLimiterOptions =>
        {
            rateLimiterOptions.AddFixedWindowLimiter("login", limiterOptions =>
            {
                limiterOptions.Window = TimeSpan.FromMinutes(15);
                limiterOptions.PermitLimit = 5;
                limiterOptions.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
                limiterOptions.QueueLimit = 0;
            });
        });
#endif
        */

        // Add controllers with this assembly and route prefix convention
        services.AddControllers(mvcOptions =>
            {
                // Prefix + ApiExplorer group for the monitoring controllers only (host controllers untouched)
                mvcOptions.Conventions.Add(
                    new Conventions.RoutePrefixConvention(options.BasePath, options.OpenApiDocumentName));
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

#if NET9_0_OR_GREATER
        // Register the isolated OpenAPI document (built-in ASP.NET Core generator). Registration is
        // unconditional so the EverTask.Monitor.Api.Scalar package can enable the document after this
        // call; the endpoint is only mapped when EnableOpenApiDocument is true (see MapEverTaskApi).
        services.AddOpenApi(options.OpenApiDocumentName, openApiOptions =>
        {
            // Strictly this document's group: ungrouped host endpoints stay in the host's documents
            openApiOptions.ShouldInclude = description => description.GroupName == options.OpenApiDocumentName;
        });
#endif

        // Register startup filter to automatically configure middleware pipeline
        services.AddSingleton<IStartupFilter>(sp =>
            new EverTaskApiStartupFilter(sp.GetRequiredService<EverTaskApiOptions>()));

        return services;
    }
}
