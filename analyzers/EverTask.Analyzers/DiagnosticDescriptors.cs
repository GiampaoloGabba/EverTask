using Microsoft.CodeAnalysis;

namespace EverTask.Analyzers;

/// <summary>
/// The EverTask payload-contract diagnostics (ET0001-ET0006). They mirror, at compile time, the
/// System.Text.Json round-trip contract enforced at runtime by <c>EverTask.Serialization.EverTaskJson</c>
/// (see <c>src/EverTask.Abstractions/CLAUDE.md</c> §Serialization Guidelines). Keep this list in lockstep
/// with <c>AnalyzerReleases.Unshipped.md</c> (RS2002).
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
