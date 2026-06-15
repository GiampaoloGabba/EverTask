using EverTask.Handler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Option B accounting semantics (f7-f8 tech-debt paydown): <c>MaxRuns</c> counts REAL executions only.
/// Occurrences skipped while the host was down keep the schedule aligned (and are logged) but do NOT
/// consume the <c>MaxRuns</c> budget, so <c>CurrentRunCount</c> always equals the number of real runs
/// (== RunsAudit rows). This replaces the previous "downtime consumes the budget" behavior (advance by
/// 1 + skipped), which overloaded the meaning of <c>CurrentRunCount</c>.
///
/// Deterministic seam: build a lazy recurring executor whose scheduled <c>ExecutionTime</c> is fixed
/// well in the past and drive <c>WorkerExecutor.DoWork</c> directly (no timing-as-gate). The single
/// run's <c>QueueNextOccourrence</c> skips forward over the past occurrences but advances the counter
/// by exactly 1.
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
    public async Task Should_count_only_real_executions_toward_currentruncount_after_downtime()
    {
        // Option B: a run scheduled 60 s in the past with a 1 s interval skips ~59 occurrences before the
        // next future one. Those skips keep the schedule aligned but must NOT inflate the run counter:
        // exactly one real execution happened, so CurrentRunCount must be 1 (not 1 + ~59).
        await CreateIsolatedHostAsync();

        var id        = Guid.NewGuid();
        var recurring = new RecurringTask { SecondInterval = new SecondInterval(1) };
        var executor  = BuildRecurringExecutor(id, recurring, DateTimeOffset.UtcNow.AddSeconds(-60));

        await Storage.Persist(executor.ToQueuedTask());

        await WorkerExecutor.DoWork(executor, CancellationToken.None);

        var task = (await Storage.Get(t => t.Id == id)).Single();

        // Exactly one real execution: the advance is a fixed 1 regardless of how many occurrences were
        // skipped to realign the schedule.
        task.CurrentRunCount.ShouldBe(1);
        task.NextRunUtc.ShouldNotBeNull(); // the series keeps going (no MaxRuns)
    }

    [Fact]
    public async Task Should_keep_series_alive_when_downtime_skips_more_than_maxruns()
    {
        // Option B (MaxRuns variant): downtime occurrences do NOT consume the MaxRuns budget. After a
        // single real run, a MaxRuns=3 series still has 2 runs of budget left even though ~59 occurrences
        // were skipped to realign — so the series stays alive and the counter reflects only the 1 real run.
        // (Pre-fix behavior: the skipped occurrences exhausted MaxRuns, NextRunUtc went null and
        // CurrentRunCount jumped to ~60.)
        await CreateIsolatedHostAsync();

        var id        = Guid.NewGuid();
        var recurring = new RecurringTask { SecondInterval = new SecondInterval(1), MaxRuns = 3 };
        var executor  = BuildRecurringExecutor(id, recurring, DateTimeOffset.UtcNow.AddSeconds(-60));

        await Storage.Persist(executor.ToQueuedTask());

        await WorkerExecutor.DoWork(executor, CancellationToken.None);

        var task = (await Storage.Get(t => t.Id == id)).Single();

        task.NextRunUtc.ShouldNotBeNull();      // not exhausted: 2 real runs of budget remain
        task.CurrentRunCount.ShouldBe(1);       // only the one real execution counts
    }
}
