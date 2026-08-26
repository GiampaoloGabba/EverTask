using System.Text.Json.Serialization;

namespace EverTask.Scheduler.Recurring;

/// <summary>
/// The persisted form of "this schedule's occurrences come from a provider": the registration key and the
/// opaque configuration string, and nothing else.
/// </summary>
/// <remarks>
/// No type name is stored, deliberately. A row outlives the assembly it was written by, and a persisted type
/// name would turn a rename or a move into an unrecoverable schedule — the same reason the payload contract
/// keeps its polymorphism to a declared alias set. The key is the indirection: what it resolves to is a
/// registration the host makes at startup.
/// </remarks>
public class ProviderSettings
{
    /// <summary>Public and parameterless for the serializer, like every interval type (payload contract).</summary>
    [JsonConstructor]
    public ProviderSettings() { }

    /// <summary>The key the provider is registered under.</summary>
    public string Key { get; set; } = "";

    /// <summary>The opaque configuration string handed back to the provider on every call.</summary>
    public string? Config { get; set; }

    /// <summary>
    /// Checks the shape on every path that accepts a schedule. A row whose key is empty could never resolve a
    /// provider, so it is corrupt schedule metadata like an unparseable cron and takes the same poison route.
    /// </summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new ArgumentException(
                "An occurrence provider needs the key it was registered under; this schedule carries none.",
                nameof(Key));
        }
    }

    /// <summary>The human-readable tail appended to a schedule's description.</summary>
    internal string Describe() =>
        string.IsNullOrEmpty(Config)
            ? $"Use occurrence provider '{Key}'"
            : $"Use occurrence provider '{Key}' with config {Config}";
}
