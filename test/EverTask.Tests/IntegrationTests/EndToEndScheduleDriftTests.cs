using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// End-to-end integration tests for recurring task schedule drift fix.
/// Tests complete scenarios with multiple components working together.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
[Collection("TimingSensitiveTests")]
public class EndToEndScheduleDriftTests : IsolatedIntegrationTestBase
{

    [Fact]
    public async Task EndToEnd_Recurring_Task_Should_Execute_3_Times_And_Track_CurrentRunCount()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Act: Dispatch recurring task every 1 second
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Wait for 3 executions
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: 10000);

        // Assert
        var tasks = await Storage.GetAll();
        var task = tasks.FirstOrDefault(t => t.Id == taskId);

        task.ShouldNotBeNull();
        task.CurrentRunCount.HasValue.ShouldBeTrue();
        task.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(3);

        // Verify via audit trail
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(3)
            .ToList();

        completedRuns.Count.ShouldBe(3);
    }

    [Fact]
    public async Task EndToEnd_Retry_Should_Not_Affect_Next_Run_Calculation()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Act: Dispatch recurring task with retry policy (every 2 seconds), failing twice then succeeding
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringWithFailure(FailUntilCount: 2),
            recurring => recurring.Schedule().Every(2).Seconds());

        // Wait for 2 successful executions (each might have retries)
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 15000);

        // Assert
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

        // Verify that retries didn't affect scheduling
        // Interval should still be approximately 2 seconds between successful runs
        var interval = (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt).TotalSeconds;

        // Allow wider tolerance due to retry delays
        interval.ShouldBeGreaterThan(1.5);
        interval.ShouldBeLessThan(5); // Should not drift significantly despite retries
    }

    [Fact]
    public async Task EndToEnd_Timeout_Should_Not_Affect_Next_Run_Calculation()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Act: Dispatch recurring task with custom timeout (every 2 seconds)
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(2).Seconds());

        // Wait for 2 executions
        var task = await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 10000);

        // Get completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(2)
            .ToList();

        completedRuns.Count.ShouldBe(2);

        // Verify scheduling is consistent: the gap between the two runs must stay on the 2s occurrence
        // grid. The previous upper bound of 3s failed on a single lost slot, which realigns the series
        // to the NEXT occurrence (an exact 4s gap) without any drift.
        (completedRuns[1].ExecutedAt - completedRuns[0].ExecutedAt)
            .ShouldBeOnOccurrenceGrid(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EndToEnd_Multiple_Concurrent_Recurring_Tasks_Should_Maintain_Independent_Schedules()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Act: Dispatch 3 different recurring tasks with different intervals
        var task1Id = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        var task2Id = await Dispatcher.Dispatch(
            new TestTaskRecurringMinutes(),
            recurring => recurring.Schedule().Every(2).Seconds());

        var task3Id = await Dispatcher.Dispatch(
            new TestTaskDelayedRecurring(delayMs: 50),
            recurring => recurring.Schedule().Every(3).Seconds());

        // Wait for all tasks to complete at least 2 runs
        // Adaptive: Local 8s/12s, CI 20s/30s (coverage overhead)
        await Task.WhenAll(
            WaitForRecurringRunsAsync(task1Id, expectedRuns: 2, timeoutMs: TestEnvironment.GetTimeout(8000, 20000)),
            WaitForRecurringRunsAsync(task2Id, expectedRuns: 2, timeoutMs: TestEnvironment.GetTimeout(8000, 20000)),
            WaitForRecurringRunsAsync(task3Id, expectedRuns: 2, timeoutMs: TestEnvironment.GetTimeout(12000, 30000))
        );

        // Assert: Each task should maintain its own schedule
        var tasks = await Storage.GetAll();

        var task1 = tasks.FirstOrDefault(t => t.Id == task1Id);
        var task2 = tasks.FirstOrDefault(t => t.Id == task2Id);
        var task3 = tasks.FirstOrDefault(t => t.Id == task3Id);

        task1.ShouldNotBeNull();
        task2.ShouldNotBeNull();
        task3.ShouldNotBeNull();

        // Each gap is checked against its OWN cadence, on the grid: the three series run concurrently,
        // so any of them can lose a slot and realign to the next occurrence. The previous windows were
        // one cadence wide (task1: 0.5s-2s on a 1s cadence), which a single lost slot broke.
        AssertFirstTwoRunsOnGrid(task1, TimeSpan.FromSeconds(1));
        AssertFirstTwoRunsOnGrid(task2, TimeSpan.FromSeconds(2));
        AssertFirstTwoRunsOnGrid(task3, TimeSpan.FromSeconds(3));

        static void AssertFirstTwoRunsOnGrid(QueuedTask task, TimeSpan cadence)
        {
            var runs = task.RunsAudits
                .Where(a => a.Status == QueuedTaskStatus.Completed)
                .OrderBy(a => a.ExecutedAt)
                .Take(2)
                .ToList();

            runs.Count.ShouldBe(2);
            (runs[1].ExecutedAt - runs[0].ExecutedAt).ShouldBeOnOccurrenceGrid(cadence);
        }
    }

    [Fact]
    public async Task EndToEnd_Recurring_Task_With_Queue_Sharding_Should_Work()
    {
        // Arrange: Create host with sharding enabled
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder
                .AddQueue("shard1")
                .AddQueue("shard2")
                .AddMemoryStorage(),
            configureEverTask: cfg => cfg.SetMaxDegreeOfParallelism(10));

        // Act: Dispatch recurring tasks with unique task keys
        var task1Id = await Dispatcher.Dispatch(
            new TestTaskRecurringQueueShard1(),
            recurring => recurring.Schedule().Every(1).Seconds());

        var task2Id = await Dispatcher.Dispatch(
            new TestTaskRecurringQueueShard2(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Wait for both tasks to complete runs
        // Adaptive: Local 8s, CI 20s (coverage overhead)
        await Task.WhenAll(
            WaitForRecurringRunsAsync(task1Id, expectedRuns: 2, timeoutMs: TestEnvironment.GetTimeout(8000, 20000)),
            WaitForRecurringRunsAsync(task2Id, expectedRuns: 2, timeoutMs: TestEnvironment.GetTimeout(8000, 20000))
        );

        // Assert
        var tasks = await Storage.GetAll();

        var task1 = tasks.FirstOrDefault(t => t.Id == task1Id);
        var task2 = tasks.FirstOrDefault(t => t.Id == task2Id);

        task1.ShouldNotBeNull();
        task2.ShouldNotBeNull();

        task1.QueueName.ShouldBe("shard1");
        task2.QueueName.ShouldBe("shard2");

        task1.CurrentRunCount.HasValue.ShouldBeTrue();
        task1.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);
        task2.CurrentRunCount.HasValue.ShouldBeTrue();
        task2.CurrentRunCount?.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task EndToEnd_Complex_Scenario_With_Downtime_Recovery()
    {
        // Arrange: Create first host
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Preserve storage and state manager for reuse after restart
        var storage = Storage;
        var stateManager = StateManager;

        // Act: Dispatch recurring task
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Wait for the first execution to be PERSISTED: the handler counter is raised inside Handle,
        // while the run counter and NextRunUtc asserted below are written after the handler returns.
        await TaskWaitHelper.WaitForRecurringRunsAsync(storage, taskId, expectedRuns: 1, timeoutMs: 5000);

        // Simulate downtime. The delay is the downtime itself, not a wait for something to observe:
        // the host is stopped on purpose, so there is nothing to poll until it is back.
        const int downtimeMs = 3000;
        var downtimeStart = DateTimeOffset.UtcNow;
        await StopHostAsync();
        await Task.Delay(downtimeMs);
        var downtimeEnd = downtimeStart.AddMilliseconds(downtimeMs);

        // Restart: Create new host with same storage and state manager
        // NOTE: configureServices adds ITaskStorage AFTER base class AddMemoryStorage().
        // This intentionally overrides the default storage to reuse the preserved instance
        // for downtime recovery testing. Last registration wins in .NET DI.
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5,
            configureServices: svc =>
            {
                // Override default storage with preserved instance (last registration wins)
                svc.AddSingleton<ITaskStorage>(storage);
                // Override default state manager with preserved instance
                svc.AddSingleton(stateManager);
            });

        // Wait for the recovery to actually run the series again: LastExecutionUtc is stamped when a
        // run completes, so a value past the downtime window proves the restarted host took over -
        // a fixed delay only hoped it had.
        var task = await TaskWaitHelper.WaitUntilAsync(
            async () => (await storage.GetAll()).FirstOrDefault(t => t.Id == taskId),
            t => t?.LastExecutionUtc > downtimeEnd,
            timeoutMs: 10000);

        task.ShouldNotBeNull();

        // Should have skipped occurrences recorded
        // FIXME: SkippedOccurrencesAudits property does not exist

        // var skippedAudits = // FIXME: SkippedOccurrencesAudits property does not exist - task.task.SkippedOccurrencesAudits;

        // Should have resumed execution
        task.CurrentRunCount.HasValue.ShouldBeTrue();
        task.CurrentRunCount?.ShouldBeGreaterThan(1);

        // The series resumed on an occurrence AFTER the downtime instead of catching up on the ones it
        // missed. Anchored on the test-owned downtime window rather than on UtcNow at assertion time:
        // with a 1s cadence the next run is only 1s ahead, so any hiccup let the wall clock overtake it.
        task.NextRunUtc.ShouldNotBeNull();
        task.NextRunUtc.Value.ShouldBeGreaterThan(downtimeEnd);
    }

    [Fact]
    public async Task EndToEnd_No_Drift_Over_Many_Executions()
    {
        // Arrange
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5);

        // Act: Dispatch recurring task every 500ms (using 1 second for test speed)
        var interval = TimeSpan.FromSeconds(1);
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            recurring => recurring.Schedule().Every(1).Seconds());

        // The Dispatcher persists the row with the slot it picked for the first occurrence, a full
        // interval away: this read cannot see a NextRunUtc already advanced by the WorkerExecutor.
        var firstSlot = (await TaskWaitHelper.WaitForTaskExistsAsync(Storage, taskId)).NextRunUtc;
        firstSlot.ShouldNotBeNull();

        // Wait for 10 executions
        var task = await WaitForRecurringRunsAsync(taskId, expectedRuns: 10, timeoutMs: 15000);

        // Get all completed runs
        var completedRuns = task.RunsAudits
            .Where(a => a.Status == QueuedTaskStatus.Completed)
            .OrderBy(a => a.ExecutedAt)
            .Take(10)
            .ToList();

        completedRuns.Count.ShouldBe(10);

        // No drift, checked on the SCHEDULED grid, which carries no wall-clock jitter at all: after 10
        // runs NextRunUtc must still be an EXACT multiple of the cadence away from the slot the
        // Dispatcher picked (more than 10 when occurrences were lost - realignment stays on the same
        // grid). This is the assertion a UtcNow-based re-schedule fails: it would land at "instant the
        // last run finished + 1s", off the grid by the accumulated execution latency.
        task.NextRunUtc.ShouldNotBeNull();
        var advance = task.NextRunUtc.Value - firstSlot.Value;
        advance.ShouldBeGreaterThanOrEqualTo(interval * 10);
        (advance.Ticks % interval.Ticks).ShouldBe(0L);

        // The executions themselves are checked against the same grid, each measured from the FIRST
        // run: this bounds the observed cadence (nothing runs off-grid or ahead of its slot) while a
        // lost slot only moves a gap to a bigger multiple. The old form asserted a total of 8s-11s and
        // an average interval under 1.3s, which two lost slots out of ten overshot.
        for (var i = 1; i < completedRuns.Count; i++)
        {
            (completedRuns[i].ExecutedAt - completedRuns[0].ExecutedAt)
                .ShouldBeOnOccurrenceGrid(interval, minSlots: i);
        }
    }
}
