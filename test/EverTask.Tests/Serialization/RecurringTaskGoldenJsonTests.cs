using EverTask.Abstractions;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;

namespace EverTask.Tests.Serialization;

/// <summary>
/// P1, byte-identical default: a schedule persisted by an earlier version must round-trip through the current
/// serializer producing the EXACT same bytes. Rows already on disk are only ever read and rewritten in place,
/// so a single extra property in the output would rewrite every stored schedule and break byte comparison with
/// an un-migrated peer.
/// </summary>
/// <remarks>
/// The fixtures below are the literal output of the version before durable occurrences existed. They are the
/// reason <see cref="RecurringTask.OccurrenceMode"/> carries <c>JsonIgnoreCondition.WhenWritingDefault</c>:
/// the EverTask serializer deliberately writes nulls and defaults (byte parity with the historical Newtonsoft
/// output), so without the attribute every one of these strings would gain an <c>"OccurrenceMode":0</c>.
/// </remarks>
public class RecurringTaskGoldenJsonTests
{
    public static TheoryData<string, string> Goldens => new()
    {
        {
            "every N seconds",
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":{"Interval":30},"MinuteInterval":null,"HourInterval":null,"DayInterval":null,"WeekInterval":null,"MonthInterval":null,"MaxRuns":null,"RunUntil":null}"""
        },
        {
            "every N minutes at second",
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":null,"MinuteInterval":{"Interval":5,"OnSecond":15},"HourInterval":null,"DayInterval":null,"WeekInterval":null,"MonthInterval":null,"MaxRuns":null,"RunUntil":null}"""
        },
        {
            "every N hours on selected hours",
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":null,"MinuteInterval":null,"HourInterval":{"Interval":2,"OnMinute":30,"OnSecond":5,"OnHours":[8,20]},"DayInterval":null,"WeekInterval":null,"MonthInterval":null,"MaxRuns":null,"RunUntil":null}"""
        },
        {
            "daily on weekdays at time",
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":null,"MinuteInterval":null,"HourInterval":null,"DayInterval":{"Interval":1,"OnTimes":["09:30:00"],"OnDays":[1,5]},"WeekInterval":null,"MonthInterval":null,"MaxRuns":null,"RunUntil":null}"""
        },
        {
            "weekly on a weekday at time",
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":null,"MinuteInterval":null,"HourInterval":null,"DayInterval":null,"WeekInterval":{"Interval":2,"OnTimes":["06:00:00"],"OnDays":[2]},"MonthInterval":null,"MaxRuns":null,"RunUntil":null}"""
        },
        {
            "monthly with day, first weekday and months",
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":null,"MinuteInterval":null,"HourInterval":null,"DayInterval":null,"WeekInterval":null,"MonthInterval":{"Interval":3,"OnDay":15,"OnDays":[],"OnFirst":1,"OnTimes":["23:59:59"],"OnMonths":[1,7]},"MaxRuns":null,"RunUntil":null}"""
        },
        {
            "cron",
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":{"CronExpression":"*/5 * * * *"},"SecondInterval":null,"MinuteInterval":null,"HourInterval":null,"DayInterval":null,"WeekInterval":null,"MonthInterval":null,"MaxRuns":null,"RunUntil":null}"""
        },
        {
            "composite with first-run config and bounds",
            """{"RunNow":true,"InitialDelay":"00:03:00","SpecificRunTime":"2026-03-01T10:00:00+00:00","CronInterval":null,"SecondInterval":null,"MinuteInterval":null,"HourInterval":null,"DayInterval":{"Interval":2,"OnTimes":["01:02:03","04:05:06"],"OnDays":[]},"WeekInterval":null,"MonthInterval":null,"MaxRuns":10,"RunUntil":"2027-01-01T00:00:00+00:00"}"""
        },
        // Genuinely composite: SEVERAL interval fields non-null at once. The single-interval goldens above
        // pin each fragment on its own, and a schedule that carries more than one is where a serializer
        // change (a reordered property, a null skipped, a collection written differently) shows up as a
        // different byte count rather than a different value — so the assembled form is pinned too.
        {
            "composite with every interval field set at once",
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":{"Interval":30},"MinuteInterval":{"Interval":5,"OnSecond":15},"HourInterval":{"Interval":2,"OnMinute":30,"OnSecond":5,"OnHours":[8,20]},"DayInterval":{"Interval":1,"OnTimes":["09:30:00"],"OnDays":[1,5]},"WeekInterval":{"Interval":2,"OnTimes":["06:00:00"],"OnDays":[2]},"MonthInterval":{"Interval":3,"OnDay":15,"OnDays":[],"OnFirst":1,"OnTimes":["23:59:59"],"OnMonths":[1,7]},"MaxRuns":null,"RunUntil":null}"""
        },
        {
            "composite cadences with first-run config and bounds",
            """{"RunNow":true,"InitialDelay":"00:03:00","SpecificRunTime":"2026-03-01T10:00:00+00:00","CronInterval":null,"SecondInterval":{"Interval":30},"MinuteInterval":{"Interval":5,"OnSecond":15},"HourInterval":{"Interval":2,"OnMinute":30,"OnSecond":5,"OnHours":[]},"DayInterval":null,"WeekInterval":null,"MonthInterval":null,"MaxRuns":10,"RunUntil":"2027-01-01T00:00:00+00:00"}"""
        }
    };

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Legacy_schedule_json_round_trips_byte_for_byte(string shape, string goldenJson)
    {
        var restored = EverTaskJson.Deserialize<RecurringTask>(goldenJson).ShouldNotBeNull();

        EverTaskJson.Serialize(restored).ShouldBe(goldenJson,
            $"the persisted form of a '{shape}' schedule must not change by a single byte");
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Legacy_schedule_json_reads_back_as_an_inline_schedule(string shape, string goldenJson)
    {
        var restored = EverTaskJson.Deserialize<RecurringTask>(goldenJson).ShouldNotBeNull();

        restored.OccurrenceMode.ShouldBe(OccurrenceMode.Inline,
            $"a '{shape}' schedule written before durable occurrences existed keeps the legacy behaviour");
        restored.Exclusions.ShouldBeNull();
    }

    [Fact]
    public void A_durable_schedule_writes_its_occurrence_mode()
    {
        // The mirror of the goldens: the property is omitted only while it holds the default, so a schedule
        // that really is durable still persists that fact.
        var durable = new RecurringTask
        {
            SecondInterval = new SecondInterval(30),
            OccurrenceMode = OccurrenceMode.Durable
        };

        var json = EverTaskJson.Serialize(durable);

        json.ShouldContain("\"OccurrenceMode\":1");
        EverTaskJson.Deserialize<RecurringTask>(json)!.OccurrenceMode.ShouldBe(OccurrenceMode.Durable);
    }

    [Fact]
    public void Schedule_exclusions_have_one_canonical_golden_json_and_round_trip()
    {
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(30),
            Exclusions = new ScheduleExclusions
            {
                Days = [DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Saturday],
                Dates = [new DateOnly(2026, 12, 25), new DateOnly(2026, 1, 1)],
                Ranges =
                [
                    new ExclusionRange
                    {
                        FromUtc = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(2)),
                        ToUtc = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.FromHours(2))
                    },
                    new ExclusionRange
                    {
                        FromUtc = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
                        ToUtc = new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)
                    }
                ]
            }
        };
        task.Validate();

        var json = EverTaskJson.Serialize(task);

        // Tranche 1 never shipped separately: both exclusion shapes debut in 4.0.0, so its golden may adopt
        // the canonical Calendars member in place without rewriting any released persisted row.
        json.ShouldBe(
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":{"Interval":30},"MinuteInterval":null,"HourInterval":null,"DayInterval":null,"WeekInterval":null,"MonthInterval":null,"MaxRuns":null,"RunUntil":null,"Exclusions":{"Days":[0,6],"Dates":["2026-01-01","2026-12-25"],"Ranges":[{"FromUtc":"2026-06-01T08:00:00+00:00","ToUtc":"2026-06-01T11:00:00+00:00"}],"Calendars":[]}}""");

        var restored = EverTaskJson.Deserialize<RecurringTask>(json).ShouldNotBeNull();
        restored.Validate();
        EverTaskJson.Serialize(restored).ShouldBe(json);
    }

    [Fact]
    public void A_tranche_one_exclusion_shape_reads_and_rewrites_with_the_new_canonical_member()
    {
        const string trancheOneJson =
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":{"Interval":30},"MinuteInterval":null,"HourInterval":null,"DayInterval":null,"WeekInterval":null,"MonthInterval":null,"MaxRuns":null,"RunUntil":null,"Exclusions":{"Days":[6],"Dates":["2026-12-25"],"Ranges":[]}}""";
        const string canonicalJson =
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":{"Interval":30},"MinuteInterval":null,"HourInterval":null,"DayInterval":null,"WeekInterval":null,"MonthInterval":null,"MaxRuns":null,"RunUntil":null,"Exclusions":{"Days":[6],"Dates":["2026-12-25"],"Ranges":[],"Calendars":[]}}""";

        var restored = EverTaskJson.Deserialize<RecurringTask>(trancheOneJson).ShouldNotBeNull();
        restored.Validate();

        restored.Exclusions.ShouldNotBeNull().Calendars.ShouldBeEmpty();
        EverTaskJson.Serialize(restored).ShouldBe(canonicalJson);
    }

    [Fact]
    public void Calendar_only_exclusions_round_trip_without_normalizing_to_null()
    {
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(30),
            Exclusions = new ScheduleExclusions { Calendars = [" holidays "] }
        };
        task.Validate();

        var json = EverTaskJson.Serialize(task);
        var restored = EverTaskJson.Deserialize<RecurringTask>(json).ShouldNotBeNull();
        restored.Validate();

        restored.Exclusions.ShouldNotBeNull().Calendars.ShouldBe(["holidays"]);
        EverTaskJson.Serialize(restored).ShouldBe(json);
    }

    [Fact]
    public void Deserialized_null_arrays_are_lenient_but_a_null_range_is_poison()
    {
        var lenient = EverTaskJson.Deserialize<RecurringTask>(
            """{"SecondInterval":{"Interval":30},"Exclusions":{"Days":null,"Dates":null,"Ranges":null,"Calendars":null}}""")
            .ShouldNotBeNull();
        var corrupt = EverTaskJson.Deserialize<RecurringTask>(
            """{"SecondInterval":{"Interval":30},"Exclusions":{"Ranges":[null]}}""")
            .ShouldNotBeNull();

        Should.NotThrow(() => lenient.Validate());
        lenient.Exclusions.ShouldBeNull();
        Should.Throw<ArgumentException>(() => corrupt.Validate());
    }
}
