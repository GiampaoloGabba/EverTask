using EverTask.Abstractions;
using EverTask.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace EverTask.Tests.Serialization;

/// <summary>
/// Closes two coverage gaps left after the Newtonsoft→STJ migration:
/// 1. <c>TolerantEnumConverter</c> across NON-int underlying enum widths and [Flags] enums (the per-width
///    read/write code paths were otherwise untested).
/// 2. The documented contract that Newtonsoft member attributes (<c>[JsonProperty]</c>/<c>[JsonIgnore]</c>)
///    are NOT honored by STJ — pinned so the documentation never silently drifts from behavior.
/// </summary>
public class EnumAndAttributeContractTests
{
    public enum ByteEnum   : byte   { A = 0, B = 200 }
    public enum SByteEnum  : sbyte  { N = -5, P = 5 }
    public enum ShortEnum  : short  { S = -1000 }
    public enum LongEnum   : long   { Big = 9_000_000_000L }
    public enum ULongEnum  : ulong  { Huge = 18_000_000_000_000_000_000UL }

    [Flags]
    public enum Perms : byte { None = 0, Read = 1, Write = 2, Exec = 4 }

    private static readonly JsonSerializerSettings LegacyStringEnum = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        Converters       = { new StringEnumConverter() }
    };

    [Fact]
    public void Enum_widths_round_trip_and_write_numeric()
    {
        EverTaskJson.Deserialize<ByteEnum>(EverTaskJson.Serialize(ByteEnum.B)).ShouldBe(ByteEnum.B);
        EverTaskJson.Serialize(ByteEnum.B).ShouldBe("200");

        EverTaskJson.Deserialize<SByteEnum>(EverTaskJson.Serialize(SByteEnum.N)).ShouldBe(SByteEnum.N);
        EverTaskJson.Serialize(SByteEnum.N).ShouldBe("-5");

        EverTaskJson.Deserialize<ShortEnum>(EverTaskJson.Serialize(ShortEnum.S)).ShouldBe(ShortEnum.S);
        EverTaskJson.Serialize(ShortEnum.S).ShouldBe("-1000");

        EverTaskJson.Deserialize<LongEnum>(EverTaskJson.Serialize(LongEnum.Big)).ShouldBe(LongEnum.Big);
        EverTaskJson.Serialize(LongEnum.Big).ShouldBe("9000000000");

        // ulong > long.MaxValue must not overflow on write (dedicated ulong branch).
        EverTaskJson.Deserialize<ULongEnum>(EverTaskJson.Serialize(ULongEnum.Huge)).ShouldBe(ULongEnum.Huge);
        EverTaskJson.Serialize(ULongEnum.Huge).ShouldBe("18000000000000000000");
    }

    [Fact]
    public void Flags_enum_round_trips_combined_value_as_number()
    {
        var combined = Perms.Read | Perms.Write | Perms.Exec; // 7
        EverTaskJson.Serialize(combined).ShouldBe("7");
        EverTaskJson.Deserialize<Perms>("7").ShouldBe(combined);
    }

    [Fact]
    public void Reads_legacy_string_names_for_non_int_enums()
    {
        // A legacy host with a global StringEnumConverter wrote these enum names as strings.
        var byteJson  = JsonConvert.SerializeObject(ByteEnum.B, LegacyStringEnum);
        var flagsJson = JsonConvert.SerializeObject(Perms.Read | Perms.Write, LegacyStringEnum);
        byteJson.ShouldContain("\"B\"");

        EverTaskJson.Deserialize<ByteEnum>(byteJson).ShouldBe(ByteEnum.B);
        // Newtonsoft writes combined flags as "Read, Write"; STJ's tolerant converter parses it back.
        EverTaskJson.Deserialize<Perms>(flagsJson).ShouldBe(Perms.Read | Perms.Write);
    }

    public record EnumHolder(Perms X);

    [Fact]
    public void Reads_string_numeric_enum_value()
    {
        // P2-9: a legacy/peer producer can write an enum value as a QUOTED number ("2"). The tolerant
        // converter must parse it back to the underlying value, both as a scalar and as an object property
        // ({"X":"2"}). Pinned here so net8/net9/net10 agree (the converter is the only reader of this form).
        EverTaskJson.Deserialize<Perms>("\"2\"").ShouldBe(Perms.Write);
        EverTaskJson.Deserialize<Perms>("2").ShouldBe(Perms.Write);
        EverTaskJson.Deserialize<EnumHolder>("{\"X\":\"2\"}")!.X.ShouldBe(Perms.Write);
    }

    // --- Newtonsoft member attributes are NOT honored by STJ (documented contract, pinned here) ---

    public record AttributedTask(
        [property: JsonProperty("oid")] Guid OrderId,
        [property: JsonIgnore] string Secret,
        string Visible) : IEverTask;

    [Fact]
    public void Newtonsoft_JsonProperty_rename_is_not_honored_by_STJ()
    {
        var task = new AttributedTask(Guid.NewGuid(), "secret", "shown");

        var json = EverTaskJson.Serialize(task);

        // STJ ignores [JsonProperty("oid")] → the key is the PascalCase property name, not "oid".
        json.ShouldContain("\"OrderId\"");
        json.ShouldNotContain("\"oid\"");
    }

    [Fact]
    public void Newtonsoft_JsonIgnore_is_not_honored_by_STJ()
    {
        var task = new AttributedTask(Guid.NewGuid(), "secret", "shown");

        var json = EverTaskJson.Serialize(task);

        // STJ ignores [Newtonsoft JsonIgnore] → the "Secret" value IS serialized (and would be broadcast via
        // monitoring). Task authors must NOT rely on Newtonsoft attributes (documented in Abstractions/CLAUDE.md).
        json.ShouldContain("secret");
    }
}
