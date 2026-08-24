using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.TimeZones;

/// <summary>
/// T5: which schedules a time zone governs. The classification decides three things at once — whether
/// <c>InTimeZone</c> is accepted, whether the global default zone is stamped on, and whether the grid is
/// still a uniform arithmetic progression — so every shape the fluent API can build is pinned here, built
/// through the real builder rather than by hand-setting intervals.
/// </summary>
public class ScheduleSemanticsTests
{
    private static RecurringTask Build(Action<IRecurringTaskBuilder> configure)
    {
        var builder = new RecurringTaskBuilder();
        configure(builder);
        return builder.RecurringTask;
    }

    public static TheoryData<string, Action<IRecurringTaskBuilder>> ElapsedShapes => new()
    {
        { "EverySecond", r => r.Schedule().EverySecond() },
        { "Every(30).Seconds", r => r.Schedule().Every(30).Seconds() },
        { "EveryMinute", r => r.Schedule().EveryMinute() },
        { "Every(5).Minutes", r => r.Schedule().Every(5).Minutes() },
        { "Every(5).Minutes().AtSecond(30)", r => r.Schedule().Every(5).Minutes().AtSecond(30) },
        { "EveryHour", r => r.Schedule().EveryHour() },
        { "Every(6).Hours", r => r.Schedule().Every(6).Hours() },
        { "EveryHour().AtMinute(30)", r => r.Schedule().EveryHour().AtMinute(30) },
        { "EveryHour().AtMinute(30).AtSecond(15)", r => r.Schedule().EveryHour().AtMinute(30).AtSecond(15) },
        // No grid at all: a one-shot expressed through the recurring builder has nothing for a zone to move.
        { "RunDelayed only", r => r.RunDelayed(TimeSpan.FromMinutes(10)) },
        { "RunNow only", r => r.RunNow() },
        { "RunAt only", r => r.RunAt(DateTimeOffset.UtcNow.AddHours(1)) }
    };

    public static TheoryData<string, Action<IRecurringTaskBuilder>> CalendarShapes => new()
    {
        { "UseCron", r => r.Schedule().UseCron("0 9 * * *") },
        { "EveryDay", r => r.Schedule().EveryDay() },
        { "EveryDay().AtTime", r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)) },
        { "EveryDay().AtTimes", r => r.Schedule().EveryDay().AtTimes(new TimeOnly(9, 0), new TimeOnly(18, 0)) },
        { "Every(2).Days", r => r.Schedule().Every(2).Days() },
        { "OnDays(Mon,Fri)", r => r.Schedule().OnDays(DayOfWeek.Monday, DayOfWeek.Friday) },
        { "EveryWeek", r => r.Schedule().EveryWeek() },
        { "EveryWeek().OnDay", r => r.Schedule().EveryWeek().OnDay(DayOfWeek.Tuesday) },
        { "Every(2).Weeks().OnDays", r => r.Schedule().Every(2).Weeks().OnDays(DayOfWeek.Monday) },
        { "EveryMonth", r => r.Schedule().EveryMonth() },
        { "EveryMonth().OnDay(15)", r => r.Schedule().EveryMonth().OnDay(15) },
        { "EveryMonth().OnDays(1,15)", r => r.Schedule().EveryMonth().OnDays(1, 15) },
        { "EveryMonth().OnFirst", r => r.Schedule().EveryMonth().OnFirst(DayOfWeek.Monday) },
        { "OnMonths(1,7)", r => r.Schedule().OnMonths(1, 7) },
        { "OnMonths(1,7).OnDay(3)", r => r.Schedule().OnMonths(1, 7).OnDay(3) },
        { "Every(3).Months", r => r.Schedule().Every(3).Months() },
        { "EveryWeek().OnDay().AtTime", r => r.Schedule().EveryWeek().OnDay(DayOfWeek.Tuesday)
                                              .AtTime(new TimeOnly(6, 30)) },
        { "EveryMonth().OnDay(15).AtTime", r => r.Schedule().EveryMonth().OnDay(15)
                                                 .AtTime(new TimeOnly(6, 30)) }
    };

    [Theory]
    [MemberData(nameof(ElapsedShapes))]
    public void A_plain_cadence_is_elapsed(string shape, Action<IRecurringTaskBuilder> configure) =>
        Build(configure).Semantics.ShouldBe(ScheduleSemantics.Elapsed,
            $"'{shape}' is a constant step in elapsed time: the same instants in every zone");

    [Theory]
    [MemberData(nameof(CalendarShapes))]
    public void Anything_anchored_to_a_calendar_is_calendar(string shape, Action<IRecurringTaskBuilder> configure) =>
        Build(configure).Semantics.ShouldBe(ScheduleSemantics.Calendar,
            $"'{shape}' snaps its result to a clock or a calendar, which only means something in some zone");

    [Theory]
    [MemberData(nameof(CalendarShapes))]
    public void A_calendar_schedule_accepts_a_zone(string shape, Action<IRecurringTaskBuilder> configure)
    {
        // The other half of the classification, and the half the day/week/month CADENCES turn on (decisions
        // §3.4): being Calendar is what makes `Every(2).Days()` keep a zone instead of throwing on it.
        var task = Build(configure);
        task.TimeZoneId = "Europe/Rome";

        task.Validate();
        task.GoverningZone.ShouldNotBeNull($"'{shape}' is read on the zone's clock, so the zone governs it");
    }

    [Theory]
    [MemberData(nameof(ElapsedShapes))]
    public void An_elapsed_schedule_refuses_a_zone(string shape, Action<IRecurringTaskBuilder> configure)
    {
        var task = Build(configure);
        task.TimeZoneId = "Europe/Rome";

        Should.Throw<InvalidOperationException>(() => task.Validate())
              .Message.ShouldContain("no effect");
        task.GoverningZone.ShouldBeNull($"'{shape}' produces the same instants in every zone");
    }

    [Fact]
    public void An_elapsed_alignment_is_a_UTC_minute_even_where_the_offset_is_fractional()
    {
        // T5's documented consequence: AtMinute refines an ELAPSED cadence, so :30 means :30 UTC. A zone
        // offset by whole hours reads that back as :30 too, but +05:30 reads it as :00 and +05:45 as :15 —
        // the exact numbers docs/recurring-tasks/time-zones.md quotes when it tells a reader to anchor to a
        // calendar instead of aligning a cadence.
        var task = Build(r => r.Schedule().EveryHour().AtMinute(30));
        task.Semantics.ShouldBe(ScheduleSemantics.Elapsed);

        var next = task.CalculateNextRun(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), 1)
                       .ShouldNotBeNull();

        next.ShouldBe(new DateTimeOffset(2026, 7, 1, 13, 30, 0, TimeSpan.Zero));
        LocalMinuteOf(next, "Europe/Rome").ShouldBe(30);      // +02:00 in July
        LocalMinuteOf(next, "Asia/Kolkata").ShouldBe(0);      // +05:30
        LocalMinuteOf(next, "Asia/Kathmandu").ShouldBe(15);   // +05:45
    }

    private static int LocalMinuteOf(DateTimeOffset utc, string zoneId) =>
        TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(zoneId)).Minute;

    [Fact]
    public void A_selected_hour_makes_an_hourly_schedule_calendar()
    {
        // A populated HourInterval.OnHours is a calendar selector: "at 08:00 and 20:00" is a wall-clock
        // statement, unlike "every 12 hours". It is only reachable from persisted metadata — see the test
        // below for what the similarly named builder method actually builds.
        var task = new RecurringTask { HourInterval = new HourInterval(1, [8, 20]) };

        task.Semantics.ShouldBe(ScheduleSemantics.Calendar);
    }

    [Fact]
    public void The_builders_OnHours_selects_no_hours_and_so_stays_elapsed()
    {
        // The trap this pins: OnHours() sits next to OnDays(params DayOfWeek[]) and OnMonths(params int[]) on
        // IntervalSchedulerBuilder and reads like their hourly sibling. It is not one. It takes no argument,
        // no builder path anywhere fills HourInterval.OnHours, and what it builds is EveryHour()'s cadence —
        // which a zone cannot govern. It is also not on IIntervalSchedulerBuilder, so it is unreachable from
        // Schedule() and only a consumer holding the concrete builder can call it at all. The skill and the
        // configuration reference both listed it among the calendar selectors.
        var task    = new RecurringTask();
        var builder = new IntervalSchedulerBuilder(task);

        builder.OnHours();

        task.HourInterval.ShouldNotBeNull().OnHours.ShouldBeEmpty();
        task.HourInterval.Interval.ShouldBe(
            Build(r => r.Schedule().EveryHour()).HourInterval.ShouldNotBeNull().Interval);
        task.Semantics.ShouldBe(ScheduleSemantics.Elapsed);

        task.TimeZoneId = "Europe/Rome";
        Should.Throw<InvalidOperationException>(() => task.Validate()).Message.ShouldContain("no effect");
    }

    [Fact]
    public void The_fluent_chain_does_not_offer_OnHours_at_all()
    {
        // The half a compile cannot catch here, because it is the ABSENCE of a member: Schedule() hands back
        // IIntervalSchedulerBuilder, which never declared OnHours. Documentation that lists it next to
        // OnDays/OnMonths is describing a call nobody can write.
        typeof(IIntervalSchedulerBuilder).GetMethod("OnHours").ShouldBeNull();
        typeof(IntervalSchedulerBuilder).GetMethod("OnHours").ShouldNotBeNull();
    }

    [Fact]
    public void A_zoned_calendar_schedule_is_not_a_uniform_grid()
    {
        // T8: local midnight is 24 hours after the previous one on every day but the two the offset moves on,
        // so the O(1) arithmetic jump would land off-grid. The same schedule without a zone keeps it.
        var zoned = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome"));
        var utc   = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)));

        zoned.IsUniformGrid().ShouldBeFalse();
        utc.IsUniformGrid().ShouldBeTrue("a schedule with no zone must reach the same code it always did");
    }

    [Fact]
    public void A_schedule_pinned_to_UTC_keeps_the_uniform_grid()
    {
        // "UTC" is a zone a user can choose explicitly (T4), and choosing it changes nothing about the math:
        // the wall clock and the instants are the same, so the legacy arithmetic still applies.
        var task = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(TimeZoneInfo.Utc));

        task.TimeZoneId.ShouldBe("UTC");
        task.IsUniformGrid().ShouldBeTrue();
    }
}
