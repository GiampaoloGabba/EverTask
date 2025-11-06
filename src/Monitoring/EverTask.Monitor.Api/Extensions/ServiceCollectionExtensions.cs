using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EverTask.Monitor.Api.Infrastructure;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Services;
using EverTask.Monitoring;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
#if NET8_0_OR_GREATER
using Microsoft.AspNetCore.RateLimiting;
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

        // Configure JWT authentication (always enabled)
        var jwtSecret = options.JwtSecret ?? GenerateRandomSecret();
        var key = Encoding.UTF8.GetBytes(jwtSecret);

        services.AddAuthentication(authOptions =>
            {
                authOptions.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                authOptions.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(jwtOptions =>
            {
                jwtOptions.RequireHttpsMetadata = false; // Allow HTTP for development
                jwtOptions.SaveToken = true;
                jwtOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = options.JwtIssuer,
                    ValidAudience = options.JwtAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ClockSkew = TimeSpan.FromMinutes(5)
                };

                // Support SignalR authentication via query string
                jwtOptions.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;

                        // If the request is for SignalR hub and has a token
                        if (!string.IsNullOrEmpty(accessToken) &&
                            path.StartsWithSegments(options.SignalRHubPath))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

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

        // Configure JWT authentication (always enabled)
        var jwtSecret = options.JwtSecret ?? GenerateRandomSecret();
        var key = Encoding.UTF8.GetBytes(jwtSecret);

        services.AddAuthentication(authOptions =>
            {
                authOptions.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                authOptions.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(jwtOptions =>
            {
                jwtOptions.RequireHttpsMetadata = false; // Allow HTTP for development
                jwtOptions.SaveToken = true;
                jwtOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = options.JwtIssuer,
                    ValidAudience = options.JwtAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ClockSkew = TimeSpan.FromMinutes(5)
                };

                // Support SignalR authentication via query string
                jwtOptions.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;

                        // If the request is for SignalR hub and has a token
                        if (!string.IsNullOrEmpty(accessToken) &&
                            path.StartsWithSegments(options.SignalRHubPath))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

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

        // Register startup filter to automatically configure middleware pipeline
        services.AddSingleton<IStartupFilter>(sp =>
            new EverTaskApiStartupFilter(sp.GetRequiredService<EverTaskApiOptions>()));

        return services;
    }

    private static string GenerateRandomSecret()
    {
        // Generate 32 bytes (256 bits) random secret
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
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
