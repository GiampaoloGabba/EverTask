using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.TimeZones;

/// <summary>
/// What a zoned schedule does across a daylight-saving transition (T5/T6/T7). The occurrence grid is pure
/// arithmetic over a definition, so these are direct unit tests of it — the same grid, reached through the
/// real builder, is what a live host walks.
/// </summary>
/// <remarks>
/// Rome's 2026 transitions are the reference: forward on 29 March (02:00 local becomes 03:00, so 02:00-02:59
/// does not exist) and back on 25 October (03:00 local becomes 02:00, so 02:00-02:59 happens twice).
/// </remarks>
public class DstTransitionTests
{
    private const string RomeId = "Europe/Rome";

    private static readonly TimeZoneInfo Rome     = TimeZoneInfo.FindSystemTimeZoneById(RomeId);
    private static readonly TimeZoneInfo LordHowe = TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe");

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi, int s = 0) =>
        new(y, mo, d, h, mi, s, TimeSpan.Zero);

    private static RecurringTask Build(Action<IRecurringTaskBuilder> configure)
    {
        var builder = new RecurringTaskBuilder();
        configure(builder);
        return builder.RecurringTask;
    }

    /// <summary>The occurrences strictly after <paramref name="from"/>, walked one at a time.</summary>
    private static List<DateTimeOffset> Occurrences(RecurringTask task, DateTimeOffset from, int count)
    {
        var occurrences = new List<DateTimeOffset>();
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

    private static DateTime LocalOf(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone).DateTime;

    [Fact]
    public void A_daily_slot_keeps_its_local_hour_on_both_sides_of_a_transition()
    {
        // The whole point: 09:00 Rome stays 09:00 Rome, which means the INSTANT moves by an hour.
        var task = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(RomeId));

        var occurrences = Occurrences(task, Utc(2026, 3, 27, 12, 0), 3);

        occurrences.ShouldBe([
            Utc(2026, 3, 28, 8, 0),  // still CET (+1)
            Utc(2026, 3, 29, 7, 0),  // CEST (+2) — the clock moved, the local hour did not
            Utc(2026, 3, 30, 7, 0)
        ]);
    }

    [Fact]
    public void A_daily_slot_the_spring_gap_removes_fires_at_the_first_local_time_that_exists()
    {
        // 02:30 does not exist on 29 March: the slot moves to 03:00 local, which is 01:00Z.
        var task = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 30)).InTimeZone(RomeId));

        var occurrences = Occurrences(task, Utc(2026, 3, 28, 12, 0), 2);

        occurrences[0].ShouldBe(Utc(2026, 3, 29, 1, 0), "the gap's exit, not a slot that does not exist");
        occurrences[1].ShouldBe(Utc(2026, 3, 30, 0, 30), "the day after is an ordinary 02:30 CEST");
    }

    [Fact]
    public void Exactly_02_00_on_the_spring_forward_day_also_lands_on_the_gaps_exit()
    {
        var task = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 0)).InTimeZone(RomeId));

        Occurrences(task, Utc(2026, 3, 28, 12, 0), 1).ShouldBe([Utc(2026, 3, 29, 1, 0)]);
    }

    [Fact]
    public void Several_slots_inside_one_gap_produce_a_single_occurrence()
    {
        // T6: 29 March 2026 is a Sunday, and both 02:15 and 02:45 resolve to 03:00 local. Firing twice at the
        // same instant would run the handler twice for one day. The next occurrence is the following Sunday,
        // where the two slots are two ordinary occurrences again.
        var task = Build(r => r.Schedule()
                               .OnDays(DayOfWeek.Sunday)
                               .AtTimes(new TimeOnly(2, 15), new TimeOnly(2, 45))
                               .InTimeZone(RomeId));

        var occurrences = Occurrences(task, Utc(2026, 3, 28, 23, 0), 3);

        occurrences.ShouldBe([
            Utc(2026, 3, 29, 1, 0),  // one occurrence for the two collapsed slots
            Utc(2026, 4, 5, 0, 15),  // 02:15 CEST
            Utc(2026, 4, 5, 0, 45)   // 02:45 CEST
        ]);
    }

    [Fact]
    public void A_walk_resuming_in_the_second_pass_of_a_repeated_hour_never_goes_backwards()
    {
        // The instant a restart hands back can land in the SECOND pass of the fall-back hour, whose local
        // reading is a slot whose first pass is already behind us. Mapping that nominal slot and returning it
        // would schedule an occurrence in the past; the walk has to recognise it as consumed and move on.
        var task = Build(r => r.Schedule()
                               .OnDays(DayOfWeek.Sunday)
                               .AtTimes(new TimeOnly(2, 15), new TimeOnly(2, 45))
                               .InTimeZone(RomeId));

        var secondPass = Utc(2026, 10, 25, 1, 30); // 02:30 CET, the repeated hour's second reading
        LocalOf(secondPass, Rome).ShouldBe(new DateTime(2026, 10, 25, 2, 30, 0));

        var next = task.CalculateNextRun(secondPass, 1).ShouldNotBeNull();

        next.ShouldBeGreaterThan(secondPass);
        next.ShouldBe(Utc(2026, 11, 1, 1, 15), "02:45's first pass is behind us, so the next Sunday it is");
    }

    [Fact]
    public void A_daily_slot_in_the_repeated_hour_fires_once_on_the_first_pass()
    {
        // T7: 02:30 happens twice on 25 October — 00:30Z (+2) and 01:30Z (+1). A calendar slot means once.
        var task = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 30)).InTimeZone(RomeId));

        var occurrences = Occurrences(task, Utc(2026, 10, 24, 12, 0), 2);

        occurrences[0].ShouldBe(Utc(2026, 10, 25, 0, 30), "the first pass, while the offset is still +2");
        occurrences[1].ShouldBe(Utc(2026, 10, 26, 1, 30), "the next day is an ordinary 02:30 CET");

        occurrences.ShouldBeUnique();
    }

    [Fact]
    public void A_plain_cadence_fires_in_both_passes_of_the_repeated_hour()
    {
        // T5: an elapsed grid is untouched by the zone, so the fall-back hour really does last two hours and
        // a 30-minute cadence fires four times in it. That is the correct answer for "every 30 minutes".
        var task = Build(r => r.Schedule().Every(30).Minutes());

        var occurrences = Occurrences(task, Utc(2026, 10, 24, 23, 45), 6);

        occurrences.ShouldBe([
            Utc(2026, 10, 25, 0, 15),
            Utc(2026, 10, 25, 0, 45),
            Utc(2026, 10, 25, 1, 15),
            Utc(2026, 10, 25, 1, 45),
            Utc(2026, 10, 25, 2, 15),
            Utc(2026, 10, 25, 2, 45)
        ]);

        // Read on Rome's clock, the repeated hour really is served twice before the grid moves past it.
        occurrences.Select(o => LocalOf(o, Rome).TimeOfDay).ShouldBe([
            new TimeSpan(2, 15, 0), new TimeSpan(2, 45, 0), // CEST
            new TimeSpan(2, 15, 0), new TimeSpan(2, 45, 0), // CET, the same wall times again
            new TimeSpan(3, 15, 0), new TimeSpan(3, 45, 0)
        ]);
    }

    [Fact]
    public void A_plain_cadence_is_not_compressed_by_the_spring_gap()
    {
        // T5, spelled out in the decisions: 01:45 local plus 30 minutes is 03:15 local, because the clock
        // jumped in between. The elapsed step is exactly 30 minutes either way.
        var task = Build(r => r.Schedule().Every(30).Minutes());

        var start = Utc(2026, 3, 29, 0, 45); // 01:45 CET
        LocalOf(start, Rome).ShouldBe(new DateTime(2026, 3, 29, 1, 45, 0));

        var next = task.CalculateNextRun(start, 1).ShouldNotBeNull();

        next.ShouldBe(Utc(2026, 3, 29, 1, 15));
        LocalOf(next, Rome).ShouldBe(new DateTime(2026, 3, 29, 3, 15, 0));
    }

    [Fact]
    public void Local_midnight_survives_both_transitions()
    {
        var task = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(0, 0)).InTimeZone(RomeId));

        var spring = Occurrences(task, Utc(2026, 3, 27, 12, 0), 3);
        spring.ShouldBe([
            Utc(2026, 3, 27, 23, 0), // 28 March 00:00 CET
            Utc(2026, 3, 28, 23, 0), // 29 March 00:00 CET — the transition is at 02:00, after midnight
            Utc(2026, 3, 29, 22, 0)  // 30 March 00:00 CEST
        ]);

        var autumn = Occurrences(task, Utc(2026, 10, 23, 12, 0), 3);
        autumn.ShouldBe([
            Utc(2026, 10, 23, 22, 0), // 24 October 00:00 CEST
            Utc(2026, 10, 24, 22, 0), // 25 October 00:00 CEST
            Utc(2026, 10, 25, 23, 0)  // 26 October 00:00 CET
        ]);

        spring.Concat(autumn).ShouldAllBe(o => LocalOf(o, Rome).TimeOfDay == TimeSpan.Zero);
    }

    [Fact]
    public void A_day_cadence_keeps_local_midnight_across_a_transition()
    {
        // Decisions §3.4: `Every(n).Days()` is Calendar, not Elapsed — a day interval snaps to a time of day,
        // midnight when none was named — so it accepts a zone AND the zone moves its instants. The same
        // definition without a zone is the constant 48-hour step it always was.
        var zoned = Build(r => r.Schedule().Every(2).Days().InTimeZone(RomeId));
        var plain = Build(r => r.Schedule().Every(2).Days());

        zoned.Semantics.ShouldBe(ScheduleSemantics.Calendar);
        zoned.Validate(); // an Elapsed classification would refuse the zone here

        var occurrences = Occurrences(zoned, Utc(2026, 3, 27, 12, 0), 2);

        occurrences.ShouldBe([
            Utc(2026, 3, 28, 23, 0), // 29 March 00:00 CET
            Utc(2026, 3, 30, 22, 0)  // 31 March 00:00 CEST — 47 hours later, because the clock moved
        ]);
        occurrences.ShouldAllBe(o => LocalOf(o, Rome).TimeOfDay == TimeSpan.Zero);

        Occurrences(plain, Utc(2026, 3, 27, 12, 0), 2)
            .ShouldBe([Utc(2026, 3, 29, 0, 0), Utc(2026, 3, 31, 0, 0)],
                "no zone, no change: the legacy grid steps a constant 48 hours");
    }

    [Fact]
    public void A_half_hour_transition_is_handled_without_assuming_an_hour()
    {
        // Lord Howe shifts by 30 minutes: 02:00 -> 02:30 on 4 October 2026.
        var task = Build(r => r.Schedule().EveryDay()
                               .AtTime(new TimeOnly(2, 15))
                               .InTimeZone("Australia/Lord_Howe"));

        var next = task.CalculateNextRun(Utc(2026, 10, 3, 12, 0), 1).ShouldNotBeNull();

        LocalOf(next, LordHowe).ShouldBe(new DateTime(2026, 10, 4, 2, 30, 0),
            "the gap's exit is 02:30, not the 03:00 an hour-wide assumption would produce");
    }

    [Fact]
    public void The_occurrence_sequence_stays_strictly_increasing_across_both_transitions()
    {
        // The monotonicity the scheduler depends on: a slot that went backwards would be scheduled in the
        // past and fire immediately, consuming the run budget.
        var task = Build(r => r.Schedule().EveryDay()
                               .AtTimes(new TimeOnly(1, 30), new TimeOnly(2, 30), new TimeOnly(3, 30))
                               .InTimeZone(RomeId));

        foreach (var start in new[] { Utc(2026, 3, 27, 0, 0), Utc(2026, 10, 23, 0, 0) })
        {
            var occurrences = Occurrences(task, start, 20);

            occurrences.Count.ShouldBe(20);
            occurrences.Zip(occurrences.Skip(1)).ShouldAllBe(pair => pair.Second > pair.First);
        }
    }

    [Fact]
    public void A_weekly_slot_keeps_its_local_hour_across_a_transition()
    {
        var task = Build(r => r.Schedule().EveryWeek()
                               .OnDay(DayOfWeek.Sunday).AtTime(new TimeOnly(9, 0))
                               .InTimeZone(RomeId));

        var occurrences = Occurrences(task, Utc(2026, 3, 20, 12, 0), 3);

        occurrences.ShouldBe([
            Utc(2026, 3, 22, 8, 0),  // CET
            Utc(2026, 3, 29, 7, 0),  // the transition Sunday itself, already CEST at 09:00
            Utc(2026, 4, 5, 7, 0)
        ]);
    }

    [Fact]
    public void A_monthly_slot_keeps_its_local_hour_across_a_transition()
    {
        var task = Build(r => r.Schedule().EveryMonth()
                               .OnDay(15).AtTime(new TimeOnly(3, 0))
                               .InTimeZone(RomeId));

        // A month interval advances the period BEFORE selecting the day, so the walk is seeded a month back.
        var occurrences = Occurrences(task, Utc(2026, 2, 20, 0, 0), 2);

        occurrences.ShouldBe([
            Utc(2026, 3, 15, 2, 0), // CET
            Utc(2026, 4, 15, 1, 0)  // CEST
        ]);
    }

    [Fact]
    public void RunUntil_is_judged_on_the_instant_the_zoned_slot_really_falls_on()
    {
        // RunUntil is an instant, the slot is a local time: the last occurrence is the last one whose
        // INSTANT precedes the bound, which the offset change moves.
        var task = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(RomeId));
        task.RunUntil = Utc(2026, 3, 29, 7, 30); // just after 09:00 CEST on the transition day

        var occurrences = Occurrences(task, Utc(2026, 3, 27, 12, 0), 5);

        occurrences.ShouldBe([Utc(2026, 3, 28, 8, 0), Utc(2026, 3, 29, 7, 0)]);
    }

    [Fact]
    public void MaxRuns_counts_the_occurrences_the_zone_produced()
    {
        var task = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 30)).InTimeZone(RomeId));
        task.MaxRuns = 3;

        // The run counter, not the walk, is what stops the series: the third occurrence is the last one.
        task.CalculateNextRun(Utc(2026, 3, 28, 12, 0), 2).ShouldNotBeNull();
        task.CalculateNextRun(Utc(2026, 3, 28, 12, 0), 3).ShouldBeNull();
    }

    [Fact]
    public void The_run_budget_is_spent_by_the_occurrences_that_really_fired_across_a_transition()
    {
        // The budget question the guard above cannot answer: a schedule whose slots the transition COMPRESSES
        // spends one run on the compressed pair, not two, so the series reaches further into the calendar than
        // a slot count would suggest. Walked the way the worker walks it — the run number advances by one per
        // occurrence — from before the spring gap to the moment the budget runs out.
        var task = Build(r => r.Schedule()
                               .OnDays(DayOfWeek.Sunday)
                               .AtTimes(new TimeOnly(2, 15), new TimeOnly(2, 45))
                               .InTimeZone(RomeId));
        task.MaxRuns = 4;

        var occurrences = new List<DateTimeOffset>();
        var cursor      = Utc(2026, 3, 28, 23, 0);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (task.CalculateNextRun(cursor, occurrences.Count) is not { } next)
                break;

            occurrences.Add(next);
            cursor = next;
        }

        occurrences.ShouldBe([
            Utc(2026, 3, 29, 1, 0),  // 02:15 and 02:45 are the same instant: ONE occurrence, ONE run
            Utc(2026, 4, 5, 0, 15),
            Utc(2026, 4, 5, 0, 45),
            Utc(2026, 4, 12, 0, 15)
        ]);

        // Five nominal slots, four runs — and the fifth call is refused by the budget, not by the walk.
        task.CalculateNextRun(occurrences[^1], 4).ShouldBeNull();
        task.MaxRuns = 5;
        task.CalculateNextRun(occurrences[^1], 4).ShouldBe(Utc(2026, 4, 12, 0, 45));
    }

    [Fact]
    public void The_slots_a_gap_compressed_are_counted_for_the_log()
    {
        // T6's other half: the deduplication is silent unless the number of slots that produced no occurrence
        // of their own travels out. It rides on NextRunResult, and WorkerExecutor logs it next to the missed
        // occurrences — logging only, since the compressed slots ARE the one occurrence.
        var task = Build(r => r.Schedule()
                               .OnDays(DayOfWeek.Sunday)
                               .AtTimes(new TimeOnly(2, 15), new TimeOnly(2, 45))
                               .InTimeZone(RomeId));

        var start  = Utc(2026, 3, 28, 23, 0);
        var result = task.CalculateNextValidRun(start, 1, referenceTime: start);

        result.NextRun.ShouldBe(Utc(2026, 3, 29, 1, 0));
        result.CollapsedSlotCount.ShouldBe(1,
            "02:45 stands for the same instant as 02:15, so the occurrence answers for one further slot");
    }

    [Fact]
    public void A_slot_discarded_as_already_served_is_counted_too()
    {
        // The other way a slot produces no occurrence: the walk resumes inside the SECOND pass of a repeated
        // hour, maps the next nominal slot and finds it already behind. That discard is a compressed slot as
        // much as a swallowed one, and the two sources share the counter.
        var task = Build(r => r.Schedule()
                               .OnDays(DayOfWeek.Sunday)
                               .AtTimes(new TimeOnly(2, 15), new TimeOnly(2, 45))
                               .InTimeZone(RomeId));

        var secondPass = Utc(2026, 10, 25, 1, 30); // 02:30 CET, the repeated hour's second reading
        var result     = task.CalculateNextValidRun(secondPass, 1, referenceTime: secondPass);

        result.NextRun.ShouldBe(Utc(2026, 11, 1, 1, 15));
        result.CollapsedSlotCount.ShouldBe(1, "02:45's first pass is behind us: it fires no occurrence");
    }

    [Fact]
    public void An_ordinary_occurrence_reports_no_compressed_slots()
    {
        // The counter has to stay quiet the rest of the year, and for the schedules a zone does not govern at
        // all — otherwise the log line means nothing when it does appear.
        var zoned = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(RomeId));
        var plain = Build(r => r.Schedule().Every(30).Minutes());

        var start = Utc(2026, 7, 1, 12, 0);

        zoned.CalculateNextValidRun(start, 1, referenceTime: start).CollapsedSlotCount.ShouldBe(0);
        plain.CalculateNextValidRun(start, 1, referenceTime: start).CollapsedSlotCount.ShouldBe(0);
    }

    [Fact]
    public void A_zoned_schedule_reports_its_zone_in_its_human_readable_form()
    {
        // T13: this string is also what the row's RecurringInfo column and the dashboard show.
        var zoned = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(RomeId));
        var plain = Build(r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)));

        zoned.ToString().ShouldBe("every 1 day(s) at 09:00 (Europe/Rome)");
        plain.ToString().ShouldBe("every 1 day(s) at 09:00",
            "a schedule without a zone reads exactly as it always did");
    }

    [Fact]
    public void A_zoned_cron_schedule_reports_its_zone_too()
    {
        var task = Build(r => r.Schedule().UseCron("0 9 * * *").InTimeZone(RomeId));

        task.ToString().ShouldBe("Use Cron expression: 0 9 * * * (Europe/Rome)");
    }
}
