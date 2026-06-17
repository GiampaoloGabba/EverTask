using System.Reflection;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using Newtonsoft.Json;

namespace EverTask.Tests.Serialization;

/// <summary>
/// B2 — the REAL production interval types (not the PoC stand-ins) must survive an STJ round-trip and a
/// legacy Newtonsoft→STJ read with their full schedule intact. <c>DayInterval.OnDays</c> /
/// <c>WeekInterval.OnDays</c> are <c>{ get; internal set; }</c> today, so STJ silently drops them — a
/// schedule-corrupting data loss on recovery. These tests pin the fix on the shipped types and guard the
/// parameterless ctor (its removal would silently flip <c>Interval</c> to a different default and re-break
/// OnDays binding). Legacy producer = Newtonsoft 13.x, kept ONLY in the test project.
/// </summary>
public class IntervalSerializationParityTests
{
    private static readonly JsonSerializerSettings Legacy = new() { TypeNameHandling = TypeNameHandling.None };

    private static string LegacyJson(object value) => JsonConvert.SerializeObject(value, Legacy);

    private static readonly DateTimeOffset Anchor = new(2026, 6, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DayInterval_OnDays_roundtrips_through_EverTaskJson()
    {
        var original = new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Friday });

        var restored = EverTaskJson.Deserialize<DayInterval>(EverTaskJson.Serialize(original))!;

        restored.Interval.ShouldBe(0);
        restored.OnDays.ShouldBe(new[] { DayOfWeek.Monday, DayOfWeek.Friday });
    }

    [Fact]
    public void WeekInterval_OnDays_roundtrips_through_EverTaskJson()
    {
        var original = new WeekInterval(2, new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday });

        var restored = EverTaskJson.Deserialize<WeekInterval>(EverTaskJson.Serialize(original))!;

        restored.Interval.ShouldBe(2);
        restored.OnDays.ShouldBe(new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday });
    }

    [Fact]
    public void Legacy_DayInterval_OnDays_recovers_with_identical_schedule()
    {
        var original = new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Friday });

        var restored = EverTaskJson.Deserialize<DayInterval>(LegacyJson(original))!;

        restored.Interval.ShouldBe(0);
        restored.OnDays.ShouldBe(new[] { DayOfWeek.Monday, DayOfWeek.Friday });
        Should.NotThrow(() => restored.GetNextOccurrence(Anchor));
        // Schedule must be byte-for-byte the same occurrence as before the migration.
        restored.GetNextOccurrence(Anchor).ShouldBe(original.GetNextOccurrence(Anchor));
    }

    [Fact]
    public void Legacy_WeekInterval_OnDays_recovers_with_identical_schedule()
    {
        var original = new WeekInterval(2, new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday })
        {
            OnTimes = new[] { new TimeOnly(9, 30) }
        };

        var restored = EverTaskJson.Deserialize<WeekInterval>(LegacyJson(original))!;

        restored.Interval.ShouldBe(2);
        restored.OnDays.ShouldBe(new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday });
        restored.GetNextOccurrence(Anchor).ShouldBe(original.GetNextOccurrence(Anchor));
    }

    // --- Cross-product Interval × OnDays (gap #2): the Day/Week default Interval is 1, others 0; Validate()
    // throws on Interval==0 && no OnDays. Each combination must read back correctly without a spurious throw
    // or a silently-corrupted schedule. ---

    [Theory]
    [InlineData(0, true)]   // OnDays-driven (top-level OnDays(...) builds DayInterval(0, days))
    [InlineData(3, true)]   // both an explicit cadence AND OnDays
    [InlineData(3, false)]  // pure cadence, no OnDays
    public void DayInterval_cross_product_interval_and_onDays(int interval, bool withDays)
    {
        var days = withDays ? new[] { DayOfWeek.Monday, DayOfWeek.Wednesday } : Array.Empty<DayOfWeek>();
        var original = withDays ? new DayInterval(interval, days) : new DayInterval(interval);

        var restored = EverTaskJson.Deserialize<DayInterval>(LegacyJson(original))!;

        restored.Interval.ShouldBe(interval);
        restored.OnDays.ShouldBe(days);
        Should.NotThrow(() => restored.GetNextOccurrence(Anchor));
        restored.GetNextOccurrence(Anchor).ShouldBe(original.GetNextOccurrence(Anchor));
    }

    [Fact]
    public void Ctor_selection_omitted_Interval_key_uses_declared_default()
    {
        // Day/Week default Interval = 1; the pure-cadence intervals default to 0.
        EverTaskJson.Deserialize<DayInterval>("{\"OnTimes\":[\"06:00:00\"]}")!.Interval.ShouldBe(1);
        EverTaskJson.Deserialize<WeekInterval>("{}")!.Interval.ShouldBe(1);
        EverTaskJson.Deserialize<SecondInterval>("{}")!.Interval.ShouldBe(0);
        EverTaskJson.Deserialize<MinuteInterval>("{}")!.Interval.ShouldBe(0);
        EverTaskJson.Deserialize<HourInterval>("{}")!.Interval.ShouldBe(0);
        EverTaskJson.Deserialize<MonthInterval>("{}")!.Interval.ShouldBe(0);
    }

    [Fact]
    public void Ctor_selection_is_case_insensitive_and_populates_collections()
    {
        var day = EverTaskJson.Deserialize<DayInterval>(
            "{\"interval\":3,\"onDays\":[1,5],\"onTimes\":[\"06:00:00\"]}")!;

        day.Interval.ShouldBe(3);
        day.OnDays.ShouldBe(new[] { DayOfWeek.Monday, DayOfWeek.Friday });
        day.OnTimes.ShouldBe(new[] { new TimeOnly(6, 0) });
    }

    // --- Build-time guard (gap #1): every interval MUST keep a public parameterless ctor annotated as the
    // STJ [JsonConstructor]. Removing it lets STJ fall back to a parameterized ctor, which flips Interval to
    // a different default and stops binding OnDays/OnTimes via setters — a silent schedule corruption. The
    // direct `new XInterval()` calls below also fail to COMPILE if the ctor is deleted. ---

    [Fact]
    public void Every_interval_keeps_a_public_parameterless_json_constructor()
    {
        // Compile-time guard: these do not build if the parameterless ctor is removed.
        _ = new SecondInterval();
        _ = new MinuteInterval();
        _ = new HourInterval();
        _ = new DayInterval();
        _ = new WeekInterval();
        _ = new MonthInterval();
        _ = new CronInterval();

        foreach (var t in new[]
                 {
                     typeof(SecondInterval), typeof(MinuteInterval), typeof(HourInterval),
                     typeof(DayInterval), typeof(WeekInterval), typeof(MonthInterval), typeof(CronInterval)
                 })
        {
            var ctor = t.GetConstructor(Type.EmptyTypes);
            ctor.ShouldNotBeNull($"{t.Name} must keep a public parameterless constructor for STJ.");
            ctor!.GetCustomAttribute<System.Text.Json.Serialization.JsonConstructorAttribute>()
                .ShouldNotBeNull($"{t.Name}'s parameterless ctor must be the STJ [JsonConstructor].");
        }
    }
}
