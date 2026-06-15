using EverTask.Tests.TestHelpers;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using Newtonsoft.Json;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// End-to-end regression for bug B: a recurring series still RECOVERABLE under IsRecoverable
/// (RunUntil &gt;= now) but whose NextRunUtc is well in the past (beyond the grace window) and whose
/// next computed occurrence falls past RunUntil. The past-NextRunUtc recovery branch
/// (Dispatcher.cs) used to compute NextRun==null and THROW ArgumentException, which WorkerService
/// recorded as a transient re-dispatch failure (RecoveryDispatchFailureCount++) and eventually poisoned —
/// a legitimate end-of-series mistaken for an error, and a per-restart poison when combined with a
/// stale NextRunUtc. The fix FINALIZES the exhausted series instead: Completed, NextRunUtc cleared,
/// no failure recorded.
/// </summary>
public class RunUntilPastNextRunRecoveryReproTests : IsolatedIntegrationTestBase
{
    private readonly ResilienceTestState _state = new();

    [Fact]
    public async Task Recovery_finalizes_exhausted_series_with_past_NextRunUtc_and_future_RunUntil()
    {
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false);

        var now = DateTimeOffset.UtcNow;

        // Finding scenario: NextRunUtc well in the past (beyond the grace window) and the next occurrence
        // (~now+120s) falls past RunUntil. RunUntil is kept far enough ahead (20s) that the row is
        // reliably still recoverable when recovery runs, yet before the next occurrence so the series is
        // genuinely exhausted.
        var recurring = new RecurringTask
        {
            SecondInterval = new SecondInterval(120),
            RunUntil       = now.AddSeconds(20)
        };

        var seeded = new QueuedTask
        {
            Id              = Guid.NewGuid(),
            Type            = typeof(ResilienceRecurringTask).AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new ResilienceRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed, // between runs
            IsRecurring     = true,
            RecurringTask   = JsonConvert.SerializeObject(recurring),
            NextRunUtc      = now.AddMinutes(-5),  // WELL in the past (beyond the grace window)
            RunUntil        = recurring.RunUntil,  // in the future => recoverable
            CurrentRunCount = 1,
            CreatedAtUtc    = now.AddMinutes(-10)
        };
        await Storage.Persist(seeded);

        // Sanity: the row is recoverable at seed time (RunUntil >= now).
        seeded.IsRecoverable(now).ShouldBeTrue();

        await Host!.StartAsync();

        // Recovery must FINALIZE the exhausted series (clear NextRunUtc), not throw/poison: poll for it.
        QueuedTask final = seeded;
        var deadline  = DateTimeOffset.UtcNow.AddSeconds(8);
        var finalized = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            final = (await Storage.GetAll()).Single();
            if (final.NextRunUtc == null)
            {
                finalized = true;
                break;
            }
            await Task.Delay(100);
        }

        // The legitimate end-of-series must be finalized terminally, NOT recorded as a re-dispatch failure
        // or poisoned (the old ArgumentException behavior).
        finalized.ShouldBeTrue(
            $"recovery must finalize the exhausted series (clear NextRunUtc), not throw/poison. " +
            $"Final Status={final.Status}, NextRunUtc={final.NextRunUtc}, " +
            $"RecoveryDispatchFailureCount={final.RecoveryDispatchFailureCount}");
        final.Status.ShouldBe(QueuedTaskStatus.Completed, "the finalized series is terminal Completed");
        final.IsRecoverable(DateTimeOffset.UtcNow).ShouldBeFalse("the finalized series must not be recoverable");
        (final.RecoveryDispatchFailureCount ?? 0).ShouldBe(0,
            "a legitimate end-of-series must NOT be recorded as a re-dispatch failure (bug B)");
    }
}
