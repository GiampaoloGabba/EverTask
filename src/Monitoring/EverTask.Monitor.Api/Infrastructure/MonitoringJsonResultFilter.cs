using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace EverTask.Monitor.Api.Infrastructure;

/// <summary>
/// Applies the monitoring API's JSON contract (camelCase, nulls omitted, enums as strings) to the
/// package's own controllers only. Attached per-controller by <c>RoutePrefixConvention</c>, so the
/// host's MVC <c>JsonOptions</c> stay untouched (issue #21).
/// </summary>
internal sealed class MonitoringJsonResultFilter : IResultFilter
{
    // An ObjectResult with a non-empty Formatters collection bypasses the host's global formatters
    private static readonly SystemTextJsonOutputFormatter Formatter = new(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // The formatter requires an explicit resolver (it freezes the options at construction)
            TypeInfoResolver       = new DefaultJsonTypeInfoResolver(),
            PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters             = { new JsonStringEnumConverter() }
        });

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult objectResult)
        {
            objectResult.Formatters.Add(Formatter);
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
