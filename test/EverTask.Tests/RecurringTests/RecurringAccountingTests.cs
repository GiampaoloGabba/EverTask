using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using Newtonsoft.Json;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// M5 (batch A) — recurring accounting / recovery pure-logic edge cases:
/// F11 (MonthInterval.OnMonths lost on round-trip), CU11/L27 (exhausted series still recoverable),
/// L34 (null CurrentRunCount dropped), L36 (sub-second interval over huge elapsed overflows/hangs),
/// CU8 (first occurrence ignores RunUntil).
/// </summary>
public class RecurringAccountingTests
{
    [Fact]
    public void Should_preserve_onmonths_after_roundtrip()
    {
        // F11: OnMonths is get-only, so Newtonsoft cannot repopulate it on deserialization — a
        // month-restricted recurrence loses its constraint after persistence/recovery and fires every month.
        var original = new MonthInterval(2, new[] { 3, 6, 9 });

        var restored = JsonConvert.DeserializeObject<MonthInterval>(JsonConvert.SerializeObject(original))!;

        restored.OnMonths.ShouldBe(new[] { 3, 6, 9 });
    }

    [Fact]
    public void Should_treat_exhausted_series_as_nonrecoverable()
    {
        // CU11/L27: a recurring series that completed its last allowed run (CurrentRunCount == MaxRuns)
        // is exhausted and must NOT be recoverable (align `< MaxRuns` with CalculateNextRun's `>= MaxRuns`).
        var task = new QueuedTask
        {
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(5),
            MaxRuns         = 3,
            CurrentRunCount = 3
        };

        task.IsRecoverable(DateTimeOffset.UtcNow).ShouldBeFalse();
    }

    [Fact]
    public void Should_treat_null_currentruncount_as_zero_in_recoverable()
    {
        // L34: a recurring row with MaxRuns set and CurrentRunCount null must be recoverable (null
        // treated as 0), not dropped by a lifted null comparison.
        var task = new QueuedTask
        {
            Status          = QueuedTaskStatus.WaitingQueue,
            IsRecurring     = true,
            MaxRuns         = 3,
            CurrentRunCount = null
        };

        task.IsRecoverable(DateTimeOffset.UtcNow).ShouldBeTrue();
    }

    [Fact]
    public void Should_not_hang_on_subsecond_interval_with_huge_elapsed()
    {
        // L36: a sub-second interval with a huge elapsed span must not overflow skippedCount nor iterate
        // unboundedly when skipping forward to the next future occurrence.
        var task          = new RecurringTask { SecondInterval = new SecondInterval(1) };
        var scheduledTime = DateTimeOffset.UtcNow.AddYears(-100);

        NextRunResult result = null!;
        Should.CompleteIn(() => result = task.CalculateNextValidRun(scheduledTime, 0), TimeSpan.FromSeconds(3));

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Should_count_real_cron_occurrences_skipped_after_downtime()
    {
        // F8: for an IRREGULAR cron schedule the skipped count must be the REAL number of missed cron
        // occurrences (iterating the actual schedule), not the coarse elapsed/min-interval division
        // that diverges on uneven gaps. Cron fires daily at 09:00 and 17:00 UTC (gaps 8h / 16h).
        var task = new RecurringTask { CronInterval = new CronInterval("0 0 9,17 * * *") };

        var scheduledTime = new DateTimeOffset(2025, 1, 6, 9, 0, 0, TimeSpan.Zero);  // occurrence that ran
        var now           = new DateTimeOffset(2025, 1, 8, 10, 0, 0, TimeSpan.Zero); // ~41h later

        var result = task.CalculateNextValidRun(scheduledTime, currentRun: 1, referenceTime: now);

        // Missed occurrences in [Jan6 17:00, now): Jan6 17:00, Jan7 09:00, Jan7 17:00, Jan8 09:00 = 4.
        // The coarse pre-fix estimate (elapsed / min-interval) is 2 or 5, never 4.
        result.SkippedCount.ShouldBe(4);
        result.NextRun.ShouldBe(new DateTimeOffset(2025, 1, 8, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Should_suppress_the_logging_only_cron_count_walk_on_the_rate_limit_skip_path()
    {
        // O: on the rate-limit skip-ahead path (computeSkippedCount=false, `now` = the limiter's far slot)
        // the LOGGING-ONLY missed-occurrence walk is pure waste — the next run is computed O(1) regardless.
        // The walk must be suppressed (SkippedCount=0) while the next run stays correct.
        var task = new RecurringTask { CronInterval = new CronInterval("0 0 9,17 * * *") };

        var scheduledTime = new DateTimeOffset(2025, 1, 6, 9, 0, 0, TimeSpan.Zero);
        var farSlot       = new DateTimeOffset(2025, 1, 8, 10, 0, 0, TimeSpan.Zero);

        var result = task.CalculateNextValidRun(scheduledTime, currentRun: 1, referenceTime: farSlot,
            isRecovery: true, computeSkippedCount: false);

        result.SkippedCount.ShouldBe(0, "the logging-only walk is suppressed on the skip-ahead path");
        // The next run is unaffected: first cron occurrence strictly after the far slot.
        result.NextRun.ShouldBe(new DateTimeOffset(2025, 1, 8, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Should_not_stop_series_for_skipped_occurrences_under_maxruns()
    {
        // Option B (f7-f8 tech-debt paydown): occurrences skipped during a downtime must NOT consume the
        // MaxRuns budget. With currentRun (real executions) still below MaxRuns, the series stays alive
        // and returns a future occurrence no matter how many occurrences were skipped to realign — the
        // only MaxRuns gate is on real runs. (Pre-fix: currentRun + skippedCount >= MaxRuns returned null.)
        var task = new RecurringTask { SecondInterval = new SecondInterval(1), MaxRuns = 3 };

        var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now      = baseTime.AddHours(1); // ~3600 occurrences skipped to realign

        var result = task.CalculateNextValidRun(baseTime, currentRun: 1, referenceTime: now);

        // 1 real run < MaxRuns(3): the series must continue with a strictly-future next run.
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(now);
        // The skip count is still reported (for logging), it just no longer gates MaxRuns.
        result.SkippedCount.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Should_validate_first_occurrence_against_rununtil()
    {
        // CU8: the first occurrence (from RunDelayed/InitialDelay) is returned WITHOUT validating it
        // against RunUntil; a first run beyond RunUntil must not be scheduled.
        var task = new RecurringTask
        {
            InitialDelay   = TimeSpan.FromHours(2),
            SecondInterval = new SecondInterval(1),
            RunUntil       = DateTimeOffset.UtcNow.AddHours(1) // the first run (now + 2h) exceeds this
        };

        task.CalculateNextRun(DateTimeOffset.UtcNow, 0).ShouldBeNull();
    }
}
