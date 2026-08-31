using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests verifying consistency between Dispatcher and WorkerExecutor
/// when calculating next run times for recurring tasks.
/// Both components should use CalculateNextValidRun() and preserve ExecutionTime.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
public class DispatcherWorkerExecutorConsistencyTests : IsolatedIntegrationTestBase
{
    // NO instance fields - use base class properties

    [Fact]
    public async Task Dispatcher_And_WorkerExecutor_Should_Both_Use_CalculateNextValidRun()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task with first run in the past (should skip)
        var pastTime = DateTimeOffset.UtcNow.AddSeconds(-10);

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.RunAt(pastTime).Then().Every(5).Seconds());

        await Task.Delay(200); // Let Dispatcher calculate

        // Get initial scheduling (done by Dispatcher)
        var tasksAfterDispatch = await Storage.GetAll();
        var taskAfterDispatch = tasksAfterDispatch.FirstOrDefault(t => t.Id == taskId);

        taskAfterDispatch.ShouldNotBeNull();
        var dispatcherNextRun = taskAfterDispatch.NextRunUtc;

        // Assert: Dispatcher should have skipped past occurrences
        dispatcherNextRun.ShouldNotBeNull();
        dispatcherNextRun.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Wait for the first execution AND its re-scheduling: CompleteRecurringRun writes the runs
        // audit, CurrentRunCount and the new NextRunUtc under a single atomic operation, so waiting
        // on the audit guarantees the NextRunUtc read below is the WorkerExecutor's value and never
        // the Dispatcher's still-unmodified one.
        var taskAfterExecution = await WaitForRecurringRunsAsync(taskId, expectedRuns: 1, timeoutMs: 8000);

        // The series has no MaxRuns - the NextRunUtc asserted below only exists while it is alive - so
        // its audits keep growing under the storage lock while this runs: snapshot before enumerating.
        var firstRun = taskAfterExecution.SnapshotRunsAudits()
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .First();

        // The occurrence that ran is the one the Dispatcher scheduled: the scheduler dequeues only
        // when the slot is due (scheduledTime <= now), so the audited execution is never earlier.
        firstRun.ExecutedAt.ShouldBeGreaterThanOrEqualTo(dispatcherNextRun.Value);

        var workerExecutorNextRun = taskAfterExecution.NextRunUtc;
        workerExecutorNextRun.ShouldNotBeNull();

        // Assert: the WorkerExecutor re-schedules from the SCHEDULED slot, not from the wall clock.
        // QueueNextOccourrence feeds CalculateNextValidRun with TaskHandlerExecutor.ExecutionTime,
        // which for this first occurrence IS the slot the Dispatcher picked (and NOT firstRun's
        // ExecutedAt, which is stamped when the run finishes). The new NextRunUtc therefore always
        // lands on the occurrence grid anchored at dispatcherNextRun: dispatcherNextRun + k * 5s,
        // with k == 1 normally and k > 1 only when the run was late enough for CalculateNextValidRun
        // to realign past missed occurrences — which stays on the SAME grid. A UtcNow-based
        // re-schedule would instead land at "instant the run finished + 5s", i.e. off that grid by
        // the execution latency. Deliberately NOT compared against "now": with a 5s interval the
        // next run is only 5s away, so a slow run/read let the wall clock catch up with it.
        var interval = TimeSpan.FromSeconds(5); // matches Every(5).Seconds() above
        var advance = workerExecutorNextRun.Value - dispatcherNextRun.Value;

        advance.ShouldBeGreaterThanOrEqualTo(interval);
        (advance.Ticks % interval.Ticks).ShouldBe(0L);

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task First_Run_Dispatcher_Should_Skip_Past_Occurrences()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task starting 1 hour ago, every 20 minutes
        var pastTime = DateTimeOffset.UtcNow.AddHours(-1);

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.RunAt(pastTime).Then().Every(20).Minutes());

        await Task.Delay(200);

        // Assert: Dispatcher should skip past occurrences
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.NextRunUtc.ShouldNotBeNull();
        task.NextRunUtc.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Should have skipped approximately 3 occurrences (60 minutes / 20 minutes = 3)
        // FIXME: SkippedOccurrencesAudits property does not exist

        // var skippedAudits = // FIXME: SkippedOccurrencesAudits property does not exist - task.task.SkippedOccurrencesAudits;

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task Subsequent_Runs_WorkerExecutor_Should_Skip_Past_Occurrences()
    {
        // NOTE: This test simulates a downtime scenario, but since we can't share storage
        // between two separate IHost instances easily without providing a logger,
        // we'll verify the skip behavior using a single host with careful timing.

        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task with first run in the past (simulates downtime recovery)
        var pastTime = DateTimeOffset.UtcNow.AddSeconds(-5); // 5 seconds ago = ~5 missed 1-second runs

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.RunAt(pastTime).Then().Every(1).Seconds());

        // Wait for the first execution AND its re-scheduling: the runs audit and the new NextRunUtc
        // are written together, so this never observes the Dispatcher's NextRunUtc by mistake.
        var task = await WaitForRecurringRunsAsync(taskId, expectedRuns: 1, timeoutMs: 3000);

        // Assert: WorkerExecutor should have skipped past occurrences.
        // The occurrence grid is {pastTime + k * 1s}. The Dispatcher realigned past the ~5 missed
        // occurrences to the first slot strictly after "now" (so >= pastTime + 6s) and the
        // WorkerExecutor advanced it by at least one more interval from that slot, hence
        // >= pastTime + 7s. An implementation catching up on missed runs would instead sit at
        // pastTime + 2s (the second missed occurrence). Anchored on pastTime — a value this test
        // owns — rather than on DateTimeOffset.UtcNow at assertion time, which raced the 1s cadence:
        // the next run is only 1s ahead, so any hiccup between the execution and the assertion let
        // the wall clock overtake it.
        task.NextRunUtc.ShouldNotBeNull();
        task.NextRunUtc.Value.ShouldBeGreaterThanOrEqualTo(pastTime.AddSeconds(7));

        // Should NOT have caught up on the ~5 missed occurrences. The storage hands out live
        // instances and the cadence is 1s, so a second legitimate run can complete between the wait
        // and this line: "counter == 1" raced it. Instead prove that every run that executed belongs
        // to a slot the Dispatcher realigned to (>= pastTime + 6s): a catching-up implementation
        // would execute the missed occurrences right after dispatch (~pastTime + 5s), and the
        // scheduler only dequeues a slot once it is due, so a delayed assertion can only make this
        // more true, never less. Snapshot first: this series has no MaxRuns (the NextRunUtc asserted
        // above only exists while it is alive) and appends to the live list while these lines run.
        var completedRuns = task.SnapshotRunsAudits()
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .ToList();

        completedRuns.ShouldNotBeEmpty();
        completedRuns.ShouldAllBe(a => a.ExecutedAt >= pastTime.AddSeconds(6));

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task ExecutionTime_Should_Be_Preserved_Across_Dispatcher_And_WorkerExecutor()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task every 3 seconds, capped at the 2 runs the interval assertion reads:
        // nothing here needs the series alive, and the cap keeps it from firing into host teardown
        // (and from appending to the run audits while they are enumerated).
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(3).Seconds().MaxRuns(2));

        // Wait for 2 executions
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 10000);

        // Get task state
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);

        // Get completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(2)
            .ToList();

        completedRuns.Count.ShouldBe(2);

        // Assert: ExecutionTime should be preserved and used consistently
        // Interval between runs should be approximately 3 seconds
        var interval = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;

        interval.ShouldBeGreaterThan(2.5);
        interval.ShouldBeLessThan(4);

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task Consistency_Test_HourInterval_Across_Multiple_Runs()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task every hour (testing with seconds for speed)
        // Using SecondInterval but verifying calculation consistency, capped at the 3 runs asserted
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds().MaxRuns(3));

        // Wait for 3 executions
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: 10000);

        // Get task state
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Get all completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(3)
            .ToList();

        completedRuns.Count.ShouldBe(3);

        // Assert: Both Dispatcher (first run) and WorkerExecutor (subsequent runs)
        // should maintain consistent 2-second intervals
        for (var i = 1; i < completedRuns.Count; i++)
        {
            var interval = (completedRuns[i].ExecutedAt - completedRuns[i - 1].ExecutedAt).TotalSeconds;
            interval.ShouldBeGreaterThan(1.5);
            interval.ShouldBeLessThan(3);
        }

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task Consistency_Test_DayInterval_Skips_Correctly()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task daily at a specific time in the past
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        var specificTime = new TimeOnly(10, 0, 0);

        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring
                .RunAt(yesterday).Then()
                .Every(1).Days()
                .AtTimes(specificTime));

        await Task.Delay(200);

        // Assert: Dispatcher should have calculated next run for today at 10:00
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.NextRunUtc.ShouldNotBeNull();

        // Next run should be in the future
        task.NextRunUtc.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Should be scheduled for 10:00 (hour and minute)
        task.NextRunUtc.Value.Hour.ShouldBe(10);
        task.NextRunUtc.Value.Minute.ShouldBe(0);

        // Cleanup automatic via IAsyncDisposable
    }

    [Fact]
    public async Task Consistency_Test_CronInterval_Across_Components()
    {
        // Arrange
        await CreateIsolatedHostAsync(channelCapacity: 10, maxDegreeOfParallelism: 5);

        // Dispatch recurring task with cron expression (every 5 seconds for testing)
        // Cron: "*/5 * * * * *" (6-field format with seconds), capped at the 2 runs asserted
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().UseCron("*/5 * * * * *").MaxRuns(2));

        // Wait for 2 executions (cron */5 * * * * * = every 5 seconds)
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 12000);

        // Get task state
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();

        // Get completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(2)
            .ToList();

        completedRuns.Count.ShouldBe(2);

        // Assert: Interval should be approximately 5 seconds (cron schedule)
        var interval = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;
        interval.ShouldBeGreaterThan(4);
        interval.ShouldBeLessThan(6);

        // Cleanup automatic via IAsyncDisposable
    }
}
