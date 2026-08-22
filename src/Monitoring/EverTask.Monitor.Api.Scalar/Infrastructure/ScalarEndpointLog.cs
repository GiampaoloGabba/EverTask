#if !NET9_0_OR_GREATER
using Microsoft.Extensions.Logging;

namespace EverTask.Monitor.Api.Scalar.Infrastructure;

// EventId range 3200-3299 (ScalarEndpointExtension). Ranges are allocated per component in the #32 plan;
// a reflection test asserts solution-wide uniqueness.
// Compiled on net8.0 only, mirroring the single call site in ScalarEndpointExtension.MapEndpoints.
internal static partial class ScalarEndpointLog
{
    [LoggerMessage(EventId = 3200, Level = LogLevel.Warning,
        Message = "EverTask.Monitor.Api.Scalar is a no-op on net8.0: the built-in ASP.NET Core OpenAPI generator " +
                  "requires net9.0 or later, so neither the OpenAPI document nor the Scalar UI are served")]
    public static partial void ScalarUnavailableOnNet8(this ILogger logger);
}
#endif
