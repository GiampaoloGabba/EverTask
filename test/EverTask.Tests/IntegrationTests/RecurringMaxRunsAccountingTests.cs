using EverTask.Handler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// M5 (batch B) — F7: downtime-skipped occurrences must count toward <c>CurrentRunCount</c> (and
/// therefore toward <c>MaxRuns</c>). The stop-check in <c>CalculateNextValidRun</c> already accounts
/// for skipped occurrences (<c>currentRun + skippedCount &gt;= MaxRuns</c>), but the persisted counter
/// is advanced by only 1 per real run, so the two drift apart after a downtime.
///
/// Deterministic seam: build a lazy recurring executor whose scheduled <c>ExecutionTime</c> is fixed
/// well in the past and drive <c>WorkerExecutor.DoWork</c> directly (no timing-as-gate). The single
/// run's <c>QueueNextOccourrence</c> skips forward over the past occurrences and must advance the
/// counter by <c>1 + SkippedCount</c>.
/// </summary>
public class RecurringMaxRunsAccountingTests : IsolatedIntegrationTestBase
{
    private static TaskHandlerExecutor BuildRecurringExecutor(Guid id, RecurringTask recurring, DateTimeOffset scheduledTime) =>
        new(
            new TestTaskRecurringSeconds(),
            Handler: null,
            HandlerTypeName: typeof(TestTaskRecurringSecondsHandler).AssemblyQualifiedName,
            ExecutionTime: scheduledTime,
            RecurringTask: recurring,
            HandlerCallback: null,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId: id,
            QueueName: null,
            TaskKey: null,
            AuditLevel: AuditLevel.None);

    [Fact]
    public async Task Should_count_skipped_occurrences_toward_currentruncount_after_downtime()
    {
        // F7: a run scheduled 60 s in the past with a 1 s interval skips ~59 occurrences before the
        // next future one. They must all be reflected in CurrentRunCount (advance by 1 + SkippedCount),
        // not lost (advance by exactly 1).
        await CreateIsolatedHostAsync();

        var id        = Guid.NewGuid();
        var recurring = new RecurringTask { SecondInterval = new SecondInterval(1) };
        var executor  = BuildRecurringExecutor(id, recurring, DateTimeOffset.UtcNow.AddSeconds(-60));

        await Storage.Persist(executor.ToQueuedTask());

        await WorkerExecutor.DoWork(executor, CancellationToken.None);

        var task = (await Storage.Get(t => t.Id == id)).Single();

        // Pre-fix: advanced by exactly 1. Post-fix: 1 + ~59 skipped. The greater-than margin is robust
        // against the exact (timing-sensitive) skip count and against an extra rescheduled run.
        task.CurrentRunCount.ShouldNotBeNull();
        task.CurrentRunCount!.Value.ShouldBeGreaterThan(10);
        task.NextRunUtc.ShouldNotBeNull(); // the series keeps going (no MaxRuns)
    }

    [Fact]
    public async Task Should_account_skipped_occurrences_against_maxruns_when_exhausted_by_downtime()
    {
        // F7 (MaxRuns variant): when the skipped occurrences consume the remaining MaxRuns budget the
        // series must stop AND the persisted counter must reflect the consumed budget (>= MaxRuns), so
        // the recoverable predicate sees the series as exhausted. Pre-fix CurrentRunCount stays at 1,
        // far below MaxRuns, making the exhausted series look like it still has budget.
        await CreateIsolatedHostAsync();

        var id        = Guid.NewGuid();
        var recurring = new RecurringTask { SecondInterval = new SecondInterval(1), MaxRuns = 3 };
        var executor  = BuildRecurringExecutor(id, recurring, DateTimeOffset.UtcNow.AddSeconds(-60));

        await Storage.Persist(executor.ToQueuedTask());

        await WorkerExecutor.DoWork(executor, CancellationToken.None);

        var task = (await Storage.Get(t => t.Id == id)).Single();

        task.NextRunUtc.ShouldBeNull(); // exhausted: no next occurrence
        task.CurrentRunCount.ShouldNotBeNull();
        task.CurrentRunCount!.Value.ShouldBeGreaterThanOrEqualTo(3);
    }
}
