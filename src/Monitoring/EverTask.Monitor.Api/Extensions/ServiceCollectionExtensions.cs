using EverTask.Monitor.Api.Infrastructure;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Services;
using EverTask.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
#if NET9_0_OR_GREATER
using Microsoft.AspNetCore.OpenApi;
#endif

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

        // Namespaced login rate-limit policy (5 attempts / 15 min per client IP, 429 on rejection).
        // Registration only adds the named policy the AuthController attribute refers to; nothing
        // is enforced unless the host pipeline runs UseRateLimiter() (issue #21).
        services.AddRateLimiter(rateLimiterOptions =>
            rateLimiterOptions.AddPolicy<string, LoginRateLimitPolicy>(EverTaskApiOptions.LoginRateLimitPolicyName));

        // Add controllers with this assembly and route prefix convention. The monitoring JSON
        // contract is attached per-controller by the convention (MonitoringJsonResultFilter), so
        // the host's shared MVC JsonOptions are never touched (issue #21).
        services.AddControllers(mvcOptions =>
            {
                // Prefix + ApiExplorer group for the monitoring controllers only (host controllers untouched)
                mvcOptions.Conventions.Add(
                    new Conventions.RoutePrefixConvention(options.BasePath, options.OpenApiDocumentName));
            })
            .AddApplicationPart(typeof(ServiceCollectionExtensions).Assembly);

        // Add CORS if enabled
        if (options.EnableCors)
        {
            services.AddCors(corsOptions =>
            {
                corsOptions.AddPolicy(EverTaskApiOptions.CorsPolicyName, policy =>
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
        services.AddOpenApi(options.OpenApiDocumentName, openApiOptions => ConfigureOpenApi(openApiOptions, options));
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

        // Namespaced login rate-limit policy (5 attempts / 15 min per client IP, 429 on rejection).
        // Registration only adds the named policy the AuthController attribute refers to; nothing
        // is enforced unless the host pipeline runs UseRateLimiter() (issue #21).
        services.AddRateLimiter(rateLimiterOptions =>
            rateLimiterOptions.AddPolicy<string, LoginRateLimitPolicy>(EverTaskApiOptions.LoginRateLimitPolicyName));

        // Add controllers with this assembly and route prefix convention. The monitoring JSON
        // contract is attached per-controller by the convention (MonitoringJsonResultFilter), so
        // the host's shared MVC JsonOptions are never touched (issue #21).
        services.AddControllers(mvcOptions =>
            {
                // Prefix + ApiExplorer group for the monitoring controllers only (host controllers untouched)
                mvcOptions.Conventions.Add(
                    new Conventions.RoutePrefixConvention(options.BasePath, options.OpenApiDocumentName));
            })
            .AddApplicationPart(typeof(ServiceCollectionExtensions).Assembly);

        // Add CORS if enabled
        if (options.EnableCors)
        {
            services.AddCors(corsOptions =>
            {
                corsOptions.AddPolicy(EverTaskApiOptions.CorsPolicyName, policy =>
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
        services.AddOpenApi(options.OpenApiDocumentName, openApiOptions => ConfigureOpenApi(openApiOptions, options));
#endif

        // Register startup filter to automatically configure middleware pipeline
        services.AddSingleton<IStartupFilter>(sp =>
            new EverTaskApiStartupFilter(sp.GetRequiredService<EverTaskApiOptions>()));

        return services;
    }

#if NET9_0_OR_GREATER
    private static void ConfigureOpenApi(OpenApiOptions openApiOptions, EverTaskApiOptions options)
    {
        // Strictly this document's group: ungrouped host endpoints stay in the host's documents
        openApiOptions.ShouldInclude = description => description.GroupName == options.OpenApiDocumentName;

        // The built-in generator ignores [Obsolete]; surface it as "deprecated" (GET /auth/magic, #22)
        openApiOptions.AddOperationTransformer((operation, context, _) =>
        {
            if (context.Description.ActionDescriptor.EndpointMetadata.OfType<ObsoleteAttribute>().Any())
            {
                operation.Deprecated = true;
            }

            return Task.CompletedTask;
        });
    }
#endif
}
