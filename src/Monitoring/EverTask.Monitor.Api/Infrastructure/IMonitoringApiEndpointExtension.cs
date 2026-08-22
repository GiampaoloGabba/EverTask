using EverTask.Monitor.Api.Options;
using Microsoft.AspNetCore.Routing;

namespace EverTask.Monitor.Api.Infrastructure;

/// <summary>
/// Extension point for companion packages (e.g. EverTask.Monitor.Api.Scalar) that need to map
/// additional endpoints under the monitoring base path. All registered implementations are
/// invoked by <c>MapEverTaskApi()</c> after the core endpoints are mapped.
/// </summary>
public interface IMonitoringApiEndpointExtension
{
    /// <summary>
    /// Maps the extension's endpoints.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="options">The monitoring API options.</param>
    void MapEndpoints(IEndpointRouteBuilder endpoints, EverTaskApiOptions options);
}
