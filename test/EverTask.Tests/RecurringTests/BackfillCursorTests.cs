using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// <see cref="RecurringTask.FirstOccurrenceOnOrAfter"/>: the one question the grid answers INCLUSIVELY, and the
/// only thing that decides where a backfilled durable schedule starts counting from.
/// </summary>
/// <remarks>
/// Every other primitive answers "strictly after", and a day, week or month interval advances its PERIOD before
/// choosing a time inside it — so a probe placed just before the instant already lands a whole period past the
/// slot the caller asked for, and a forward-only walk can never come back for it. That is what these cases pin,
/// shape by shape and then as an invariant no shape may break.
/// </remarks>
public class BackfillCursorTests
{
    private static RecurringTask Build(Action<IRecurringTaskBuilder> configure)
    {
        var builder = new RecurringTaskBuilder(TimeProvider.System);
        configure(builder);

        return builder.RecurringTask;
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    // ---- Calendar shapes: the period is advanced before the time inside it is chosen -----------------

    [Fact]
    public void A_daily_schedule_backfills_the_slot_of_the_very_day_it_was_asked_for()
    {
        var schedule = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 0)));

        // 01:00 on the 10th: the 02:00 slot of THAT day is still ahead, so it is the first one on or after.
        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 10, 1, 0))
                .ShouldBe(Utc(2026, 1, 10, 2, 0));
    }

    [Fact]
    public void A_daily_schedule_backfilled_from_its_own_slot_starts_on_that_slot()
    {
        var schedule = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 0)));

        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 10, 2, 0))
                .ShouldBe(Utc(2026, 1, 10, 2, 0), "the question is inclusive");
    }

    [Fact]
    public void A_daily_schedule_backfilled_past_its_slot_starts_on_the_next_day()
    {
        var schedule = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 0)));

        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 10, 2, 0, 1))
                .ShouldBe(Utc(2026, 1, 11, 2, 0));
    }

    [Fact]
    public void A_multi_day_cadence_keeps_its_phase_relative_to_the_backfill_instant()
    {
        var schedule = Build(r => r.Schedule().Every(3).Days().AtTime(new TimeOnly(2, 0)));

        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 10, 1, 0))
                .ShouldBe(Utc(2026, 1, 10, 2, 0));
    }

    [Fact]
    public void A_weekly_schedule_backfills_the_slot_of_the_very_day_it_was_asked_for()
    {
        // 2026-01-12 is a Monday.
        var schedule = Build(r => r.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).AtTime(new TimeOnly(9, 0)));

        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 12, 8, 0))
                .ShouldBe(Utc(2026, 1, 12, 9, 0));
    }

    [Fact]
    public void A_monthly_schedule_backfills_the_slot_of_the_very_month_it_was_asked_for()
    {
        var schedule = Build(r => r.Schedule().EveryMonth().OnDay(15).AtTime(new TimeOnly(3, 0)));

        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 15, 2, 0))
                .ShouldBe(Utc(2026, 1, 15, 3, 0));
    }

    [Fact]
    public void A_day_of_week_selector_backfills_the_slot_of_the_very_day_it_was_asked_for()
    {
        var schedule = Build(r => r.Schedule().OnDays(DayOfWeek.Monday, DayOfWeek.Thursday)
                                   .AtTime(new TimeOnly(7, 30)));

        // Thursday 2026-01-15, an hour before the slot.
        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 15, 6, 30))
                .ShouldBe(Utc(2026, 1, 15, 7, 30));
    }

    // ---- Elapsed shapes and cron: unchanged --------------------------------------------------------

    [Fact]
    public void An_elapsed_cadence_backfills_from_the_instant_itself()
    {
        var schedule = Build(r => r.Schedule().Every(5).Minutes());

        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 10, 1, 0))
                .ShouldBe(Utc(2026, 1, 10, 1, 0), "every instant is on the grid of a plain cadence");
    }

    [Fact]
    public void An_elapsed_cadence_with_a_phase_backfills_the_next_phased_slot()
    {
        var schedule = Build(r => r.Schedule().Every(1).Minutes().AtSecond(30));

        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 10, 1, 0, 10))
                .ShouldBe(Utc(2026, 1, 10, 1, 0, 30));
    }

    [Fact]
    public void A_cron_schedule_backfills_the_slot_of_the_very_day_it_was_asked_for()
    {
        var schedule = new RecurringTask { CronInterval = new CronInterval("0 2 * * *") };

        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 10, 1, 0))
                .ShouldBe(Utc(2026, 1, 10, 2, 0));
    }

    // ---- Bounds ------------------------------------------------------------------------------------

    [Fact]
    public void A_backfill_beyond_the_end_of_the_series_has_no_cursor_to_start_from()
    {
        var schedule = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 0)));
        schedule.RunUntil = Utc(2026, 1, 5);

        schedule.FirstOccurrenceOnOrAfter(Utc(2026, 1, 10, 1, 0)).ShouldBeNull();
    }

    // ---- The invariant no shape may break ----------------------------------------------------------

    public static TheoryData<string, RecurringTask, TimeSpan> Shapes() => new()
    {
        { "EveryDay().AtTime(02:00)", Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 0))),
            TimeSpan.FromDays(1) },
        { "Every(3).Days().AtTime(02:00)", Build(r => r.Schedule().Every(3).Days().AtTime(new TimeOnly(2, 0))),
            TimeSpan.FromDays(3) },
        { "EveryWeek().OnDay(Monday).AtTime(09:00)",
            Build(r => r.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).AtTime(new TimeOnly(9, 0))),
            TimeSpan.FromDays(7) },
        { "OnDays(Mon, Thu).AtTime(07:30)",
            Build(r => r.Schedule().OnDays(DayOfWeek.Monday, DayOfWeek.Thursday).AtTime(new TimeOnly(7, 30))),
            TimeSpan.FromDays(7) },
        { "EveryMonth().OnDay(15).AtTime(03:00)",
            Build(r => r.Schedule().EveryMonth().OnDay(15).AtTime(new TimeOnly(3, 0))), TimeSpan.FromDays(31) },
        { "EveryHour().AtMinute(20)", Build(r => r.Schedule().EveryHour().AtMinute(20)), TimeSpan.FromHours(1) },
        { "Every(5).Minutes()", Build(r => r.Schedule().Every(5).Minutes()), TimeSpan.FromMinutes(5) },
        { "cron 0 2 * * *", new RecurringTask { CronInterval = new CronInterval("0 2 * * *") },
            TimeSpan.FromDays(1) }
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_backfill_cursor_is_never_more_than_one_period_past_the_instant_it_was_asked_for(
        string shape, RecurringTask schedule, TimeSpan period)
    {
        // Probing the grid an hour at a time across a fortnight: the answer must always be at or after the
        // instant, and never further than one period away — a whole period of daylight between the two means
        // a slot was passed over, which is exactly what a forward-only probe used to do to a calendar grid.
        var start = Utc(2026, 1, 5);

        for (var offset = TimeSpan.Zero; offset < TimeSpan.FromDays(14); offset += TimeSpan.FromHours(1))
        {
            var instant = start + offset;
            var cursor  = schedule.FirstOccurrenceOnOrAfter(instant);

            cursor.ShouldNotBeNull($"'{shape}' has occurrences forever and must answer for {instant:O}");
            cursor!.Value.ShouldBeGreaterThanOrEqualTo(instant, $"'{shape}' answered before the instant asked for");
            (cursor.Value - instant).ShouldBeLessThanOrEqualTo(period,
                $"'{shape}' skipped a whole period past {instant:O}");
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_backfill_cursor_is_a_real_grid_slot(string shape, RecurringTask schedule, TimeSpan period)
    {
        _ = period;

        var instant = Utc(2026, 1, 7, 13, 17, 42);
        var cursor  = schedule.FirstOccurrenceOnOrAfter(instant);

        cursor.ShouldNotBeNull();

        // A slot the grid produces is a slot the grid can walk on from: stepping once from it must move
        // forward, which a value invented off the grid would not do consistently.
        var next = schedule.NextGridOccurrenceAfter(cursor!.Value);

        next.ShouldNotBeNull($"'{shape}' answered with an instant its own grid does not continue from");
        next!.Value.ShouldBeGreaterThan(cursor.Value);
    }
}
