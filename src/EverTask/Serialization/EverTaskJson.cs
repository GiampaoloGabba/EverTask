using Newtonsoft.Json;

namespace EverTask.Serialization;

/// <summary>
/// The single, isolated Newtonsoft.Json configuration EverTask uses to (de)serialize task payloads and
/// recurring metadata. Passing explicit settings makes <see cref="JsonConvert"/> ignore the process-global
/// <see cref="JsonConvert.DefaultSettings"/>, so a host that sets them — or opens
/// <see cref="TypeNameHandling"/> globally, which would be a deserialization gadget surface on recovery —
/// cannot corrupt or weaponize EverTask's task round-trip (L33).
/// </summary>
internal static class EverTaskJson
{
    /// <summary>
    /// <see cref="TypeNameHandling.None"/> never emits or honors a <c>$type</c> marker, closing the
    /// gadget-deserialization surface on recovery. The remaining values mirror Newtonsoft's own defaults
    /// so payloads serialized before this change still round-trip unchanged.
    /// </summary>
    internal static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameHandling = TypeNameHandling.None
    };

    internal static string Serialize(object? value) =>
        JsonConvert.SerializeObject(value, Settings);

    internal static object? Deserialize(string value, Type type) =>
        JsonConvert.DeserializeObject(value, type, Settings);

    internal static T? Deserialize<T>(string value) =>
        JsonConvert.DeserializeObject<T>(value, Settings);
}
