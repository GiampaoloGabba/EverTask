using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.TimeZones;

/// <summary>
/// T9: Cronos is the oracle the fluent API's own zone arithmetic is measured against. The two walk the same
/// calendar by entirely different code — Cronos parses an expression and applies its own DST rules, the
/// builder cascades interval objects over a wall clock and maps each nominal slot back through
/// <see cref="WallClock"/> — so 400 consecutive occurrences agreeing in nine zones is a real cross-check of
/// both, and of the gap and repeated-hour rules in particular.
/// </summary>
/// <remarks>
/// From the SECOND occurrence on, by construction: a day or month interval advances its period before
/// selecting inside it, so the fluent API's first occurrence can be one period further out than the cron
/// expression's. That difference is pinned shape by shape below. The plan's ninth zone — a custom one with EU
/// rules — cannot reach a schedule at all, since nothing could resolve its id on the next run, so it runs the
/// same 400-occurrence comparison against the mapping directly.
/// </remarks>
public class CronOracleTests
{
    private static readonly string[] Zones =
    [
        "UTC",
        "Europe/Rome",         // EU rules, one-hour shift
        "America/New_York",    // US rules, different transition dates
        "Australia/Sydney",    // southern hemisphere: the transitions are the other way round
        "Asia/Kolkata",        // +05:30, no DST
        "Asia/Kathmandu",      // +05:45, no DST
        "Australia/Lord_Howe", // half-hour DST shift
        "Pacific/Chatham",     // +12:45 base, with DST
        "America/Sao_Paulo"    // DST abolished in 2019: rules that changed shape
    ];

    /// <summary>Shapes the fluent API and a cron expression both describe.</summary>
    public static TheoryData<string, string, Action<IIntervalSchedulerBuilder>> Shapes => new()
    {
        { "daily at 09:30", "30 9 * * *", s => s.EveryDay().AtTime(new TimeOnly(9, 30)) },
        // 02:30 is the wall time Rome's transitions remove and repeat: the two implementations have to agree
        // on the gap's exit and on which pass of the fall-back hour is the occurrence.
        { "daily at 02:30", "30 2 * * *", s => s.EveryDay().AtTime(new TimeOnly(2, 30)) },
        { "Mon/Wed/Fri at 07:15", "15 7 * * 1,3,5",
            s => s.OnDays(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday).AtTime(new TimeOnly(7, 15)) },
        { "Sundays at 02:30", "30 2 * * 0", s => s.EveryWeek().OnDay(DayOfWeek.Sunday).AtTime(new TimeOnly(2, 30)) },
        { "the 15th at 23:59", "59 23 15 * *", s => s.EveryMonth().OnDay(15).AtTime(new TimeOnly(23, 59)) }
    };

    public static TheoryData<string, string, string, Action<IIntervalSchedulerBuilder>> ShapesInEveryZone
    {
        get
        {
            var cases = new TheoryData<string, string, string, Action<IIntervalSchedulerBuilder>>();

            foreach (var row in Shapes)
            {
                foreach (var zone in Zones)
                    cases.Add(zone, (string)row[0], (string)row[1], (Action<IIntervalSchedulerBuilder>)row[2]);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ShapesInEveryZone))]
    public void The_fluent_grid_matches_Cronos_for_400_occurrences(
        string zoneId, string shape, string cronExpression, Action<IIntervalSchedulerBuilder> configure)
    {
        var fluent = BuildFluent(configure, zoneId);
        var cron   = BuildCron(cronExpression, zoneId);

        // Seeded on the fluent grid's own first occurrence, which is where the two are guaranteed to be on
        // the same slot; from there they must not diverge by a tick.
        var seed = fluent.CalculateNextRun(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1)
                         .ShouldNotBeNull($"'{shape}' must produce a first occurrence in {zoneId}");

        var fluentOccurrences = Walk(fluent, seed, 400);
        var cronOccurrences   = Walk(cron, seed, 400);

        fluentOccurrences.Count.ShouldBe(400, $"'{shape}' in {zoneId} must keep producing occurrences");
        fluentOccurrences.ShouldBe(cronOccurrences,
            $"the fluent grid for '{shape}' in {zoneId} must be the grid Cronos computes");
    }

    [Theory]
    [MemberData(nameof(ShapesInEveryZone))]
    public void Skip_forward_lands_where_the_oracle_says_the_next_occurrence_is(
        string zoneId, string shape, string cronExpression, Action<IIntervalSchedulerBuilder> configure)
    {
        // The O(1)/walk realignment used after a downtime must reach the same slot the oracle does — this is
        // where a zoned calendar schedule taking the uniform-grid shortcut by mistake would show up.
        var fluent = BuildFluent(configure, zoneId);
        var cron   = BuildCron(cronExpression, zoneId);

        var seed = fluent.CalculateNextRun(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1)
                         .ShouldNotBeNull();

        foreach (var elapsed in new[] { TimeSpan.FromDays(1), TimeSpan.FromDays(40), TimeSpan.FromDays(200) })
        {
            var now = seed + elapsed;

            fluent.NextOccurrenceStrictlyAfter(seed, now)
                  .ShouldBe(cron.NextOccurrenceStrictlyAfter(seed, now),
                      $"'{shape}' in {zoneId}, realigning {elapsed.TotalDays} days after the seed");
        }
    }

    /// <summary>
    /// The occurrence the oracle comparison is seeded past, pinned for EVERY shape above rather than for the
    /// day interval alone: what the two produce first, from one start, in one zone.
    /// </summary>
    /// <remarks>
    /// The start is Sunday 10 May 2026 06:00Z — 08:00 in Rome — chosen so each shape has a slot both behind
    /// and ahead of it that day. The difference T9 warns about belongs to the intervals that advance their
    /// PERIOD before selecting inside it: a day interval moves to tomorrow and a month interval to next month,
    /// so a slot still ahead today is the cron expression's first occurrence and not the fluent API's. The
    /// day-of-week shapes do not: they scan the rest of the current week first, so their first occurrence is
    /// the same one Cronos picks. Existing behaviour, unchanged by time zones.
    /// </remarks>
    public static TheoryData<string, string, Action<IIntervalSchedulerBuilder>, DateTimeOffset, DateTimeOffset>
        FirstOccurrences => new()
        {
            // The period-advancing pair: today's 09:30 belongs to cron, tomorrow's to the fluent grid.
            { "daily at 09:30", "30 9 * * *", s => s.EveryDay().AtTime(new TimeOnly(9, 30)),
                Utc(2026, 5, 11, 7, 30), Utc(2026, 5, 10, 7, 30) },
            // The same day interval agrees when the day's slot is already behind the start: both land tomorrow.
            { "daily at 02:30", "30 2 * * *", s => s.EveryDay().AtTime(new TimeOnly(2, 30)),
                Utc(2026, 5, 11, 0, 30), Utc(2026, 5, 11, 0, 30) },
            { "Mon/Wed/Fri at 07:15", "15 7 * * 1,3,5",
                s => s.OnDays(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday).AtTime(new TimeOnly(7, 15)),
                Utc(2026, 5, 11, 5, 15), Utc(2026, 5, 11, 5, 15) },
            { "Sundays at 02:30", "30 2 * * 0",
                s => s.EveryWeek().OnDay(DayOfWeek.Sunday).AtTime(new TimeOnly(2, 30)),
                Utc(2026, 5, 17, 0, 30), Utc(2026, 5, 17, 0, 30) },
            // The month interval: the 15th is still eleven days ahead, and the fluent grid still skips it.
            { "the 15th at 23:59", "59 23 15 * *", s => s.EveryMonth().OnDay(15).AtTime(new TimeOnly(23, 59)),
                Utc(2026, 6, 15, 21, 59), Utc(2026, 5, 15, 21, 59) }
        };

    [Theory]
    [MemberData(nameof(FirstOccurrences))]
    public void The_first_occurrence_of_each_shape_is_pinned_next_to_the_oracles(
        string shape, string cronExpression, Action<IIntervalSchedulerBuilder> configure,
        DateTimeOffset fluentFirst, DateTimeOffset cronFirst)
    {
        var start = Utc(2026, 5, 10, 6, 0); // Sunday, 08:00 in Rome

        BuildFluent(configure, "Europe/Rome").CalculateNextRun(start, 1)
                                             .ShouldBe(fluentFirst, $"the fluent grid for '{shape}'");
        BuildCron(cronExpression, "Europe/Rome").CalculateNextRun(start, 1)
                                                .ShouldBe(cronFirst, $"Cronos for '{shape}'");
    }

    [Fact]
    public void The_zoned_mapping_matches_Cronos_for_400_occurrences_in_a_zone_the_tz_database_lacks()
    {
        // The oracle's ninth entry in the plan: a zone with EU rules assembled in process. It cannot reach a
        // RecurringTask — a custom zone has no IANA id, so it is refused before anything is stored (T2) — so
        // the comparison is made one level down, on the two production pieces the zoned walk is composed of:
        // the interval cascade over a wall clock, and WallClock's mapping of each nominal slot back to an
        // instant. Nothing in either reads the tz database, and Cronos takes any TimeZoneInfo, so 400
        // occurrences either agree or they do not. Ratified in the decisions, §3.4.
        var custom   = WallClockTests.CreateEuRuledZone("Test/EuRules", TimeSpan.FromHours(1));
        var interval = new DayInterval(1) { OnTimes = [new TimeOnly(2, 30)] };
        var cron     = new CronInterval("30 2 * * *");

        var seed = NextInCustomZone(Utc(2026, 1, 1, 0, 0)).ShouldNotBeNull();

        var mapped = new List<DateTimeOffset>(400);
        var oracle = new List<DateTimeOffset>(400);

        var mappedCursor = seed;
        var oracleCursor = seed;

        for (var i = 0; i < 400; i++)
        {
            mapped.Add((mappedCursor = NextInCustomZone(mappedCursor).ShouldNotBeNull()));
            oracle.Add((oracleCursor = cron.GetNextOccurrence(oracleCursor, custom).ShouldNotBeNull()));
        }

        mapped.ShouldBe(oracle, "02:30 crosses both of this zone's transitions, and the two must agree on all "
                              + "400 occurrences even though only one of them has ever heard of the zone");

        // The gap and the repeated hour really are in the range, or the comparison would prove nothing.
        mapped.ShouldContain(Utc(2026, 3, 29, 1, 0), "the spring gap's exit");
        mapped.ShouldContain(Utc(2026, 10, 25, 0, 30), "the repeated hour's first pass");
        return;

        // The walk of RecurringTask.NextGridOccurrenceInZone, over the same two primitives it uses.
        DateTimeOffset? NextInCustomZone(DateTimeOffset current)
        {
            var wall = WallClock.ToWall(current, custom);

            for (var step = 0; step < 100; step++)
            {
                var nextWall = interval.GetNextOccurrence(wall).ShouldNotBeNull();
                if (nextWall < wall.AddSeconds(1))
                    return null;

                var mapping = WallClock.ToUtc(nextWall.DateTime, custom, current);
                if (!mapping.Consumed)
                    return mapping.Utc;

                wall = nextWall;
            }

            return null;
        }
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static RecurringTask BuildFluent(Action<IIntervalSchedulerBuilder> configure, string zoneId)
    {
        var builder = new RecurringTaskBuilder();
        configure(builder.Schedule());

        // Assigned rather than chained through InTimeZone: the shapes above end on different builder
        // interfaces, and what is under test is the definition, not the chain (which has its own tests).
        builder.RecurringTask.TimeZoneId = zoneId;
        builder.RecurringTask.Validate();

        return builder.RecurringTask;
    }

    private static RecurringTask BuildCron(string cronExpression, string zoneId) =>
        new() { CronInterval = new CronInterval(cronExpression), TimeZoneId = zoneId };

    private static List<DateTimeOffset> Walk(RecurringTask task, DateTimeOffset from, int count)
    {
        var occurrences = new List<DateTimeOffset>(count);
        var cursor      = from;

        for (var i = 0; i < count; i++)
        {
            if (task.CalculateNextRun(cursor, 1) is not { } next)
                break;

            occurrences.Add(next);
            cursor = next;
        }

        return occurrences;
    }
}
