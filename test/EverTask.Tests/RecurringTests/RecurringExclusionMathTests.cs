using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

public class RecurringExclusionMathTests
{
    [Fact]
    public void Should_preserve_cadence_phase_across_a_range_and_include_its_exit()
    {
        var anchor = Utc(2026, 1, 1, 0);
        var task = Valid(new RecurringTask
        {
            HourInterval = new HourInterval(4),
            Exclusions = new ScheduleExclusions
            {
                Ranges = [Range(Utc(2026, 1, 1, 4), Utc(2026, 1, 1, 5))]
            }
        });

        task.CalculateNextRun(anchor, 1).ShouldBe(Utc(2026, 1, 1, 8));

        var exitOnGrid = Valid(new RecurringTask
        {
            HourInterval = new HourInterval(4),
            Exclusions = new ScheduleExclusions
            {
                Ranges = [Range(Utc(2026, 1, 1, 4), Utc(2026, 1, 1, 8))]
            }
        });

        exitOnGrid.CalculateNextRun(anchor, 1).ShouldBe(Utc(2026, 1, 1, 8));
    }

    [Fact]
    public void Should_filter_days_dates_and_consecutive_excluded_regions()
    {
        var task = Valid(new RecurringTask
        {
            DayInterval = new DayInterval(1) { OnTimes = [new TimeOnly(8, 0)] },
            Exclusions = new ScheduleExclusions
            {
                Days = [DayOfWeek.Saturday, DayOfWeek.Sunday],
                Dates = [new DateOnly(2026, 1, 5)]
            }
        });

        task.CalculateNextRun(Utc(2026, 1, 2, 8), 1).ShouldBe(Utc(2026, 1, 6, 8));
    }

    [Fact]
    public void Should_filter_the_first_grid_slot_and_stop_before_an_excluded_RunUntil_tail()
    {
        var firstExcluded = Valid(new RecurringTask
        {
            DayInterval = new DayInterval(1),
            Exclusions = new ScheduleExclusions { Dates = [new DateOnly(2026, 12, 25)] }
        });

        firstExcluded.CalculateNextRun(Utc(2026, 12, 24, 23), 0).ShouldBe(Utc(2026, 12, 26, 0));

        var bounded = Valid(new RecurringTask
        {
            DayInterval = new DayInterval(1) { OnTimes = [new TimeOnly(8, 0)] },
            RunUntil = Utc(2026, 1, 5, 8),
            Exclusions = new ScheduleExclusions { Days = [DayOfWeek.Saturday, DayOfWeek.Sunday] }
        });

        bounded.CalculateNextRun(Utc(2026, 1, 2, 8), 1).ShouldBeNull();
    }

    [Fact]
    public void Should_skip_forward_on_the_uniform_base_and_match_a_walked_reference()
    {
        var task = Valid(new RecurringTask
        {
            HourInterval = new HourInterval(4),
            Exclusions = new ScheduleExclusions { Days = [DayOfWeek.Saturday, DayOfWeek.Sunday] }
        });
        var anchor = Utc(2026, 1, 2, 0);
        var after = Utc(2026, 2, 1, 10);

        var expected = anchor;
        do
        {
            expected = expected.AddHours(4);
        } while (expected <= after || expected.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);

        task.NextOccurrenceStrictlyAfter(anchor, after).ShouldBe(expected);

        var fineGrid = Valid(new RecurringTask
        {
            SecondInterval = new SecondInterval(1),
            Exclusions = new ScheduleExclusions { Days = [DayOfWeek.Saturday, DayOfWeek.Sunday] }
        });

        fineGrid.NextOccurrenceStrictlyAfter(anchor, after).ShouldBe(Utc(2026, 2, 2, 0));
    }

    [Fact]
    public void Should_end_on_an_excluded_max_date_but_allow_a_slot_at_a_max_range_exit()
    {
        var maxDateExcluded = Valid(new RecurringTask
        {
            DayInterval = new DayInterval(1),
            Exclusions = new ScheduleExclusions { Dates = [DateOnly.MaxValue] }
        });

        maxDateExcluded.CalculateNextRun(
            new DateTimeOffset(9999, 12, 30, 0, 0, 0, TimeSpan.Zero), 1).ShouldBeNull();

        var max = DateTimeOffset.MaxValue;
        var rangeToMax = Valid(new RecurringTask
        {
            SecondInterval = new SecondInterval(1),
            Exclusions = new ScheduleExclusions
            {
                Ranges = [Range(max.AddSeconds(-1), max)]
            }
        });

        rangeToMax.CalculateNextRun(max.AddSeconds(-2), 1).ShouldBe(max);
        Should.NotThrow(() => rangeToMax.CalculateNextRun(max, 1).ShouldBeNull());

        var noSlotAtMax = Valid(new RecurringTask
        {
            SecondInterval = new SecondInterval(2),
            Exclusions = new ScheduleExclusions
            {
                Ranges = [Range(max.AddSeconds(-1), max)]
            }
        });

        Should.NotThrow(() => noSlotAtMax.CalculateNextRun(max.AddSeconds(-3), 1).ShouldBeNull());
    }

    [Fact]
    public void Should_throw_the_typed_failure_when_the_filtered_grid_is_empty_forever()
    {
        var anchor = Utc(2026, 1, 3, 0);
        var task = Valid(new RecurringTask
        {
            DayInterval = new DayInterval(7),
            Exclusions = new ScheduleExclusions { Days = [DayOfWeek.Saturday] }
        });

        var exception = Should.Throw<ExclusionSearchBudgetExceededException>(
            () => task.CalculateNextRun(anchor, 1));

        RecurringTask.MaxExclusionSearchIterations.ShouldBe(200_000);
        exception.StandingInstant.ShouldBeGreaterThan(anchor);
        exception.StandingInstant.DayOfWeek.ShouldBe(DayOfWeek.Saturday);
    }

    [Fact]
    public void Should_jump_a_minute_cron_across_a_thirty_day_window()
    {
        var start = Utc(2026, 1, 1, 0);
        var exit = Utc(2026, 1, 31, 0);
        var task = Valid(new RecurringTask
        {
            CronInterval = new CronInterval("* * * * *"),
            Exclusions = new ScheduleExclusions { Ranges = [Range(start, exit)] }
        });

        task.CalculateNextRun(start.AddMinutes(-1), 1).ShouldBe(exit);
    }

    [Fact]
    public void Should_allow_a_composite_shape_to_move_off_an_inclusion_day_before_filtering()
    {
        var task = Valid(new RecurringTask
        {
            WeekInterval = new WeekInterval(1, [DayOfWeek.Saturday]),
            DayInterval = new DayInterval(1),
            Exclusions = new ScheduleExclusions { Days = [DayOfWeek.Saturday] }
        });

        task.CalculateNextRun(Utc(2026, 1, 2, 0), 1).ShouldBe(Utc(2026, 1, 4, 0));
    }

    [Fact]
    public void Should_count_only_filtered_occurrences_and_honour_caps()
    {
        var anchor = Utc(2026, 1, 5, 0);
        var after = Utc(2026, 1, 23, 23);
        var rangeFrom = Utc(2026, 1, 14, 0);
        var rangeTo = Utc(2026, 1, 15, 0);
        var task = Valid(new RecurringTask
        {
            HourInterval = new HourInterval(4),
            Exclusions = new ScheduleExclusions
            {
                Days = [DayOfWeek.Saturday, DayOfWeek.Sunday],
                Dates = [new DateOnly(2026, 1, 12)],
                Ranges = [Range(rangeFrom, rangeTo)]
            }
        });

        var expected = 0;
        for (var slot = anchor; slot <= after; slot = slot.AddHours(4))
        {
            if (slot.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                DateOnly.FromDateTime(slot.DateTime) == new DateOnly(2026, 1, 12) ||
                slot >= rangeFrom && slot < rangeTo)
            {
                continue;
            }

            expected++;
        }

        task.CountsMissedInConstantTime().ShouldBeFalse();
        task.CountMissedOccurrences(anchor, after).ShouldBe(expected);

        var capped = Valid(new RecurringTask
        {
            MinuteInterval = new MinuteInterval(1),
            Exclusions = new ScheduleExclusions { Days = [DayOfWeek.Saturday, DayOfWeek.Sunday] }
        });

        capped.CountMissedOccurrences(Utc(2026, 1, 2, 23, 58), Utc(2026, 1, 5, 0, 10), cap: 5)
            .ShouldBe(6);
    }

    private static RecurringTask Valid(RecurringTask task)
    {
        task.Validate();
        return task;
    }

    private static ExclusionRange Range(DateTimeOffset from, DateTimeOffset to) => new()
    {
        FromUtc = from,
        ToUtc = to
    };

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
