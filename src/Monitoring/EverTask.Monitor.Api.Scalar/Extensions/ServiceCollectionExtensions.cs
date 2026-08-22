using EverTask.Monitor.Api.Infrastructure;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Scalar.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Monitor.Api.Scalar.Extensions;

/// <summary>
/// Extension methods for adding the Scalar API reference UI to the EverTask Monitoring API.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Scalar API reference UI for the EverTask Monitoring API, served at
    /// /evertask-monitoring/scalar by <c>MapEverTaskApi()</c>. Enables the monitoring OpenAPI
    /// document automatically. Must be called after <c>AddMonitoringApi()</c>.
    /// Requires net9.0+ at runtime (no-op with a startup warning on net8.0).
    /// </summary>
    /// <param name="builder">The EverTask service builder.</param>
    /// <returns>The EverTask service builder for chaining.</returns>
    public static EverTaskServiceBuilder AddMonitoringApiScalar(this EverTaskServiceBuilder builder)
    {
        AddScalarCore(builder.Services);
        return builder;
    }

    /// <summary>
    /// Adds the Scalar API reference UI for the standalone EverTask Monitoring API.
    /// Must be called after <c>AddEverTaskMonitoringApiStandalone()</c>.
    /// Requires net9.0+ at runtime (no-op with a startup warning on net8.0).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMonitoringApiScalar(this IServiceCollection services)
    {
        AddScalarCore(services);
        return services;
    }

    private static void AddScalarCore(IServiceCollection services)
    {
        // The monitoring options singleton is created by AddMonitoringApi: the Scalar UI cannot
        // exist without the monitoring API (and its OpenAPI document) being registered first
        var options = services.FirstOrDefault(d => d.ServiceType == typeof(EverTaskApiOptions))
                              ?.ImplementationInstance as EverTaskApiOptions
                      ?? throw new InvalidOperationException(
                          "AddMonitoringApiScalar() must be called after AddMonitoringApi() (or " +
                          "AddEverTaskMonitoringApiStandalone()).");

        // The Scalar UI renders the monitoring OpenAPI document, so make sure it is served
        options.EnableOpenApiDocument = true;

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMonitoringApiEndpointExtension, ScalarEndpointExtension>());
    }
}
