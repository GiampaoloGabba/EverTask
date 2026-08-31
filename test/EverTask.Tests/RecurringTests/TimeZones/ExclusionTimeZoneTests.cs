using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.TimeZones;

public class ExclusionTimeZoneTests
{
    [Fact]
    public void Should_read_a_local_date_on_the_persisted_zone_for_an_elapsed_grid()
    {
        var task = Valid(new RecurringTask
        {
            HourInterval = new HourInterval(1),
            TimeZoneId = "Europe/Rome",
            Exclusions = new ScheduleExclusions { Dates = [new DateOnly(2026, 1, 2)] }
        });

        task.CalculateNextRun(Utc(2026, 1, 1, 22, 0), 1)
            .ShouldBe(Utc(2026, 1, 2, 23, 0),
                "23:00 UTC is already local midnight in Rome, so the whole local date is absent");
    }

    [Fact]
    public void Should_apply_a_date_exclusion_to_both_passes_of_a_repeated_hour()
    {
        var nonExcluded = Valid(new RecurringTask
        {
            MinuteInterval = new MinuteInterval(30),
            TimeZoneId = "Europe/Rome",
            Exclusions = new ScheduleExclusions { Dates = [new DateOnly(2026, 1, 1)] }
        });
        var excluded = Valid(new RecurringTask
        {
            MinuteInterval = new MinuteInterval(30),
            TimeZoneId = "Europe/Rome",
            Exclusions = new ScheduleExclusions { Dates = [new DateOnly(2026, 10, 25)] }
        });

        var firstPass = nonExcluded.CalculateNextRun(Utc(2026, 10, 25, 0, 0), 1).ShouldNotBeNull();
        var secondPass = nonExcluded.CalculateNextRun(firstPass, 1).ShouldNotBeNull();

        firstPass.ShouldBe(Utc(2026, 10, 25, 0, 30));
        secondPass.ShouldBe(Utc(2026, 10, 25, 1, 0));
        excluded.CalculateNextRun(Utc(2026, 10, 24, 21, 30), 1).ShouldBe(Utc(2026, 10, 25, 23, 0));
    }

    [Fact]
    public void Should_map_an_excluded_day_exit_through_a_midnight_gap()
    {
        var task = Valid(new RecurringTask
        {
            MinuteInterval = new MinuteInterval(30),
            TimeZoneId = "America/Havana",
            Exclusions = new ScheduleExclusions { Dates = [new DateOnly(2026, 3, 7)] }
        });

        var next = task.CalculateNextRun(Utc(2026, 3, 7, 4, 30), 1).ShouldNotBeNull();
        var local = TimeZoneInfo.ConvertTime(next, TimeZoneInfo.FindSystemTimeZoneById("America/Havana"));

        next.ShouldBe(Utc(2026, 3, 8, 5, 0));
        local.DateTime.ShouldBe(new DateTime(2026, 3, 8, 1, 0, 0));
    }

    [Fact]
    public void Should_discard_the_collapse_count_of_an_excluded_DST_candidate()
    {
        var task = Valid(new RecurringTask
        {
            DayInterval = new DayInterval(0, [DayOfWeek.Sunday])
            {
                OnTimes = [new TimeOnly(2, 15), new TimeOnly(2, 45)]
            },
            TimeZoneId = "Europe/Rome",
            Exclusions = new ScheduleExclusions { Dates = [new DateOnly(2026, 3, 29)] }
        });
        var start = Utc(2026, 3, 28, 23, 0);

        var result = task.CalculateNextValidRun(start, 1, referenceTime: start);

        result.NextRun.ShouldBe(Utc(2026, 4, 5, 0, 15));
        result.CollapsedSlotCount.ShouldBe(0);
    }

    private static RecurringTask Valid(RecurringTask task)
    {
        task.Validate();
        return task;
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
