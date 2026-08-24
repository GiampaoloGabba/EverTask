using EverTask.Scheduler.Recurring;

namespace EverTask.Tests.RecurringTests.TimeZones;

/// <summary>
/// The nominal-slot-to-instant mapping on its own (T6/T7): pure functions over a zone's rules, so they are
/// unit-tested directly. Everything above them — the grid walk, the schedules, the deliveries — is exercised
/// against real parts elsewhere in this folder.
/// </summary>
public class WallClockTests
{
    private static readonly TimeZoneInfo Rome     = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
    private static readonly TimeZoneInfo LordHowe = TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe");
    private static readonly TimeZoneInfo Kolkata  = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static readonly DateTimeOffset LongAgo = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi, int s = 0) =>
        new(y, mo, d, h, mi, s, TimeSpan.Zero);

    [Fact]
    public void A_wall_time_the_zone_really_has_maps_to_the_instant_of_its_own_offset()
    {
        // 2026-07-15 is inside Rome's summer time: 09:00 local is 07:00Z.
        var mapping = WallClock.ToUtc(new DateTime(2026, 7, 15, 9, 0, 0), Rome, LongAgo);

        mapping.Kind.ShouldBe(WallMappingKind.Exact);
        mapping.Consumed.ShouldBeFalse();
        mapping.Utc.ShouldBe(Utc(2026, 7, 15, 7, 0));
    }

    [Fact]
    public void The_same_wall_time_in_winter_maps_an_hour_later_because_the_offset_changed()
    {
        // The point of storing an id instead of an offset: 09:00 Rome is 08:00Z in January and 07:00Z in July.
        var winter = WallClock.ToUtc(new DateTime(2026, 1, 15, 9, 0, 0), Rome, LongAgo);
        var summer = WallClock.ToUtc(new DateTime(2026, 7, 15, 9, 0, 0), Rome, LongAgo);

        winter.Utc.ShouldBe(Utc(2026, 1, 15, 8, 0));
        summer.Utc.ShouldBe(Utc(2026, 7, 15, 7, 0));
    }

    [Fact]
    public void A_zone_with_a_fractional_offset_is_mapped_on_that_offset()
    {
        // Kolkata is +05:30 all year: the mapping must not round the offset to whole hours anywhere.
        var mapping = WallClock.ToUtc(new DateTime(2026, 3, 10, 9, 0, 0), Kolkata, LongAgo);

        mapping.Utc.ShouldBe(Utc(2026, 3, 10, 3, 30));
    }

    [Theory]
    // Rome's 2026 spring forward removes 02:00 -> 03:00 local. Every nominal slot inside it, wherever it
    // falls and however odd its seconds are, resolves to the first local time that exists: 03:00 = 01:00Z.
    [InlineData(2, 0, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 30, 0)]
    [InlineData(2, 59, 59)]
    public void A_wall_time_a_gap_removed_moves_to_the_first_local_time_that_exists(int hour, int minute, int second)
    {
        var mapping = WallClock.ToUtc(new DateTime(2026, 3, 29, hour, minute, second), Rome, LongAgo);

        mapping.Kind.ShouldBe(WallMappingKind.Shifted);
        mapping.Utc.ShouldBe(Utc(2026, 3, 29, 1, 0));
    }

    [Fact]
    public void A_half_hour_gap_is_found_without_assuming_the_shift_is_an_hour()
    {
        // Lord Howe moves by THIRTY minutes: 02:00 -> 02:30 local. A search that assumed a 60-minute gap
        // would answer 03:00 and be half an hour late, every spring.
        var mapping = WallClock.ToUtc(new DateTime(2026, 10, 4, 2, 15, 0), LordHowe, LongAgo);

        mapping.Kind.ShouldBe(WallMappingKind.Shifted);

        var resolved = TimeZoneInfo.ConvertTime(mapping.Utc, LordHowe);
        resolved.DateTime.ShouldBe(new DateTime(2026, 10, 4, 2, 30, 0));
    }

    [Fact]
    public void The_first_valid_local_time_is_exact_to_the_tick()
    {
        // One tick before the transition is still inside the gap; the mapping's answer must be the boundary
        // itself, not the next second or the next minute.
        var boundary = new DateTime(2026, 3, 29, 3, 0, 0);

        Rome.IsInvalidTime(boundary.AddTicks(-1)).ShouldBeTrue("the tick before the transition is in the gap");

        var mapping = WallClock.ToUtc(boundary.AddTicks(-1), Rome, LongAgo);

        TimeZoneInfo.ConvertTime(mapping.Utc, Rome).DateTime.ShouldBe(boundary);
    }

    [Fact]
    public void Several_nominal_slots_inside_one_gap_collapse_onto_the_same_instant()
    {
        var first  = WallClock.ToUtc(new DateTime(2026, 3, 29, 2, 15, 0), Rome, LongAgo);
        var second = WallClock.ToUtc(new DateTime(2026, 3, 29, 2, 45, 0), Rome, LongAgo);

        second.Utc.ShouldBe(first.Utc,
            "a gap has one exit, so every slot inside it stands for the same instant — which is why the walk " +
            "has to report the second one as already consumed instead of firing twice");
    }

    [Fact]
    public void A_single_mapping_never_claims_to_have_collapsed_anything()
    {
        // The counter T6 asks for belongs to the WALK: one call sees one slot and cannot know how many others
        // ended up on its instant, so it must answer 0 rather than guess. DstTransitionTests is where the
        // number the log carries is pinned.
        foreach (var slot in new[]
                 {
                     new DateTime(2026, 7, 15, 9, 0, 0),   // exact
                     new DateTime(2026, 3, 29, 2, 30, 0),  // inside the gap
                     new DateTime(2026, 10, 25, 2, 30, 0)  // repeated
                 })
        {
            WallClock.ToUtc(slot, Rome, LongAgo).CollapsedCount.ShouldBe(0);
        }
    }

    [Fact]
    public void A_repeated_wall_time_takes_the_first_pass()
    {
        // Rome's 2026 fall back repeats 02:00 -> 03:00 local. 02:30 happens twice: 00:30Z (CEST, +2) and
        // 01:30Z (CET, +1). A calendar slot means the first one.
        var mapping = WallClock.ToUtc(new DateTime(2026, 10, 25, 2, 30, 0), Rome, LongAgo);

        mapping.Kind.ShouldBe(WallMappingKind.Ambiguous);
        mapping.Utc.ShouldBe(Utc(2026, 10, 25, 0, 30));
    }

    [Fact]
    public void An_instant_at_or_before_the_walk_position_is_reported_as_consumed_not_returned()
    {
        var slot = new DateTime(2026, 7, 15, 9, 0, 0);
        var utc  = Utc(2026, 7, 15, 7, 0);

        WallClock.ToUtc(slot, Rome, utc).Consumed.ShouldBeTrue("the same instant is not progress");
        WallClock.ToUtc(slot, Rome, utc.AddTicks(1)).Consumed.ShouldBeTrue("an earlier instant is not progress");
        WallClock.ToUtc(slot, Rome, utc.AddTicks(-1)).Consumed.ShouldBeFalse();
    }

    [Fact]
    public void ToWall_reads_an_instant_on_the_zones_clock_with_its_offset_attached()
    {
        var summer = WallClock.ToWall(Utc(2026, 7, 15, 7, 0), Rome);
        var winter = WallClock.ToWall(Utc(2026, 1, 15, 8, 0), Rome);

        summer.DateTime.ShouldBe(new DateTime(2026, 7, 15, 9, 0, 0));
        summer.Offset.ShouldBe(TimeSpan.FromHours(2));

        winter.DateTime.ShouldBe(new DateTime(2026, 1, 15, 9, 0, 0));
        winter.Offset.ShouldBe(TimeSpan.FromHours(1),
            "the offset identifies which pass of a repeated hour a local reading belongs to");
    }

    [Fact]
    public void A_zone_whose_rules_are_not_in_the_tz_database_is_mapped_by_the_same_search()
    {
        // A custom zone with EU-shaped rules: nothing here reads the tz database, so a zone assembled in
        // process must go through the same gap search and the same first-pass rule. It cannot be persisted
        // (no IANA id), which is why it is exercised at this level and not through a schedule.
        var custom = CreateEuRuledZone("Test/EuRules", TimeSpan.FromHours(1));

        var gap = WallClock.ToUtc(new DateTime(2026, 3, 29, 2, 30, 0), custom, LongAgo);
        gap.Kind.ShouldBe(WallMappingKind.Shifted);
        gap.Utc.ShouldBe(Utc(2026, 3, 29, 1, 0));

        var repeated = WallClock.ToUtc(new DateTime(2026, 10, 25, 2, 30, 0), custom, LongAgo);
        repeated.Kind.ShouldBe(WallMappingKind.Ambiguous);
        repeated.Utc.ShouldBe(Utc(2026, 10, 25, 0, 30));
    }

    /// <summary>
    /// A zone with the European rule: forward at 02:00 local on the last Sunday of March, back at 03:00 local
    /// on the last Sunday of October.
    /// </summary>
    internal static TimeZoneInfo CreateEuRuledZone(string id, TimeSpan baseOffset)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0), month: 3, week: 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 3, 0, 0), month: 10, week: 5, DayOfWeek.Sunday));

        return TimeZoneInfo.CreateCustomTimeZone(id, baseOffset, id, id, id, [rule]);
    }
}
