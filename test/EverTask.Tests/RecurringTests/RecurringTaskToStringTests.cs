using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

public class RecurringTaskToStringTests
{
    [Fact]
    public void ToString_WithMinuteInterval_ShouldIncludeMinuteDetails()
    {
        var task = new RecurringTask { MinuteInterval = new MinuteInterval(15) { OnSecond = 30 } };
        var str = task.ToString();

        Assert.Contains("every 15 minute(s) at second 30", str);
    }

    [Fact]
    public void ToString_WithHourInterval_ShouldIncludeHourDetails()
    {
        var task = new RecurringTask { HourInterval = new HourInterval(2) { OnMinute = 15 } };
        var str = task.ToString();

        Assert.Contains("every 2 hour(s) at minute 15", str);
    }

    [Fact]
    public void ToString_WithMultipleHourInterval_ShouldIncludeHourDetails()
    {
        var task = new RecurringTask { HourInterval = new HourInterval(0, [1, 15, 16]) { OnMinute = 15 } };
        var str  = task.ToString();

        Assert.Contains("at hour(s) 1 - 15 - 16 at minute 15", str);
    }

    [Fact]
    public void ToString_WithDayIntervalAndSpecificDays_ShouldIncludeDayDetails()
    {
        var task = new RecurringTask { DayInterval = new DayInterval(1, [DayOfWeek.Monday, DayOfWeek.Friday]) };
        var str  = task.ToString();

        Assert.Contains("every 1 day(s) at 00:00 on Monday - Friday", str);
    }

    [Fact]
    public void ToString_WithMonthIntervalAndSpecificMonths_ShouldIncludeMonthDetails()
    {
        var task = new RecurringTask { MonthInterval = new MonthInterval(3, [1, 6, 12]) };
        var str  = task.ToString();

        Assert.Contains("every 3 month(s) at 00:00 in 1 - 6 - 12", str);
    }

    [Fact]
    public void ToString_WithRunNowAndInterval_ShouldIncludeRunNowAndInterval()
    {
        var task = new RecurringTask { RunNow = true, SecondInterval = new SecondInterval(10) };
        var str = task.ToString();

        Assert.Contains("Run immediately then every 10 second(s)", str);
    }

    [Fact]
    public void ToString_WithInitialDelayAndInterval_ShouldIncludeDelayAndInterval()
    {
        var delay = TimeSpan.FromMinutes(5);
        var task = new RecurringTask { InitialDelay = delay, MinuteInterval = new MinuteInterval(30) };
        var str = task.ToString();

        Assert.Contains($"Start after a delay of {delay} then every 30 minute(s)", str);
    }

    [Fact]
    public void A_cron_description_includes_its_bounds()
    {
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 9 * * *"),
            RunUntil     = new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero),
            MaxRuns      = 50
        };

        task.ToString().ShouldBe(
            "Use Cron expression: 0 9 * * * until 2026-12-31 23:00:00 UTC up to 50 times");
    }

    // The description is persisted as QueuedTask.RecurringInfo and served by the monitoring API on both the
    // list and the detail, so it is what an operator reads to answer "when does this series stop?". Rendering
    // an absolute bound with ToLocalTime() answered on the HOST's clock — a different sentence per machine for
    // one definition, and a wall time the zone appended right after it did not own.

    [Fact]
    public void A_bound_is_rendered_on_the_zone_the_description_names()
    {
        var task = new RecurringTask
        {
            DayInterval = new DayInterval { Interval = 1, OnTimes = [new TimeOnly(9, 0)] },
            TimeZoneId  = "Europe/Rome",
            MaxRuns     = 50,
            RunUntil    = new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero)
        };

        // Rome is UTC+1 on the 31st of December, so the bound is midnight of the 1st there — and that is the
        // clock the "(Europe/Rome)" at the end of the sentence claims.
        task.ToString().ShouldBe(
            "every 1 day(s) at 09:00 until 2027-01-01 00:00:00 up to 50 times (Europe/Rome)");
    }

    [Fact]
    public void A_bound_of_a_schedule_with_no_zone_is_rendered_in_UTC_and_says_so()
    {
        var task = new RecurringTask
        {
            DayInterval = new DayInterval { Interval = 1, OnTimes = [new TimeOnly(9, 0)] },
            RunUntil    = new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero)
        };

        task.ToString().ShouldBe("every 1 day(s) at 09:00 until 2026-12-31 23:00:00 UTC",
            "with no zone to name there is nothing to read the number on, so the clock is spelled out");
    }

    [Fact]
    public void A_first_run_instant_is_rendered_on_the_same_clock_as_the_bound()
    {
        var task = new RecurringTask
        {
            SpecificRunTime = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
            DayInterval     = new DayInterval { Interval = 1, OnTimes = [new TimeOnly(9, 0)] },
            TimeZoneId      = "Europe/Rome"
        };

        // July, so Rome is UTC+2.
        task.ToString().ShouldBe("Run at 2026-07-01 12:00:00 then every 1 day(s) at 09:00 (Europe/Rome)");
    }

    [Fact]
    public void Exclusions_are_rendered_as_a_human_readable_clause_on_the_schedule_clock()
    {
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(4),
            TimeZoneId = "Europe/Rome",
            Exclusions = new ScheduleExclusions
            {
                Days = [DayOfWeek.Sunday, DayOfWeek.Saturday],
                Dates = [new DateOnly(2026, 12, 25)],
                Ranges =
                [
                    new ExclusionRange
                    {
                        FromUtc = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
                        ToUtc = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero)
                    }
                ]
            }
        };
        task.Validate();

        task.ToString().ShouldBe(
            "every 4 hour(s) except Saturday - Sunday, 2026-12-25, [2026-07-01 10:00:00, " +
            "2026-07-01 11:00:00) (Europe/Rome)");
    }

    [Fact]
    public void Large_date_and_window_sets_are_summarized()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(1),
            Exclusions = new ScheduleExclusions
            {
                Dates = Enumerable.Range(0, 4).Select(day => new DateOnly(2026, 1, 1).AddDays(day)).ToArray(),
                Ranges = Enumerable.Range(0, 4).Select(day => new ExclusionRange
                {
                    FromUtc = start.AddDays(day).AddHours(1),
                    ToUtc = start.AddDays(day).AddHours(2)
                }).ToArray()
            }
        };
        task.Validate();

        task.ToString().ShouldBe("every 1 day(s) at 00:00 except 4 dates, 4 windows");
    }
}

