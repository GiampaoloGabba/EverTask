using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;

namespace EverTask.Tests.RecurringTests.TimeZones;

/// <summary>
/// T1/T2/T4/T10: what a schedule persists for its zone, and what happens to an id it cannot resolve. The
/// stored form has to be the one that resolves on the next run — on a different host, and after a redeploy
/// onto a different operating system.
/// </summary>
public class TimeZoneIdNormalizationTests
{
    private static RecurringTask Build(Action<IIntervalSchedulerBuilder> configure)
    {
        var builder = new RecurringTaskBuilder();
        configure(builder.Schedule());
        return builder.RecurringTask;
    }

    [Fact]
    public void An_IANA_id_is_stored_as_it_was_given()
    {
        Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome"))
            .TimeZoneId.ShouldBe("Europe/Rome");
    }

    [Fact]
    public void A_Windows_id_is_stored_as_its_IANA_equivalent()
    {
        // The row has to be readable on a Linux replica of the same deployment, where a Windows id resolves
        // to nothing.
        Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("W. Europe Standard Time"))
            .TimeZoneId.ShouldBe("Europe/Berlin", "the CLDR mapping of that Windows zone");
    }

    [Fact]
    public void A_TimeZoneInfo_is_stored_as_its_IANA_id()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(zone))
            .TimeZoneId.ShouldBe("America/New_York");
    }

    [Fact]
    public void UTC_is_stored_as_UTC_and_only_when_it_was_asked_for()
    {
        // T4: "UTC" in the row means someone chose it; the absence of a zone is what legacy UTC looks like.
        Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(TimeZoneInfo.Utc))
            .TimeZoneId.ShouldBe("UTC");

        Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)))
            .TimeZoneId.ShouldBeNull();
    }

    [Fact]
    public void An_id_this_system_cannot_resolve_is_refused_before_it_reaches_a_row()
    {
        var exception = Should.Throw<ArgumentException>(
            () => Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Mars/Olympus_Mons")));

        exception.Message.ShouldContain("Mars/Olympus_Mons");
    }

    [Fact]
    public void An_empty_id_is_refused()
    {
        Should.Throw<ArgumentException>(
            () => Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("  ")));
    }

    [Fact]
    public void A_custom_zone_is_refused_because_no_id_could_bring_its_rules_back()
    {
        var custom = WallClockTests.CreateEuRuledZone("Test/Refused", TimeSpan.FromHours(1));

        var exception = Should.Throw<ArgumentException>(
            () => Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(custom)));

        exception.Message.ShouldContain("IANA");
    }

    [Fact]
    public void A_zone_on_a_plain_cadence_is_refused_when_the_schedule_is_validated()
    {
        // T5: the call itself is accepted — the chain has no final shape yet — and the built definition is
        // what gets judged. Validate() runs on every path that accepts a schedule.
        var task = Build(s => s.Every(30).Minutes().InTimeZone("Europe/Rome"));

        task.TimeZoneId.ShouldBe("Europe/Rome");
        Should.Throw<InvalidOperationException>(() => task.Validate())
              .Message.ShouldContain("no effect");
    }

    [Fact]
    public void The_zone_can_be_named_before_the_interval()
    {
        // Schedule().InTimeZone(z).EveryDay()... — the same definition, built the other way round, and by
        // then it IS calendar-anchored, so validation accepts it.
        var builder = new RecurringTaskBuilder();
        builder.Schedule().InTimeZone("Europe/Rome").EveryDay().AtTime(new TimeOnly(9, 0));

        builder.RecurringTask.TimeZoneId.ShouldBe("Europe/Rome");
        builder.RecurringTask.Validate();
    }

    [Fact]
    public void The_zone_can_be_named_before_a_time_of_day()
    {
        // The position the docs advertise for a daily schedule. It has to COMPILE — the zone call returns the
        // daily builder, so AtTime is still reachable — and it has to build the schedule the tail position
        // builds, down to the instants.
        var midChain = Build(s => s.EveryDay().InTimeZone("Europe/Rome").AtTime(new TimeOnly(9, 0)));
        var tail     = Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome"));

        ShouldMatch(midChain, tail);
    }

    [Fact]
    public void The_zone_can_be_named_before_a_day_of_the_week()
    {
        var midChain = Build(s => s.EveryWeek().InTimeZone("Europe/Rome")
                                   .OnDay(DayOfWeek.Monday).AtTime(new TimeOnly(9, 0)));
        var tail     = Build(s => s.EveryWeek().OnDay(DayOfWeek.Monday)
                                   .AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome"));

        ShouldMatch(midChain, tail);
    }

    [Fact]
    public void The_zone_can_be_named_before_a_day_of_the_month()
    {
        var midChain = Build(s => s.EveryMonth().InTimeZone("Europe/Rome")
                                   .OnDay(15).AtTime(new TimeOnly(9, 0)));
        var tail     = Build(s => s.EveryMonth().OnDay(15)
                                   .AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome"));

        ShouldMatch(midChain, tail);
    }

    [Fact]
    public void The_zone_can_sit_between_a_month_selector_and_its_time_of_day()
    {
        // The monthly selector hands back the daily builder, so the same mid-chain position exists one step
        // further in.
        var midChain = Build(s => s.EveryMonth().OnDay(15)
                                   .InTimeZone("Europe/Rome").AtTime(new TimeOnly(9, 0)));
        var tail     = Build(s => s.EveryMonth().OnDay(15)
                                   .AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome"));

        ShouldMatch(midChain, tail);
    }

    [Fact]
    public void The_zone_still_arrives_when_the_builder_is_held_as_a_buildable_one()
    {
        // The refining overloads hide the inherited one, which now reaches the schedule through an explicit
        // implementation: a caller holding the base interface must still set the zone, not meet the default
        // body's NotSupportedException.
        var task    = new RecurringTask();
        var builder = new DailyTimeSchedulerBuilder(task);

        ((IBuildableSchedulerBuilder)builder).InTimeZone("Europe/Rome");
        task.TimeZoneId.ShouldBe("Europe/Rome");

        var weekly = new RecurringTask();
        ((IBuildableSchedulerBuilder)new WeeklySchedulerBuilder(weekly))
            .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"));
        weekly.TimeZoneId.ShouldBe("Europe/Rome");

        var monthly = new RecurringTask();
        ((IBuildableSchedulerBuilder)new MonthlySchedulerBuilder(monthly)).InTimeZone("Europe/Rome");
        monthly.TimeZoneId.ShouldBe("Europe/Rome");
    }

    /// <summary>
    /// Two spellings of the same schedule: same stored zone, same human-readable form, same grid.
    /// </summary>
    private static void ShouldMatch(RecurringTask midChain, RecurringTask tail)
    {
        midChain.TimeZoneId.ShouldBe(tail.TimeZoneId);
        midChain.ToString().ShouldBe(tail.ToString());
        midChain.Validate();

        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
        {
            var next = midChain.CalculateNextRun(from, 1).ShouldNotBeNull();
            tail.CalculateNextRun(from, 1).ShouldBe(next);
            from = next;
        }
    }

    [Fact]
    public void A_definition_built_by_hand_gets_its_id_canonicalized_when_it_is_validated()
    {
        // T2 does not hold only for the fluent builder: `ExecuteDispatch` is public and takes a RecurringTask
        // directly, and that definition is serialized into the row as it stands. Validation is the one gate
        // every path shares, so it is where the Windows spelling becomes the IANA one that a Linux replica of
        // the same deployment can resolve.
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(1) { OnTimes = [new TimeOnly(9, 0)] },
            TimeZoneId  = "W. Europe Standard Time"
        };

        task.Validate();

        task.TimeZoneId.ShouldBe("Europe/Berlin");
        EverTaskJson.Serialize(task).ShouldContain("\"TimeZoneId\":\"Europe/Berlin\"");
    }

    [Fact]
    public void An_id_that_is_already_canonical_is_left_exactly_as_it_was()
    {
        // The other half: validation runs on every recurring dispatch and on every recovered row, so it must
        // not rewrite what a row already says — a rewritten id would be a schedule changing under its owner.
        foreach (var id in new[] { "Europe/Rome", "America/New_York", "UTC" })
        {
            var task = new RecurringTask
            {
                DayInterval = new DayInterval(1) { OnTimes = [new TimeOnly(9, 0)] },
                TimeZoneId  = id
            };

            task.Validate();
            task.TimeZoneId.ShouldBe(id);
        }
    }

    [Fact]
    public void A_stored_id_that_no_longer_resolves_is_corrupt_schedule_metadata()
    {
        // T10: the same verdict an unparseable cron gets, so recovery routes the row to the terminal poison
        // path instead of scheduling it on a zone that is not there.
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(1) { OnTimes = [new TimeOnly(9, 0)] },
            TimeZoneId  = "Mars/Olympus_Mons"
        };

        Should.Throw<ArgumentException>(() => task.Validate());
    }

    [Fact]
    public void A_schedule_without_a_zone_serializes_exactly_as_it_did_before_zones_existed()
    {
        // P1: the serializer writes nulls by design, so the property is what keeps the bytes identical.
        var legacy =
            """{"RunNow":false,"InitialDelay":null,"SpecificRunTime":null,"CronInterval":null,"SecondInterval":{"Interval":30},"MinuteInterval":null,"HourInterval":null,"DayInterval":null,"WeekInterval":null,"MonthInterval":null,"MaxRuns":null,"RunUntil":null}""";

        var restored = EverTaskJson.Deserialize<RecurringTask>(legacy).ShouldNotBeNull();

        restored.TimeZoneId.ShouldBeNull();
        restored.Semantics.ShouldBe(ScheduleSemantics.Elapsed);
        EverTaskJson.Serialize(restored).ShouldBe(legacy);
    }

    [Fact]
    public void A_zoned_schedule_round_trips_its_zone()
    {
        var task = Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome"));

        var json = EverTaskJson.Serialize(task);
        json.ShouldContain("\"TimeZoneId\":\"Europe/Rome\"");

        var restored = EverTaskJson.Deserialize<RecurringTask>(json).ShouldNotBeNull();

        restored.TimeZoneId.ShouldBe("Europe/Rome");
        restored.CalculateNextRun(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), 1)
                .ShouldBe(new DateTimeOffset(2026, 7, 2, 7, 0, 0, TimeSpan.Zero),
                    "a schedule read back from a row must compute the same grid it did before it was stored");
    }

    [Fact]
    public void Semantics_and_the_resolved_zone_are_not_serialized()
    {
        // Both are derived: persisting them would freeze a classification, and a TimeZoneInfo cannot survive
        // the round-trip at all.
        var json = EverTaskJson.Serialize(
            Build(s => s.EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome")));

        json.ShouldNotContain("Semantics");
        json.ShouldNotContain("Zone\":");
    }
}
