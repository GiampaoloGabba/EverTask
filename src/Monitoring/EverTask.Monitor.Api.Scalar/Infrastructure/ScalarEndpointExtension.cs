#if NET9_0_OR_GREATER
using Scalar.AspNetCore;
#else
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
#endif
using EverTask.Monitor.Api.Infrastructure;
using EverTask.Monitor.Api.Options;
using Microsoft.AspNetCore.Routing;

namespace EverTask.Monitor.Api.Scalar.Infrastructure;

/// <summary>
/// Maps the Scalar API reference UI under the monitoring base path, pointed at the
/// monitoring OpenAPI document. Invoked by <c>MapEverTaskApi()</c>.
/// </summary>
internal sealed class ScalarEndpointExtension : IMonitoringApiEndpointExtension
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints, EverTaskApiOptions options)
    {
#if NET9_0_OR_GREATER
        endpoints.MapScalarApiReference($"{options.BasePath}/scalar", scalar =>
        {
            scalar.AddDocument(
                options.OpenApiDocumentName,
                "EverTask Monitoring API",
                $"{options.BasePath}/openapi/{{documentName}}.json");
        });
#else
        // The built-in OpenAPI generator does not exist on net8.0, so there is no document to render
        var logger = endpoints.ServiceProvider
                              .GetRequiredService<ILoggerFactory>()
                              .CreateLogger("EverTask.Monitor.Api.Scalar");
        logger.LogWarning(
            "EverTask.Monitor.Api.Scalar is a no-op on net8.0: the built-in ASP.NET Core OpenAPI generator " +
            "requires net9.0 or later, so neither the OpenAPI document nor the Scalar UI are served");
#endif
    }
}
