using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace EverTask.Serialization;

/// <summary>
/// The single, isolated System.Text.Json configuration EverTask uses to (de)serialize task payloads and
/// recurring metadata. The <see cref="JsonSerializerOptions"/> instance is static and private, so a host
/// that configures the process-global ASP.NET JSON options — or a hostile global Newtonsoft
/// <c>JsonConvert.DefaultSettings</c> (which STJ never consults) — cannot corrupt or weaponize EverTask's
/// task round-trip (L33). STJ never emits a <c>$type</c> marker, so there is no gadget-deserialization
/// surface on recovery.
/// </summary>
internal static class EverTaskJson
{
    // PascalCase + case-insensitive read + relaxed encoder give byte-parity with the legacy Newtonsoft
    // output (and with data already on disk). NumberHandling.AllowReadingFromString and the tolerant enum
    // converter replicate Newtonsoft's LENIENT read (quoted numbers, string-named enums) so a legacy row
    // never throws on recovery; both still WRITE the historical numeric form (byte-parity, peer-readable).
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy        = null,
        PropertyNameCaseInsensitive = true,
        Encoder                     = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling              = JsonNumberHandling.AllowReadingFromString,
        Converters                  = { new TolerantEnumConverterFactory() }
    };

    // Serialize(object?) is LOAD-BEARING: a root typed as object makes STJ serialize the CONCRETE runtime
    // type's properties. Do NOT make this generic (Serialize<T>) — that would emit {} for an IEverTask root.
    // Serialize(object?) is LOAD-BEARING: a root typed as object makes STJ serialize the CONCRETE runtime
    // type's properties. Do NOT make this generic (Serialize<T>) — that would emit {} for an IEverTask root.
    internal static string Serialize(object? value) =>
        JsonSerializer.Serialize(value, Options);

    internal static object? Deserialize(string value, Type type) =>
        JsonSerializer.Deserialize(value, type, Options);

    internal static T? Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, Options);
}
