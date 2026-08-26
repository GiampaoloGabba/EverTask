using Microsoft.CodeAnalysis;

namespace EverTask.Analyzers;

/// <summary>
/// The EverTask diagnostics: the payload-contract rules (ET0001-ET0007), which mirror at compile time the
/// System.Text.Json round-trip contract enforced at runtime by <c>EverTask.Serialization.EverTaskJson</c>
/// (see <c>src/EverTask.Abstractions/CLAUDE.md</c> §Serialization Guidelines), plus the monitoring rule
/// ET0008, the resilience rule ET0009 (delays above the maximum timer duration) and the scheduling rule
/// ET0010 (a time zone on a plain cadence).
/// Keep this list in lockstep with <c>AnalyzerReleases.Unshipped.md</c> (RS2002).
/// </summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "EverTask.Serialization";

    private const string HelpLink =
        "https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask.Abstractions/CLAUDE.md";

    public static readonly DiagnosticDescriptor PublicField = new(
        id: "ET0001",
        title: "Public field is not serialized by EverTask",
        messageFormat: "Public field '{0}' is not persisted by EverTask (System.Text.Json serializes properties only); convert it to a property",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "EverTask serializes task payloads with System.Text.Json and IncludeFields is off, so public fields are silently dropped on recovery. Use a public property instead.",
        helpLinkUri: HelpLink);

    public static readonly DiagnosticDescriptor DroppedSetter = new(
        id: "ET0002",
        title: "Property is dropped on recovery",
        messageFormat: "Property '{0}' is dropped on recovery (non-public setter and no matching constructor parameter); add a public setter or a matching constructor parameter",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "System.Text.Json can only populate a property through a public/init setter or a constructor parameter. A property with only a non-public setter and no matching constructor parameter is silently dropped on read.",
        helpLinkUri: HelpLink);

    public static readonly DiagnosticDescriptor NewtonsoftAttribute = new(
        id: "ET0003",
        title: "Newtonsoft.Json attribute is ignored by EverTask",
        messageFormat: "Newtonsoft.Json attribute '{0}' is ignored by EverTask (System.Text.Json); remove it or use the System.Text.Json equivalent",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "EverTask migrated to System.Text.Json, which does not honor Newtonsoft.Json attributes such as [JsonProperty], [JsonIgnore] or [JsonConstructor]. Relying on them changes the persisted shape silently.",
        helpLinkUri: HelpLink);

    public static readonly DiagnosticDescriptor UndeclaredPolymorphism = new(
        id: "ET0004",
        title: "Polymorphic payload property throws on recovery",
        messageFormat: "Polymorphic payload property '{0}' (type '{1}') throws on recovery; declare [JsonPolymorphic] + [JsonDerivedType] on '{1}', or flatten the payload",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A property typed as an abstract class or interface does not round-trip under System.Text.Json unless the declared type opts into declarative polymorphism with [JsonPolymorphic] and at least one [JsonDerivedType]. Otherwise the derived members are dropped on write and read throws.",
        helpLinkUri: HelpLink);

    public static readonly DiagnosticDescriptor JsonElementProperty = new(
        id: "ET0005",
        title: "Property deserializes to JsonElement after recovery",
        messageFormat: "Property '{0}' deserializes to JsonElement after recovery (not boxed primitives); convert it in the handler",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "System.Text.Json deserializes object / dynamic / Dictionary<string, object> values to JsonElement, not to the boxed primitive types Newtonsoft produced. The handler must convert them explicitly.",
        helpLinkUri: HelpLink);

    // ET0006 is heuristic (false-positive risk on entity-looking types) -> shipped OFF; opt in via .editorconfig.
    public static readonly DiagnosticDescriptor NonSerializableType = new(
        id: "ET0006",
        title: "Property type is unlikely to round-trip",
        messageFormat: "Property '{0}' has type '{1}', which is unlikely to round-trip; use a stable id or a named type instead",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: false,
        description: "Types such as delegates, Stream, Type, IntPtr, CancellationToken, EF Core DbContext or ValueTuple do not survive a JSON round-trip (ValueTuple exposes its elements as fields, which System.Text.Json drops). Persist a stable identifier or a named type and resolve the instance in the handler.",
        helpLinkUri: HelpLink);

    public static readonly DiagnosticDescriptor MonitoringOpenApiOnNet8 = new(
        id: "ET0008",
        title: "Monitoring OpenAPI document is a no-op on net8.0",
        messageFormat: "'{0}' has no effect on net8.0: the built-in ASP.NET Core OpenAPI generator requires net9.0 or later, so no monitoring OpenAPI document (or Scalar UI) is served",
        category: "EverTask.Monitoring",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "EverTask.Monitor.Api generates its OpenAPI document with the built-in ASP.NET Core generator (Microsoft.AspNetCore.OpenApi), which only exists on net9.0+. On a net8.0 target, enabling EnableOpenApiDocument or adding the Scalar UI serves nothing.",
        helpLinkUri: "https://github.com/GiampaoloGabba/EverTask/blob/master/docs/monitoring-dashboard.md");

    public static readonly DiagnosticDescriptor DelayExceedsTimerLimit = new(
        id: "ET0009",
        title: "Delay exceeds the maximum timer duration",
        messageFormat: "'{0}' exceeds the largest delay .NET timers support (uint.MaxValue - 1 ms, about 49.7 days); {1}",
        category: "EverTask.Resilience",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Task.Delay and CancellationTokenSource.CancelAfter reject delays above uint.MaxValue - 1 milliseconds (about 49.7 days). EverTask validates retry delays at construction and clamps timeouts and cleanup intervals to that ceiling, so a larger value either throws or silently behaves as ~49.7 days instead of what was written.",
        helpLinkUri: "https://github.com/GiampaoloGabba/EverTask/blob/master/docs/resilience/retry-policies.md");

    public static readonly DiagnosticDescriptor TimeZoneOnElapsedSchedule = new(
        id: "ET0010",
        title: "Time zone has no effect on a plain cadence",
        messageFormat: "'InTimeZone' throws when this schedule is built: a plain cadence (every N seconds/minutes/hours) is a constant step in elapsed time and produces the same instants in every zone; anchor the schedule to a calendar (a time of day, a day of the week, a month selector or a cron expression) or drop the call",
        category: "EverTask.Scheduling",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A time zone governs calendar-anchored schedules only. On a cadence in seconds, minutes or hours EverTask refuses it with an InvalidOperationException when the schedule is built, rather than accepting a call it could not honor. Reported only for a chain whose shape is provable in place; a chain split across variables or methods is left to the runtime check.",
        helpLinkUri: "https://github.com/GiampaoloGabba/EverTask/blob/master/docs/recurring-tasks/time-zones.md");

    public static readonly DiagnosticDescriptor UnresolvableConstructor = new(
        id: "ET0007",
        title: "Payload type has no constructor System.Text.Json can use",
        messageFormat: "Type '{0}' has multiple public constructors but none is parameterless or marked [JsonConstructor]; System.Text.Json throws on recovery",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "When a type exposes more than one public constructor, System.Text.Json cannot choose one to deserialize through unless a public parameterless constructor exists or exactly one constructor is annotated with [JsonConstructor]. Otherwise recovery throws.",
        helpLinkUri: HelpLink);
}
