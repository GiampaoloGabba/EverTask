using EverTask.Abstractions;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace EverTask.Tests.Serialization;

/// <summary>
/// B1 — read parity with the lenient Newtonsoft reader. System.Text.Json is a STRICT reader: by default it
/// throws on a quoted number (<c>{"Count":"42"}</c>) or a string-named enum (<c>"High"</c>) that Newtonsoft
/// accepted. Such rows can already exist on disk (written by a host that had a global
/// <see cref="StringEnumConverter"/> before the L33 isolation hardening, by a member-level converter, or by
/// a hand-edited / widened row). On recovery a deserialization throw is turned into permanent task loss, so
/// <c>EverTaskJson</c> MUST read those legacy shapes. The legacy producer is Newtonsoft 13.x, kept ONLY in
/// the test project.
/// </summary>
public class StjLegacyReadParityTests
{
    public enum Priority { Low, Normal, High }

    public record QuotedNumberTask(int Count, long Big, double Ratio) : IEverTask;

    public record EnumPayloadTask(Priority Priority) : IEverTask;

    // A host that had a global StringEnumConverter before L33 isolation: enums written as string NAMES.
    private static readonly JsonSerializerSettings LegacyStringEnum = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        Converters       = { new StringEnumConverter() }
    };

    private static readonly JsonSerializerSettings LegacyDefault = new()
    {
        TypeNameHandling = TypeNameHandling.None
    };

    [Fact]
    public void Reads_legacy_quoted_number_payload()
    {
        // A row on disk whose numbers were quoted (member-level converter / widened type / hand-edited).
        const string legacyJson = "{\"Count\":\"42\",\"Big\":\"9000000000\",\"Ratio\":\"0.5\"}";

        var restored = EverTaskJson.Deserialize<QuotedNumberTask>(legacyJson)!;

        restored.Count.ShouldBe(42);
        restored.Big.ShouldBe(9_000_000_000L);
        restored.Ratio.ShouldBe(0.5);
    }

    [Fact]
    public void Reads_legacy_string_enum_payload()
    {
        var legacyJson = JsonConvert.SerializeObject(new EnumPayloadTask(Priority.High), LegacyStringEnum);
        legacyJson.ShouldContain("\"High\""); // sanity: producer wrote the enum as a string name

        var restored = EverTaskJson.Deserialize<EnumPayloadTask>(legacyJson)!;

        restored.Priority.ShouldBe(Priority.High);
    }

    [Fact]
    public void Reads_legacy_string_enum_on_recurring_graph()
    {
        // MonthInterval.OnFirst is DayOfWeek? with a PUBLIC setter, so this isolates the enum-as-string
        // read parity (B1) from the internal-setter OnDays gap (B2).
        var original   = new RecurringTask { MonthInterval = new MonthInterval(1, new[] { 6 }) { OnFirst = DayOfWeek.Saturday } };
        var legacyJson = JsonConvert.SerializeObject(original, LegacyStringEnum);
        legacyJson.ShouldContain("\"Saturday\"");

        var restored = EverTaskJson.Deserialize<RecurringTask>(legacyJson)!;

        restored.MonthInterval!.OnFirst.ShouldBe(DayOfWeek.Saturday);
        restored.MonthInterval!.OnMonths.ShouldBe(new[] { 6 });
    }

    [Fact]
    public void Reads_legacy_datetimeoffset_with_nonzero_offset()
    {
        var original   = new DateTimeOffset(2026, 6, 17, 14, 30, 15, TimeSpan.FromHours(2)).AddTicks(1234567);
        var legacyJson = JsonConvert.SerializeObject(original, LegacyDefault);

        EverTaskJson.Deserialize<DateTimeOffset>(legacyJson).ShouldBe(original);
    }

    [Fact]
    public void Writes_enums_as_numbers_for_byte_parity_with_newtonsoft()
    {
        // B1 decision: keep NUMERIC enum WRITING (byte-parity with the historical Newtonsoft default) so a
        // freshly written row stays readable by an un-migrated peer, while still READING string enums.
        EverTaskJson.Serialize(Priority.High).ShouldBe("2");
    }

    [Fact]
    public void Public_fields_are_not_serialized_documents_payload_contract()
    {
        // Doc correction (review §3.4): STJ DROPS public fields by default (Newtonsoft serialized them).
        // We deliberately do NOT enable IncludeFields → the payload contract is "public PROPERTIES only".
        var json = EverTaskJson.Serialize(new PublicFieldPayload { Foo = 7 });
        json.ShouldNotContain("Foo");

        var back = EverTaskJson.Deserialize<PublicFieldPayload>("{\"Foo\":7}")!;
        back.Foo.ShouldBe(0); // not bound on read either
    }
}

public sealed class PublicFieldPayload : IEverTask
{
    public int Foo;
}
