using System.Text.Json;
using System.Text.Json.Serialization;

namespace EverTask.Serialization;

/// <summary>
/// Enum converter that mirrors Newtonsoft's lenient READ while keeping the historical numeric WRITE.
/// <para>
/// System.Text.Json's built-in enum converter writes numbers but THROWS on a string-named enum on read.
/// Legacy rows can carry string enum names — written by a host that had a global
/// <c>StringEnumConverter</c> before the L33 isolation hardening, or by a member-level converter — and the
/// recovery path turns a deserialization throw into permanent task loss. So this converter reads BOTH a
/// numeric value AND a (case-insensitive) string name, and on write emits the underlying NUMERIC value for
/// byte-parity with the historical Newtonsoft default (a freshly written row stays readable by an
/// un-migrated peer). The built-in <see cref="JsonStringEnumConverter"/> cannot be used because it writes
/// string names.
/// </para>
/// </summary>
internal sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;
}

internal sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var name = reader.GetString();
                if (!string.IsNullOrEmpty(name) && Enum.TryParse<T>(name, ignoreCase: true, out var parsed))
                    return parsed;
                throw new JsonException(
                    $"Unable to convert \"{name}\" to enum {typeof(T)}.");

            case JsonTokenType.Number:
                var underlying = Enum.GetUnderlyingType(typeof(T));
                // Read with the enum's actual underlying width, then box back to the enum.
                if (underlying == typeof(int))    return (T)Enum.ToObject(typeof(T), reader.GetInt32());
                if (underlying == typeof(uint))   return (T)Enum.ToObject(typeof(T), reader.GetUInt32());
                if (underlying == typeof(long))   return (T)Enum.ToObject(typeof(T), reader.GetInt64());
                if (underlying == typeof(ulong))  return (T)Enum.ToObject(typeof(T), reader.GetUInt64());
                if (underlying == typeof(short))  return (T)Enum.ToObject(typeof(T), reader.GetInt16());
                if (underlying == typeof(ushort)) return (T)Enum.ToObject(typeof(T), reader.GetUInt16());
                if (underlying == typeof(byte))   return (T)Enum.ToObject(typeof(T), reader.GetByte());
                if (underlying == typeof(sbyte))  return (T)Enum.ToObject(typeof(T), reader.GetSByte());
                return (T)Enum.ToObject(typeof(T), reader.GetInt64());

            default:
                throw new JsonException(
                    $"Unexpected token {reader.TokenType} when reading enum {typeof(T)}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var underlying = Enum.GetUnderlyingType(typeof(T));
        if (underlying == typeof(ulong))
            writer.WriteNumberValue(Convert.ToUInt64(value));
        else
            writer.WriteNumberValue(Convert.ToInt64(value));
    }
}
